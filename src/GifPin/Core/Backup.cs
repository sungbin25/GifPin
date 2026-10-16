using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GifPin.Core;

/// <summary>
/// 설정·라이브러리·화면 배치를 파일 하나(.zip)로 백업하고 복원한다.
///  - gifpin-backup.json: 형식 표시
///  - state.json: 상태 (사진 경로는 zip 안의 Library/… 기준)
///  - Library/…: 사진 파일 (라이브러리 밖에 있던 원본도 함께 담음)
/// </summary>
public static class Backup
{
    private const string ManifestName = "gifpin-backup.json";
    private const string StateName = "state.json";
    private const int FormatVersion = 1;
    private const long MaxStateBytes = 32L * 1024 * 1024;
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxEntries = 20000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Manifest
    {
        public int Format { get; set; }
        public string App { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public int Photos { get; set; }
    }

    public static string BackupDir => Path.Combine(Store.DataDir, "Backups");

    public static string DefaultFileName() => $"MotiNook-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <summary>백업 파일을 만든다. 돌려주는 값: 담은 사진 수, 찾지 못한 사진 수.</summary>
    public static (int photos, int missing) Create(string zipPath, AppState state, string appVersion)
    {
        // 상태를 복사해 사진 경로만 zip 안 경로로 바꿈 (원래 상태는 건드리지 않음)
        var copy = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state, Json), Json)!;
        string tmp = zipPath + ".tmp";
        int photos = 0, missing = 0;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in copy.Library)
            {
                var original = state.FindPhoto(p.Id);
                string src = original != null ? Store.ResolvePath(original) : "";
                if (!File.Exists(src)) { missing++; continue; }
                string name = Path.GetFileName(src);
                if (!used.Add(name)) { name = p.Id + Path.GetExtension(src); used.Add(name); }
                zip.CreateEntryFromFile(src, "Library/" + name, CompressionLevel.NoCompression); // GIF·WebP는 이미 압축됨
                p.File = Path.Combine("Library", name);
                p.SourcePath = null;
                photos++;
            }
            copy.Library.RemoveAll(p => !p.File.StartsWith("Library", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(p.File));
            var manifest = new Manifest { Format = FormatVersion, App = appVersion, CreatedAt = DateTimeOffset.Now, Photos = photos };
            WriteJson(zip, ManifestName, manifest);
            WriteJson(zip, StateName, copy);
        }
        File.Move(tmp, zipPath, overwrite: true);
        return (photos, missing);
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        JsonSerializer.Serialize(s, value, Json);
    }

    /// <summary>
    /// 백업 파일을 확인하고 사진을 라이브러리 폴더로 풀어 넣은 뒤, 복원할 상태를 돌려준다 (저장·적용은 부른 쪽에서).
    /// 잘못된 파일이면 InvalidDataException.
    /// </summary>
    public static AppState Restore(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException(Loc.T("backup.invalid"));
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException(Loc.T("backup.invalid"));
        var stateEntry = zip.GetEntry(StateName) ?? throw new InvalidDataException(Loc.T("backup.invalid"));
        if (stateEntry.Length > MaxStateBytes || manifestEntry.Length > 1024 * 1024) throw new InvalidDataException(Loc.T("backup.invalid"));
        Manifest manifest;
        AppState state;
        try
        {
            using (var s = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<Manifest>(s, Json) ?? throw new InvalidDataException();
            using (var s = stateEntry.Open()) state = JsonSerializer.Deserialize<AppState>(s, Json) ?? throw new InvalidDataException();
        }
        catch (JsonException) { throw new InvalidDataException(Loc.T("backup.invalid")); }
        if (manifest.Format < 1) throw new InvalidDataException(Loc.T("backup.invalid"));
        if (manifest.Format > FormatVersion) throw new InvalidDataException(Loc.T("backup.newer"));
        if (zip.Entries.Sum(e => e.Length) > MaxTotalBytes) throw new InvalidDataException(Loc.T("backup.invalid"));

        state = Store.Migrate(state);
        Directory.CreateDirectory(Store.LibraryDir);
        string libRoot = Path.GetFullPath(Store.LibraryDir) + Path.DirectorySeparatorChar;
        var keep = new List<PhotoItem>();
        foreach (var p in state.Library)
        {
            if (p == null || string.IsNullOrEmpty(p.File) || Path.IsPathRooted(p.File)) continue;
            string entryName = p.File.Replace('\\', '/');
            var entry = zip.GetEntry(entryName);
            if (entry == null) continue;
            string dest = Path.GetFullPath(Path.Combine(Store.DataDir, p.File));
            if (!dest.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase)) continue; // 폴더 밖으로 나가는 경로는 무시
            entry.ExtractToFile(dest, overwrite: true);
            keep.Add(p);
        }
        state.Library = keep;
        var ids = keep.Select(p => p.Id).ToHashSet();
        state.Active.RemoveAll(a => a == null || !ids.Contains(a.PhotoId));
        foreach (var a in state.Active) a.Normalize();
        foreach (var sc in state.Scenes) sc.Items.RemoveAll(a => !ids.Contains(a.PhotoId));
        return state;
    }

    /// <summary>복원하기 전에 지금 상태를 자동으로 백업 (실수로 덮어써도 되돌릴 수 있게). 최근 5개만 남김.</summary>
    public static string? AutoBackup(AppState state, string appVersion, string reason)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            string path = Path.Combine(BackupDir, $"auto-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            Create(path, state, appVersion);
            foreach (var old in new DirectoryInfo(BackupDir).GetFiles("auto-*.zip").OrderByDescending(f => f.CreationTimeUtc).Skip(5))
            {
                try { old.Delete(); } catch { }
            }
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("Automatic backup failed", ex);
            return null;
        }
    }
}
