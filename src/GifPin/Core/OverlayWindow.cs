using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;
using static GifPin.Core.Native;

namespace GifPin.Core;

/// <summary>
/// 화면에 떠 있는 GIF 하나 = 레이어드 윈도우 하나. OverlayEngine 스레드에서만 다룬다.
///  - 표시 모드: 클릭 통과(설정에 따라), 테두리 없음
///  - 편집 모드: 점선 테두리 + 옅은 배경, 선택된 것은 실선 + 모서리 핸들 4개.
///    안쪽 드래그 = 이동, 모서리 드래그 = 비율 고정 크기 조절, 휠 = ±10%
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    public const string ClassName = "GifPinOverlay";
    private const int EditPad = 10;      // 편집 모드에서 핸들이 들어갈 여백
    private const int HandleSize = 12;
    private const int HandleHit = 18;
    private const int SnapPx = 8;
    private const int KeepVisiblePx = 20;
    private const nuint TIMER_FLASH = 2, TIMER_HOLD = 4, TIMER_RESUME = 5;
    private const double ThrowWindowSec = 0.1; // 놓기 직전 이 시간 동안의 마우스 속도로 던짐
    private const long MotionReportMs = 3000; // 움직이는 중 위치를 UI에 알리는 간격
    private const double MinScale = 0.1, MaxScale = 4.0;

    private enum Drag { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }
    /// <summary>편집 모드가 아닐 때의 마우스: 누르고 기다리는 중(움직이는 GIF) / 잡고 끄는 중.</summary>
    private enum Press { None, Waiting, Grab }

    private readonly OverlayEngine _engine;
    public IntPtr Hwnd { get; }
    public ActiveItem Item { get; private set; }
    public string? AnimPath { get; private set; }
    /// <summary>공유 GIF 키 (파일|크기). 해제는 엔진이 참조 수로 관리 — 창이 직접 Dispose하지 않음.</summary>
    public string? AnimKey { get; private set; }
    /// <summary>불러오는 중인 GIF 키.</summary>
    public string? PendingKey { get; set; }
    internal GifAnimation? Anim => _anim;

    private GifAnimation? _anim;
    private int _frame;
    private bool _editMode, _selected, _globalHidden, _flash, _showSize;

    private readonly MotionState _motion = new();
    private double _reportAcc; // 움직이는 중 위치를 알린 뒤 지난 시간 (초)
    private bool _motionRunning, _facingLeft;

    // 효과 레이어
    private readonly EffectState _fx = new();
    private bool _fxRunning;

    // 프레임 (엔진의 공용 박자에 맞춰 진행)
    private long _lastFrameTs;        // 이 창이 마지막으로 처리한 프레임 시각 (Stopwatch 틱, 0 = 멈춤)
    private bool _dirty;              // 프레임 사이에 GIF 장면이 바뀜 → 다음 프레임에 그림
    private double _subX, _subY;      // 움직임 위치의 소수점 부분 (효과가 있을 때 GIF를 소수점 단위로 그려 덜컥임 제거)
    private bool _pixelated;
    private int _frameCount;
    private long _fpsWindowStart;
    /// <summary>최근 1초 동안 처리한 프레임 수 (점검·진단용).</summary>
    public int FramesPerSecond { get; private set; }

    // GIF 재생: Windows 타이머(15.6ms 단위) 대신 공용 프레임 박자와 정확한 시각으로 장면을 넘김
    private bool _gifPlaying;
    private long _gifDue; // 다음 장면으로 넘어갈 시각 (Stopwatch 틱)
    /// <summary>프레임 처리 중 난 오류 수 (로그를 한 번만 남기려고).</summary>
    internal int FrameErrors;
    /// <summary>지금까지 넘긴 GIF 장면 수 (점검용).</summary>
    public long GifFramesAdvanced { get; private set; }
    /// <summary>GIF 장면이 바뀌어 다시 그린 횟수 (점검용: 초당 그리기 제한 확인).</summary>
    public long GifDraws { get; private set; }

    /// <summary>매 프레임 계산이 필요함 (움직이는 중, 효과가 도는 중, 또는 GIF 재생 중).</summary>
    internal bool NeedsFrames => _motionRunning || _fxRunning || _gifPlaying || _fading;

    // 마우스를 올리면 흐리게: 지금 진하기(0~1)와 목표. 바뀌는 동안만 프레임을 받음
    private double _fadeNow = 1, _fadeTarget = 1;
    private bool _fading, _hovered;
    private const double FadeSeconds = 0.15;
    private const int HoverMargin = 8;
    private bool _captureExcluded;
    private Pads _pads = Pads.Zero; // 지금 창 가장자리 여백 (창 위치 = GIF 위치 − 여백)
    private List<GifAnimation>? _sprites; // 내 이미지 파티클 (이 창이 주인)

    /// <summary>지금 불러온(또는 불러오는 중인) 파티클 이미지 목록 — 바뀌었는지 비교용.</summary>
    public string ParticleKey { get; set; } = "";

    internal int ParticleCount => _fx.ParticleCount;
    internal int ParticleBudget { set => _fx.ParticleBudget = value; }

    // ── 멈춤(가려짐·잠금) / 메모리에서 내리기 ─────────────────
    private bool _occluded;
    /// <summary>다른 창에 완전히 가려짐 (바탕화면 층만).</summary>
    internal bool Occluded => _occluded;
    /// <summary>보이지만 재생·효과·움직임을 멈춰야 함 (가려짐, 화면 잠금 등).</summary>
    private bool Suspended => _occluded || _engine.SystemPaused;
    /// <summary>화면에 보이고 멈출 이유도 없음 → 재생·효과·움직임을 돌림.</summary>
    private bool IsActive => IsShown && !Suspended;

    internal void SetOccluded(bool occluded)
    {
        if (_occluded == occluded) return;
        _occluded = occluded;
        ApplySuspend();
    }

    /// <summary>멈춤 상태가 바뀌면 재생·효과·움직임을 다시 맞춤 (엔진이 잠금 상태가 바뀔 때도 부름).</summary>
    internal void ApplySuspend()
    {
        UpdateMotionTimer();
        UpdateFxTimer();
        UpdateGifPlayback();
        if (!Suspended) Render();
    }

    /// <summary>보여야 하는 상태 (GIF를 불러왔는지와 상관없이).</summary>
    internal bool WantsShown => _editMode || (Item.Visible && !_globalHidden);
    /// <summary>숨겨진 시각 (Stopwatch, 0 = 보이는 중) — 오래 숨겨지면 메모리에서 내림.</summary>
    internal long HiddenSince { get; set; }
    /// <summary>메모리에서 내린 GIF (다시 보일 때 이 파일·크기로 불러옴).</summary>
    internal string? UnloadedPath { get; set; }
    internal (int w, int h) UnloadedTarget { get; set; }

    /// <summary>디코딩한 GIF를 놓음 (엔진이 참조 수를 줄임). 돌려주는 값: 놓은 키.</summary>
    internal string? Unload(int tw, int th)
    {
        if (_anim == null || AnimKey == null) return null;
        string key = AnimKey;
        UnloadedPath = AnimPath;
        UnloadedTarget = (tw, th);
        _anim = null;
        AnimKey = null;
        _gifPlaying = false;
        _fxRunning = false;
        _motionRunning = false;
        _lastFrameTs = 0;
        FreeSurface(); // 창 그림 버퍼도 놓음
        ShowWindow(Hwnd, SW_HIDE);
        return key;
    }

    private long _lastGifDraw; // GIF 장면을 마지막으로 그린 시각 (초당 그리기 제한용)
    private bool _gifDrawPending;

    public void SetParticleSprites(List<GifAnimation>? sprites)
    {
        _fx.SetSprites(sprites);
        if (_sprites != null) foreach (var s in _sprites) s.Dispose();
        _sprites = sprites;
        UpdateFxTimer();
    }

    private Press _press;
    private bool _held;              // 손으로 잡아서 움직임을 멈춤 (놓은 뒤 다시 출발하기 전까지 포함)
    private int _grabDx, _grabDy;    // 잡은 지점 (GIF 안쪽 좌표)
    private int _ptX, _ptY;          // 마지막 마우스 위치 (화면 좌표)
    private readonly System.Collections.Generic.List<(long t, int x, int y)> _samples = new();

    private Drag _drag;
    private POINT _dragStart;
    private int _sx, _sy;
    private double _sScale;

    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private int _dibW, _dibH;
    private SKSurface? _surface;

    private static readonly SKColor Accent = new(76, 141, 255);
    private readonly SKPaint _framePaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    private readonly SKPaint _editFill = new() { Color = new SKColor(76, 141, 255, 30) };
    private readonly SKPaint _dashed = new()
    {
        Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(143, 182, 255, 210), IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0),
    };
    private readonly SKPaint _solid = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Accent, IsAntialias = true };
    private readonly SKPaint _handleFill = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _bubbleBg = new() { Color = new SKColor(12, 14, 18, 230), IsAntialias = true };
    private readonly SKPaint _bubbleText = new()
    {
        Color = SKColors.White, IsAntialias = true, TextSize = 12,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
    };

    public OverlayWindow(OverlayEngine engine, ActiveItem item)
    {
        _engine = engine;
        Item = item;
        long ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (Layers.Normalize(item.Layer) == Layers.Top) ex |= WS_EX_TOPMOST;
        if (item.ClickThrough) ex |= WS_EX_TRANSPARENT;
        Hwnd = CreateWindowExW((uint)ex, ClassName, "GifPin overlay", WS_POPUP,
            item.X, item.Y, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx 실패 (오류 {Marshal.GetLastWin32Error()})");
        // 저장된 움직임(걷기 등)으로 바로 시작. 움직임 계산은 "모드가 바뀔 때" 초기화되는데,
        // 처음 만들 때는 바뀐 게 없어서 초기화되지 않아 걷기로 표시돼도 움직이지 않던 문제
        ResetMotion();
    }

    // ── 엔진에서 호출 ─────────────────────────────────────────

    public void SetAnimation(GifAnimation anim, string path, string key)
    {
        UnloadedPath = null;
        bool samePath = AnimPath == path && _anim != null && _anim.Frames.Count == anim.Frames.Count;
        _anim = anim;
        AnimPath = path;
        AnimKey = key;
        if (!samePath) _frame = 0; // 같은 GIF를 다른 크기로 다시 불러온 경우엔 재생 위치 유지
        _frame = Math.Min(_frame, anim.Frames.Count - 1);
        _gifPlaying = false; // 재생 시각을 새로 맞춤
        Refresh();
    }

    public void Update(ActiveItem item)
    {
        var prev = Item;
        bool prevMoving = Motion.IsMoving(prev.MotionMode);
        // 드래그 중이거나 움직이는 중에는 위치를 엔진 쪽 값으로 유지 (위치를 강제로 바꿀 때는 Relocate)
        if (_drag != Drag.None || _press == Press.Grab || prevMoving)
        {
            item.X = prev.X;
            item.Y = prev.Y;
        }
        if (_drag != Drag.None) item.Scale = prev.Scale;
        Item = item;
        ApplyStyles();
        if (prev.MotionMode != item.MotionMode || Math.Abs(prev.MotionSpeed - item.MotionSpeed) > 0.01)
        {
            ResetMotion();
            if (_press != Press.Grab) CancelHold();
        }
        else _motion.Configure(MotionParams.From(item));
        UpdateFacing();
        // 보이기·층이 바뀐 경우만 창 전체를 다시 설정. 그 밖에(슬라이더 등)는 그림만 다시 → 슬라이더를 끌어도 GIF가 멈추지 않음
        if (prev.Visible != item.Visible || Layers.Normalize(prev.Layer) != Layers.Normalize(item.Layer)) Refresh();
        else
        {
            UpdateMotionTimer();
            UpdateFxTimer();
            UpdateGifPlayback();
            if (NeedsFrames) _dirty = true; // 다음 프레임에 함께 그림
            else Render();
        }
        if (prevMoving && !Motion.IsMoving(item.MotionMode)) _engine.RaiseChanged(Item); // 멈춘 위치 저장
    }

    /// <summary>위치를 강제로 옮김 (모니터 이동, 9분할 배치, 복구 도구). 움직이는 중이면 그 자리에서 다시 출발.</summary>
    public void Relocate(int x, int y)
    {
        Item.X = x;
        Item.Y = y;
        ResetMotion();
        if (_press != Press.Grab) CancelHold();
        Render();
        UpdateMotionTimer();
        _engine.RaiseChanged(Item);
    }

    /// <summary>화면 녹화·공유·캡처에서 이 창을 뺌 (Windows 10 2004 이상).</summary>
    public void SetCaptureExcluded(bool on)
    {
        if (_captureExcluded == on) return;
        _captureExcluded = on;
        if (!SetWindowDisplayAffinity(Hwnd, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE) && on)
            Log.Warn($"Could not hide overlay from screen capture (error {Marshal.GetLastWin32Error()}); needs Windows 10 2004 or later");
    }

    internal bool CaptureExcludedNow => GetWindowDisplayAffinity(Hwnd, out uint a) && a == WDA_EXCLUDEFROMCAPTURE;

    /// <summary>마우스가 (x, y)에 있을 때 흐려져야 하나: 클릭 통과 GIF의 그림 영역(+여백) 안.</summary>
    internal bool HitsHover(int x, int y)
    {
        if (!IsShown || _editMode || !Item.ClickThrough || _anim == null) return false;
        var (w, h) = ContentSize(Item.Scale);
        return x >= Item.X - HoverMargin && x < Item.X + w + HoverMargin && y >= Item.Y - HoverMargin && y < Item.Y + h + HoverMargin;
    }

    internal void SetHover(bool hovered)
    {
        if (_hovered == hovered && Math.Abs(_fadeTarget - (hovered ? _engine.HoverOpacity : 1)) < 0.001) return;
        _hovered = hovered;
        _fadeTarget = hovered ? _engine.HoverOpacity : 1;
        if (Math.Abs(_fadeNow - _fadeTarget) < 0.001) return;
        if (!IsShown) { _fadeNow = _fadeTarget; return; }
        if (!_fading)
        {
            _fading = true;
            SyncFrameClock();
        }
    }

    /// <summary>진하기를 목표 쪽으로 조금 옮김. 다 왔으면 false.</summary>
    private bool StepFade(double dt)
    {
        if (!_fading) return false;
        double step = dt <= 0 ? 1.0 / 60 / FadeSeconds : dt / FadeSeconds;
        _fadeNow = _fadeNow < _fadeTarget ? Math.Min(_fadeTarget, _fadeNow + step) : Math.Max(_fadeTarget, _fadeNow - step);
        if (Math.Abs(_fadeNow - _fadeTarget) < 0.001)
        {
            _fadeNow = _fadeTarget;
            _fading = false;
        }
        return true;
    }

    public void SetPixelated(bool pixelated)
    {
        _pixelated = pixelated;
        ApplyQuality();
    }

    /// <summary>확대·축소 품질: 도트 그래픽은 항상 그대로, 나머지는 성능 설정(빠르게/보통/선명하게)을 따름.</summary>
    internal void ApplyQuality()
    {
        var q = _pixelated ? SKFilterQuality.None : _engine.FilterQuality;
        if (_framePaint.FilterQuality == q && _framePaint.IsAntialias == !_pixelated) return;
        _framePaint.FilterQuality = q;
        _framePaint.IsAntialias = !_pixelated;
        _glowPaint.FilterQuality = _pixelated ? SKFilterQuality.Medium : q;
        Render();
    }

    private void ResetMotion()
    {
        _motion.Reset(Item.MotionMode, MotionParams.From(Item), Item.X, Item.Y);
        UpdateFacing();
    }

    private bool UpdateFacing()
    {
        bool facing = Item.MotionFaceDirection && Motion.IsMoving(Item.MotionMode) && _motion.FacingLeft;
        if (facing == _facingLeft) return false;
        _facingLeft = facing;
        return true;
    }

    /// <summary>잡기·재출발 대기를 모두 취소 (움직임은 다시 돌 수 있게).</summary>
    private void CancelHold()
    {
        KillTimer(Hwnd, TIMER_HOLD);
        KillTimer(Hwnd, TIMER_RESUME);
        _held = false;
    }

    private bool MotionShouldRun =>
        Motion.IsMoving(Item.MotionMode) && !_editMode && _drag == Drag.None && _press != Press.Grab && !_held && IsActive
        && (Motion.IsAutonomous(Item.MotionMode) || _motion.IsSliding || _motion.AutoPush);

    /// <summary>눈에 보이게 움직이는 중 (이때만 길게 눌러야 잡힘). 자동 밀기를 기다리며 멈춰 있는 GIF는 바로 잡힌다.</summary>
    private bool VisiblyMoving => _motionRunning && (Motion.IsAutonomous(Item.MotionMode) || _motion.IsSliding);

    private void UpdateMotionTimer()
    {
        bool run = MotionShouldRun;
        if (run == _motionRunning) return;
        _motionRunning = run;
        if (run)
        {
            // 움직임 계산이 지금 모드로 준비돼 있지 않으면 여기서 준비 (어떤 순서로 불려도 안전하게)
            if (_motion.Mode != Motion.Normalize(Item.MotionMode)) ResetMotion();
            _motion.X = Item.X; // 편집·숨김 뒤 다시 출발할 때 현재 위치에서
            _motion.Y = Item.Y;
            _reportAcc = 0;
        }
        else _subX = _subY = 0;
        UpdateFxTimer();
        SyncFrameClock();
    }

    /// <summary>프레임이 필요하면 엔진 박자에 등록, 아니면 멈춤.</summary>
    private void SyncFrameClock()
    {
        if (NeedsFrames)
        {
            if (_lastFrameTs == 0) _lastFrameTs = System.Diagnostics.Stopwatch.GetTimestamp();
            _engine.WantFrames();
        }
        else
        {
            _lastFrameTs = 0;
            FramesPerSecond = 0;
        }
    }

    /// <summary>
    /// 한 프레임: 움직임 계산 → 효과 계산 → 그리기 (한 번).
    /// 위치와 그림이 늘 같은 박자로 바뀌므로 GIF와 파티클이 서로 어긋나 흔들리지 않는다.
    /// </summary>
    internal void OnFrame(long now)
    {
        if (!NeedsFrames || _anim == null) { _lastFrameTs = 0; _fading = false; return; }
        double dt = _lastFrameTs == 0 ? 0 : (now - _lastFrameTs) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastFrameTs = now;
        CountFrame(now);

        bool render = _dirty, moved = false;
        _dirty = false;
        // GIF 장면: 초당 그리기 제한이 있으면 그 간격이 지났을 때만 (장면 시간은 그대로 흘러 속도는 같음)
        if (AdvanceGif(now)) _gifDrawPending = true;
        if (_gifDrawPending)
        {
            long cap = _engine.GifFpsCap;
            if (cap <= 0 || now - _lastGifDraw >= System.Diagnostics.Stopwatch.Frequency / cap * 0.95) render = true;
        }
        if (StepFade(dt)) render = true;
        var (w, h) = ContentSize(Item.Scale);

        if (_motionRunning)
        {
            _reportAcc += dt;
            var work = WorkAreaAt(Item.X + w / 2, Item.Y + h / 2);
            bool wasSliding = _motion.IsSliding;
            _motion.Step(dt, w, h, work.Left, work.Top, work.Right, work.Bottom);
            int nx = (int)Math.Round(_motion.X), ny = (int)Math.Round(_motion.Y);
            // 도트 그래픽은 픽셀이 번지지 않게 정수 위치 그대로
            _subX = _pixelated ? 0 : _motion.X - nx;
            _subY = _pixelated ? 0 : _motion.Y - ny;
            if (UpdateFacing()) render = true;
            if (nx != Item.X || ny != Item.Y) { Item.X = nx; Item.Y = ny; moved = true; }

            if (!MotionShouldRun)
            {
                UpdateMotionTimer(); // 미끄러짐이 멈춤 → 멈춘 자리 저장
                ReportPosition();
                render = true;
            }
            else if (wasSliding && !_motion.IsSliding) ReportPosition(); // 자동 밀기 사이에 멈춤
            else if (_reportAcc * 1000 >= MotionReportMs)
            {
                _reportAcc = 0;
                ReportPosition();
            }
        }

        if (_fxRunning)
        {
            _fx.Advance(dt, Item, Item.X + _subX, Item.Y + _subY, w, h, Motion.IsMoving(Item.MotionMode) ? _motion : null);
            render = true;
        }

        if (render)
        {
            if (_gifDrawPending) GifDraws++;
            Render();
            _lastGifDraw = now;
            _gifDrawPending = false;
        }
        else if (moved) SetWindowPos(Hwnd, IntPtr.Zero, Item.X - _pads.Left, Item.Y - _pads.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        if (_fxRunning) UpdateFxTimer(); // 파티클이 다 사라졌으면 멈춤
    }

    private void CountFrame(long now)
    {
        if (_fpsWindowStart == 0) _fpsWindowStart = now;
        _frameCount++;
        double span = (now - _fpsWindowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (span >= 1)
        {
            FramesPerSecond = (int)Math.Round(_frameCount / span);
            _frameCount = 0;
            _fpsWindowStart = now;
        }
    }

    private int _reportedX = int.MinValue, _reportedY = int.MinValue;

    /// <summary>움직이는 중 위치를 UI에 알림 (제자리면 생략 → 쓸데없는 저장 방지).</summary>
    private void ReportPosition()
    {
        if (Item.X == _reportedX && Item.Y == _reportedY) return;
        _reportedX = Item.X;
        _reportedY = Item.Y;
        _engine.RaiseMoved(Item); // 저장은 느긋하게 (자주 바뀌므로)
    }

    public void SetEditMode(bool edit)
    {
        if (_editMode == edit) return;
        if (!edit && _drag != Drag.None) EndDrag(notify: true);
        if (_press != Press.None) EndPress(notify: true, throwIt: false);
        CancelHold();
        _editMode = edit;
        ApplyStyles();
        Refresh();
        if (!edit) ResetMotion(); // 편집에서 옮긴 자리에서 다시 출발
        UpdateMotionTimer();
    }

    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        Render();
    }

    public void SetGlobalHidden(bool hidden)
    {
        _globalHidden = hidden;
        Refresh();
        UpdateMotionTimer();
    }

    public void Flash()
    {
        _flash = true;
        Render();
        SetTimer(Hwnd, TIMER_FLASH, 1000, IntPtr.Zero);
    }

    public void Nudge(int dx, int dy)
    {
        Item.X += dx;
        Item.Y += dy;
        Render();
        _engine.RaiseChanged(Item);
    }

    /// <summary>바탕화면 층: 편집 중에는 다루기 쉽게 잠시 맨 위로 올린다.</summary>
    private bool OnDesktopLayer => Layers.Normalize(Item.Layer) == Layers.Desktop && !_editMode;

    /// <summary>층에 맞게 z-순서를 다시 맞춤: 맨 위 층은 항상 위, 바탕화면 층은 바탕화면 바로 위(다른 프로그램 아래).</summary>
    public void BringToTop()
    {
        if (!IsShown) return;
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (!OnDesktopLayer)
        {
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            return;
        }

        if ((GetWindowLongPtrW(Hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0)
            SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        IntPtr desktop = _engine.DesktopHost();
        if (desktop == IntPtr.Zero)
        {
            SetWindowPos(Hwnd, HWND_BOTTOM, 0, 0, 0, 0, flags); // 탐색기가 없으면 그냥 맨 아래
            return;
        }
        // 이미 바탕화면 바로 위(사이에 GifPin 창만 있음)면 그대로
        for (IntPtr n = GetWindow(Hwnd, GW_HWNDNEXT); n != IntPtr.Zero; n = GetWindow(n, GW_HWNDNEXT))
        {
            if (n == desktop) return;
            if (!_engine.IsOverlay(n)) break;
        }
        // 바탕화면 바로 위에 있는 (GifPin이 아닌) 창 아래로 넣는다 = 바탕화면 바로 위
        IntPtr above = GetWindow(desktop, GW_HWNDPREV);
        while (above != IntPtr.Zero && _engine.IsOverlay(above)) above = GetWindow(above, GW_HWNDPREV);
        SetWindowPos(Hwnd, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0, flags);
    }

    /// <summary>
    /// 바탕화면 층 GIF가 위에 있는 다른 창들로 완전히 덮였나 (최대화한 창, 나란히 놓은 창 여러 개 등).
    /// 반투명일 수 있는 창(레이어드·클릭 통과)과 숨겨진 창은 가린다고 보지 않음.
    /// </summary>
    internal bool IsCoveredByOtherWindows()
    {
        if (!OnDesktopLayer || _anim == null || !WantsShown) return false;
        var (w, h) = ContentSize(Item.Scale);
        IntPtr rgn = CreateRectRgn(Item.X, Item.Y, Item.X + w, Item.Y + h);
        try
        {
            int guard = 0;
            for (IntPtr n = GetWindow(Hwnd, GW_HWNDPREV); n != IntPtr.Zero && guard++ < 600; n = GetWindow(n, GW_HWNDPREV))
            {
                if (!IsWindowVisible(n) || IsIconic(n) || _engine.IsOverlay(n)) continue;
                long ex = GetWindowLongPtrW(n, GWL_EXSTYLE).ToInt64();
                if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) != 0) continue;
                if (DwmGetWindowAttributeInt(n, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                if (DwmGetWindowAttribute(n, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, sizeof(RECT)) != 0 && !GetWindowRect(n, out r)) continue;
                if (r.Right <= r.Left || r.Bottom <= r.Top) continue;
                IntPtr wr = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                int result = CombineRgn(rgn, rgn, wr, RGN_DIFF);
                DeleteObject(wr);
                if (result == NULLREGION) return true;
                if (result == ERROR_REGION) return false;
            }
            return false;
        }
        finally { DeleteObject(rgn); }
    }

    internal bool IsTopmost => (GetWindowLongPtrW(Hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    private bool IsShown => _anim != null && (_editMode || (Item.Visible && !_globalHidden));

    /// <summary>보이기/숨기기, 재생 타이머, 그리기를 현재 상태에 맞춘다.</summary>
    private void Refresh()
    {
        if (IsShown)
        {
            Render();
            ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
            BringToTop();
        }
        else ShowWindow(Hwnd, SW_HIDE);
        UpdateMotionTimer();
        UpdateFxTimer();
        UpdateGifPlayback();
    }

    // ── 효과 타이머 ─────────────────────────────────────────

    /// <summary>효과가 움직여야 할 때만 프레임을 받음 (편집 중·숨김·일시정지 중에는 멈춤).</summary>
    internal void UpdateFxTimer()
    {
        bool motionActive = Motion.IsMoving(Item.MotionMode) && (_motionRunning || _held || _press == Press.Grab);
        _fx.SuppressParticles = _engine.LightMode;
        bool run = !_editMode && IsActive && !_engine.FxPaused && _fx.IsAnimated(Item, motionActive);
        if (run == _fxRunning) return;
        _fxRunning = run;
        if (!run) Render(); // 멈춘 모습으로 한 번 그림
        SyncFrameClock();
    }

    /// <summary>보이는 움직이는 GIF면 프레임 박자에 맞춰 재생.</summary>
    private void UpdateGifPlayback()
    {
        bool play = (IsActive || (_editMode && IsShown)) && _anim != null && _anim.Frames.Count > 1;
        if (play == _gifPlaying) return;
        _gifPlaying = play;
        if (play) _gifDue = System.Diagnostics.Stopwatch.GetTimestamp() + DelayTicks(_frame);
        SyncFrameClock();
    }

    private long DelayTicks(int frame)
    {
        double speed = Item.Speed <= 0 ? 1 : Item.Speed;
        double ms = Math.Max(10, _anim!.DelaysMs[Math.Min(frame, _anim.DelaysMs.Count - 1)] / speed);
        return (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
    }

    /// <summary>정해진 시각이 지났으면 다음 장면으로 (늦었으면 따라잡되, 1초 넘게 밀렸으면 지금부터 다시).</summary>
    private bool AdvanceGif(long now)
    {
        if (!_gifPlaying || _anim == null || _anim.Frames.Count < 2) return false;
        if (now - _gifDue > System.Diagnostics.Stopwatch.Frequency) _gifDue = now;
        bool changed = false;
        int guard = _anim.Frames.Count;
        while (now >= _gifDue && guard-- > 0)
        {
            _frame = (_frame + 1) % _anim.Frames.Count;
            _gifDue += DelayTicks(_frame);
            GifFramesAdvanced++;
            changed = true;
        }
        return changed;
    }

    private void ApplyStyles()
    {
        long ex = GetWindowLongPtrW(Hwnd, GWL_EXSTYLE).ToInt64();
        bool through = !_editMode && Item.ClickThrough;
        long want = through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex)
        {
            SetWindowLongPtrW(Hwnd, GWL_EXSTYLE, new IntPtr(want));
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
        }
    }

    // ── 메시지 처리 ──────────────────────────────────────────

    public IntPtr? WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if ((nuint)wParam == TIMER_HOLD)
                {
                    KillTimer(Hwnd, TIMER_HOLD);
                    if (_press == Press.Waiting) BeginGrab(_ptX, _ptY, System.Diagnostics.Stopwatch.GetTimestamp());
                }
                else if ((nuint)wParam == TIMER_RESUME)
                {
                    KillTimer(Hwnd, TIMER_RESUME);
                    if (_press == Press.None) Resume();
                }
                else if ((nuint)wParam == TIMER_FLASH)
                {
                    KillTimer(Hwnd, TIMER_FLASH);
                    _flash = false;
                    Render();
                }
                return IntPtr.Zero;

            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);

            case WM_SETCURSOR:
                if (!_editMode) break;
                GetCursorPos(out var cp);
                ScreenToClient(Hwnd, ref cp);
                var hit = _drag != Drag.None ? _drag : HitTest(cp.X, cp.Y);
                int cursor = hit is Drag.TopLeft or Drag.BottomRight ? IDC_SIZENWSE
                           : hit is Drag.TopRight or Drag.BottomLeft ? IDC_SIZENESW
                           : IDC_SIZEALL;
                SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(cursor)));
                return new IntPtr(1);

            case WM_LBUTTONDOWN when !_editMode:
                if (_anim == null) break;
                {
                    GetCursorPos(out var pd);
                    PointerDown(pd.X, pd.Y, System.Diagnostics.Stopwatch.GetTimestamp());
                }
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                {
                    int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
                    var h = HitTest(x, y);
                    _engine.RaiseClicked(Item.Id); // 선택 (선택 전이면 이동만)
                    if (!_selected && h != Drag.Move) h = Drag.Move;
                    _drag = h;
                    GetCursorPos(out _dragStart);
                    _sx = Item.X;
                    _sy = Item.Y;
                    _sScale = Item.Scale;
                    _showSize = h != Drag.Move;
                    SetCapture(Hwnd);
                    Render();
                }
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (_press != Press.None)
                {
                    GetCursorPos(out var pm);
                    PointerMove(pm.X, pm.Y, System.Diagnostics.Stopwatch.GetTimestamp());
                    return IntPtr.Zero;
                }
                if (_drag == Drag.None) break;
                OnDrag();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_press != Press.None)
                {
                    GetCursorPos(out var pu);
                    PointerUp(pu.X, pu.Y, System.Diagnostics.Stopwatch.GetTimestamp());
                    return IntPtr.Zero;
                }
                if (_drag == Drag.None) break;
                ReleaseCapture(); // → WM_CAPTURECHANGED에서 마무리
                if (_drag != Drag.None) EndDrag(notify: true);
                return IntPtr.Zero;

            case WM_CAPTURECHANGED:
                if (_drag != Drag.None) EndDrag(notify: true);
                if (_press != Press.None) EndPress(notify: true, throwIt: false); // 다른 창이 마우스를 가져감
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                if (!_editMode || _anim == null) break;
                {
                    short delta = (short)(((long)wParam >> 16) & 0xFFFF);
                    var (w0, h0) = ContentSize(Item.Scale);
                    double cx = Item.X + w0 / 2.0, cy = Item.Y + h0 / 2.0;
                    double s = Math.Round(Math.Clamp(Item.Scale * (delta > 0 ? 1.1 : 1 / 1.1), MinScale, MaxScale), 3);
                    var (w1, h1) = ContentSize(s);
                    Item.Scale = s;
                    Item.X = (int)Math.Round(cx - w1 / 2.0);
                    Item.Y = (int)Math.Round(cy - h1 / 2.0);
                    _engine.RaiseClicked(Item.Id);
                    Render();
                    _engine.RaiseChanged(Item);
                }
                return IntPtr.Zero;
        }
        return null;
    }

    // ── 표시 모드 마우스: 끌어서 옮기기 / 움직이는 GIF 잡기 / 던지기 ─────────

    /// <summary>클릭 통과가 꺼진 GIF를 눌렀을 때. 움직이는 중이면 일정 시간 눌러야 잡힌다.</summary>
    internal void PointerDown(int x, int y, long ts)
    {
        if (_editMode || _anim == null || _press != Press.None) return;
        _ptX = x;
        _ptY = y;
        if (Item.FxParticles && Item.FxParticleMode == Fx.Click && !_engine.FxPaused && !_engine.LightMode)
        {
            var (bw, bh) = ContentSize(Item.Scale);
            _fx.Burst(Item, Item.X, Item.Y, bw, bh);
            UpdateFxTimer();
        }
        SetCapture(Hwnd);
        KillTimer(Hwnd, TIMER_RESUME); // 다시 출발하기 전에 또 잡음
        int hold = _engine.HoldToGrabMs;
        if (VisiblyMoving && hold > 0)
        {
            _press = Press.Waiting;
            SetTimer(Hwnd, TIMER_HOLD, (uint)hold, IntPtr.Zero);
        }
        else BeginGrab(x, y, ts);
    }

    internal void PointerMove(int x, int y, long ts)
    {
        _ptX = x;
        _ptY = y;
        if (_press != Press.Grab) return;
        AddSample(ts, x, y);
        MoveTo(x - _grabDx, y - _grabDy);
        _engine.RaiseLive(Item);
    }

    internal void PointerUp(int x, int y, long ts)
    {
        if (_press == Press.None) return;
        _ptX = x;
        _ptY = y;
        if (_press == Press.Grab)
        {
            AddSample(ts, x, y);
            MoveTo(x - _grabDx, y - _grabDy);
        }
        EndPress(notify: true, throwIt: true, ts);
    }

    private void BeginGrab(int x, int y, long ts)
    {
        KillTimer(Hwnd, TIMER_HOLD);
        _press = Press.Grab;
        _held = Motion.IsMoving(Item.MotionMode);
        UpdateMotionTimer(); // 움직임 멈춤
        var (w, h) = ContentSize(Item.Scale);
        // 움직이는 GIF는 기다리는 동안 마우스에서 멀어졌을 수 있음 → 잡은 점이 GIF 안에 오도록
        _grabDx = Math.Clamp(x - Item.X, 0, w - 1);
        _grabDy = Math.Clamp(y - Item.Y, 0, h - 1);
        _samples.Clear();
        AddSample(ts, x, y);
        MoveTo(x - _grabDx, y - _grabDy);
    }

    private void EndPress(bool notify, bool throwIt, long ts = 0)
    {
        var was = _press;
        _press = Press.None; // ReleaseCapture → WM_CAPTURECHANGED 에서 다시 들어오지 않게 먼저 비움
        KillTimer(Hwnd, TIMER_HOLD);
        if (GetCapture() == Hwnd) ReleaseCapture();
        if (was != Press.Grab) return; // 짧게 눌렀다 뗌 → 아무 일 없음

        int bx = Item.X, by = Item.Y;
        ClampToScreen();
        if (Item.X != bx || Item.Y != by) MoveTo(Item.X, Item.Y);
        var (vx, vy) = throwIt && !_editMode ? ThrowVelocity(ts) : (0.0, 0.0);
        _samples.Clear();

        string mode = Motion.Normalize(Item.MotionMode);
        if (mode == Motion.Slide)
        {
            _held = false;
            _motion.Release(Item.X, Item.Y, vx, vy);
            UpdateFacing();
            Render();
        }
        else if (Motion.IsAutonomous(mode) && !_editMode)
        {
            _motion.Release(Item.X, Item.Y, 0, 0);
            int delay = _engine.ResumeDelayMs;
            if (delay <= 0) _held = false;
            else
            {
                _held = true;
                SetTimer(Hwnd, TIMER_RESUME, (uint)delay, IntPtr.Zero);
            }
        }
        else _held = false;

        UpdateMotionTimer();
        if (notify) _engine.RaiseChanged(Item);
    }

    private void Resume()
    {
        _held = false;
        UpdateMotionTimer();
    }

    private void AddSample(long ts, int x, int y)
    {
        _samples.Add((ts, x, y));
        long keep = ts - (long)(System.Diagnostics.Stopwatch.Frequency * ThrowWindowSec * 2);
        int drop = 0;
        while (drop < _samples.Count - 2 && _samples[drop].t < keep) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
    }

    /// <summary>놓기 직전 ThrowWindowSec 동안의 평균 마우스 속도 (px/s). 멈췄다 놓으면 0.</summary>
    private (double vx, double vy) ThrowVelocity(long ts)
    {
        if (_samples.Count < 2) return (0, 0);
        long from = ts - (long)(System.Diagnostics.Stopwatch.Frequency * ThrowWindowSec);
        var last = _samples[^1];
        int i = _samples.Count - 1;
        while (i > 0 && _samples[i - 1].t >= from) i--;
        var first = _samples[i];
        double dt = (double)(last.t - first.t) / System.Diagnostics.Stopwatch.Frequency;
        if (dt < 0.01) return (0, 0);
        return ((last.x - first.x) / dt, (last.y - first.y) / dt);
    }

    private void MoveTo(int x, int y)
    {
        Item.X = x;
        Item.Y = y;
        _motion.X = x;
        _motion.Y = y;
        SetWindowPos(Hwnd, IntPtr.Zero, x - _pads.Left, y - _pads.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>점검용 상태.</summary>
    internal OverlayProbe Probe() => new(Item.Clone(), _motionRunning, _held, _press != Press.None, _motion.IsSliding, _motion.Jumps, IsTopmost, _motion.Pushes)
    {
        LightMode = _engine.LightMode,
        CaptureExcluded = CaptureExcludedNow,
        FadeOpacity = _fadeNow,
        FxPaused = _engine.FxPaused,
        Shown = IsShown,
        GifPlaying = _gifPlaying,
        Occluded = _occluded,
        Unloaded = _anim == null && UnloadedPath != null,
        Quality = _framePaint.FilterQuality.ToString(),
        GifDraws = GifDraws,
        FxRunning = _fxRunning,
        Particles = _fx.ParticleCount,
        Squashes = _fx.Squashes,
        DustPuffs = _fx.DustPuffs,
        WindowWidth = _dibW,
        WindowHeight = _dibH,
        Sprites = _fx.SpriteCount,
        FramesPerSecond = FramesPerSecond,
        GifFrames = GifFramesAdvanced,
        PushedArea = LastPushedArea,
        GlowBuilds = _fx.GlowBuilds,
        GlowHits = _fx.GlowHits,
        AnimWidth = _anim?.Width ?? 0,
        AnimKey = AnimKey,
    };

    private Drag HitTest(int x, int y)
    {
        if (!_selected || _anim == null) return Drag.Move;
        int pad = _editMode ? EditPad : 0;
        var (w, h) = ContentSize(Item.Scale);
        bool left = Math.Abs(x - pad) <= HandleHit, right = Math.Abs(x - (pad + w)) <= HandleHit;
        bool top = Math.Abs(y - pad) <= HandleHit, bottom = Math.Abs(y - (pad + h)) <= HandleHit;
        if (top && left) return Drag.TopLeft;
        if (top && right) return Drag.TopRight;
        if (bottom && left) return Drag.BottomLeft;
        if (bottom && right) return Drag.BottomRight;
        return Drag.Move;
    }

    private void OnDrag()
    {
        if (_anim == null) return;
        GetCursorPos(out var p);
        int dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;

        if (_drag == Drag.Move)
        {
            var (w, h) = ContentSize(Item.Scale);
            int nx = _sx + dx, ny = _sy + dy;
            if (GetKeyState(VK_MENU) >= 0) // Alt를 누르지 않았을 때만 자석 정렬
            {
                var work = WorkAreaAt(p.X, p.Y);
                if (Math.Abs(nx - work.Left) <= SnapPx) nx = work.Left;
                if (Math.Abs(nx + w - work.Right) <= SnapPx) nx = work.Right - w;
                if (Math.Abs(ny - work.Top) <= SnapPx) ny = work.Top;
                if (Math.Abs(ny + h - work.Bottom) <= SnapPx) ny = work.Bottom - h;
            }
            Item.X = nx;
            Item.Y = ny;
        }
        else
        {
            double baseW = _anim.SourceWidth, baseH = _anim.SourceHeight;
            double startW = baseW * _sScale, startH = baseH * _sScale;
            double wc, hc;
            switch (_drag)
            {
                case Drag.TopLeft: wc = startW - dx; hc = startH - dy; break;
                case Drag.TopRight: wc = startW + dx; hc = startH - dy; break;
                case Drag.BottomLeft: wc = startW - dx; hc = startH + dy; break;
                default: wc = startW + dx; hc = startH + dy; break;
            }
            double s = Math.Round(Math.Clamp(Math.Max(wc / baseW, hc / baseH), MinScale, MaxScale), 3);
            var (nw, nh) = ContentSize(s);
            int ax = _drag is Drag.TopLeft or Drag.BottomLeft ? _sx + (int)Math.Round(startW) : _sx; // 고정되는 반대쪽 모서리
            int ay = _drag is Drag.TopLeft or Drag.TopRight ? _sy + (int)Math.Round(startH) : _sy;
            Item.Scale = s;
            Item.X = _drag is Drag.TopLeft or Drag.BottomLeft ? ax - nw : ax;
            Item.Y = _drag is Drag.TopLeft or Drag.TopRight ? ay - nh : ay;
        }
        Render();
        _engine.RaiseLive(Item);
    }

    private void EndDrag(bool notify)
    {
        _drag = Drag.None;
        _showSize = false;
        ClampToScreen();
        Render();
        if (notify) _engine.RaiseChanged(Item);
    }

    private void ClampToScreen()
    {
        var (w, h) = ContentSize(Item.Scale);
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        Item.X = Math.Clamp(Item.X, vx - w + KeepVisiblePx, vx + vw - KeepVisiblePx);
        Item.Y = Math.Clamp(Item.Y, vy - h + KeepVisiblePx, vy + vh - KeepVisiblePx);
    }

    // ── 그리기 ───────────────────────────────────────────────

    private (int w, int h) ContentSize(double scale)
    {
        if (_anim == null) return (1, 1);
        // 디코딩은 줄여서 했을 수 있으므로 화면 크기는 원본 크기 기준
        return (Math.Max(1, (int)Math.Round(_anim.SourceWidth * scale)), Math.Max(1, (int)Math.Round(_anim.SourceHeight * scale)));
    }

    private void Render()
    {
        if (_anim == null || !IsShown) return;
        var (w, h) = ContentSize(Item.Scale);
        // 편집 중에는 효과 없이 핸들 여백만, 평소에는 효과가 잘리지 않을 만큼 여백
        _pads = _editMode ? Pads.Uniform(EditPad) : EffectState.ComputePads(Item, w, h);
        int ww = w + _pads.Left + _pads.Right, wh = h + _pads.Top + _pads.Bottom;
        EnsureSurface(ww, wh);

        var c = _surface!.Canvas;
        // 효과 화면은 지난번에 그린 영역만 지우고, 이번에 그린 영역과 합친 부분만 화면에 보낸다 (큰 여백 전체를 매번 복사하지 않음)
        bool partial = !_editMode && !_surfaceFresh && !_lastDrawn.IsEmpty;
        if (partial)
        {
            c.Save();
            c.ClipRect(_lastDrawn);
            c.Clear(SKColors.Transparent);
            c.Restore();
        }
        else c.Clear(SKColors.Transparent);
        _surfaceFresh = false;
        var drawn = SKRect.Empty;
        // 움직이는 중에는 소수점 위치까지 반영 (창은 정수 픽셀로만 움직일 수 있으므로 그 차이를 안쪽에서 보정)
        float sx = _editMode ? 0 : (float)_subX, sy = _editMode ? 0 : (float)_subY;
        var content = new SKRect(_pads.Left + sx, _pads.Top + sy, _pads.Left + sx + w, _pads.Top + sy + h);
        var frame = _anim.Frames[_frame];
        bool flip = Item.FlipX ^ (_facingLeft && !_editMode);

        if (_editMode)
        {
            c.DrawRect(content, _editFill);
            c.Save();
            if (flip) c.Scale(-1, 1, content.MidX, 0);
            c.DrawBitmap(frame, content, _framePaint);
            c.Restore();
        }
        else
        {
            double originX = Item.X - _pads.Left, originY = Item.Y - _pads.Top;
            double ground = Motion.Normalize(Item.MotionMode) is Motion.Walk or Motion.Fall ? _motion.GroundDistance : 0;
            var bounds = new SKRect(0, 0, ww, wh);
            drawn = _fx.DrawBehind(c, Item, content, originX, originY, frame, flip, ground, bounds);

            c.Save();
            _fx.ApplyTransform(c, Item, content);
            if (flip) c.Scale(-1, 1, content.MidX, 0);
            byte alpha = _fx.FrameAlpha(Item);
            var main = content;
            // 외곽 빛: 장면별로 한 번 만든 그림을 재사용하고 밝기(두근두근·깜빡임)는 투명도로만 조절
            var glow = _fx.GlowLayer(Item, frame, _frame, w, h);
            if (glow is { } g)
            {
                var gr = new SKRect(content.Left - g.margin, content.Top - g.margin, content.Right + g.margin, content.Bottom + g.margin);
                _glowPaint.Color = new SKColor(255, 255, 255, (byte)(g.alpha * alpha / 255));
                c.DrawBitmap(g.bmp, gr, _glowPaint);
                main = gr;
            }
            _framePaint.Color = new SKColor(255, 255, 255, alpha);
            c.DrawBitmap(frame, content, _framePaint);
            _framePaint.Color = SKColors.White;
            var mapped = c.TotalMatrix.MapRect(main); // 둥실·흔들림·회전·크기 변형이 적용된 실제 영역
            mapped.Inflate(2, 2);
            drawn = drawn.IsEmpty ? mapped : SKRect.Union(drawn, mapped);
            c.Restore();

            var front = _fx.DrawFront(c, Item, content, originX, originY, bounds);
            if (!front.IsEmpty) drawn = SKRect.Union(drawn, front);
        }

        if (_editMode)
        {
            if (_selected)
            {
                c.DrawRect(content, _solid);
                foreach (var (hx, hy) in new[] { (content.Left, content.Top), (content.Right, content.Top), (content.Left, content.Bottom), (content.Right, content.Bottom) })
                {
                    var r = new SKRect(hx - HandleSize / 2f, hy - HandleSize / 2f, hx + HandleSize / 2f, hy + HandleSize / 2f);
                    c.DrawRoundRect(r, 2, 2, _handleFill);
                    c.DrawRoundRect(r, 2, 2, _solid);
                }
            }
            else
            {
                var r = content;
                r.Inflate(-0.75f, -0.75f);
                c.DrawRect(r, _dashed);
            }

            if (_showSize)
            {
                string text = $"{w} × {h} · {Item.Scale * 100:0}%";
                float tw = _bubbleText.MeasureText(text);
                var box = new SKRect(content.Left + 8, content.Top + 8, content.Left + 8 + tw + 16, content.Top + 8 + 24);
                c.DrawRoundRect(box, 6, 6, _bubbleBg);
                c.DrawText(text, box.Left + 8, box.Top + 16.5f, _bubbleText);
            }
        }
        else if (_flash)
        {
            var r = content;
            r.Inflate(-1.5f, -1.5f);
            using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Accent, IsAntialias = true };
            c.DrawRect(r, p);
        }
        c.Flush();

        var full = new SKRect(0, 0, ww, wh);
        if (_editMode || drawn.IsEmpty) drawn = full; // 편집 화면 등은 전체
        drawn.Intersect(full);
        // 화면에 보낼 영역 = 지난번 그림(지워야 함) + 이번 그림
        var dirty = partial ? SKRect.Union(_lastDrawn, drawn) : full;
        _lastDrawn = drawn;

        var dst = new POINT(Item.X - _pads.Left, Item.Y - _pads.Top);
        var size = new SIZE(ww, wh);
        var src = new POINT(0, 0);
        double op = Math.Clamp(Item.Opacity, 0.1, 1.0);
        if (_editMode) op = Math.Max(op, 0.6);
        else op *= _fadeNow;
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(op * 255), AlphaFormat = AC_SRC_ALPHA };
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            bool done = false;
            if (partial && _partialPushOk)
            {
                var rc = new RECT
                {
                    Left = Math.Max(0, (int)Math.Floor(dirty.Left)), Top = Math.Max(0, (int)Math.Floor(dirty.Top)),
                    Right = Math.Min(ww, (int)Math.Ceiling(dirty.Right)), Bottom = Math.Min(wh, (int)Math.Ceiling(dirty.Bottom)),
                };
                LastPushedArea = Math.Max(0, rc.Right - rc.Left) * Math.Max(0, rc.Bottom - rc.Top);
                var info = new UPDATELAYEREDWINDOWINFO
                {
                    cbSize = (uint)sizeof(UPDATELAYEREDWINDOWINFO),
                    hdcDst = screenDc, pptDst = &dst, psize = &size, hdcSrc = _memDc, pptSrc = &src,
                    pblend = &blend, dwFlags = ULW_ALPHA, prcDirty = &rc,
                };
                done = UpdateLayeredWindowIndirect(Hwnd, &info);
                if (!done)
                {
                    _partialPushOk = false; // 이 PC에서 안 되면 늘 전체로
                    Log.Warn($"Partial layered update failed (error {Marshal.GetLastWin32Error()}); using full updates");
                }
            }
            if (!done)
            {
                LastPushedArea = ww * wh;
                UpdateLayeredWindow(Hwnd, screenDc, &dst, &size, _memDc, &src, 0, &blend, ULW_ALPHA);
            }
        }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // 바뀐 부분만 화면에 보내기
    private SKRect _lastDrawn = SKRect.Empty;  // 지난번에 실제로 그린 영역 (그 밖은 모두 투명)
    private bool _surfaceFresh = true;        // 새로 만든 표면 → 처음엔 전체를 보냄
    private static bool _partialPushOk = true;
    /// <summary>마지막으로 화면에 보낸 픽셀 수 (점검용).</summary>
    public int LastPushedArea { get; private set; }
    private readonly SKPaint _glowPaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };

    private void EnsureSurface(int w, int h)
    {
        if (_surface != null && w == _dibW && h == _dibH) return;
        FreeSurface();
        _surfaceFresh = true;
        _lastDrawn = SKRect.Empty;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_memDc, ref bih, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection 실패");
        _oldBmp = SelectObject(_memDc, _dib);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _bits, w * 4) ?? throw new InvalidOperationException("SKSurface 생성 실패");
        _dibW = w;
        _dibH = h;
    }

    private void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBmp);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        KillTimer(Hwnd, TIMER_FLASH);
        DestroyWindow(Hwnd);
        _fx.Dispose();
        if (_sprites != null) foreach (var s in _sprites) s.Dispose();
        _sprites = null;
        FreeSurface();
        _anim = null; // 공유 GIF는 엔진이 참조 수로 해제
        _framePaint.Dispose();
        _glowPaint.Dispose();
        _editFill.Dispose();
        _dashed.Dispose();
        _solid.Dispose();
        _handleFill.Dispose();
        _bubbleBg.Dispose();
        _bubbleText.Dispose();
    }
}
