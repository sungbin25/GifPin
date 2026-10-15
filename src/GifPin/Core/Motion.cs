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
    public const double DefaultJumpAngleRange = 45;               // 랜덤 방향: −범위 ~ +범위
    public const double DefaultJumpHeightMin = 80, DefaultJumpHeightMax = 260;

    // 미끄러짐
    public const double MinFriction = 1, MaxFriction = 10, DefaultFriction = 4;
    public const double MaxThrowSpeed = 6000; // px/s
    // 미끄러짐 자동 밀기: 힘 = 처음 속도(px/s), 방향 = 0° 오른쪽, 90° 위 (시계 반대 방향)
    public const double MinPushForce = 100, MaxPushForce = 5000, DefaultPushForce = 1200, DefaultPushForceMin = 500, DefaultPushForceMax = 2000;
    public const double MinPushInterval = 0.5, MaxPushInterval = 60, DefaultPushInterval = 3, DefaultPushIntervalMin = 2, DefaultPushIntervalMax = 6;

    public static string Normalize(string? mode) => mode is Walk or Bounce or Fall or Slide ? mode : None;

    /// <summary>움직임 모드가 켜져 있음 (미끄러짐 포함).</summary>
    public static bool IsMoving(string? mode) => Normalize(mode) != None;

    /// <summary>스스로 계속 움직이는 모드 (걷기·튕기기·떨어지기). 미끄러짐은 던졌을 때만 움직이므로 제외.</summary>
    public static bool IsAutonomous(string? mode) => Normalize(mode) is Walk or Bounce or Fall;

    /// <summary>마찰 단계(1~10) → 감속도(px/s²).</summary>
    public static double FrictionToDecel(double level) => 150 * Math.Clamp(level, MinFriction, MaxFriction);
}

/// <summary>움직임 설정값 (ActiveItem에서 복사). "Random"이 켜진 값은 매번 범위 안에서 새로 뽑는다.</summary>
internal sealed record MotionParams
{
    public double Speed { get; init; } = Motion.DefaultSpeed;
    public bool Gravity { get; init; } = true;
    public double Friction { get; init; } = Motion.DefaultFriction;

    // 점프 (떨어지기)
    public bool JumpEnabled { get; init; } = true;
    public double JumpInterval { get; init; } = Motion.DefaultJumpInterval;
    public double JumpAngle { get; init; } = Motion.DefaultJumpAngle;
    public bool JumpAngleRandom { get; init; }
    public double JumpAngleRange { get; init; } = Motion.DefaultJumpAngleRange;
    public double JumpHeight { get; init; } = Motion.DefaultJumpHeight;
    public bool JumpHeightRandom { get; init; }
    public double JumpHeightMin { get; init; } = Motion.DefaultJumpHeightMin;
    public double JumpHeightMax { get; init; } = Motion.DefaultJumpHeightMax;

    // 미끄러짐 자동 밀기
    public bool SlideAuto { get; init; }
    public bool PushDirRandom { get; init; } = true;
    public double PushAngle { get; init; }
    public bool PushForceRandom { get; init; } = true;
    public double PushForce { get; init; } = Motion.DefaultPushForce;
    public double PushForceMin { get; init; } = Motion.DefaultPushForceMin;
    public double PushForceMax { get; init; } = Motion.DefaultPushForceMax;
    public bool PushIntervalRandom { get; init; } = true;
    public double PushInterval { get; init; } = Motion.DefaultPushInterval;
    public double PushIntervalMin { get; init; } = Motion.DefaultPushIntervalMin;
    public double PushIntervalMax { get; init; } = Motion.DefaultPushIntervalMax;

    public static MotionParams From(ActiveItem a) => new()
    {
        Speed = a.MotionSpeed, Gravity = a.MotionGravity, Friction = a.SlideFriction,
        JumpEnabled = a.JumpEnabled, JumpInterval = a.JumpInterval, JumpAngle = a.JumpAngle,
        JumpAngleRandom = a.JumpAngleRandom, JumpAngleRange = a.JumpAngleRange,
        JumpHeight = a.JumpHeight, JumpHeightRandom = a.JumpHeightRandom, JumpHeightMin = a.JumpHeightMin, JumpHeightMax = a.JumpHeightMax,
        SlideAuto = a.SlideAuto, PushDirRandom = a.PushDirRandom, PushAngle = a.PushAngle,
        PushForceRandom = a.PushForceRandom, PushForce = a.PushForce, PushForceMin = a.PushForceMin, PushForceMax = a.PushForceMax,
        PushIntervalRandom = a.PushIntervalRandom, PushInterval = a.PushInterval, PushIntervalMin = a.PushIntervalMin, PushIntervalMax = a.PushIntervalMax,
    };
}

/// <summary>
/// 움직임 계산 (OverlayEngine 스레드 전용). 위치는 소수점까지 들고 있다가 그릴 때 반올림한다.
/// 경계는 GIF가 있는 모니터의 작업 영역(작업표시줄 제외).
/// </summary>
internal sealed class MotionState
{
    /// <summary>효과(찌그러짐·먼지)를 위한 충돌 종류.</summary>
    public enum Impact { None, Land, Jump, Ceiling, WallLeft, WallRight }

    private const double Gravity = 1800;     // px/s²
    private const double FloorBounce = 0.55; // 바닥에서 튕길 때 남는 속도 비율
    private const double RestSpeed = 60;     // 이보다 느리면 바닥에 멈춤
    private const double WallBounce = 0.85;  // 미끄러짐: 벽에 부딪힐 때 남는 속도 비율
    private const double StopSpeed = 8;      // 미끄러짐: 이보다 느리면 멈춤
    private const double BounceRecoveryDelay = 3;
    private double _bounceQuiet;
    private bool _bounceRecovering;
    private double _bounceDirX = 1 / Math.Sqrt(1.49), _bounceDirY = .7 / Math.Sqrt(1.49);

    private void RememberBounceDirection()
    {
        double speed = Math.Sqrt(Vx * Vx + Vy * Vy);
        if (speed <= 1e-9) return;
        _bounceDirX = Vx / speed; _bounceDirY = Vy / speed;
    }

    private void RecoverBounceSpeed(double dt)
    {
        if (!_bounceRecovering) return;
        RememberBounceDirection();
        double elapsed = Math.Min(dt, Math.Max(0, _bounceQuiet - BounceRecoveryDelay));
        if (elapsed <= 0) return;
        // Preserve the original bounce setting (vx = speed, vy = speed * .7),
        // but keep the direction produced by collisions instead of resetting the angle.
        double target = Math.Clamp(_p.Speed, Motion.MinSpeed, Motion.MaxSpeed) * Math.Sqrt(1.49);
        double speed = Math.Sqrt(Vx * Vx + Vy * Vy);
        double next = target + (speed - target) * Math.Exp(-elapsed * 1.5);
        if (Math.Abs(next - target) < .1) { next = target; _bounceRecovering = false; }
        Vx = _bounceDirX * next; Vy = _bounceDirY * next;
    }

    public double X, Y, Vx, Vy;
    public bool FacingLeft { get; private set; }
    /// <summary>미끄러지는 중 (던진 뒤 아직 멈추지 않음).</summary>
    public bool IsSliding => _mode == Motion.Slide && (Vx != 0 || Vy != 0);
    /// <summary>점프한 횟수 (점검용).</summary>
    public int Jumps { get; private set; }
    /// <summary>자동으로 민 횟수 (점검용).</summary>
    public int Pushes { get; private set; }
    /// <summary>미끄러짐 자동 밀기: 멈춰 있어도 시간을 재야 하므로 계속 돌아야 함.</summary>
    public bool AutoPush => _mode == Motion.Slide && _p.SlideAuto;

    /// <summary>충돌이 일어날 때마다 1씩 증가 (효과가 새 충돌을 알아챔).</summary>
    public int ImpactSeq { get; private set; }
    public Impact LastImpact { get; private set; }
    /// <summary>0.15 ~ 1 (빠를수록 큼)</summary>
    public double ImpactStrength { get; private set; }
    /// <summary>걷기·떨어지기: 땅까지 남은 거리 (그림자용). 그 밖에는 0.</summary>
    public double GroundDistance { get; private set; }

    private void Hit(Impact kind, double speed)
    {
        if (_mode == Motion.Bounce) _bounceQuiet = 0; // Even slow wall contacts restart the delay.
        speed = Math.Abs(speed);
        // 아주 느린 접촉(바닥에 멈춰 가는 중 등)만 무시. 예전 기준(120px/s)은 튕기기의 위아래 속도(84px/s)보다 높아
        // 위·아래 충돌에서 말랑말랑이 빠졌음
        if (speed < 40) return;
        ImpactSeq++;
        LastImpact = kind;
        ImpactStrength = Math.Clamp(speed / 800, 0.25, 1); // 느린 충돌도 눈에 보이게
    }

    private readonly Random _rng;
    private string _mode = Motion.None;
    private MotionParams _p = new();
    private double _restTime;
    private double _nextWait = -1; // 자동 밀기: 이번에 기다릴 시간 (−1 = 아직 안 뽑음)
    private double _groundY; // 중력을 끈 경우의 땅 높이 = 놓은(배치한) 위치
    private int _jumpDir = 1;

    public string Mode => _mode;

    public void Collide(double x, double y, double vx, double vy)
    {
        if (_mode == Motion.Bounce)
        {
            RememberBounceDirection(); // Keep a direction if this impact stops us completely.
            _bounceQuiet = 0; _bounceRecovering = true;
        }
        X = x; Y = y;
        if (_mode == Motion.Walk && !_p.Gravity) _groundY = y;
        double speed = Math.Sqrt(vx * vx + vy * vy);
        double limit = speed > Motion.MaxThrowSpeed ? Motion.MaxThrowSpeed / speed : 1;
        Vx = vx * limit; Vy = vy * limit;
        if (Vx != 0) { FacingLeft = Vx < 0; _jumpDir = Vx < 0 ? -1 : 1; }
        if (IsSliding) { _restTime = 0; _nextWait = -1; }
    }

    public MotionState(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();

    /// <summary>random이면 [min, max]에서 뽑고, 아니면 고정값.</summary>
    private double Pick(bool random, double value, double min, double max)
    {
        if (!random) return value;
        if (max < min) (min, max) = (max, min);
        return min + _rng.NextDouble() * (max - min);
    }

    public void Reset(string mode, MotionParams p, double x, double y)
    {
        _mode = Motion.Normalize(mode);
        _bounceQuiet = 0; _bounceRecovering = false;
        X = x;
        Y = y;
        _groundY = y;
        _restTime = 0;
        _nextWait = -1;
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
        if (_mode == Motion.Bounce) RememberBounceDirection();
        FacingLeft = Vx < 0 || (Vx == 0 && _mode == Motion.Fall && p.JumpAngle < 0);
    }

    /// <summary>설정만 바꿈 (위치·속도는 유지).</summary>
    public void Configure(MotionParams p)
    {
        bool angleSignChanged = Math.Sign(p.JumpAngle) != Math.Sign(_p.JumpAngle);
        if (p.Gravity != _p.Gravity) { _groundY = Y; Vy = 0; _restTime = 0; } // 중력을 끄면 지금 높이가 땅
        _p = p;
        _nextWait = -1; // 간격 설정이 바뀌었을 수 있으니 다시 뽑음
        if (angleSignChanged && p.JumpAngle != 0) _jumpDir = p.JumpAngle < 0 ? -1 : 1;
    }

    /// <summary>손으로 놓았을 때: 그 자리에서 다시 출발. 미끄러짐이면 던진 속도로.</summary>
    public void Release(double x, double y, double vx, double vy)
    {
        X = x;
        Y = y;
        _groundY = y; // 중력이 꺼져 있으면 놓은 높이가 새 땅
        _restTime = 0;
        _nextWait = -1;
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
        if (_mode == Motion.Bounce && _bounceRecovering) _bounceQuiet += dt;
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
                    if (Y >= floor) { Hit(Impact.Land, Vy); Y = floor; Vy = 0; }
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
                if (Y < top) { Hit(Impact.Ceiling, Vy); Y = top; Vy = Math.Abs(Vy); }
                if (Y > maxY) { Hit(Impact.Land, Vy); Y = maxY; Vy = -Math.Abs(Vy); }
                break;

            case Motion.Fall:
                StepFall(dt, top, floor);
                break;

            case Motion.Slide:
                if (Vx == 0 && Vy == 0)
                {
                    if (!_p.SlideAuto) { _restTime = 0; return; }
                    if (_nextWait < 0)
                        _nextWait = Math.Clamp(Pick(_p.PushIntervalRandom, _p.PushInterval, _p.PushIntervalMin, _p.PushIntervalMax),
                            Motion.MinPushInterval, Motion.MaxPushInterval);
                    _restTime += dt;
                    if (_restTime >= _nextWait) Push();
                    return;
                }
                X += Vx * dt;
                Y += Vy * dt;
                if (Y < top) { Hit(Impact.Ceiling, Vy); Y = top; Vy = Math.Abs(Vy) * WallBounce; Vx *= WallBounce; }
                if (Y > maxY) { Hit(Impact.Land, Vy); Y = maxY; Vy = -Math.Abs(Vy) * WallBounce; Vx *= WallBounce; }
                if (X < left) { Hit(Impact.WallLeft, Vx); X = left; Vx = Math.Abs(Vx) * WallBounce; Vy *= WallBounce; }
                if (X > maxX) { Hit(Impact.WallRight, Vx); X = maxX; Vx = -Math.Abs(Vx) * WallBounce; Vy *= WallBounce; }
                // 당구공처럼 일정하게 감속
                double sp = Math.Sqrt(Vx * Vx + Vy * Vy);
                double next = sp - Motion.FrictionToDecel(_p.Friction) * dt;
                if (next < StopSpeed) { Vx = Vy = 0; _restTime = 0; _nextWait = -1; }
                else { Vx *= next / sp; Vy *= next / sp; }
                if (Vx != 0) FacingLeft = Vx < 0;
                return;

            default:
                return;
        }

        GroundDistance = _mode is Motion.Walk or Motion.Fall ? Math.Max(0, floor - Y) : 0;

        // 좌우 벽
        if (X < left) { Hit(Impact.WallLeft, Vx); X = left; Vx = Math.Abs(Vx); hitWall = true; }
        if (X > maxX) { Hit(Impact.WallRight, Vx); X = maxX; Vx = -Math.Abs(Vx); hitWall = true; }
        if (hitWall && _mode == Motion.Fall && _p.JumpAngle != 0) _jumpDir = Vx < 0 ? -1 : Vx > 0 ? 1 : -_jumpDir;
        if (_mode == Motion.Bounce) RecoverBounceSpeed(dt);
        if (Math.Abs(Vx) > 1) FacingLeft = Vx < 0;
        else if (_mode == Motion.Fall && !_p.JumpAngleRandom && _p.JumpAngle != 0 && Vy == 0) FacingLeft = _jumpDir < 0; // 다음에 뛸 방향을 바라봄
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
        if (Y < top) { Hit(Impact.Ceiling, Vy); Y = top; Vy = Math.Abs(Vy) * 0.5; }
        if (Y >= maxY)
        {
            Hit(Impact.Land, Vy);
            Y = maxY;
            Vy = Math.Abs(Vy) > RestSpeed ? -Math.Abs(Vy) * FloorBounce : 0;
        }
    }

    private void Jump(double room)
    {
        _restTime = 0;
        double height = Math.Clamp(Pick(_p.JumpHeightRandom, _p.JumpHeight, _p.JumpHeightMin, _p.JumpHeightMax),
            Motion.MinJumpHeight, Motion.MaxJumpHeight);
        height = Math.Min(height, Math.Max(10, room)); // 화면 위로 넘어가지 않게

        double deg, dir;
        if (_p.JumpAngleRandom)
        {
            double range = Math.Clamp(_p.JumpAngleRange, 0, Motion.MaxJumpAngle);
            deg = (_rng.NextDouble() * 2 - 1) * range; // −범위 ~ +범위
            dir = Math.Sign(deg);
            deg = Math.Abs(deg);
        }
        else
        {
            deg = Math.Min(Math.Abs(_p.JumpAngle), Motion.MaxJumpAngle);
            dir = _jumpDir;
        }
        double up = Math.Sqrt(2 * Gravity * height); // 최고점이 height가 되는 수직 속도
        Vy = -up;
        Vx = deg < 0.01 ? 0 : dir * up * Math.Tan(deg * Math.PI / 180);
        if (Vx != 0) FacingLeft = Vx < 0;
        Jumps++;
        ImpactSeq++;
        LastImpact = Impact.Jump;
        ImpactStrength = Math.Clamp(height / 300, 0.3, 1);
    }

    /// <summary>미끄러짐 자동 밀기: 정해진(또는 랜덤) 방향·힘으로 툭 민다.</summary>
    private void Push()
    {
        double deg = _p.PushDirRandom ? _rng.NextDouble() * 360 : _p.PushAngle;
        double force = Math.Clamp(Pick(_p.PushForceRandom, _p.PushForce, _p.PushForceMin, _p.PushForceMax),
            Motion.MinPushForce, Motion.MaxPushForce);
        double rad = deg * Math.PI / 180;
        Vx = Math.Cos(rad) * force;
        Vy = -Math.Sin(rad) * force; // 화면 좌표는 아래가 +
        if (Math.Abs(Vx) < 1e-6) Vx = 0;
        if (Math.Abs(Vy) < 1e-6) Vy = 0;
        if (Vx != 0) FacingLeft = Vx < 0;
        _restTime = 0;
        _nextWait = -1;
        Pushes++;
    }
}
