using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>효과 레이어 상수·검증.</summary>
public static class Fx
{
    // 파티클 모양
    public const string Sparkle = "sparkle", Heart = "heart", Star = "star", Bubble = "bubble", Dot = "dot";
    // 파티클이 나오는 때
    public const string Always = "always", Moving = "moving", Click = "click";
    // 파티클 움직임
    public const string Rise = "rise", Fall = "fall", Burst = "burst";
    public const string Rainbow = "rainbow";

    public static readonly string[] Kinds = { Sparkle, Heart, Star, Bubble, Dot };
    public static readonly string[] Modes = { Always, Moving, Click };
    public static readonly string[] Styles = { Rise, Fall, Burst };

    /// <summary>색 견본 (발광·파티클 공용).</summary>
    public static readonly string[] Swatches = { "#FFFFFF", "#FFD54F", "#FF8A80", "#F48FB1", "#CE93D8", "#81D4FA", "#A5D6A7", "#FFAB40" };

    private static string Pick(string? v, string[] allowed, string fallback) => v != null && Array.IndexOf(allowed, v) >= 0 ? v : fallback;

    public static bool TryParseColor(string? hex, out SKColor color)
    {
        color = SKColors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        color = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>범위를 벗어난 값을 바로잡는다 (UI·팩·저장 파일 공통).</summary>
    public static void Normalize(ActiveItem a)
    {
        a.FxFloatAmp = Math.Clamp(a.FxFloatAmp, 2, 60);
        a.FxFloatPeriod = Math.Clamp(a.FxFloatPeriod, 0.5, 10);
        a.FxBreatheAmount = Math.Clamp(a.FxBreatheAmount, 1, 30);
        a.FxBreathePeriod = Math.Clamp(a.FxBreathePeriod, 0.5, 10);
        a.FxSwayAngle = Math.Clamp(a.FxSwayAngle, 1, 45);
        a.FxSwayPeriod = Math.Clamp(a.FxSwayPeriod, 0.5, 10);
        a.FxSpinSpeed = Math.Clamp(a.FxSpinSpeed, 10, 720);
        a.FxBlinkMin = Math.Clamp(a.FxBlinkMin, 0, 90);
        a.FxBlinkPeriod = Math.Clamp(a.FxBlinkPeriod, 0.3, 10);
        a.FxSquashAmount = Math.Clamp(a.FxSquashAmount, 5, 50);
        a.FxTrailCount = Math.Round(Math.Clamp(a.FxTrailCount, 2, 10));
        a.FxTrailOpacity = Math.Clamp(a.FxTrailOpacity, 10, 80);
        a.FxShadowOpacity = Math.Clamp(a.FxShadowOpacity, 10, 90);
        a.FxShadowSize = Math.Clamp(a.FxShadowSize, 30, 150);
        a.FxGlowSize = Math.Clamp(a.FxGlowSize, 2, 40);
        a.FxGlowStrength = Math.Clamp(a.FxGlowStrength, 10, 100);
        if (!TryParseColor(a.FxGlowColor, out _)) a.FxGlowColor = "#FFD54F";
        a.FxParticleKind = Pick(a.FxParticleKind, Kinds, Sparkle);
        a.FxParticleMode = Pick(a.FxParticleMode, Modes, Always);
        a.FxParticleStyle = Pick(a.FxParticleStyle, Styles, Rise);
        a.FxParticleRate = Math.Clamp(a.FxParticleRate, 1, 60);
        a.FxParticleSize = Math.Clamp(a.FxParticleSize, 4, 48);
        a.FxParticleLife = Math.Clamp(a.FxParticleLife, 0.5, 5);
        if (a.FxParticleColor != Rainbow && !TryParseColor(a.FxParticleColor, out _)) a.FxParticleColor = Rainbow;
    }

    /// <summary>켜진 효과가 하나라도 있음.</summary>
    public static bool Any(ActiveItem a) =>
        a.FxFloat || a.FxBreathe || a.FxSway || a.FxSpin || a.FxBlink || a.FxSquash || a.FxDust
        || a.FxTrail || a.FxShadow || a.FxGlow || a.FxParticles;
}

/// <summary>창 가장자리 여백 (효과가 잘리지 않게 GIF 둘레에 남기는 공간).</summary>
internal readonly record struct Pads(int Left, int Top, int Right, int Bottom)
{
    public static readonly Pads Zero = new(0, 0, 0, 0);
    public static Pads Uniform(int p) => new(p, p, p, p);
}

/// <summary>
/// GIF 하나의 효과 상태 (OverlayEngine 스레드 전용).
/// 변형(둥실·숨쉬기·흔들림·회전·찌그러짐), 깜빡임, 발광, 그림자, 잔상, 파티클을 계산하고 그린다.
/// 파티클·잔상 위치는 화면 좌표로 들고 있어서 GIF가 움직여도 제자리에 남는다.
/// </summary>
internal sealed class EffectState : IDisposable
{
    public const int MaxParticles = 300;
    private const int MaxPad = 420;
    private const double TrailStep = 0.035;   // 잔상 간격 (초)
    private const double SquashTime = 0.6;    // 찌그러짐이 풀리는 시간 (초)

    private struct Particle
    {
        public double X, Y, Vx, Vy, Age, Life, Size, Rot, VRot, Seed;
        public SKColor Color;
        public string Kind;
        public string Style;
        public bool Dust;
    }

    private readonly List<Particle> _particles = new();
    private readonly List<(double x, double y)> _trail = new();
    private readonly Random _rng;
    private readonly double _phase;

    private double _emitAcc, _trailAcc;
    private double _lastX = double.NaN, _lastY;
    private int _seenImpact = -1;
    private double _squashT = 99, _squashStrength;
    private MotionState.Impact _squashKind;

    private SKImageFilter? _glowFilter;
    private (uint color, float sigma) _glowKey;
    private readonly SKPaint _shadowPaint = new() { IsAntialias = true };
    private readonly SKPaint _particlePaint = new() { IsAntialias = true };
    private readonly SKPaint _ghostPaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };

    public double Time { get; private set; }
    public double Speed { get; private set; }
    public int ParticleCount => _particles.Count;
    public int Squashes { get; private set; }
    public int DustPuffs { get; private set; }
    public int TrailCount => _trail.Count;
    public bool SquashActive => _squashT < SquashTime;

    public EffectState(int? seed = null)
    {
        _rng = seed is int s ? new Random(s) : new Random();
        _phase = _rng.NextDouble() * 10; // 여러 GIF가 똑같이 움직이지 않게
    }

    private double Rnd(double min, double max) => min + _rng.NextDouble() * (max - min);

    /// <summary>계속 다시 그려야 하는 효과가 있음 (타이머가 필요).</summary>
    public bool IsAnimated(ActiveItem a, bool motionActive) =>
        a.FxFloat || a.FxBreathe || a.FxSway || a.FxSpin || a.FxBlink || (a.FxGlow && a.FxGlowPulse)
        || (a.FxParticles && a.FxParticleMode != Fx.Click)
        || _particles.Count > 0 || _trail.Count > 0 || SquashActive
        || (motionActive && (a.FxSquash || a.FxTrail || a.FxShadow || a.FxDust || a.FxParticles));

    /// <summary>효과에 필요한 여백 (설정이 같으면 늘 같은 값 → 창 크기가 매 프레임 바뀌지 않음).</summary>
    public static Pads ComputePads(ActiveItem a, int w, int h)
    {
        if (!Fx.Any(a)) return Pads.Zero;
        double l = 0, t = 0, r = 0, b = 0;
        void All(double v) { l += v; t += v; r += v; b += v; }

        if (a.FxFloat) { t += a.FxFloatAmp; b += a.FxFloatAmp; }

        double grow = 1;
        if (a.FxBreathe) grow *= 1 + a.FxBreatheAmount / 100;
        if (a.FxSquash) grow *= 1 + a.FxSquashAmount / 100;
        double gw = w * grow, gh = h * grow;
        if (a.FxSpin)
        {
            double d = Math.Sqrt(gw * gw + gh * gh);
            All(Math.Max((d - w) / 2, (d - h) / 2));
        }
        else
        {
            double ex = (gw - w) / 2, ey = gh - h; // 찌그러짐은 바닥 기준이라 위로 늘어남
            if (a.FxSway)
            {
                double th = a.FxSwayAngle * Math.PI / 180;
                ex += gh * Math.Sin(th);            // 바닥을 축으로 기울어짐
                ey += gw / 2 * Math.Sin(th);
            }
            l += ex; r += ex; t += ey; b += (gh - h) / 2;
        }

        if (a.FxGlow) All(a.FxGlowSize * 3);
        if (a.FxTrail) All(110);
        if (a.FxDust) { l += 50; r += 50; b += 16; t += 10; }
        if (a.FxShadow)
        {
            string mode = Motion.Normalize(a.MotionMode);
            b += 24 + (mode == Motion.Fall ? 260 : mode == Motion.Walk ? 60 : 0);
        }
        if (a.FxParticles)
        {
            double travel = Math.Min(240, a.FxParticleSize + (a.FxParticleStyle == Fx.Burst ? 110 : 60) * a.FxParticleLife);
            double side = a.FxParticleSize * 1.5 + 20;
            switch (a.FxParticleStyle)
            {
                case Fx.Rise: t += travel; l += side; r += side; b += a.FxParticleSize; break;
                case Fx.Fall: b += travel; l += side; r += side; t += a.FxParticleSize * 2; break;
                default: All(travel); break;
            }
            if (a.FxParticleMode == Fx.Click) All(a.FxParticleSize + 110 * a.FxParticleLife * 0.6);
        }

        static int C(double v) => (int)Math.Min(MaxPad, Math.Ceiling(v) + 2);
        return new Pads(C(l), C(t), C(r), C(b));
    }

    /// <summary>dt초 진행. (x, y) = GIF 내용의 화면 좌표, m = 움직임 상태(없으면 null).</summary>
    public void Advance(double dt, ActiveItem a, double x, double y, int w, int h, MotionState? m)
    {
        dt = Math.Clamp(dt, 0, 0.1);
        Time += dt;

        // 속도 (잔상·파티클용, 부드럽게)
        if (!double.IsNaN(_lastX) && dt > 0)
        {
            double inst = Math.Sqrt((x - _lastX) * (x - _lastX) + (y - _lastY) * (y - _lastY)) / dt;
            Speed = Speed * 0.6 + inst * 0.4;
        }
        _lastX = x;
        _lastY = y;

        // 충돌 → 찌그러짐 · 착지 먼지
        if (m != null && m.ImpactSeq != _seenImpact)
        {
            bool first = _seenImpact < 0;
            _seenImpact = m.ImpactSeq;
            if (!first)
            {
                if (a.FxSquash)
                {
                    _squashT = 0;
                    _squashKind = m.LastImpact;
                    _squashStrength = m.ImpactStrength;
                    Squashes++;
                }
                if (a.FxDust && m.LastImpact == MotionState.Impact.Land && m.ImpactStrength >= 0.2) SpawnDust(x, y, w, h, m.ImpactStrength);
            }
        }
        _squashT += dt;

        // 잔상: 움직이는 동안 위치를 쌓고, 멈추면 하나씩 지움
        _trailAcc += dt;
        if (_trailAcc >= TrailStep)
        {
            _trailAcc = 0;
            if (a.FxTrail && Speed > 40)
            {
                _trail.Add((x, y));
                while (_trail.Count > (int)a.FxTrailCount) _trail.RemoveAt(0);
            }
            else if (_trail.Count > 0) _trail.RemoveAt(0);
        }

        // 파티클 만들기
        if (a.FxParticles)
        {
            double rate = a.FxParticleMode switch
            {
                Fx.Always => a.FxParticleRate,
                Fx.Moving => Speed > 30 ? a.FxParticleRate * Math.Min(2, 0.5 + Speed / 400) : 0,
                _ => 0,
            };
            _emitAcc += rate * dt;
            while (_emitAcc >= 1)
            {
                _emitAcc -= 1;
                Spawn(a, x, y, w, h, a.FxParticleStyle);
            }
        }
        else _emitAcc = 0;

        // 파티클 움직이기
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _particles.RemoveAt(i); continue; }
            if (p.Dust) { p.Vy += 260 * dt; p.Vx *= 1 - 2.5 * dt; }
            else if (p.Style == Fx.Burst) { double k = Math.Max(0, 1 - 1.6 * dt); p.Vx *= k; p.Vy *= k; }
            else p.X += Math.Sin(p.Age * 3 + p.Seed) * 10 * dt; // 하늘하늘
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Rot += p.VRot * dt;
            _particles[i] = p;
        }
    }

    /// <summary>클릭했을 때 한꺼번에 터뜨림.</summary>
    public void Burst(ActiveItem a, double x, double y, int w, int h)
    {
        int n = (int)Math.Clamp(a.FxParticleRate * 1.5, 8, 40);
        for (int i = 0; i < n; i++) Spawn(a, x, y, w, h, Fx.Burst);
    }

    private SKColor ParticleColor(ActiveItem a)
    {
        if (a.FxParticleColor != Fx.Rainbow && Fx.TryParseColor(a.FxParticleColor, out var c)) return c;
        return SKColor.FromHsv((float)Rnd(0, 360), 65, 100);
    }

    private void Spawn(ActiveItem a, double x, double y, int w, int h, string style)
    {
        if (_particles.Count >= MaxParticles) return;
        var p = new Particle
        {
            Life = a.FxParticleLife * Rnd(0.75, 1.25),
            Size = a.FxParticleSize * Rnd(0.7, 1.3),
            Rot = Rnd(0, 360),
            VRot = a.FxParticleKind is Fx.Sparkle or Fx.Star ? Rnd(-120, 120) : Rnd(-20, 20),
            Seed = Rnd(0, 10),
            Color = ParticleColor(a),
            Kind = a.FxParticleKind,
            Style = style,
        };
        switch (style)
        {
            case Fx.Rise:
                p.X = x + Rnd(0.1, 0.9) * w; p.Y = y + Rnd(0.2, 0.8) * h;
                p.Vx = Rnd(-15, 15); p.Vy = -Rnd(30, 70);
                break;
            case Fx.Fall:
                p.X = x + Rnd(-0.1, 1.1) * w; p.Y = y - p.Size;
                p.Vx = Rnd(-10, 10); p.Vy = Rnd(30, 70);
                break;
            default:
                double ang = Rnd(0, Math.PI * 2), sp = Rnd(60, 170);
                p.X = x + w / 2.0 + Math.Cos(ang) * w * 0.15; p.Y = y + h / 2.0 + Math.Sin(ang) * h * 0.15;
                p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp;
                break;
        }
        _particles.Add(p);
    }

    private void SpawnDust(double x, double y, int w, int h, double strength)
    {
        DustPuffs++;
        int n = 6 + (int)(strength * 6);
        for (int i = 0; i < n && _particles.Count < MaxParticles; i++)
        {
            int side = i % 2 == 0 ? -1 : 1;
            _particles.Add(new Particle
            {
                X = x + w / 2.0 + side * Rnd(0.05, 0.3) * w,
                Y = y + h - 2,
                Vx = side * Rnd(40, 130) * (0.6 + strength),
                Vy = -Rnd(15, 60),
                Life = Rnd(0.35, 0.6),
                Size = Rnd(5, 10) * (0.7 + strength * 0.6),
                Color = new SKColor(225, 222, 215),
                Kind = Fx.Dot,
                Style = Fx.Burst,
                Dust = true,
            });
        }
    }

    // ── 그리기 ──────────────────────────────────────────────

    private double Wave(double period) => Math.Sin((Time + _phase) * Math.PI * 2 / Math.Max(0.1, period));

    /// <summary>둥실 효과로 위로 뜬 거리 (그림자 계산용, px).</summary>
    private double FloatOffset(ActiveItem a) => a.FxFloat ? -a.FxFloatAmp * Wave(a.FxFloatPeriod) : 0;

    /// <summary>찌그러짐 정도: 감쇠 진동 (0이면 원래 모양).</summary>
    private double SquashValue(ActiveItem a) =>
        !a.FxSquash || !SquashActive ? 0
        : _squashStrength * a.FxSquashAmount / 100 * Math.Exp(-7 * _squashT) * Math.Cos(22 * _squashT);

    /// <summary>GIF 뒤에 그리는 것: 바닥 그림자, 잔상.</summary>
    public void DrawBehind(SKCanvas c, ActiveItem a, SKRect content, double originX, double originY,
        SKBitmap frame, bool flip, double groundDistance)
    {
        if (a.FxShadow)
        {
            double dist = Math.Clamp(groundDistance - FloatOffset(a), 0, 400);
            double near = 1 - dist / 400;
            float rx = (float)(content.Width / 2 * a.FxShadowSize / 100 * (0.4 + 0.6 * near));
            float ry = Math.Max(3, rx * 0.2f);
            float cy = (float)(content.Bottom + dist) - ry * 0.3f;
            _shadowPaint.Color = new SKColor(0, 0, 0, (byte)(255 * a.FxShadowOpacity / 100 * (0.35 + 0.65 * near)));
            _shadowPaint.MaskFilter?.Dispose();
            _shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.5f, ry * 0.6f));
            c.DrawOval(content.MidX, cy, rx, ry, _shadowPaint);
        }

        if (a.FxTrail && _trail.Count > 0)
        {
            int n = _trail.Count;
            for (int i = 0; i < n; i++)
            {
                var (gx, gy) = _trail[i];
                double fade = a.FxTrailOpacity / 100 * (i + 1) / (n + 1);
                var r = SKRect.Create((float)(gx - originX), (float)(gy - originY), content.Width, content.Height);
                _ghostPaint.Color = new SKColor(255, 255, 255, (byte)(255 * fade));
                c.Save();
                if (flip) c.Scale(-1, 1, r.MidX, 0);
                c.DrawBitmap(frame, r, _ghostPaint);
                c.Restore();
            }
        }
    }

    /// <summary>GIF 자체에 적용할 변형 (둥실·찌그러짐·숨쉬기·회전/흔들림).</summary>
    public void ApplyTransform(SKCanvas c, ActiveItem a, SKRect content)
    {
        float cx = content.MidX, cy = content.MidY;
        if (a.FxFloat) c.Translate(0, (float)FloatOffset(a));

        double v = SquashValue(a);
        if (v != 0)
        {
            float f = (float)v;
            switch (_squashKind)
            {
                case MotionState.Impact.Jump: c.Scale(1 - 0.4f * f, 1 + f, cx, content.Bottom); break;
                case MotionState.Impact.Ceiling: c.Scale(1 + 0.6f * f, 1 - f, cx, content.Top); break;
                case MotionState.Impact.WallLeft: c.Scale(1 - f, 1 + 0.6f * f, content.Left, cy); break;
                case MotionState.Impact.WallRight: c.Scale(1 - f, 1 + 0.6f * f, content.Right, cy); break;
                default: c.Scale(1 + 0.6f * f, 1 - f, cx, content.Bottom); break;
            }
        }

        if (a.FxBreathe)
        {
            float s = (float)(1 + a.FxBreatheAmount / 100 * (0.5 - 0.5 * Math.Cos((Time + _phase) * Math.PI * 2 / a.FxBreathePeriod)));
            c.Scale(s, s, cx, cy);
        }

        if (a.FxSpin)
            c.RotateDegrees((float)((a.FxSpinClockwise ? 1 : -1) * a.FxSpinSpeed * (Time + _phase) % 360), cx, cy);
        else if (a.FxSway)
            c.RotateDegrees((float)(a.FxSwayAngle * Wave(a.FxSwayPeriod)), cx, content.Bottom);
    }

    /// <summary>깜빡임 투명도 (0~255).</summary>
    public byte FrameAlpha(ActiveItem a)
    {
        if (!a.FxBlink) return 255;
        double min = a.FxBlinkMin / 100;
        double k = 0.5 + 0.5 * Math.Cos((Time + _phase) * Math.PI * 2 / a.FxBlinkPeriod);
        return (byte)Math.Round(255 * (min + (1 - min) * k));
    }

    /// <summary>외곽 발광 필터 (없으면 null). 같은 설정이면 다시 만들지 않음.</summary>
    public SKImageFilter? GlowFilter(ActiveItem a)
    {
        if (!a.FxGlow) return null;
        Fx.TryParseColor(a.FxGlowColor, out var col);
        double strength = a.FxGlowStrength / 100;
        if (a.FxGlowPulse) strength *= 0.55 + 0.45 * (0.5 + 0.5 * Wave(1.6));
        var color = col.WithAlpha((byte)Math.Round(255 * Math.Clamp(strength, 0, 1)));
        float sigma = (float)a.FxGlowSize;
        var key = ((uint)color, sigma);
        if (_glowFilter == null || key != _glowKey)
        {
            _glowFilter?.Dispose();
            _glowFilter = SKImageFilter.CreateDropShadow(0, 0, sigma, sigma, color);
            _glowKey = key;
        }
        return _glowFilter;
    }

    /// <summary>GIF 앞에 그리는 것: 파티클.</summary>
    public void DrawFront(SKCanvas c, double originX, double originY)
    {
        foreach (var p in _particles)
        {
            double t = p.Age / p.Life;
            double alpha = t < 0.15 ? t / 0.15 : t > 0.6 ? (1 - t) / 0.4 : 1;
            _particlePaint.Color = p.Color.WithAlpha((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1) * (p.Dust ? 0.7 : 1)));
            float px = (float)(p.X - originX), py = (float)(p.Y - originY), r = (float)(p.Size / 2);
            switch (p.Kind)
            {
                case Fx.Dot:
                    _particlePaint.Style = SKPaintStyle.Fill;
                    c.DrawCircle(px, py, r, _particlePaint);
                    break;
                case Fx.Bubble:
                    _particlePaint.Style = SKPaintStyle.Stroke;
                    _particlePaint.StrokeWidth = Math.Max(1, r * 0.18f);
                    c.DrawCircle(px, py, r, _particlePaint);
                    _particlePaint.Style = SKPaintStyle.Fill;
                    c.DrawCircle(px - r * 0.35f, py - r * 0.35f, r * 0.18f, _particlePaint);
                    break;
                default:
                    _particlePaint.Style = SKPaintStyle.Fill;
                    c.Save();
                    c.Translate(px, py);
                    c.RotateDegrees((float)p.Rot);
                    c.Scale(r, r);
                    c.DrawPath(p.Kind == Fx.Heart ? HeartPath : p.Kind == Fx.Star ? StarPath : SparklePath, _particlePaint);
                    c.Restore();
                    break;
            }
        }
    }

    // 반지름 1 기준 모양 (그릴 때 크기만큼 늘림)
    private static readonly SKPath SparklePath = MakeSparkle();
    private static readonly SKPath StarPath = MakeStar();
    private static readonly SKPath HeartPath = MakeHeart();

    private static SKPath MakeSparkle()
    {
        var p = new SKPath();
        p.MoveTo(0, -1); p.LineTo(0.22f, -0.22f); p.LineTo(1, 0); p.LineTo(0.22f, 0.22f);
        p.LineTo(0, 1); p.LineTo(-0.22f, 0.22f); p.LineTo(-1, 0); p.LineTo(-0.22f, -0.22f);
        p.Close();
        return p;
    }

    private static SKPath MakeStar()
    {
        var p = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5;
            float rad = i % 2 == 0 ? 1f : 0.45f;
            float x = (float)(Math.Cos(ang) * rad), y = (float)(Math.Sin(ang) * rad);
            if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
        }
        p.Close();
        return p;
    }

    private static SKPath MakeHeart()
    {
        var p = new SKPath();
        p.MoveTo(0, 0.85f);
        p.CubicTo(-0.25f, 0.6f, -1f, 0.15f, -1f, -0.35f);
        p.CubicTo(-1f, -0.85f, -0.35f, -1.05f, 0f, -0.55f);
        p.CubicTo(0.35f, -1.05f, 1f, -0.85f, 1f, -0.35f);
        p.CubicTo(1f, 0.15f, 0.25f, 0.6f, 0, 0.85f);
        p.Close();
        return p;
    }

    public void Dispose()
    {
        _glowFilter?.Dispose();
        _shadowPaint.MaskFilter?.Dispose();
        _shadowPaint.Dispose();
        _particlePaint.Dispose();
        _ghostPaint.Dispose();
    }
}
