using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace GifPin.Core;

/// <summary>라이브러리에 등록된 사진 한 장.</summary>
public sealed class PhotoItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>파일 경로. 데이터 폴더 기준 상대 경로이거나 절대 경로.</summary>
    public string File { get; set; } = "";
    /// <summary>처음 추가할 때의 원본 경로 (중복 추가 방지용).</summary>
    public string? SourcePath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; }
    public long FileSize { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>배경을 지운 사진이면 원본 사진의 Id.</summary>
    public string? SourceId { get; set; }
    public BgRemoveSettings? BgRemoval { get; set; }
}

public sealed class BgRemoveSettings
{
    public string Color { get; set; } = "#FFFFFF";
    public int Tolerance { get; set; } = 15;
    public bool ConnectedOnly { get; set; } = true;
    public int Refine { get; set; } = 1;
}

/// <summary>지금 화면에 떠 있는 GIF 하나.</summary>
public sealed class ActiveItem
{
    public string Id { get; set; } = "";
    public string PhotoId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public double Speed { get; set; } = 1;
    public bool FlipX { get; set; }
    public bool ClickThrough { get; set; } = true;
    public bool Visible { get; set; } = true;

    public ActiveItem Clone() => (ActiveItem)MemberwiseClone();
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool OpenWindowOnStart { get; set; }
    public bool CopyOnAdd { get; set; } = true;
    public bool AllHidden { get; set; }
    public bool TrayNoticeShown { get; set; }
    public WindowBounds? Window { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 2;
    public List<PhotoItem> Library { get; set; } = new();
    public List<ActiveItem> Active { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public PhotoItem? FindPhoto(string? id) => id == null ? null : Library.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// 상태 파일 저장소: %AppData%\GifPin\state.json
/// 변경은 UI 스레드에서 SaveSoon()으로 알리고, 실제 디스크 쓰기는 1초 뒤 한 번에 한다.
/// </summary>
public static class Store
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GifPin");
    public static string LibraryDir => Path.Combine(DataDir, "Library");
    public static string ThumbDir => Path.Combine(DataDir, "Thumbs");
    private static string StateFile => Path.Combine(DataDir, "state.json");
    private static string BackupFile => Path.Combine(DataDir, "state.bak");
    private static string PrototypeFile => Path.Combine(DataDir, "prototype.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static string? _pending;
    private static Timer? _timer;

    public static string ResolvePath(PhotoItem p) =>
        Path.IsPathRooted(p.File) ? p.File : Path.Combine(DataDir, p.File);

    public static string ThumbPath(PhotoItem p) => Path.Combine(ThumbDir, p.Id + ".png");

    public static AppState Load()
    {
        Directory.CreateDirectory(DataDir);
        foreach (var file in new[] { StateFile, BackupFile })
        {
            try
            {
                if (System.IO.File.Exists(file))
                {
                    var s = JsonSerializer.Deserialize<AppState>(System.IO.File.ReadAllText(file), JsonOptions);
                    if (s != null) return s;
                }
            }
            catch { /* 손상 → 다음 후보(백업) */ }
        }
        return MigrateFromPrototype() ?? new AppState { Settings = { OpenWindowOnStart = true } };
    }

    /// <summary>v0.1 프로토타입 설정(prototype.json)이 있으면 그 GIF를 라이브러리와 화면에 옮긴다.</summary>
    private static AppState? MigrateFromPrototype()
    {
        try
        {
            if (!System.IO.File.Exists(PrototypeFile)) return null;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(PrototypeFile));
            var root = doc.RootElement;
            var state = new AppState { Settings = { OpenWindowOnStart = true } };
            if (root.TryGetProperty("GifPath", out var gp) && gp.GetString() is string path && System.IO.File.Exists(path))
            {
                var added = Library.AddFiles(new[] { path }, state, copy: true);
                var photo = added.Added.FirstOrDefault();
                if (photo != null)
                {
                    var active = new ActiveItem { Id = NewId("a"), PhotoId = photo.Id };
                    if (root.TryGetProperty("X", out var x) && x.ValueKind == JsonValueKind.Number) active.X = x.GetInt32();
                    if (root.TryGetProperty("Y", out var y) && y.ValueKind == JsonValueKind.Number) active.Y = y.GetInt32();
                    if (root.TryGetProperty("Scale", out var sc) && sc.ValueKind == JsonValueKind.Number) active.Scale = sc.GetDouble();
                    state.Active.Add(active);
                    photo.LastUsedAt = DateTimeOffset.Now;
                }
            }
            SaveNow(state);
            return state;
        }
        catch { return null; }
    }

    public static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>UI 스레드에서 호출: 지금 상태를 직렬화해 두고 1초 뒤 디스크에 쓴다.</summary>
    public static void SaveSoon(AppState state)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        lock (Gate)
        {
            _pending = json;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(1000, Timeout.Infinite);
        }
    }

    public static void SaveNow(AppState state)
    {
        lock (Gate) _pending = JsonSerializer.Serialize(state, JsonOptions);
        Flush();
    }

    private static void Flush()
    {
        string? json;
        lock (Gate)
        {
            json = _pending;
            _pending = null;
        }
        if (json == null) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = StateFile + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            if (System.IO.File.Exists(StateFile)) System.IO.File.Copy(StateFile, BackupFile, overwrite: true);
            System.IO.File.Move(tmp, StateFile, overwrite: true);
        }
        catch { /* 다음 저장 때 다시 시도 */ }
    }
}
