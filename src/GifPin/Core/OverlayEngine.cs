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
    private const string MsgClassName = "GifPinEngine";
    private const uint WM_RUN = WM_APP + 1;
    private const uint WM_FRAME = WM_APP + 2;
    private const nuint TIMER_TOPMOST = 1, TIMER_HOVER = 2;
    private const int HoverPollMs = 40;

    /// <summary>드래그·휠·방향키로 위치·크기가 확정됨 (사본 전달).</summary>
    public event Action<ActiveItem>? Changed;
    /// <summary>움직임으로 위치가 바뀜 (자주 옴 → 바로 저장하지 않아도 됨).</summary>
    public event Action<ActiveItem>? Moved;
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
            try { w.OnFrame(now); }
            catch (Exception ex)
            {
                // 한 GIF의 오류가 다른 GIF를 멈추지 않게. 같은 오류가 매 프레임 쌓이지 않도록 로그는 한 번만
                if (w.FrameErrors++ == 0) Log.Error($"Frame update failed for overlay {w.Item.Id}", ex);
            }
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
        _winEventProc = (_, _, hwnd, _, _, _, _) =>
        {
            if (_byHwnd.ContainsKey(hwnd)) return;
            BringAllToTop();
            CheckAppRules();
        };
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
        foreach (var e in _anims.Values) e.Anim?.Dispose();
        _anims.Clear();
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
                    // 작업 하나가 실패해도 나머지는 계속 처리
                    while (_queue.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { Log.Error("Overlay task failed", ex); }
                    }
                    return IntPtr.Zero;
                case WM_FRAME:
                    OnFrame();
                    return IntPtr.Zero;
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_TIMER:
                    if ((nuint)wParam == TIMER_HOVER) { CheckHover(); return IntPtr.Zero; }
                    BringAllToTop();
                    CheckFxPause();
                    CheckAppRules();
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
    public void Sync(ActiveItem item, string filePath, bool pixelated = false, IReadOnlyList<string>? particlePaths = null,
        int srcWidth = 0, int srcHeight = 0)
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
                w.SetGlobalHidden(_allHidden || RuleHiddenBy != null);
                w.SetCaptureExcluded(_hideFromCapture);
                UpdateHoverTimer();
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

            // GIF 본체: 화면에 보이는 크기에 맞춰 디코딩 (같은 파일·같은 크기는 다른 창과 공유)
            var (tw, th) = AnimTarget(srcWidth, srcHeight, snapshot.Scale, pixelated);
            string want = AnimKey(filePath, tw, th);
            if (w.PendingKey == want) { } // 바로 그것을 불러오는 중
            else if (w.AnimKey == want || KeepCurrent(w, filePath, tw))
                w.PendingKey = null; // 지금 것으로 충분 → 다른 사진을 불러오던 중이었다면 취소 (A→B→A로 빨리 바꿀 때)
            else LoadFor(w, filePath, tw, th);
        });
    }

    // ── 디코딩한 GIF 공유 ───────────────────────────────────
    // 같은 파일을 같은 크기로 띄운 창들은 디코딩 결과 하나를 함께 쓰고(참조 수), 아무도 안 쓰면 해제한다.

    private sealed class SharedAnim
    {
        public GifAnimation? Anim;
        public Task<GifAnimation>? Loading;
        public int Refs;
    }

    private readonly Dictionary<string, SharedAnim> _anims = new();

    private static string AnimKey(string path, int w, int h) => $"{path}|{w}x{h}";

    /// <summary>
    /// 창에 필요한 디코딩 크기. 화면에 보이는 크기를 64×1.25ⁿ 단계로 올림 → 크기 슬라이더를 끌 때마다 다시 디코딩하지 않음.
    /// 도트 그래픽은 원본 그대로 (확대할 때 픽셀이 선명해야 하므로).
    /// </summary>
    internal static (int w, int h) AnimTarget(int srcW, int srcH, double scale, bool pixelated)
    {
        if (srcW <= 0 || srcH <= 0) return (0, 0);
        if (pixelated || scale >= 1) return (srcW, srcH);
        int srcLong = Math.Max(srcW, srcH);
        double desired = srcLong * Math.Max(0.01, scale);
        double bucket = 64;
        while (bucket < desired) bucket *= 1.25;
        int longSide = (int)Math.Min(srcLong, Math.Ceiling(bucket));
        if (longSide >= srcLong) return (srcW, srcH);
        double k = (double)longSide / srcLong;
        return (Math.Max(1, (int)Math.Round(srcW * k)), Math.Max(1, (int)Math.Round(srcH * k)));
    }

    /// <summary>지금 디코딩된 것을 그대로 써도 되나: 같은 파일이고, 너무 작지(흐려짐) 않고 2배 넘게 크지(메모리 낭비) 않음.</summary>
    private static bool KeepCurrent(OverlayWindow w, string path, int tw)
    {
        if (w.AnimPath != path || w.Anim == null) return false;
        if (tw <= 0) return true; // 원본 크기를 모름 → 지금 것 유지
        return w.Anim.Width >= tw * 0.98 && w.Anim.Width <= tw * 2.0;
    }

    private void LoadFor(OverlayWindow w, string path, int tw, int th)
    {
        string key = AnimKey(path, tw, th);
        w.PendingKey = key;
        if (_anims.TryGetValue(key, out var e) && e.Anim != null) { Attach(w, key, path, e); return; }
        if (e == null)
        {
            e = new SharedAnim();
            _anims[key] = e;
        }
        e.Loading ??= Task.Run(() => GifAnimation.Load(path, tw, th, GifAnimation.DefaultBudget));
        string id = w.Item.Id;
        e.Loading.ContinueWith(t => Post(() => OnLoaded(key, path, id, t)));
    }

    private void OnLoaded(string key, string path, string windowId, Task<GifAnimation> t)
    {
        _byId.TryGetValue(windowId, out var w);
        bool current = w != null && w.PendingKey == key;
        _anims.TryGetValue(key, out var e);

        if (t.IsFaulted)
        {
            if (e != null && e.Refs == 0) _anims.Remove(key);
            if (!current) return; // 이미 다른 사진으로 바뀜 → 알릴 필요 없음
            w!.PendingKey = null;
            var err = t.Exception?.GetBaseException();
            Log.Error($"Failed to load {path}", err);
            LoadFailed?.Invoke(windowId, err?.Message ?? "unknown error");
            return;
        }

        if (e == null)
        {
            // 아무도 안 써서 이미 정리됨 → 기다리던 창이 있으면 다시 불러옴
            if (current) { w!.PendingKey = null; LoadFor(w, path, t.Result.Width, t.Result.Height); }
            return;
        }
        if (e.Anim == null)
        {
            e.Anim = t.Result;
            e.Loading = null;
            if (e.Anim.FrameStep > 1 || e.Anim.Width < e.Anim.SourceWidth)
                Log.Info($"Decoded {System.IO.Path.GetFileName(path)} at {e.Anim.Width}x{e.Anim.Height} (source {e.Anim.SourceWidth}x{e.Anim.SourceHeight}), " +
                         $"{e.Anim.Frames.Count} frames, step {e.Anim.FrameStep}, {e.Anim.ByteSize / (1024 * 1024)} MB");
        }
        if (current) Attach(w!, key, path, e);
        else if (e.Refs == 0 && !_byId.Values.Any(x => x.PendingKey == key))
        {
            e.Anim.Dispose();
            _anims.Remove(key);
        }
    }

    private void Attach(OverlayWindow w, string key, string path, SharedAnim e)
    {
        e.Refs++;
        string? old = w.AnimKey;
        w.PendingKey = null;
        w.SetAnimation(e.Anim!, path, key);
        if (old != null && old != key) ReleaseAnim(old);
    }

    private void ReleaseAnim(string key)
    {
        if (!_anims.TryGetValue(key, out var e)) return;
        if (--e.Refs > 0 || e.Loading != null) return;
        e.Anim?.Dispose();
        _anims.Remove(key);
    }

    /// <summary>점검용: 디코딩해 둔 GIF 수와 전체 메모리.</summary>
    public Task<(int count, long bytes)> AnimStatsAsync()
    {
        var tcs = new TaskCompletionSource<(int, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult((_anims.Count(kv => kv.Value.Anim != null), _anims.Values.Sum(v => v.Anim?.ByteSize ?? 0))));
        return tcs.Task;
    }

    public void Remove(string id) => Post(() =>
    {
        if (_byId.Remove(id, out var w))
        {
            _byHwnd.Remove(w.Hwnd);
            string? key = w.AnimKey;
            w.Dispose();
            if (key != null) ReleaseAnim(key);
        }
        if (_selectedId == id) _selectedId = null;
        UpdateHoverTimer();
    });

    public void SetEditMode(bool edit) => Post(() =>
    {
        _editMode = edit;
        foreach (var w in _byId.Values) w.SetEditMode(edit);
        UpdateHoverTimer();
    });

    // ── 단축키 ───────────────────────────────────────────────

    private readonly List<int> _registered = new();

    /// <summary>
    /// 전역 단축키를 다시 등록한다 (동작 이름 → "Ctrl+Alt+E"). 등록하지 못한 것(다른 프로그램이 사용 중)의 목록을 돌려준다.
    /// UI 스레드에서 불러도 됨 — 엔진 스레드가 처리할 때까지 잠깐 기다림.
    /// </summary>
    public IReadOnlyList<string> SetHotkeys(IReadOnlyDictionary<string, string> map)
    {
        var done = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            foreach (int id in _registered) UnregisterHotKey(_msgHwnd, id);
            _registered.Clear();
            var failed = new List<string>();
            foreach (var (action, text) in map)
            {
                int id = Hotkeys.IdOf(action);
                if (id == 0 || !Hotkeys.TryParse(text, out uint mods, out uint vk)) continue;
                if (RegisterHotKey(_msgHwnd, id, mods | MOD_NOREPEAT, vk)) _registered.Add(id);
                else failed.Add(text);
            }
            done.TrySetResult(failed);
        });
        var result = done.Task.Wait(3000) ? done.Task.Result : new List<string>();
        _hotkeyFailures.Clear();
        _hotkeyFailures.AddRange(result);
        return result;
    }

    // ── 화면 캡처에서 숨기기 ─────────────────────────────────

    private bool _hideFromCapture;

    /// <summary>켜면 화면 녹화·공유·캡처에 GIF가 찍히지 않음 (내 화면에는 그대로 보임).</summary>
    public void SetHideFromCapture(bool on) => Post(() =>
    {
        _hideFromCapture = on;
        foreach (var w in _byId.Values) w.SetCaptureExcluded(on);
    });

    // ── 마우스를 올리면 흐리게 ───────────────────────────────

    private bool _hoverFade;
    /// <summary>흐려졌을 때 남는 진하기 (0~1). 엔진 스레드에서 읽음.</summary>
    internal double HoverOpacity { get; private set; } = 0.25;
    private bool _hoverTimerOn;

    public void SetHoverFade(bool on, int opacityPercent) => Post(() =>
    {
        _hoverFade = on;
        HoverOpacity = Math.Clamp(opacityPercent, 0, 90) / 100.0;
        UpdateHoverTimer();
        if (!on) foreach (var w in _byId.Values) w.SetHover(false);
        else CheckHover();
    });

    /// <summary>켜져 있고 화면에 GIF가 있을 때만 마우스 위치를 확인 (40ms마다, 아주 가벼움).</summary>
    private void UpdateHoverTimer()
    {
        bool want = _hoverFade && !_editMode && _byId.Count > 0;
        if (want == _hoverTimerOn) return;
        _hoverTimerOn = want;
        if (want) SetTimer(_msgHwnd, TIMER_HOVER, HoverPollMs, IntPtr.Zero);
        else
        {
            KillTimer(_msgHwnd, TIMER_HOVER);
            foreach (var w in _byId.Values) w.SetHover(false);
        }
    }

    private void CheckHover()
    {
        if (!_hoverFade || _editMode) return;
        POINT pt;
        if (_hoverTestPoint is { } tp) pt = new POINT(tp.x, tp.y);
        else if (!GetCursorPos(out pt)) return;
        foreach (var w in _byId.Values) w.SetHover(w.HitsHover(pt.X, pt.Y));
    }

    private (int x, int y)? _hoverTestPoint;

    /// <summary>점검용: 실제 마우스 대신 (x, y)에 있는 것으로 침 (null = 실제 마우스).</summary>
    public void SimulateHover(int? x, int? y) => Post(() =>
    {
        _hoverTestPoint = x is int xx && y is int yy ? (xx, yy) : null;
        CheckHover();
    });

    // ── 모든 효과 끄기 ───────────────────────────────────────

    private bool _fxUserOff;

    public void SetEffectsOff(bool off) => Post(() =>
    {
        _fxUserOff = off;
        CheckFxPause(force: true);
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
        ApplyGlobalHidden();
    });

    private void ApplyGlobalHidden()
    {
        foreach (var w in _byId.Values) w.SetGlobalHidden(_allHidden || RuleHiddenBy != null);
    }

    // ── 프로그램별 숨기기 ────────────────────────────────────

    private readonly AppRules _rules = new();
    /// <summary>지금 이 프로그램 때문에 모두 숨긴 상태 (없으면 null).</summary>
    public string? RuleHiddenBy { get; private set; }
    /// <summary>프로그램별 숨기기 상태가 바뀜 (엔진 스레드).</summary>
    public event Action<string?>? RuleHiddenChanged;

    public void SetAppRules(IReadOnlyList<string> apps, bool whileRunning)
    {
        var copy = apps.ToList();
        Post(() =>
        {
            _rules.Configure(copy, whileRunning);
            CheckAppRules();
        });
    }

    private void CheckAppRules()
    {
        string? by;
        try { by = _rules.Check(RuleHiddenBy); }
        catch (Exception ex) { Log.Warn("Could not check program rules", ex); return; }
        if (by == RuleHiddenBy) return;
        Log.Info(by != null ? $"Program rule: hiding GIFs for {by}" : "Program rule ended: showing GIFs");
        RuleHiddenBy = by;
        ApplyGlobalHidden();
        RuleHiddenChanged?.Invoke(by);
    }

    /// <summary>위치를 강제로 옮김 (움직이는 GIF 포함).</summary>
    public void Relocate(string id, int x, int y) => Post(() =>
    {
        if (_byId.TryGetValue(id, out var w)) w.Relocate(x, y);
    });

    /// <summary>효과 성능 설정: 초당 프레임, 전체 화면·배터리일 때 멈춤.</summary>
    public void SetEffectsSettings(int fps, bool pauseFullscreen, bool pauseOnBattery, bool autoLight = true, int cpuPercent = 80) => Post(() =>
    {
        FxFps = fps >= 45 ? 60 : 30;
        _pauseFxFullscreen = pauseFullscreen;
        _pauseFxBattery = pauseOnBattery;
        _autoLight = autoLight;
        _busy.ThresholdPercent = Math.Clamp(cpuPercent, 30, 98);
        if (!autoLight) _busy.Reset();
        CheckFxPause(force: true);
    });

    // ── 절약 모드: 다른 프로그램이 CPU를 많이 쓰면(게임 등) 파티클을 끔 ──
    private readonly CpuSampler _cpu = new();
    private readonly BusyDetector _busy = new();
    private bool _autoLight = true;
    private bool? _lightOverride;
    /// <summary>파티클을 끈 절약 모드 중.</summary>
    public bool LightMode { get; private set; }
    /// <summary>마지막으로 잰 다른 프로그램의 CPU 사용률 (%).</summary>
    public double OtherCpuPercent { get; private set; }

    /// <summary>점검용: 절약 모드를 강제로 켜고 끔 (null = 자동).</summary>
    public void ForceLightMode(bool? on) => Post(() => { _lightOverride = on; CheckFxPause(force: true); });

    /// <summary>전체 화면 게임·발표 중이거나 배터리 사용 중이면 효과를 멈춘다 (1.5초마다 확인).</summary>
    private void CheckFxPause(bool force = false)
    {
        bool paused = _fxUserOff;
        try
        {
            if (!paused && _pauseFxFullscreen && SHQueryUserNotificationState(out int state) == 0)
                paused = state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
            if (!paused && _pauseFxBattery && GetSystemPowerStatus(out var ps))
                paused = ps.ACLineStatus == 0;
        }
        catch (Exception ex) { Log.Warn("Could not check full-screen / power state", ex); }

        bool light = false;
        try
        {
            var cpu = _cpu.SampleOthersPercent();
            if (cpu is double c) OtherCpuPercent = c;
            light = _lightOverride ?? (_autoLight && cpu is double v ? _busy.Feed(v) : _autoLight && _busy.Busy);
        }
        catch (Exception ex) { Log.Warn("Could not read CPU usage", ex); }

        if (paused == FxPaused && light == LightMode && !force) return;
        if (paused != FxPaused) Log.Info(paused ? (_fxUserOff ? "Effects turned off" : "Effects paused (full screen or battery)") : "Effects resumed");
        if (light != LightMode) Log.Info(light ? $"Busy PC (other apps {OtherCpuPercent:0}% CPU): particles off" : "PC no longer busy: particles back on");
        FxPaused = paused;
        LightMode = light;
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
    internal void RaiseMoved(ActiveItem item) => Moved?.Invoke(item.Clone());
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
    public long GifFrames { get; init; }
    public int AnimWidth { get; init; }
    public string? AnimKey { get; init; }
    public int PushedArea { get; init; }
    public int GlowBuilds { get; init; }
    public int GlowHits { get; init; }
    public bool LightMode { get; init; }
    public bool CaptureExcluded { get; init; }
    public double FadeOpacity { get; init; }
    public bool FxPaused { get; init; }
    public bool Shown { get; init; }
    public bool GifPlaying { get; init; }
}
