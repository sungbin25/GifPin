using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_TIMER:
                    BringAllToTop();
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
    public void Sync(ActiveItem item, string filePath, bool pixelated = false)
    {
        var snapshot = item.Clone();
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
        Post(() => PostQuitMessage(0));
        _thread.Join(2000);
        _thread = null;
    }
}

/// <summary>오버레이 상태 (점검용).</summary>
public sealed record OverlayProbe(ActiveItem Item, bool MotionRunning, bool Held, bool Pressed, bool Sliding, int Jumps);
