using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// 스프라이트 시트(한 장에 프레임이 격자로 배치된 이미지)나 프레임 폴더(PNG 여러 장)를
/// 프레임 목록으로 만들고, 애니메이션 WebP로 저장한다.
/// </summary>
public static class SpriteBuilder
{
    public const int MaxFrames = 1000;
    public const int MaxFrameSide = 4096;
    public static readonly string[] FrameExtensions = { ".png", ".webp", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>이미지를 BGRA premultiplied 비트맵으로 읽는다.</summary>
    public static SKBitmap LoadBgra(string path)
    {
        using var src = SKBitmap.Decode(path) ?? throw new InvalidDataException(Loc.F("err.cannotOpenImage", Path.GetFileName(path)));
        var bmp = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Transparent);
        using var c = new SKCanvas(bmp);
        c.DrawBitmap(src, 0, 0);
        c.Flush();
        return bmp;
    }

    /// <summary>가로로 길면 가로 한 줄, 세로로 길면 세로 한 줄로 추정 (정사각 프레임 가정).</summary>
    public static (int columns, int rows) GuessGrid(int width, int height)
    {
        if (width > height && height > 0 && width % height == 0) return (width / height, 1);
        if (height > width && width > 0 && height % width == 0) return (1, height / width);
        return (1, 1);
    }

    /// <summary>시트를 columns×rows 격자로 잘라 왼쪽→오른쪽, 위→아래 순서로 count개를 꺼낸다.</summary>
    public static List<SKBitmap> FromSheet(SKBitmap sheet, int columns, int rows, int count)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        int fw = sheet.Width / columns, fh = sheet.Height / rows;
        if (fw < 1 || fh < 1) throw new ArgumentException(Loc.T("err.gridTooFine"));
        if (fw > MaxFrameSide || fh > MaxFrameSide) throw new ArgumentException(Loc.T("err.frameTooLarge"));
        int total = Math.Clamp(count <= 0 ? columns * rows : count, 1, Math.Min(columns * rows, MaxFrames));

        var frames = new List<SKBitmap>(total);
        var info = new SKImageInfo(fw, fh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var copy = new SKPaint { BlendMode = SKBlendMode.Src };
        for (int i = 0; i < total; i++)
        {
            int cx = i % columns, cy = i / columns;
            var frame = new SKBitmap(info);
            frame.Erase(SKColors.Transparent);
            using (var c = new SKCanvas(frame))
            {
                c.DrawBitmap(sheet, SKRect.Create(cx * fw, cy * fh, fw, fh), SKRect.Create(0, 0, fw, fh), copy);
                c.Flush();
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>폴더 안의 이미지 파일을 이름 순(숫자는 크기 순: 2 &lt; 10)으로 정렬.</summary>
    public static List<string> ListFrameFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => FrameExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), NaturalComparer.Instance)
            .Take(MaxFrames)
            .ToList();

    /// <summary>프레임 파일들을 읽는다. 크기가 다르면 첫 프레임 크기 캔버스의 가운데에 놓는다.</summary>
    public static List<SKBitmap> FromFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) throw new ArgumentException(Loc.T("err.noFrames"));
        var frames = new List<SKBitmap>();
        try
        {
            int w = 0, h = 0;
            foreach (var f in files)
            {
                using var img = LoadBgra(f);
                if (frames.Count == 0)
                {
                    w = img.Width;
                    h = img.Height;
                    if (w > MaxFrameSide || h > MaxFrameSide) throw new ArgumentException(Loc.T("err.frameTooLarge"));
                }
                var frame = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                frame.Erase(SKColors.Transparent);
                using (var c = new SKCanvas(frame))
                {
                    c.DrawBitmap(img, (w - img.Width) / 2f, (h - img.Height) / 2f);
                    c.Flush();
                }
                frames.Add(frame);
            }
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }
        return frames;
    }

    /// <summary>모든 프레임을 같은 간격(fps)으로 애니메이션 WebP에 저장.</summary>
    public static void Save(IReadOnlyList<SKBitmap> frames, double fps, string outPath)
    {
        int delay = (int)Math.Round(1000.0 / Math.Clamp(fps, 1, 60));
        WebpWriter.Save(outPath, frames, Enumerable.Repeat(delay, frames.Count).ToList());
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        private static readonly Regex Parts = new(@"\d+|\D+", RegexOptions.Compiled);

        public int Compare(string? a, string? b)
        {
            var pa = Parts.Matches(a ?? "");
            var pb = Parts.Matches(b ?? "");
            for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            {
                string x = pa[i].Value, y = pb[i].Value;
                int r = char.IsDigit(x[0]) && char.IsDigit(y[0])
                    ? (x.TrimStart('0').Length != y.TrimStart('0').Length
                        ? x.TrimStart('0').Length.CompareTo(y.TrimStart('0').Length)
                        : string.CompareOrdinal(x.TrimStart('0'), y.TrimStart('0')))
                    : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                if (r != 0) return r;
            }
            return pa.Count.CompareTo(pb.Count);
        }
    }
}
