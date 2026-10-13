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
    /// <summary>도트 그래픽: 확대할 때 흐리지 않고 픽셀을 그대로 키운다.</summary>
    public bool Pixelated { get; set; }
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
    /// <summary>움직임: none | walk | bounce | fall (Motion 참고)</summary>
    public string MotionMode { get; set; } = Motion.None;
    /// <summary>움직이는 속도 (px/초)</summary>
    public double MotionSpeed { get; set; } = Motion.DefaultSpeed;
    /// <summary>움직이는 방향을 바라보도록 좌우 자동 반전</summary>
    public bool MotionFaceDirection { get; set; } = true;
    /// <summary>걷기·떨어지기: 켜면 작업표시줄 위가 바닥, 끄면 배치한 높이가 땅 (그 높이에서 수평 이동·점프)</summary>
    public bool MotionGravity { get; set; } = true;
    /// <summary>표시 위치: top = 모든 프로그램 위, desktop = 바탕화면 (모든 프로그램 아래)</summary>
    public string Layer { get; set; } = Layers.Top;
    /// <summary>떨어지기: 바닥에 멈춘 뒤 주기적으로 뛰어오름</summary>
    public bool JumpEnabled { get; set; } = true;
    /// <summary>점프 높이 (px)</summary>
    public double JumpHeight { get; set; } = Motion.DefaultJumpHeight;
    /// <summary>바닥에 내려온 뒤 다음 점프까지 (초)</summary>
    public double JumpInterval { get; set; } = Motion.DefaultJumpInterval;
    /// <summary>점프 각도 (도). 0 = 수직, + 오른쪽 / − 왼쪽. 벽에 부딪히면 방향을 바꿈</summary>
    public double JumpAngle { get; set; } = Motion.DefaultJumpAngle;
    /// <summary>점프 방향을 매번 랜덤으로 (−JumpAngleRange ~ +JumpAngleRange)</summary>
    public bool JumpAngleRandom { get; set; }
    public double JumpAngleRange { get; set; } = Motion.DefaultJumpAngleRange;
    /// <summary>점프 높이를 매번 랜덤으로 (JumpHeightMin ~ JumpHeightMax)</summary>
    public bool JumpHeightRandom { get; set; }
    public double JumpHeightMin { get; set; } = Motion.DefaultJumpHeightMin;
    public double JumpHeightMax { get; set; } = Motion.DefaultJumpHeightMax;

    /// <summary>미끄러짐: 멈춰 있으면 일정(또는 랜덤) 간격으로 저절로 밀려남</summary>
    public bool SlideAuto { get; set; }
    /// <summary>미는 방향: 랜덤 또는 PushAngle (0° 오른쪽, 90° 위, 180° 왼쪽, 270° 아래)</summary>
    public bool PushDirRandom { get; set; } = true;
    public double PushAngle { get; set; }
    /// <summary>미는 힘(처음 속도 px/s): 랜덤(PushForceMin~Max) 또는 PushForce</summary>
    public bool PushForceRandom { get; set; } = true;
    public double PushForce { get; set; } = Motion.DefaultPushForce;
    public double PushForceMin { get; set; } = Motion.DefaultPushForceMin;
    public double PushForceMax { get; set; } = Motion.DefaultPushForceMax;
    /// <summary>멈춘 뒤 다시 밀기까지(초): 랜덤(PushIntervalMin~Max) 또는 PushInterval</summary>
    public bool PushIntervalRandom { get; set; } = true;
    public double PushInterval { get; set; } = Motion.DefaultPushInterval;
    public double PushIntervalMin { get; set; } = Motion.DefaultPushIntervalMin;
    public double PushIntervalMax { get; set; } = Motion.DefaultPushIntervalMax;

    /// <summary>미끄러짐 마찰 (1~10). 높을수록 빨리 멈춤</summary>
    public double SlideFriction { get; set; } = Motion.DefaultFriction;

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
    /// <summary>UI 언어: "en"(기본) 또는 "ko"</summary>
    public string Language { get; set; } = "en";
    public bool StartWithWindows { get; set; }
    public bool OpenWindowOnStart { get; set; }
    public bool CopyOnAdd { get; set; } = true;
    public bool AllHidden { get; set; }
    /// <summary>움직이는 GIF를 이만큼(ms) 누르고 있으면 멈추고 잡음</summary>
    public int HoldToGrabMs { get; set; } = 200;
    /// <summary>잡았다 놓은 뒤 이만큼(ms) 지나면 다시 움직임</summary>
    public int ResumeDelayMs { get; set; } = 500;
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

/// <summary>GIF를 그리는 층.</summary>
public static class Layers
{
    public const string Top = "top";         // 모든 프로그램 위 (게임 포함)
    public const string Desktop = "desktop"; // 바탕화면 바로 위, 모든 프로그램 아래
    public static string Normalize(string? layer) => layer == Desktop ? Desktop : Top;
}
