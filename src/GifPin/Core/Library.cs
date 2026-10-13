using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace GifPin.Core;

public sealed class ImportResult
{
    public List<PhotoItem> Added { get; } = new();
    /// <summary>이미 라이브러리에 있어 새로 추가하지 않은 사진.</summary>
    public List<PhotoItem> Existing { get; } = new();
    /// <summary>지원하지 않거나 열 수 없어 건너뛴 파일 수.</summary>
    public int Skipped { get; set; }
}

/// <summary>사진 파일 등록·썸네일·삭제. 상태(AppState)는 바꾸지 않으므로 백그라운드 스레드에서 호출해도 된다.</summary>
public static class Library
{
    public static readonly string[] SupportedExtensions = { ".gif", ".webp", ".png", ".apng", ".jpg", ".jpeg", ".bmp" };
    public static string OpenFileFilter =>
        Loc.T("file.images") + " (*.gif;*.webp;*.png;*.apng;*.jpg;*.jpeg;*.bmp)|*.gif;*.webp;*.png;*.apng;*.jpg;*.jpeg;*.bmp|" + Loc.T("file.all") + " (*.*)|*.*";

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>파일·폴더 목록을 가져온다. 폴더는 바로 안의 이미지 파일만.</summary>
    public static ImportResult Import(IEnumerable<string> paths, IReadOnlyCollection<PhotoItem> existing, bool copy)
    {
        var result = new ImportResult();
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p).Where(IsSupported).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (System.IO.File.Exists(p))
                files.Add(p);
        }

        foreach (var file in files)
        {
            if (!IsSupported(file)) { result.Skipped++; continue; }

            var full = Path.GetFullPath(file);
            var dup = existing.Concat(result.Added).FirstOrDefault(e =>
                string.Equals(e.SourcePath, full, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Store.ResolvePath(e), full, StringComparison.OrdinalIgnoreCase));
            if (dup != null) { result.Existing.Add(dup); continue; }

            var photo = CreatePhoto(full, copy, Path.GetFileNameWithoutExtension(full));
            if (photo == null) result.Skipped++;
            else result.Added.Add(photo);
        }
        return result;
    }

    /// <summary>마이그레이션 등 UI 밖에서 바로 상태에 추가할 때.</summary>
    public static ImportResult AddFiles(IEnumerable<string> paths, AppState state, bool copy)
    {
        var r = Import(paths, state.Library, copy);
        state.Library.AddRange(r.Added);
        return r;
    }

    /// <summary>파일 하나를 검사하고(열 수 있는지·크기·프레임 수), 필요하면 라이브러리로 복사하고 썸네일을 만든다.</summary>
    public static PhotoItem? CreatePhoto(string path, bool copy, string name)
    {
        int w, h, frames;
        using (var codec = SKCodec.Create(path))
        {
            if (codec == null) return null;
            w = codec.Info.Width;
            h = codec.Info.Height;
            frames = Math.Max(1, codec.FrameCount);
        }
        if (ApngDecoder.Probe(path) is { } apng) // APNG는 Skia가 1프레임으로 보고함
        {
            (w, h, frames) = apng;
        }

        var id = Store.NewId("p");
        string stored = path;
        if (copy)
        {
            Directory.CreateDirectory(Store.LibraryDir);
            var dest = Path.Combine(Store.LibraryDir, id + Path.GetExtension(path).ToLowerInvariant());
            System.IO.File.Copy(path, dest, overwrite: true);
            stored = Path.GetRelativePath(Store.DataDir, dest);
        }

        var photo = new PhotoItem
        {
            Id = id,
            Name = name,
            File = stored,
            SourcePath = path,
            Width = w,
            Height = h,
            FrameCount = frames,
            FileSize = new FileInfo(path).Length,
            AddedAt = DateTimeOffset.Now,
        };
        TryCreateThumbnail(photo);
        return photo;
    }

    /// <summary>라이브러리 폴더 안에 직접 만든 파일(배경 제거 결과 등)을 사진으로 등록.</summary>
    public static PhotoItem? RegisterGenerated(string pathInLibrary, string name)
    {
        var photo = CreatePhoto(pathInLibrary, copy: false, name);
        if (photo == null) return null;
        photo.File = Path.GetRelativePath(Store.DataDir, pathInLibrary);
        photo.SourcePath = null;
        return photo;
    }

    /// <summary>첫 프레임을 최대 320px PNG로 저장 (카드 160px의 2배 해상도).</summary>
    public static bool TryCreateThumbnail(PhotoItem photo)
    {
        try
        {
            using var src = SKBitmap.Decode(Store.ResolvePath(photo));
            if (src == null) return false;
            const int max = 320;
            float k = Math.Min(1f, (float)max / Math.Max(src.Width, src.Height));
            var info = new SKImageInfo(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)),
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = src.Resize(info, SKFilterQuality.Medium) ?? src.Copy();
            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            Directory.CreateDirectory(Store.ThumbDir);
            using var fs = System.IO.File.Create(Store.ThumbPath(photo));
            data.SaveTo(fs);
            return true;
        }
        catch { return false; }
    }

    /// <summary>라이브러리 폴더 안의 복사본과 썸네일을 지운다. 원본 파일(라이브러리 밖)은 건드리지 않는다.</summary>
    public static void DeleteFiles(PhotoItem photo)
    {
        try
        {
            var path = Path.GetFullPath(Store.ResolvePath(photo));
            var libDir = Path.GetFullPath(Store.LibraryDir) + Path.DirectorySeparatorChar;
            if (path.StartsWith(libDir, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch { }
        try { System.IO.File.Delete(Store.ThumbPath(photo)); } catch { }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
