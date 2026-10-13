using System;
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
    private const nuint TIMER_FRAME = 1, TIMER_FLASH = 2, TIMER_MOTION = 3;
    private const uint MotionIntervalMs = 15;
    private const long MotionReportMs = 3000; // 움직이는 중 위치를 UI에 알리는 간격
    private const double MinScale = 0.1, MaxScale = 4.0;

    private enum Drag { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

    private readonly OverlayEngine _engine;
    public IntPtr Hwnd { get; }
    public ActiveItem Item { get; private set; }
    public string? AnimPath { get; private set; }
    public string? PendingPath { get; set; }

    private GifAnimation? _anim;
    private int _frame;
    private bool _editMode, _selected, _globalHidden, _flash, _showSize;

    private readonly MotionState _motion = new();
    private readonly System.Diagnostics.Stopwatch _motionClock = new();
    private long _lastMotionReport;
    private bool _motionRunning, _facingLeft;

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
        long ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        if (item.ClickThrough) ex |= WS_EX_TRANSPARENT;
        Hwnd = CreateWindowExW((uint)ex, ClassName, "GifPin overlay", WS_POPUP,
            item.X, item.Y, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx 실패 (오류 {Marshal.GetLastWin32Error()})");
    }

    // ── 엔진에서 호출 ─────────────────────────────────────────

    public void SetAnimation(GifAnimation anim, string path)
    {
        _anim?.Dispose();
        _anim = anim;
        AnimPath = path;
        _frame = 0;
        Refresh();
    }

    public void Update(ActiveItem item)
    {
        var prev = Item;
        bool prevMoving = Motion.IsMoving(prev.MotionMode);
        // 드래그 중이거나 움직이는 중에는 위치를 엔진 쪽 값으로 유지 (위치를 강제로 바꿀 때는 Relocate)
        if (_drag != Drag.None || prevMoving)
        {
            item.X = prev.X;
            item.Y = prev.Y;
        }
        if (_drag != Drag.None) item.Scale = prev.Scale;
        Item = item;
        ApplyStyles();
        if (prev.MotionMode != item.MotionMode || Math.Abs(prev.MotionSpeed - item.MotionSpeed) > 0.01) ResetMotion();
        Refresh();
        if (prevMoving && !Motion.IsMoving(item.MotionMode)) _engine.RaiseChanged(Item); // 멈춘 위치 저장
    }

    /// <summary>위치를 강제로 옮김 (모니터 이동, 9분할 배치, 복구 도구). 움직이는 중이면 그 자리에서 다시 출발.</summary>
    public void Relocate(int x, int y)
    {
        Item.X = x;
        Item.Y = y;
        ResetMotion();
        Render();
        _engine.RaiseChanged(Item);
    }

    public void SetPixelated(bool pixelated)
    {
        var q = pixelated ? SKFilterQuality.None : SKFilterQuality.Medium;
        if (_framePaint.FilterQuality == q) return;
        _framePaint.FilterQuality = q;
        _framePaint.IsAntialias = !pixelated;
        Render();
    }

    private void ResetMotion()
    {
        _motion.Reset(Item.MotionMode, Item.MotionSpeed, Item.X, Item.Y);
        _facingLeft = false;
    }

    private bool MotionShouldRun => Motion.IsMoving(Item.MotionMode) && !_editMode && _drag == Drag.None && IsShown;

    private void UpdateMotionTimer()
    {
        bool run = MotionShouldRun;
        if (run == _motionRunning) return;
        _motionRunning = run;
        if (run)
        {
            _motion.X = Item.X; // 편집·숨김 뒤 다시 출발할 때 현재 위치에서
            _motion.Y = Item.Y;
            _motionClock.Restart();
            _lastMotionReport = 0;
            SetTimer(Hwnd, TIMER_MOTION, MotionIntervalMs, IntPtr.Zero);
        }
        else
        {
            KillTimer(Hwnd, TIMER_MOTION);
            _motionClock.Stop();
        }
    }

    private void OnMotionTick()
    {
        if (_anim == null || !MotionShouldRun) { UpdateMotionTimer(); return; }
        double dt = _motionClock.Elapsed.TotalSeconds;
        long now = _motionClock.ElapsedMilliseconds;
        _motionClock.Restart();
        _lastMotionReport += now;

        var (w, h) = ContentSize(Item.Scale);
        var work = WorkAreaAt(Item.X + w / 2, Item.Y + h / 2);
        _motion.Step(dt, w, h, work.Left, work.Top, work.Right, work.Bottom);
        int nx = (int)Math.Round(_motion.X), ny = (int)Math.Round(_motion.Y);
        bool facing = Item.MotionFaceDirection && _motion.FacingLeft;

        if (facing != _facingLeft)
        {
            _facingLeft = facing;
            Item.X = nx;
            Item.Y = ny;
            Render(); // 방향이 바뀌면 다시 그림 (위치도 함께 반영)
        }
        else if (nx != Item.X || ny != Item.Y)
        {
            Item.X = nx;
            Item.Y = ny;
            SetWindowPos(Hwnd, IntPtr.Zero, nx, ny, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        if (_lastMotionReport >= MotionReportMs)
        {
            _lastMotionReport = 0;
            _engine.RaiseChanged(Item);
        }
    }

    public void SetEditMode(bool edit)
    {
        if (_editMode == edit) return;
        if (!edit && _drag != Drag.None) EndDrag(notify: true);
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

    public void BringToTop()
    {
        if (IsShown)
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private bool IsShown => _anim != null && (_editMode || (Item.Visible && !_globalHidden));

    /// <summary>보이기/숨기기, 재생 타이머, 그리기를 현재 상태에 맞춘다.</summary>
    private void Refresh()
    {
        if (IsShown)
        {
            Render();
            ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
            BringToTop();
            StartTimer();
        }
        else
        {
            KillTimer(Hwnd, TIMER_FRAME);
            ShowWindow(Hwnd, SW_HIDE);
        }
        UpdateMotionTimer();
    }

    private void StartTimer()
    {
        if (_anim == null || _anim.Frames.Count < 2) { KillTimer(Hwnd, TIMER_FRAME); return; }
        double speed = Item.Speed <= 0 ? 1 : Item.Speed;
        uint delay = (uint)Math.Max(10, _anim.DelaysMs[_frame] / speed);
        SetTimer(Hwnd, TIMER_FRAME, delay, IntPtr.Zero);
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
                if ((nuint)wParam == TIMER_FRAME && _anim != null && _anim.Frames.Count > 1)
                {
                    _frame = (_frame + 1) % _anim.Frames.Count;
                    Render();
                    StartTimer();
                }
                else if ((nuint)wParam == TIMER_MOTION)
                {
                    OnMotionTick();
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

            case WM_LBUTTONDOWN:
                if (!_editMode) break;
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
                if (_drag == Drag.None) break;
                OnDrag();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_drag == Drag.None) break;
                ReleaseCapture(); // → WM_CAPTURECHANGED에서 마무리
                if (_drag != Drag.None) EndDrag(notify: true);
                return IntPtr.Zero;

            case WM_CAPTURECHANGED:
                if (_drag != Drag.None) EndDrag(notify: true);
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
            double baseW = _anim.Width, baseH = _anim.Height;
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
        return (Math.Max(1, (int)Math.Round(_anim.Width * scale)), Math.Max(1, (int)Math.Round(_anim.Height * scale)));
    }

    private void Render()
    {
        if (_anim == null || !IsShown) return;
        int pad = _editMode ? EditPad : 0;
        var (w, h) = ContentSize(Item.Scale);
        int ww = w + pad * 2, wh = h + pad * 2;
        EnsureSurface(ww, wh);

        var c = _surface!.Canvas;
        c.Clear(SKColors.Transparent);
        var content = new SKRect(pad, pad, pad + w, pad + h);

        if (_editMode) c.DrawRect(content, _editFill);

        bool flip = Item.FlipX ^ (_motionRunning && _facingLeft);
        if (flip)
        {
            c.Save();
            c.Scale(-1, 1, pad + w / 2f, 0);
            c.DrawBitmap(_anim.Frames[_frame], content, _framePaint);
            c.Restore();
        }
        else c.DrawBitmap(_anim.Frames[_frame], content, _framePaint);

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

        var dst = new POINT(Item.X - pad, Item.Y - pad);
        var size = new SIZE(ww, wh);
        var src = new POINT(0, 0);
        double op = Math.Clamp(Item.Opacity, 0.1, 1.0);
        if (_editMode) op = Math.Max(op, 0.6);
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(op * 255), AlphaFormat = AC_SRC_ALPHA };
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try { UpdateLayeredWindow(Hwnd, screenDc, &dst, &size, _memDc, &src, 0, &blend, ULW_ALPHA); }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }
    }

    private void EnsureSurface(int w, int h)
    {
        if (_surface != null && w == _dibW && h == _dibH) return;
        FreeSurface();

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
        KillTimer(Hwnd, TIMER_FRAME);
        KillTimer(Hwnd, TIMER_FLASH);
        KillTimer(Hwnd, TIMER_MOTION);
        DestroyWindow(Hwnd);
        FreeSurface();
        _anim?.Dispose();
        _anim = null;
        _framePaint.Dispose();
        _editFill.Dispose();
        _dashed.Dispose();
        _solid.Dispose();
        _handleFill.Dispose();
        _bubbleBg.Dispose();
        _bubbleText.Dispose();
    }
}
