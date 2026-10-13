using System;

namespace GifPin.Core;

/// <summary>데스크톱 펫 움직임 모드.</summary>
public static class Motion
{
    public const string None = "none";
    public const string Walk = "walk";     // 작업표시줄 위를 좌우로 걷기
    public const string Bounce = "bounce"; // 화면 안을 대각선으로 튕기며 이동
    public const string Fall = "fall";     // 중력으로 떨어져 바닥에 멈추고, 주기적으로 뛰어오름
    public const string Slide = "slide";   // 마우스로 던지면 벽에 튕기며 미끄러지다 멈춤
    public const double MinSpeed = 20, MaxSpeed = 600, DefaultSpeed = 120;

    // 점프 (떨어지기)
    public const double MinJumpHeight = 20, MaxJumpHeight = 800, DefaultJumpHeight = 160;
    public const double MinJumpInterval = 0.5, MaxJumpInterval = 10, DefaultJumpInterval = 3;
    public const double MaxJumpAngle = 75, DefaultJumpAngle = 0; // 0 = 수직, + 오른쪽 / − 왼쪽

    // 미끄러짐
    public const double MinFriction = 1, MaxFriction = 10, DefaultFriction = 4;
    public const double MaxThrowSpeed = 6000; // px/s

    public static string Normalize(string? mode) => mode is Walk or Bounce or Fall or Slide ? mode : None;

    /// <summary>움직임 모드가 켜져 있음 (미끄러짐 포함).</summary>
    public static bool IsMoving(string? mode) => Normalize(mode) != None;

    /// <summary>스스로 계속 움직이는 모드 (걷기·튕기기·떨어지기). 미끄러짐은 던졌을 때만 움직이므로 제외.</summary>
    public static bool IsAutonomous(string? mode) => Normalize(mode) is Walk or Bounce or Fall;

    /// <summary>마찰 단계(1~10) → 감속도(px/s²).</summary>
    public static double FrictionToDecel(double level) => 150 * Math.Clamp(level, MinFriction, MaxFriction);
}

/// <summary>움직임 설정값 (ActiveItem에서 복사).</summary>
internal readonly record struct MotionParams(
    double Speed, bool JumpEnabled, double JumpHeight, double JumpInterval, double JumpAngle, double Friction, bool Gravity = true)
{
    public static MotionParams From(ActiveItem a) => new(
        a.MotionSpeed, a.JumpEnabled, a.JumpHeight, a.JumpInterval, a.JumpAngle, a.SlideFriction, a.MotionGravity);
}

/// <summary>
/// 움직임 계산 (OverlayEngine 스레드 전용). 위치는 소수점까지 들고 있다가 그릴 때 반올림한다.
/// 경계는 GIF가 있는 모니터의 작업 영역(작업표시줄 제외).
/// </summary>
internal sealed class MotionState
{
    private const double Gravity = 1800;     // px/s²
    private const double FloorBounce = 0.55; // 바닥에서 튕길 때 남는 속도 비율
    private const double RestSpeed = 60;     // 이보다 느리면 바닥에 멈춤
    private const double WallBounce = 0.85;  // 미끄러짐: 벽에 부딪힐 때 남는 속도 비율
    private const double StopSpeed = 8;      // 미끄러짐: 이보다 느리면 멈춤

    public double X, Y, Vx, Vy;
    public bool FacingLeft { get; private set; }
    /// <summary>미끄러지는 중 (던진 뒤 아직 멈추지 않음).</summary>
    public bool IsSliding => _mode == Motion.Slide && (Vx != 0 || Vy != 0);
    /// <summary>점프한 횟수 (점검용).</summary>
    public int Jumps { get; private set; }

    private string _mode = Motion.None;
    private MotionParams _p = new(Motion.DefaultSpeed, true, Motion.DefaultJumpHeight, Motion.DefaultJumpInterval, 0, Motion.DefaultFriction);
    private double _restTime;
    private double _groundY; // 중력을 끈 경우의 땅 높이 = 놓은(배치한) 위치
    private int _jumpDir = 1;

    public string Mode => _mode;

    public void Reset(string mode, MotionParams p, double x, double y)
    {
        _mode = Motion.Normalize(mode);
        X = x;
        Y = y;
        _groundY = y;
        _restTime = 0;
        _p = p;
        Configure(p);
        double speed = Math.Clamp(p.Speed, Motion.MinSpeed, Motion.MaxSpeed);
        switch (_mode)
        {
            case Motion.Walk: Vx = speed; Vy = 0; break;
            case Motion.Bounce: Vx = speed; Vy = speed * 0.7; break;
            case Motion.Fall: Vx = speed * 0.5; Vy = 0; break;
            default: Vx = Vy = 0; break;
        }
        _jumpDir = p.JumpAngle < 0 ? -1 : 1;
        FacingLeft = Vx < 0 || (Vx == 0 && _mode == Motion.Fall && p.JumpAngle < 0);
    }

    /// <summary>설정만 바꿈 (위치·속도는 유지).</summary>
    public void Configure(MotionParams p)
    {
        bool angleSignChanged = Math.Sign(p.JumpAngle) != Math.Sign(_p.JumpAngle);
        if (p.Gravity != _p.Gravity) { _groundY = Y; Vy = 0; _restTime = 0; } // 중력을 끄면 지금 높이가 땅
        _p = p;
        if (angleSignChanged && p.JumpAngle != 0) _jumpDir = p.JumpAngle < 0 ? -1 : 1;
    }

    /// <summary>손으로 놓았을 때: 그 자리에서 다시 출발. 미끄러짐이면 던진 속도로.</summary>
    public void Release(double x, double y, double vx, double vy)
    {
        X = x;
        Y = y;
        _groundY = y; // 중력이 꺼져 있으면 놓은 높이가 새 땅
        _restTime = 0;
        switch (_mode)
        {
            case Motion.Slide:
                double sp = Math.Sqrt(vx * vx + vy * vy);
                if (sp > Motion.MaxThrowSpeed) { vx *= Motion.MaxThrowSpeed / sp; vy *= Motion.MaxThrowSpeed / sp; }
                Vx = sp < StopSpeed ? 0 : vx;
                Vy = sp < StopSpeed ? 0 : vy;
                break;
            case Motion.Fall:
                Vy = 0; // 놓은 자리에서 떨어짐
                break;
            case Motion.Walk:
                Vy = 0;
                break;
        }
        if (Vx != 0) FacingLeft = Vx < 0;
    }

    /// <summary>dt초 만큼 진행. w,h = GIF 크기, 경계 = 작업 영역.</summary>
    public void Step(double dt, int w, int h, int left, int top, int right, int bottom)
    {
        dt = Math.Clamp(dt, 0, 0.1); // 잠깐 멈췄다 돌아와도 순간이동하지 않게
        int maxX = Math.Max(left, right - w), maxY = Math.Max(top, bottom - h);
        // 땅: 중력이 켜져 있으면 작업 영역 바닥(작업표시줄 위), 꺼져 있으면 배치한 높이
        double floor = _p.Gravity ? maxY : Math.Clamp(_groundY, top, maxY);
        bool hitWall = false;

        switch (_mode)
        {
            case Motion.Walk:
                if (_p.Gravity && Y < floor - 0.5)
                {
                    // 공중에서 놓으면 먼저 바닥까지 떨어진 뒤 걷는다
                    Vy += Gravity * dt;
                    Y += Vy * dt;
                    if (Y >= floor) { Y = floor; Vy = 0; }
                }
                else
                {
                    Y = floor; // 바닥(또는 중력을 끈 경우 배치한 높이)에서 수평으로 걷기
                    Vy = 0;
                }
                X += Vx * dt;
                break;

            case Motion.Bounce:
                X += Vx * dt;
                Y += Vy * dt;
                if (Y < top) { Y = top; Vy = Math.Abs(Vy); }
                if (Y > maxY) { Y = maxY; Vy = -Math.Abs(Vy); }
                break;

            case Motion.Fall:
                StepFall(dt, top, floor);
                break;

            case Motion.Slide:
                if (Vx == 0 && Vy == 0) return;
                X += Vx * dt;
                Y += Vy * dt;
                if (Y < top) { Y = top; Vy = Math.Abs(Vy) * WallBounce; Vx *= WallBounce; }
                if (Y > maxY) { Y = maxY; Vy = -Math.Abs(Vy) * WallBounce; Vx *= WallBounce; }
                if (X < left) { X = left; Vx = Math.Abs(Vx) * WallBounce; Vy *= WallBounce; }
                if (X > maxX) { X = maxX; Vx = -Math.Abs(Vx) * WallBounce; Vy *= WallBounce; }
                // 당구공처럼 일정하게 감속
                double sp = Math.Sqrt(Vx * Vx + Vy * Vy);
                double next = sp - Motion.FrictionToDecel(_p.Friction) * dt;
                if (next < StopSpeed) { Vx = Vy = 0; }
                else { Vx *= next / sp; Vy *= next / sp; }
                if (Vx != 0) FacingLeft = Vx < 0;
                return;

            default:
                return;
        }

        // 좌우 벽
        if (X < left) { X = left; Vx = Math.Abs(Vx); hitWall = true; }
        if (X > maxX) { X = maxX; Vx = -Math.Abs(Vx); hitWall = true; }
        if (hitWall && _mode == Motion.Fall && _p.JumpAngle != 0) _jumpDir = Vx < 0 ? -1 : Vx > 0 ? 1 : -_jumpDir;
        if (Math.Abs(Vx) > 1) FacingLeft = Vx < 0;
        else if (_mode == Motion.Fall && _p.JumpAngle != 0 && Vy == 0) FacingLeft = _jumpDir < 0; // 다음에 뛸 방향을 바라봄
    }

    private void StepFall(double dt, int top, double maxY)
    {
        bool onFloor = Y >= maxY - 0.5 && Vy == 0;
        if (onFloor)
        {
            Y = maxY;
            Vx *= Math.Max(0, 1 - 6 * dt); // 바닥 마찰
            if (Math.Abs(Vx) < 2) Vx = 0;
            X += Vx * dt;

            if (_p.JumpEnabled)
            {
                _restTime += dt;
                if (_restTime >= Math.Clamp(_p.JumpInterval, Motion.MinJumpInterval, Motion.MaxJumpInterval)) Jump(maxY - top);
            }
            else _restTime = 0;
            return;
        }

        _restTime = 0;
        Vy += Gravity * dt;
        X += Vx * dt;
        Y += Vy * dt;
        if (Y < top) { Y = top; Vy = Math.Abs(Vy) * 0.5; }
        if (Y >= maxY)
        {
            Y = maxY;
            Vy = Math.Abs(Vy) > RestSpeed ? -Math.Abs(Vy) * FloorBounce : 0;
        }
    }

    private void Jump(double room)
    {
        _restTime = 0;
        double height = Math.Clamp(_p.JumpHeight, Motion.MinJumpHeight, Motion.MaxJumpHeight);
        height = Math.Min(height, Math.Max(10, room)); // 화면 위로 넘어가지 않게
        double angle = Math.Clamp(_p.JumpAngle, -Motion.MaxJumpAngle, Motion.MaxJumpAngle) * Math.PI / 180;
        double up = Math.Sqrt(2 * Gravity * height); // 최고점이 height가 되는 수직 속도
        Vy = -up;
        Vx = angle == 0 ? 0 : _jumpDir * up * Math.Tan(Math.Abs(angle));
        Jumps++;
    }
}
