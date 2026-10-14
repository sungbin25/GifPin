using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>효과 레이어 상수·검증.</summary>
public static class Fx
{
    // 파티클 모양
    public const string Sparkle = "sparkle", Heart = "heart", Star = "star", Bubble = "bubble", Dot = "dot", Image = "image";
    public const int MaxParticleImages = 12;
    /// <summary>파티클 이미지는 이 크기(긴 변)로 줄여서 들고 있음 → 메모리·CPU 절약</summary>
    public const int ParticleSpriteSize = 96;
    // 파티클이 나오는 때
    public const string Always = "always", Moving = "moving", Click = "click";
    // 파티클 움직임
    public const string Rise = "rise", Fall = "fall", Burst = "burst";
    public const string Rainbow = "rainbow";

    public static readonly string[] Kinds = { Sparkle, Heart, Star, Bubble, Dot, Image };

    /// <summary>파티클 이미지로 고른 사진 id 목록.</summary>
    public static string[] ParticleImageIds(ActiveItem a) =>
        (a.FxParticleImages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>내 이미지 파티클을 실제로 쓰는 중 (이미지를 불러와야 함).</summary>
    public static bool UsesParticleImages(ActiveItem a) => a.FxParticles && HasKind(a, Image) && ParticleImageIds(a).Length > 0;

    /// <summary>
    /// 고른 파티클 모양들 (여러 개 가능, 쉼표로 구분). 여러 개면 같은 비율로 섞여 나온다.
    /// 예: "heart,star" → 하트 50% · 별 50%. "내 이미지"도 한 모양으로 셈 (그 안에서 이미지를 랜덤으로).
    /// </summary>
    public static string[] ParticleKinds(ActiveItem a) => ParseKinds(a.FxParticleKind);

    public static bool HasKind(ActiveItem a, string kind) => Array.IndexOf(ParticleKinds(a), kind) >= 0;

    private static string? _kindsKey;
    private static string[] _kindsCache = { Sparkle };
    private static readonly object KindsGate = new();

    private static string[] ParseKinds(string? value)
    {
        lock (KindsGate)
        {
            if (value == _kindsKey) return _kindsCache;
            var list = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => Array.IndexOf(Kinds, k) >= 0).Distinct().ToArray();
            _kindsKey = value;
            _kindsCache = list.Length > 0 ? list : new[] { Sparkle };
            return _kindsCache;
        }
    }
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
        a.FxSquashAmount = Math.Clamp(a.FxSquashAmount, 5, 100);
        a.FxTrailCount = Math.Round(Math.Clamp(a.FxTrailCount, 2, 10));
        a.FxTrailOpacity = Math.Clamp(a.FxTrailOpacity, 10, 80);
        a.FxTrailSpacing = Math.Clamp(a.FxTrailSpacing, 4, 120);
        a.FxParticleImages = string.Join(",", ParticleImageIds(a).Distinct().Take(MaxParticleImages));
        a.FxShadowOpacity = Math.Clamp(a.FxShadowOpacity, 10, 90);
        a.FxShadowSize = Math.Clamp(a.FxShadowSize, 30, 150);
        a.FxGlowSize = Math.Clamp(a.FxGlowSize, 2, 40);
        a.FxGlowStrength = Math.Clamp(a.FxGlowStrength, 10, MaxGlowStrength);
        if (!TryParseColor(a.FxGlowColor, out _)) a.FxGlowColor = "#FFD54F";
        a.FxParticleKind = string.Join(",", ParticleKinds(a));
        a.FxParticleMode = Pick(a.FxParticleMode, Modes, Always);
        a.FxParticleStyle = Pick(a.FxParticleStyle, Styles, Rise);
        a.FxParticleRate = Math.Clamp(a.FxParticleRate, 1, 60);
        a.FxParticleSize = Math.Clamp(a.FxParticleSize, 4, 48);
        a.FxParticleLife = Math.Clamp(a.FxParticleLife, 0.5, 5);
        a.FxParticleColor = NormalizeColorList(a.FxParticleColor);
    }

    /// <summary>외곽 빛 세기 최대 (%): 100을 넘으면 빛을 더 진하고 넓게 (안쪽 빛 한 겹 + 진하기 증폭).</summary>
    public const double MaxGlowStrength = 300;
    /// <summary>파티클 색으로 고를 수 있는 최대 개수.</summary>
    public const int MaxParticleColors = 16;

    /// <summary>파티클 색: "rainbow" 또는 "#RRGGBB,#RRGGBB,…" (여러 개면 파티클마다 그중 하나). 틀린 값은 빼고, 비면 rainbow.</summary>
    public static string NormalizeColorList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Rainbow) return Rainbow;
        var list = ParticleColors(value);
        return list.Length == 0 ? Rainbow : string.Join(",", list);
    }

    /// <summary>파티클 색 목록 ("#RRGGBB" 대문자, 중복 없음). rainbow면 빈 목록.</summary>
    public static string[] ParticleColors(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Rainbow) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (TryParseColor(part, out var c))
            {
                string hex = $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
                if (!result.Contains(hex)) result.Add(hex);
                if (result.Count >= MaxParticleColors) break;
            }
        return result.ToArray();
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
    /// <summary>GIF 파티클은 파티클마다 애니메이션을 돌리므로 더 적게</summary>
    public const int MaxAnimatedParticles = 80;
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
        public bool Local;        // "따라오기": 좌표가 GIF 기준 (GIF와 함께 움직임). 아니면 화면 좌표
        public int Sprite;        // 내 이미지 파티클: 몇 번째 이미지
        public double AnimOffset; // GIF 파티클: 재생 시작 위치 (ms) — 파티클마다 다르게
    }

    /// <summary>파티클 이미지 하나 (프레임별 누적 시간 포함).</summary>
    private sealed record Sprite(GifAnimation Anim, int[] Ends, int Total);
    private readonly List<Sprite> _sprites = new();
    private readonly Dictionary<uint, SKColorFilter> _tints = new();
    private readonly SKPaint _imagePaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };

    private readonly List<Particle> _particles = new();
    private readonly List<(double x, double y)> _trail = new();
    private readonly Random _rng;
    private readonly double _phase;

    private double _emitAcc, _trailAcc;
    private double _lastX = double.NaN, _lastY;
    private int _seenImpact = -1;
    private double _squashT = 99, _squashStrength;
    private MotionState.Impact _squashKind;

    private readonly SKPaint _shadowPaint = new() { IsAntialias = true };
    private readonly SKPaint _particlePaint = new() { IsAntialias = true };
    private readonly SKPaint _ghostPaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };

    public double Time { get; private set; }
    /// <summary>절약 모드: 새 파티클을 만들지 않음 (이미 날던 것은 자연스럽게 사라짐).</summary>
    public bool SuppressParticles { get; set; }
    public double Speed { get; private set; }
    public int ParticleCount => _particles.Count;
    /// <summary>점검용: 지금 날고 있는 파티클 색들.</summary>
    public List<uint> ParticleColorValues() => _particles.Select(p => (uint)p.Color).ToList();
    /// <summary>이 창이 가질 수 있는 파티클 수 (모든 GIF를 합친 상한에서 엔진이 매 프레임 나눠 줌).</summary>
    public int ParticleBudget { get; set; } = int.MaxValue;
    public int Squashes { get; private set; }
    public int DustPuffs { get; private set; }
    public int TrailCount => _trail.Count;

    /// <summary>날고 있는 파티클·착지 먼지·잔상을 바로 지움 (효과를 끄거나 멈출 때 그 자리에 얼어붙어 남지 않게).</summary>
    public void ClearTransient()
    {
        _particles.Clear();
        _trail.Clear();
    }
    public IReadOnlyList<(double x, double y)> Trail => _trail;
    public int SpriteCount => _sprites.Count;
    internal int CountKind(string kind) { int n = 0; foreach (var p in _particles) if (p.Kind == kind) n++; return n; }

    /// <summary>점검용: i번째 파티클 좌표와 기준 (따라오기면 GIF 기준).</summary>
    internal (double x, double y, bool local) ParticleAt(int i) => (_particles[i].X, _particles[i].Y, _particles[i].Local);
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
        || (a.FxParticles && a.FxParticleMode != Fx.Click && !SuppressParticles)
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
        if (a.FxTrail) All(Math.Min(400, a.FxTrailCount * a.FxTrailSpacing + 20));
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

        // 이번 프레임 사이에 GIF가 지나온 길 (파티클을 길 위에 고르게 만들기 위해)
        double prevX = double.IsNaN(_lastX) ? x : _lastX, prevY = double.IsNaN(_lastX) ? y : _lastY;

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
                if (a.FxDust && m.LastImpact == MotionState.Impact.Land && m.ImpactStrength >= 0.375) SpawnDust(x, y, w, h, m.ImpactStrength);
            }
        }
        _squashT += dt;

        // 잔상: 움직이는 동안 "간격"만큼 갈 때마다 하나씩 쌓고, 멈추면 오래된 것부터 지움
        if (a.FxTrail && Speed > 40)
        {
            _trailAcc = 0;
            double gap = double.MaxValue;
            if (_trail.Count > 0)
            {
                var last = _trail[^1];
                gap = Math.Sqrt((x - last.x) * (x - last.x) + (y - last.y) * (y - last.y));
            }
            if (gap >= a.FxTrailSpacing)
            {
                _trail.Add((x, y));
                while (_trail.Count > (int)a.FxTrailCount) _trail.RemoveAt(0);
            }
        }
        else if (_trail.Count > 0)
        {
            _trailAcc += dt;
            if (_trailAcc >= TrailStep) { _trailAcc = 0; _trail.RemoveAt(0); }
        }

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
        // 파티클 만들기: 이번 프레임 사이에 생겼어야 할 개수를 GIF가 지나온 길 위에 고르게 나눠 놓고,
        // 그 사이 흐른 시간만큼 미리 진행시킴 → 빠르게 움직여도 도장 찍힌 듯 끊기지 않음
        if (a.FxParticles && !SuppressParticles)
        {
            double rate = a.FxParticleMode switch
            {
                Fx.Always => a.FxParticleRate,
                Fx.Moving => Speed > 30 ? a.FxParticleRate * Math.Min(2, 0.5 + Speed / 400) : 0,
                _ => 0,
            };
            _emitAcc += rate * dt;
            int n = (int)_emitAcc;
            _emitAcc -= n;
            for (int i = 0; i < n; i++)
            {
                double t = (i + 1.0) / n; // 1 = 지금
                // 따라오기면 GIF에 붙어 있으니 지금 자리 기준, 아니면 지나온 길 위
                double sx = a.FxParticleFollow ? x : prevX + (x - prevX) * t;
                double sy = a.FxParticleFollow ? y : prevY + (y - prevY) * t;
                Spawn(a, sx, sy, w, h, a.FxParticleStyle, (1 - t) * dt, x, y);
            }
        }
        else _emitAcc = 0;
    }

    /// <summary>클릭했을 때 한꺼번에 터뜨림.</summary>
    public void Burst(ActiveItem a, double x, double y, int w, int h)
    {
        if (SuppressParticles) return;
        int n = (int)Math.Clamp(a.FxParticleRate * 1.5, 8, 40);
        for (int i = 0; i < n; i++) Spawn(a, x, y, w, h, Fx.Burst, 0, x, y);
    }

    private string? _colorListKey;
    private SKColor[] _colorList = Array.Empty<SKColor>();

    private SKColor ParticleColor(ActiveItem a)
    {
        if (a.FxParticleColor != Fx.Rainbow)
        {
            // 고른 색들 중 하나씩 (내가 고른 색으로 만드는 무지개)
            if (_colorListKey != a.FxParticleColor)
            {
                _colorListKey = a.FxParticleColor;
                _colorList = Fx.ParticleColors(a.FxParticleColor).Select(h => { Fx.TryParseColor(h, out var c); return c; }).ToArray();
            }
            if (_colorList.Length == 1) return _colorList[0];
            if (_colorList.Length > 1) return _colorList[_rng.Next(_colorList.Length)];
        }
        return SKColor.FromHsv(_rng.Next(24) * 15f, 65, 100); // 24가지 색 (물들이기 필터를 재사용하려고)
    }

    /// <summary>
    /// 파티클 하나. (x, y) = 생기는 순간의 GIF 위치, preAge = 이미 흐른 시간,
    /// (curX, curY) = 지금 GIF 위치 ("따라오기"면 이 기준의 상대 좌표로 저장).
    /// </summary>
    private void Spawn(ActiveItem a, double x, double y, int w, int h, string style, double preAge, double curX, double curY)
    {
        // 여러 모양을 골랐으면 같은 확률로 하나를 뽑음
        var kinds = Fx.ParticleKinds(a);
        string kind = kinds.Length == 1 ? kinds[0] : kinds[_rng.Next(kinds.Length)];
        bool image = kind == Fx.Image && _sprites.Count > 0;
        int cap = a.FxParticleAnimate && _sprites.Count > 0 && Array.IndexOf(kinds, Fx.Image) >= 0 ? MaxAnimatedParticles : MaxParticles;
        if (_particles.Count >= Math.Min(cap, ParticleBudget)) return;
        var p = new Particle
        {
            Life = a.FxParticleLife * Rnd(0.75, 1.25),
            Size = a.FxParticleSize * Rnd(0.7, 1.3),
            Rot = a.FxParticleRotate ? Rnd(0, 360) : 0,
            VRot = !a.FxParticleRotate ? 0 : kind is Fx.Sparkle or Fx.Star ? Rnd(-120, 120) : Rnd(-40, 40),
            Seed = Rnd(0, 10),
            Color = ParticleColor(a),
            // 이미지를 아직 불러오는 중이면 반짝이로 대신
            Kind = kind == Fx.Image && !image ? Fx.Sparkle : kind,
            Style = style,
        };
        if (image)
        {
            p.Sprite = _rng.Next(_sprites.Count);
            p.AnimOffset = Rnd(0, Math.Max(1, _sprites[p.Sprite].Total));
        }
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
        // 생긴 뒤 흐른 시간만큼 미리 이동
        if (preAge > 0)
        {
            p.Age = preAge;
            p.X += p.Vx * preAge;
            p.Y += p.Vy * preAge;
            p.Rot += p.VRot * preAge;
        }
        if (a.FxParticleFollow)
        {
            p.Local = true;
            p.X -= curX;
            p.Y -= curY;
        }
        _particles.Add(p);
    }

    private void SpawnDust(double x, double y, int w, int h, double strength)
    {
        DustPuffs++;
        int n = 6 + (int)(strength * 6);
        for (int i = 0; i < n && _particles.Count < Math.Min(MaxParticles, ParticleBudget); i++)
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

    /// <summary>내 이미지 파티클에 쓸 그림들 (주인은 OverlayWindow — 여기서는 해제하지 않음).</summary>
    public void SetSprites(IReadOnlyList<GifAnimation>? sprites)
    {
        _sprites.Clear();
        if (sprites != null)
            foreach (var anim in sprites)
            {
                if (anim.Frames.Count == 0) continue;
                var ends = new int[anim.Frames.Count];
                int t = 0;
                for (int i = 0; i < ends.Length; i++) { t += Math.Max(10, i < anim.DelaysMs.Count ? anim.DelaysMs[i] : 100); ends[i] = t; }
                _sprites.Add(new Sprite(anim, ends, t));
            }
        // 바뀐 이미지 목록과 맞지 않는 파티클은 정리
        _particles.RemoveAll(p => p.Kind == Fx.Image && p.Sprite >= _sprites.Count);
    }

    /// <summary>GIF 파티클: ms 시점의 프레임 번호.</summary>
    internal int SpriteFrameAt(int sprite, double ms)
    {
        var sp = _sprites[sprite];
        if (sp.Anim.Frames.Count < 2) return 0;
        int t = (int)(ms % sp.Total);
        int i = Array.BinarySearch(sp.Ends, t + 1);
        if (i < 0) i = ~i;
        return Math.Min(i, sp.Ends.Length - 1);
    }

    /// <summary>미리 곱한 알파(premultiplied) 그림의 진하기를 k배 (255에서 멈춤). 색 비율은 그대로.</summary>
    private static unsafe void AmplifyAlpha(SKBitmap bmp, float k)
    {
        byte* p = (byte*)bmp.GetPixels();
        int n = bmp.Width * bmp.Height;
        for (int i = 0; i < n; i++, p += 4)
        {
            int a = p[3];
            if (a == 0) continue;
            int na = Math.Min(255, (int)(a * k + 0.5f));
            if (na == a) continue;
            float s = (float)na / a;
            p[0] = (byte)Math.Min(na, (int)(p[0] * s + 0.5f));
            p[1] = (byte)Math.Min(na, (int)(p[1] * s + 0.5f));
            p[2] = (byte)Math.Min(na, (int)(p[2] * s + 0.5f));
            p[3] = (byte)na;
        }
    }

    private SKColorFilter Tint(SKColor color)
    {
        uint key = (uint)color.WithAlpha(255);
        if (!_tints.TryGetValue(key, out var f))
        {
            f = SKColorFilter.CreateBlendMode(color.WithAlpha(255), SKBlendMode.SrcIn); // 모양은 그대로, 색만 바꿈
            _tints[key] = f;
        }
        return f;
    }

    // ── 그리기 ──────────────────────────────────────────────

    private double Wave(double period) => Math.Sin((Time + _phase) * Math.PI * 2 / Math.Max(0.1, period));

    /// <summary>둥실 효과로 위로 뜬 거리 (그림자 계산용, px).</summary>
    private double FloatOffset(ActiveItem a) => a.FxFloat ? -a.FxFloatAmp * Wave(a.FxFloatPeriod) : 0;

    /// <summary>찌그러짐 정도: 감쇠 진동 (0이면 원래 모양).</summary>
    private double SquashValue(ActiveItem a) =>
        !a.FxSquash || !SquashActive ? 0
        : Math.Min(0.8, _squashStrength * a.FxSquashAmount / 100) * Math.Exp(-7 * _squashT) * Math.Cos(22 * _squashT);
    // 100%에서도 납작해져 사라지지 않게 최대 80%까지만 찌그러짐 (늘어날 때는 최대 1.8배)

    /// <summary>GIF 뒤에 그리는 것: 바닥 그림자, 잔상.</summary>
    /// <summary>창 가장자리에 가까울수록 투명하게 (여백 끝에서 뚝 잘려 보이지 않게).</summary>
    private static double EdgeFade(float px, float py, SKRect bounds, double margin)
    {
        double d = Math.Min(Math.Min(px - bounds.Left, bounds.Right - px), Math.Min(py - bounds.Top, bounds.Bottom - py));
        return Math.Clamp(d / Math.Max(1, margin), 0, 1);
    }

    /// <summary>GIF 뒤에 그리는 것 (그림자, 잔상). 실제로 그린 영역을 돌려줌 (바뀐 부분만 화면에 보내기 위해).</summary>
    public SKRect DrawBehind(SKCanvas c, ActiveItem a, SKRect content, double originX, double originY,
        SKBitmap frame, bool flip, double groundDistance, SKRect bounds)
    {
        var drawn = SKRect.Empty;
        if (a.FxShadow)
        {
            var sh = ShadowShape(a, content, groundDistance, FloatOffset(a));
            float rx = sh.rx, ry = sh.ry, cy = sh.cy;
            _shadowPaint.Color = new SKColor(0, 0, 0, sh.alpha);
            float sigma = Math.Max(1.5f, ry * 0.6f);
            _shadowPaint.MaskFilter = ShadowBlur(sigma);
            c.DrawOval(content.MidX, cy, rx, ry, _shadowPaint);
            Union(ref drawn, new SKRect(content.MidX - rx, cy - ry, content.MidX + rx, cy + ry), sigma * 3);
        }

        if (a.FxTrail && _trail.Count > 0)
        {
            int n = _trail.Count;
            for (int i = 0; i < n; i++)
            {
                var (gx, gy) = _trail[i];
                var r = SKRect.Create((float)(gx - originX), (float)(gy - originY), content.Width, content.Height);
                double fade = a.FxTrailOpacity / 100 * (i + 1) / (n + 1)
                              * EdgeFade(r.MidX, r.MidY, bounds, Math.Max(r.Width, r.Height) * 0.5);
                if (fade <= 0.003) continue;
                _ghostPaint.Color = new SKColor(255, 255, 255, (byte)(255 * fade));
                c.Save();
                if (flip) c.Scale(-1, 1, r.MidX, 0);
                c.DrawBitmap(frame, r, _ghostPaint);
                c.Restore();
                Union(ref drawn, r, 1);
            }
        }
        return drawn;
    }

    /// <summary>
    /// 바닥 그림자 모양. 그림자는 땅(둥실둥실이면 가장 낮게 내려온 높이)에 고정되고,
    /// GIF가 올라갈수록(점프 높이·둥실둥실) 작고 옅어지며 내려오면 커지고 진해진다.
    /// </summary>
    internal static (float cy, float rx, float ry, byte alpha) ShadowShape(ActiveItem a, SKRect content, double groundDistance, double floatOffset)
    {
        double dist = Math.Clamp(groundDistance, 0, 400);
        double near = 1 - dist / 400;
        // 둥실둥실: 0 = 가장 낮음, 1 = 가장 높음
        double up = a.FxFloat && a.FxFloatAmp > 0 ? Math.Clamp((a.FxFloatAmp - floatOffset) / (2 * a.FxFloatAmp), 0, 1) : 0;
        double size = (0.4 + 0.6 * near) * (1 - 0.35 * up);
        double fade = (0.35 + 0.65 * near) * (1 - 0.45 * up);
        float rx = (float)(content.Width / 2 * a.FxShadowSize / 100 * size);
        float ry = Math.Max(3, rx * 0.2f);
        float ground = (float)(content.Bottom + dist + (a.FxFloat ? a.FxFloatAmp : 0));
        byte alpha = (byte)Math.Round(255 * Math.Clamp(a.FxShadowOpacity / 100 * fade, 0, 1));
        // 발밑에 살짝 겹치도록 올리는 양은 가장 큰 그림자 기준으로 고정 → 크기가 변해도 그림자 위치는 그대로
        float lift = Math.Max(3, (float)(content.Width / 2 * a.FxShadowSize / 100) * 0.2f) * 0.3f;
        return (ground - lift, rx, ry, alpha);
    }

    private static void Union(ref SKRect acc, SKRect r, float inflate)
    {
        r.Inflate(inflate, inflate);
        acc = acc.IsEmpty ? r : SKRect.Union(acc, r);
    }

    // 그림자 흐림 필터: 크기를 0.5px 단위로 묶어 재사용 (매 프레임 새로 만들지 않음)
    private readonly Dictionary<int, SKMaskFilter> _shadowBlurs = new();
    private SKMaskFilter ShadowBlur(float sigma)
    {
        int q = (int)Math.Round(sigma * 2);
        if (!_shadowBlurs.TryGetValue(q, out var f))
        {
            if (_shadowBlurs.Count > 64) { foreach (var old in _shadowBlurs.Values) old.Dispose(); _shadowBlurs.Clear(); }
            f = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, q / 2f);
            _shadowBlurs[q] = f;
        }
        return f;
    }

    // ── 외곽 빛 캐시 ──
    // 빛(흐림)은 비싼 계산이라 GIF 장면·크기·색이 같으면 한 번 만든 것을 재사용한다.
    // 두근두근(맥동)과 깜빡임은 그릴 때 투명도만 바꾼다.
    private const long GlowCacheBudget = 48L * 1024 * 1024;
    private readonly Dictionary<(int frame, int w, int h, uint color, int sigma), SKBitmap> _glows = new();
    private readonly LinkedList<(int frame, int w, int h, uint color, int sigma)> _glowOrder = new();
    private long _glowBytes;
    public int GlowBuilds { get; private set; }
    public int GlowHits { get; private set; }

    /// <summary>외곽 빛 그림 (frameIndex 장면, w×h 크기). margin = 빛이 GIF 밖으로 퍼지는 폭.</summary>
    public (SKBitmap bmp, float margin, byte alpha)? GlowLayer(ActiveItem a, SKBitmap frame, int frameIndex, int w, int h)
    {
        if (!a.FxGlow) return null;
        Fx.TryParseColor(a.FxGlowColor, out var col);
        double strength = a.FxGlowStrength / 100;
        // 100% 넘는 만큼은 빛 그림 자체를 진하게 (캐시), 두근두근은 투명도로만 → 세게 해도 맥동이 보임
        double boost = Math.Max(1, strength);
        int boostKey = (int)Math.Round(boost * 10);
        double level = Math.Min(1, strength);
        if (a.FxGlowPulse) level *= 0.55 + 0.45 * (0.5 + 0.5 * Wave(1.6));
        byte alpha = (byte)Math.Round(255 * Math.Clamp(level, 0, 1));
        int sigma = (int)Math.Round(a.FxGlowSize);
        int margin = sigma * 3;
        var key = (frameIndex, w, h, (uint)col.WithAlpha(255), sigma * 1000 + boostKey);
        if (_glows.TryGetValue(key, out var bmp))
        {
            GlowHits++;
            _glowOrder.Remove(key);
            _glowOrder.AddFirst(key);
            return (bmp, margin, alpha);
        }

        long bytes = (long)(w + 2 * margin) * (h + 2 * margin) * 4;
        while (_glowOrder.Count > 0 && _glowBytes + bytes > GlowCacheBudget)
        {
            var oldest = _glowOrder.Last!.Value;
            _glowOrder.RemoveLast();
            if (_glows.Remove(oldest, out var old)) { _glowBytes -= (long)old.Width * old.Height * 4; old.Dispose(); }
        }
        bmp = new SKBitmap(new SKImageInfo(w + 2 * margin, h + 2 * margin, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Transparent);
        using (var cv = new SKCanvas(bmp))
        using (var glow = SKImageFilter.CreateDropShadowOnly(0, 0, sigma, sigma, col.WithAlpha(255)))
        using (var paint = new SKPaint { ImageFilter = glow, FilterQuality = SKFilterQuality.Medium, IsAntialias = true })
        {
            cv.DrawBitmap(frame, SKRect.Create(margin, margin, w, h), paint);
            if (boost > 1.01)
            {
                // 안쪽에 촘촘한 빛을 한 겹 더 (윤곽 가까이가 확실히 밝아짐)
                float inner = Math.Max(1.5f, sigma * 0.35f);
                using var core = SKImageFilter.CreateDropShadowOnly(0, 0, inner, inner, col.WithAlpha((byte)Math.Round(255 * Math.Min(1, boost - 1))));
                using var corePaint = new SKPaint { ImageFilter = core, FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
                cv.DrawBitmap(frame, SKRect.Create(margin, margin, w, h), corePaint);
            }
            cv.Flush();
        }
        if (boost > 1.01) AmplifyAlpha(bmp, (float)boost); // 흐린 가장자리까지 진하게
        GlowBuilds++;
        _glows[key] = bmp;
        _glowOrder.AddFirst(key);
        _glowBytes += bytes;
        return (bmp, margin, alpha);
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

    /// <summary>GIF 앞에 그리는 것: 파티클.</summary>
    public SKRect DrawFront(SKCanvas c, ActiveItem a, SKRect content, double originX, double originY, SKRect bounds)
    {
        var drawn = SKRect.Empty;
        foreach (var p in _particles)
        {
            double t = p.Age / p.Life;
            double alpha = t < 0.15 ? t / 0.15 : t > 0.6 ? (1 - t) / 0.4 : 1;
            // 따라오기 파티클은 GIF 기준, 나머지는 화면 기준 좌표
            float px = p.Local ? content.Left + (float)p.X : (float)(p.X - originX);
            float py = p.Local ? content.Top + (float)p.Y : (float)(p.Y - originY);
            float r = (float)(p.Size / 2);
            alpha *= EdgeFade(px, py, bounds, Math.Max(10, p.Size * 0.75));
            if (alpha <= 0.003) continue;
            byte alphaByte = (byte)Math.Round(255 * Math.Clamp(alpha, 0, 1) * (p.Dust ? 0.7 : 1));
            _particlePaint.Color = p.Color.WithAlpha(alphaByte);
            float reach = p.Kind == Fx.Image ? r * 1.5f : r * 1.2f; // 회전해도 들어가는 반경
            Union(ref drawn, new SKRect(px - reach, py - reach, px + reach, py + reach), 2);
            switch (p.Kind)
            {
                case Fx.Image when p.Sprite < _sprites.Count:
                {
                    var sp = _sprites[p.Sprite];
                    int fi = a.FxParticleAnimate ? SpriteFrameAt(p.Sprite, p.Age * 1000 + p.AnimOffset) : 0;
                    var bmp = sp.Anim.Frames[fi];
                    float k = (float)p.Size / Math.Max(1, Math.Max(bmp.Width, bmp.Height));
                    float hw = bmp.Width * k / 2, hh = bmp.Height * k / 2;
                    _imagePaint.Color = new SKColor(255, 255, 255, alphaByte);
                    _imagePaint.ColorFilter = a.FxParticleKeepColors ? null : Tint(p.Color);
                    c.Save();
                    c.Translate(px, py);
                    if (p.Rot != 0) c.RotateDegrees((float)p.Rot);
                    c.DrawBitmap(bmp, new SKRect(-hw, -hh, hw, hh), _imagePaint);
                    c.Restore();
                    break;
                }
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
        return drawn;
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
        _shadowPaint.MaskFilter = null;
        foreach (var f in _shadowBlurs.Values) f.Dispose();
        _shadowBlurs.Clear();
        foreach (var b in _glows.Values) b.Dispose();
        _glows.Clear();
        _glowOrder.Clear();
        _shadowPaint.Dispose();
        _particlePaint.Dispose();
        _ghostPaint.Dispose();
        _imagePaint.ColorFilter = null;
        _imagePaint.Dispose();
        foreach (var f in _tints.Values) f.Dispose();
        _tints.Clear();
    }
}
