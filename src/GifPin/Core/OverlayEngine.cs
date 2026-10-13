using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static GifPin.Core.Native;

namespace GifPin.Core;

/// <summary>
/// 모든 오버레이 창을 전용 스레드(자체 메시지 루프)에서 돌린다.
/// 메인 창(WPF)이 바쁘거나 멈춰도 GIF 재생이 끊기지 않게 하기 위함.
/// 공개 메서드는 어느 스레드에서 불러도 되며, 이벤트는 엔진 스레드에서 발생한다(UI는 Dispatcher로 넘길 것).
/// </summary>
public sealed class OverlayEngine : IDisposable
{
    public const int HotkeyEdit = 1, HotkeyHide = 2, HotkeyQuit = 3;
    private const string MsgClassName = "GifPinEngine";
    private const uint WM_RUN = WM_APP + 1;
    private const uint WM_FRAME = WM_APP + 2;
    private const nuint TIMER_TOPMOST = 1;

    /// <summary>드래그·휠·방향키로 위치·크기가 확정됨 (사본 전달).</summary>
    public event Action<ActiveItem>? Changed;
    /// <summary>드래그 중 실시간 위치·크기 (편집 툴바 표시용).</summary>
    public event Action<ActiveItem>? LiveChanged;
    /// <summary>편집 모드에서 GIF를 클릭해 선택함.</summary>
    public event Action<string>? Clicked;
    public event Action<int>? HotkeyPressed;
    public event Action<string, string>? LoadFailed;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<string, OverlayWindow> _byId = new();
    private readonly Dictionary<IntPtr, OverlayWindow> _byHwnd = new();
    private Thread? _thread;
    private IntPtr _msgHwnd, _winEventHook;
    private bool _editMode, _allHidden;
    private string? _selectedId;
    private readonly List<string> _hotkeyFailures = new();

    /// <summary>움직이는 GIF를 잡기까지 눌러야 하는 시간 / 놓은 뒤 다시 출발하기까지 (ms). 엔진 스레드에서 읽음.</summary>
    internal volatile int HoldToGrabMs = 200, ResumeDelayMs = 500;

    /// <summary>효과 설정 (엔진 스레드에서 읽음).</summary>
    internal volatile int FxFps = 60;
    private volatile bool _pauseFxFullscreen = true, _pauseFxBattery;
    /// <summary>전체 화면 앱·배터리 때문에 효과를 멈춘 상태.</summary>
    internal bool FxPaused { get; private set; }

    // ── 프레임 박자 ──
    // 움직임·효과는 창마다 따로 타이머를 돌리지 않고, 이 박자에 맞춰 "계산 → 그리기"를 한 번에 한다.
    // 박자는 화면 갱신(DwmFlush)에 맞추고, 그게 안 되면 고정밀 타이머로 정확한 간격을 지킨다.
    private Thread? _pacer;
    private readonly AutoResetEvent _pacerWake = new(false);
    private volatile bool _pacerRun = true;
    private volatile bool _framesWanted;
    private int _framePending;
    /// <summary>지금 박자 방식 (진단용): "vsync" 또는 "timer".</summary>
    public string FramePacing { get; private set; } = "timer";

    /// <summary>창이 매 프레임 계산·그리기가 필요해졌을 때 (엔진 스레드에서 호출).</summary>
    internal void WantFrames()
    {
        if (_framesWanted) return;
        _framesWanted = true;
        _pacerWake.Set();
    }

    private void PacerLoop()
    {
        IntPtr timer = IntPtr.Zero;
        try { timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS); } catch { }
        bool lowRes = timer == IntPtr.Zero;
        if (lowRes) { try { timeBeginPeriod(1); } catch { } }
        double freq = System.Diagnostics.Stopwatch.Frequency;
        bool useVsync = true;
        int vsyncMisses = 0;
        double vsyncPeriod = 1 / 60.0;
        long last = System.Diagnostics.Stopwatch.GetTimestamp(), lastFlush = last;
        Log.Info($"Frame pacer started ({(lowRes ? "1 ms sleep" : "high-resolution timer")} + display sync)");

        void SleepUntil(long deadline)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (deadline <= now) return;
            double sec = (deadline - now) / freq;
            if (!lowRes)
            {
                long due = -(long)(sec * 10_000_000); // 100ns 단위, 음수 = 지금부터
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) { WaitForSingleObject(timer, INFINITE); return; }
            }
            Thread.Sleep(Math.Max(1, (int)(sec * 1000)));
        }

        try
        {
            while (_pacerRun)
            {
                if (!_framesWanted)
                {
                    _pacerWake.WaitOne(500);
                    last = System.Diagnostics.Stopwatch.GetTimestamp();
                    continue;
                }
                double interval = 1.0 / Math.Max(1, FxFps);
                bool synced = false;
                if (useVsync)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int hr;
                    try { hr = DwmFlush(); } catch { hr = -1; }
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    synced = hr == 0 && (t1 - t0) / freq >= 0.002; // 실제로 화면 갱신을 기다렸을 때만
                    if (synced)
                    {
                        vsyncMisses = 0;
                        double p = (t1 - lastFlush) / freq;
                        if (p > 0.002 && p < 0.05) vsyncPeriod = vsyncPeriod * 0.9 + p * 0.1;
                        lastFlush = t1;
                        if (FramePacing != "vsync") FramePacing = "vsync";
                    }
                    else if (++vsyncMisses > 30)
                    {
                        useVsync = false; // 화면 동기화가 안 되는 환경 → 타이머만
                        FramePacing = "timer";
                        Log.Info("Display sync unavailable; using the precise timer for frame pacing");
                    }
                }
                if (!synced)
                {
                    if (FramePacing != "timer" && !useVsync) FramePacing = "timer";
                    SleepUntil(last + (long)(interval * freq)); // 바쁘게 돌지 않도록 항상 기다림
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsed = (now - last) / freq;
                // 화면 갱신에 맞출 때: 목표 간격에 반 프레임 이내로 가까우면 이번 갱신에 그림 (60Hz에서 60fps, 144Hz에서 72fps)
                if (elapsed >= interval - (synced ? vsyncPeriod * 0.5 : 0.0005))
                {
                    last = now;
                    if (Interlocked.Exchange(ref _framePending, 1) == 0)
                        PostMessageW(_msgHwnd, WM_FRAME, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch (Exception ex) { Log.Error("Frame pacer stopped", ex); }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
            if (lowRes) { try { timeEndPeriod(1); } catch { } }
        }
    }

    /// <summary>한 프레임: 프레임이 필요한 모든 창을 같은 시각 기준으로 진행.</summary>
    private void OnFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool any = false;
        foreach (var w in _byId.Values.ToList())
        {
            if (!w.NeedsFrames) continue;
            w.OnFrame(now);
            any |= w.NeedsFrames;
        }
        if (!any) _framesWanted = false;
    }

    // 네이티브 콜백은 GC 수거 방지를 위해 필드로 보관
    private readonly WndProc _overlayProc, _msgProc;
    private readonly WinEventProc _winEventProc;

    public OverlayEngine()
    {
        _overlayProc = OverlayProc;
        _msgProc = MsgProc;
        _winEventProc = (_, _, hwnd, _, _, _, _) => { if (!_byHwnd.ContainsKey(hwnd)) BringAllToTop(); };
    }

    /// <summary>등록하지 못한 단축키 이름 (다른 프로그램이 사용 중).</summary>
    public IReadOnlyList<string> HotkeyFailures => _hotkeyFailures;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "GifPin.Overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        IntPtr hInstance = GetModuleHandleW(null);
        Register(OverlayWindow.ClassName, _overlayProc, hInstance);
        Register(MsgClassName, _msgProc, hInstance);

        _msgHwnd = CreateWindowExW(0, MsgClassName, "GifPin engine", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        if (!RegisterHotKey(_msgHwnd, HotkeyEdit, mods, 0x45)) _hotkeyFailures.Add("Ctrl+Alt+E");
        if (!RegisterHotKey(_msgHwnd, HotkeyHide, mods, 0x48)) _hotkeyFailures.Add("Ctrl+Alt+H");
        if (!RegisterHotKey(_msgHwnd, HotkeyQuit, mods, 0x51)) _hotkeyFailures.Add("Ctrl+Alt+Q");

        _pacer = new Thread(PacerLoop) { IsBackground = true, Name = "GifPin.FramePacer", Priority = ThreadPriority.AboveNormal };
        _pacer.Start();

        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        SetTimer(_msgHwnd, TIMER_TOPMOST, 1500, IntPtr.Zero);
        _ready.Set();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        foreach (var w in _byId.Values) w.Dispose();
        _byId.Clear();
        _byHwnd.Clear();
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
    }

    private static void Register(string name, WndProc proc, IntPtr hInstance)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(IDC_SIZEALL)),
            lpszClassName = name,
        };
        RegisterClassExW(ref wc);
    }

    private IntPtr OverlayProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_byHwnd.TryGetValue(hWnd, out var w))
            {
                var r = w.WndProc(msg, wParam, lParam);
                if (r.HasValue) return r.Value;
            }
        }
        catch (Exception ex) { Log.Error("Overlay message handling failed", ex); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr MsgProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_RUN:
                    while (_queue.TryDequeue(out var action)) action();
                    return IntPtr.Zero;
                case WM_FRAME:
                    OnFrame();
                    return IntPtr.Zero;
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_TIMER:
                    BringAllToTop();
                    CheckFxPause();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log.Error("Overlay message handling failed", ex); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Post(Action action)
    {
        _queue.Enqueue(action);
        PostMessageW(_msgHwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    private void BringAllToTop()
    {
        if (_editMode) return; // 편집 중에는 편집 툴바가 GIF보다 위에 있어야 함
        foreach (var w in _byId.Values) w.BringToTop();
    }

    // ── 공개 API (아무 스레드) ─────────────────────────────────

    /// <summary>화면의 GIF를 만들거나 설정을 바꾼다. filePath가 바뀌면 새로 디코딩한다.</summary>
    public void Sync(ActiveItem item, string filePath, bool pixelated = false, IReadOnlyList<string>? particlePaths = null)
    {
        var snapshot = item.Clone();
        var spritePaths = particlePaths?.ToList() ?? new List<string>();
        string spriteKey = string.Join("|", spritePaths);
        Post(() =>
        {
            if (!_byId.TryGetValue(snapshot.Id, out var w))
            {
                w = new OverlayWindow(this, snapshot);
                _byId[snapshot.Id] = w;
                _byHwnd[w.Hwnd] = w;
                w.SetEditMode(_editMode);
                w.SetSelected(_selectedId == snapshot.Id);
                w.SetGlobalHidden(_allHidden);
            }
            w.Update(snapshot);
            w.SetPixelated(pixelated);

            // 내 이미지 파티클: 목록이 바뀌었을 때만 작게 줄여서 새로 불러옴
            if (w.ParticleKey != spriteKey)
            {
                w.ParticleKey = spriteKey;
                if (spritePaths.Count == 0) w.SetParticleSprites(null);
                else
                    Task.Run(() =>
                    {
                        var list = new List<GifAnimation>();
                        foreach (var path in spritePaths)
                        {
                            try { list.Add(GifAnimation.Load(path, Fx.ParticleSpriteSize)); }
                            catch (Exception ex) { Log.Warn($"Particle image could not be loaded: {path}", ex); }
                        }
                        return list;
                    }).ContinueWith(t => Post(() =>
                    {
                        var list = t.IsFaulted ? new List<GifAnimation>() : t.Result;
                        if (_byId.TryGetValue(snapshot.Id, out var target) && target.ParticleKey == spriteKey) target.SetParticleSprites(list);
                        else foreach (var s in list) s.Dispose();
                    }));
            }

            if (w.AnimPath != filePath && w.PendingPath != filePath)
            {
                w.PendingPath = filePath;
                Task.Run(() => GifAnimation.Load(filePath)).ContinueWith(t => Post(() =>
                {
                    bool current = _byId.TryGetValue(snapshot.Id, out var target) && target.PendingPath == filePath;
                    if (t.IsFaulted)
                    {
                        if (current) target!.PendingPath = null;
                        Log.Error($"Failed to load {filePath}", t.Exception?.GetBaseException());
                        LoadFailed?.Invoke(snapshot.Id, t.Exception?.GetBaseException().Message ?? "unknown error");
                        return;
                    }
                    if (!current) { t.Result.Dispose(); return; }
                    target!.PendingPath = null;
                    target.SetAnimation(t.Result, filePath);
                }));
            }
        });
    }

    public void Remove(string id) => Post(() =>
    {
        if (_byId.Remove(id, out var w))
        {
            _byHwnd.Remove(w.Hwnd);
            w.Dispose();
        }
        if (_selectedId == id) _selectedId = null;
    });

    public void SetEditMode(bool edit) => Post(() =>
    {
        _editMode = edit;
        foreach (var w in _byId.Values) w.SetEditMode(edit);
    });

    public void Select(string? id) => Post(() =>
    {
        _selectedId = id;
        foreach (var w in _byId.Values) w.SetSelected(w.Item.Id == id);
    });

    public void Flash(string id) => Post(() =>
    {
        if (!_editMode && _byId.TryGetValue(id, out var w)) w.Flash();
    });

    public void SetAllHidden(bool hidden) => Post(() =>
    {
        _allHidden = hidden;
        foreach (var w in _byId.Values) w.SetGlobalHidden(hidden);
    });

    /// <summary>위치를 강제로 옮김 (움직이는 GIF 포함).</summary>
    public void Relocate(string id, int x, int y) => Post(() =>
    {
        if (_byId.TryGetValue(id, out var w)) w.Relocate(x, y);
    });

    /// <summary>효과 성능 설정: 초당 프레임, 전체 화면·배터리일 때 멈춤.</summary>
    public void SetEffectsSettings(int fps, bool pauseFullscreen, bool pauseOnBattery) => Post(() =>
    {
        FxFps = fps >= 45 ? 60 : 30;
        _pauseFxFullscreen = pauseFullscreen;
        _pauseFxBattery = pauseOnBattery;
        CheckFxPause(force: true);
    });

    /// <summary>전체 화면 게임·발표 중이거나 배터리 사용 중이면 효과를 멈춘다 (1.5초마다 확인).</summary>
    private void CheckFxPause(bool force = false)
    {
        bool paused = false;
        try
        {
            if (_pauseFxFullscreen && SHQueryUserNotificationState(out int state) == 0)
                paused = state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
            if (!paused && _pauseFxBattery && GetSystemPowerStatus(out var ps))
                paused = ps.ACLineStatus == 0;
        }
        catch (Exception ex) { Log.Warn("Could not check full-screen / power state", ex); }
        if (paused == FxPaused && !force) return;
        if (paused != FxPaused) Log.Info(paused ? "Effects paused (full screen or battery)" : "Effects resumed");
        FxPaused = paused;
        foreach (var w in _byId.Values) w.UpdateFxTimer();
    }

    public void SetPointerTiming(int holdMs, int resumeMs)
    {
        HoldToGrabMs = Math.Clamp(holdMs, 0, 5000);
        ResumeDelayMs = Math.Clamp(resumeMs, 0, 10000);
    }

    // ── 점검용 (스모크 테스트) ──────────────────────────────

    public enum PointerAction { Down, Move, Up }

    /// <summary>마우스 동작을 흉내 냄. atMs = 기준 시각(baseTs)으로부터의 가상 시각.</summary>
    public void SimulatePointer(string id, PointerAction action, int x, int y, long baseTs, double atMs) => Post(() =>
    {
        if (!_byId.TryGetValue(id, out var w)) return;
        long ts = baseTs + (long)(atMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        switch (action)
        {
            case PointerAction.Down: w.PointerDown(x, y, ts); break;
            case PointerAction.Move: w.PointerMove(x, y, ts); break;
            default: w.PointerUp(x, y, ts); break;
        }
    });

    public Task<OverlayProbe?> ProbeAsync(string id)
    {
        var tcs = new TaskCompletionSource<OverlayProbe?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult(_byId.TryGetValue(id, out var w) ? w.Probe() : null));
        return tcs.Task;
    }

    public void Nudge(string id, int dx, int dy) => Post(() =>
    {
        if (_byId.TryGetValue(id, out var w)) w.Nudge(dx, dy);
    });

    // ── 바탕화면 층 ─────────────────────────────────────────

    private IntPtr _desktopHost;

    /// <summary>바탕화면 아이콘을 품은 최상위 창 (Progman 또는 WorkerW). 탐색기가 다시 시작되면 다시 찾는다.</summary>
    internal IntPtr DesktopHost()
    {
        if (_desktopHost != IntPtr.Zero && IsWindow(_desktopHost)) return _desktopHost;
        IntPtr progman = FindWindowW("Progman", null);
        IntPtr host = IntPtr.Zero;
        if (progman != IntPtr.Zero && FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) host = progman;
        for (IntPtr w = IntPtr.Zero; host == IntPtr.Zero && (w = FindWindowExW(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero;)
            if (FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) host = w;
        _desktopHost = host != IntPtr.Zero ? host : progman;
        return _desktopHost;
    }

    internal bool IsOverlay(IntPtr hwnd) => _byHwnd.ContainsKey(hwnd);

    // ── OverlayWindow → 엔진 → UI ────────────────────────────

    internal void RaiseChanged(ActiveItem item) => Changed?.Invoke(item.Clone());
    internal void RaiseLive(ActiveItem item) => LiveChanged?.Invoke(item.Clone());

    internal void RaiseClicked(string id)
    {
        if (_selectedId != id)
        {
            _selectedId = id;
            foreach (var w in _byId.Values) w.SetSelected(w.Item.Id == id);
        }
        Clicked?.Invoke(id);
    }

    public void Dispose()
    {
        if (_thread == null) return;
        _pacerRun = false;
        _pacerWake.Set();
        _pacer?.Join(500);
        Post(() => PostQuitMessage(0));
        _thread.Join(2000);
        _thread = null;
    }
}

/// <summary>오버레이 상태 (점검용).</summary>
public sealed record OverlayProbe(ActiveItem Item, bool MotionRunning, bool Held, bool Pressed, bool Sliding, int Jumps, bool Topmost, int Pushes)
{
    public bool FxRunning { get; init; }
    public int Particles { get; init; }
    public int Squashes { get; init; }
    public int DustPuffs { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public int Sprites { get; init; }
    public int FramesPerSecond { get; init; }
}
