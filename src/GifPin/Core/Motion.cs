using System;

namespace GifPin.Core;

/// <summary>데스크톱 펫 움직임 모드.</summary>
public static class Motion
{
    public const string None = "none";
    public const string Walk = "walk";     // 작업표시줄 위를 좌우로 걷기
    public const string Bounce = "bounce"; // 화면 안을 대각선으로 튕기며 이동
    public const string Fall = "fall";     // 중력으로 떨어져 바닥에서 튕기다 멈춤
    public const double MinSpeed = 20, MaxSpeed = 600, DefaultSpeed = 120;

    public static string Normalize(string? mode) => mode is Walk or Bounce or Fall ? mode : None;
    public static bool IsMoving(string? mode) => Normalize(mode) != None;
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

    public double X, Y, Vx, Vy;
    public bool FacingLeft => Vx < 0;
    private string _mode = Motion.None;

    public void Reset(string mode, double speed, double x, double y)
    {
        _mode = Motion.Normalize(mode);
        X = x;
        Y = y;
        speed = Math.Clamp(speed, Motion.MinSpeed, Motion.MaxSpeed);
        switch (_mode)
        {
            case Motion.Walk: Vx = speed; Vy = 0; break;
            case Motion.Bounce: Vx = speed; Vy = speed * 0.7; break;
            case Motion.Fall: Vx = speed * 0.5; Vy = 0; break;
            default: Vx = Vy = 0; break;
        }
    }

    /// <summary>dt초 만큼 진행. w,h = GIF 크기, 경계 = 작업 영역.</summary>
    public void Step(double dt, int w, int h, int left, int top, int right, int bottom)
    {
        dt = Math.Clamp(dt, 0, 0.1); // 잠깐 멈췄다 돌아와도 순간이동하지 않게
        int maxX = Math.Max(left, right - w), maxY = Math.Max(top, bottom - h);

        switch (_mode)
        {
            case Motion.Walk:
                Y = maxY; // 작업표시줄(바닥) 위에 서 있음
                X += Vx * dt;
                break;

            case Motion.Bounce:
                X += Vx * dt;
                Y += Vy * dt;
                if (Y < top) { Y = top; Vy = Math.Abs(Vy); }
                if (Y > maxY) { Y = maxY; Vy = -Math.Abs(Vy); }
                break;

            case Motion.Fall:
                bool onFloor = Y >= maxY - 0.5 && Math.Abs(Vy) < 1;
                if (!onFloor) Vy += Gravity * dt;
                X += Vx * dt;
                Y += Vy * dt;
                if (Y < top) { Y = top; Vy = Math.Abs(Vy) * 0.5; }
                if (Y >= maxY)
                {
                    Y = maxY;
                    Vy = Math.Abs(Vy) > RestSpeed ? -Math.Abs(Vy) * FloorBounce : 0;
                }
                if (Vy == 0) Vx *= Math.Max(0, 1 - 2.5 * dt); // 바닥 마찰
                if (Math.Abs(Vx) < 2) Vx = 0;
                break;

            default:
                return;
        }

        // 좌우 벽
        if (X < left) { X = left; Vx = Math.Abs(Vx); }
        if (X > maxX) { X = maxX; Vx = -Math.Abs(Vx); }
    }
}
