using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GifPin.UI;

/// <summary>
/// 시작 애니메이션 (하루에 한 번, A·B·C 중 하나). 투명한 창에 도형으로 그려 움직인다 — 그림 파일 없음.
///  A: 빈 창 아래에서 손이 먼저 올라오고 몸이 올라와 빼꼼 내다봄
///  B: 창 뒤에서 위 테두리를 두 손으로 잡고 캐릭터가 튀어나옴
///  C: 스티커처럼 화면에 착 붙고 모서리가 살짝 들림
/// 클릭하거나 키를 누르면 바로 넘어감. 끝나면 Finished.
/// </summary>
public sealed class SplashWindow : Window
{
    public enum Variant { A, B, C }

    private static readonly Color Navy = Color.FromRgb(0x1E, 0x27, 0x42);
    private static readonly Color Cream = Color.FromRgb(0xFB, 0xF6, 0xEA);
    private static readonly Color Mint = Color.FromRgb(0x63, 0xD6, 0xB6);
    private static readonly Color Teal = Color.FromRgb(0x2B, 0xB3, 0xA3);
    private static readonly Color Yellow = Color.FromRgb(0xF2, 0xC1, 0x4E);

    private const double W = 360, H = 300;
    private const double Total = 2.6; // 초
    private readonly Canvas _root = new() { Width = W, Height = H };
    private readonly Storyboard _story = new();
    private bool _done;

    public Variant Shown { get; }
    /// <summary>애니메이션이 끝나거나 건너뜀.</summary>
    public event Action? Finished;

    public SplashWindow(Variant variant)
    {
        Shown = variant;
        NameScope.SetNameScope(this, new NameScope());
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = W;
        Height = H;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _root;
        Cursor = Cursors.Hand;

        switch (variant)
        {
            case Variant.A: BuildA(); break;
            case Variant.B: BuildB(); break;
            default: BuildC(); break;
        }
        // 끝: 전체가 서서히 사라짐
        Fade(_root, 1, 0, Total - 0.35, 0.35);
        _story.Completed += (_, _) => Finish();

        Loaded += (_, _) => _story.Begin(this, true);
        MouseLeftButtonUp += (_, _) => Skip();
        KeyDown += (_, _) => Skip();
    }

    public static Variant RandomVariant() => (Variant)Random.Shared.Next(3);

    /// <summary>띄우고 끝나면 onDone을 부름.</summary>
    public static SplashWindow Play(Variant variant, Action onDone)
    {
        var w = new SplashWindow(variant);
        w.Finished += onDone;
        w.Show();
        return w;
    }

    private void Skip()
    {
        if (_done) return;
        _story.Stop(this);
        Finish();
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        Close();
        Finished?.Invoke();
    }

    // ── 장면 ─────────────────────────────────────────────────

    private const double WinX = 50, WinY = 70, WinW = 260, WinH = 180;

    /// <summary>A: 창 안 오른쪽 아래, 아래 테두리 밑에서 손 → 몸 순서로 올라와 빼꼼.</summary>
    private void BuildA()
    {
        AddWindowFrame(fill: true);
        var inside = new Canvas { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(WinX + 3, WinY + 3, WinW - 6, WinH - 6), 15, 15) };
        _root.Children.Add(inside);

        var (body, eyes) = Blob(112);
        Place(inside, body, 176, 150);
        var lift = Move(body, 0, 140);
        Animate(lift, TranslateTransform.YProperty, 140, 0, 0.55, 0.6, new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut });

        foreach (double hx in new[] { 182.0, 248.0 })
        {
            var hand = Hand();
            Place(inside, hand, hx, 222);
            Animate(Move(hand, 0, 46), TranslateTransform.YProperty, 46, 0, 0.15, 0.38, new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut });
        }
        AddWindowFrame(fill: false); // 테두리를 위에 한 번 더 → 아래쪽이 깔끔하게 가려짐
        Blink(eyes, 1.55);
        Sparkle(112, 150, 1.2);
    }

    /// <summary>B: 창 뒤에 숨어 있다가 위 테두리를 잡고 튀어나옴.</summary>
    private void BuildB()
    {
        var (body, eyes) = Blob(116);
        Place(_root, body, 122, 6); // 창보다 먼저 그려서 창 뒤에 있음
        Animate(Move(body, 0, 110), TranslateTransform.YProperty, 110, 0, 0.75, 0.42, new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut });
        AddWindowFrame(fill: true);

        // 손은 창 앞에 있지만 위 테두리 위쪽만 보이게 (아래는 창에 가려진 것처럼)
        var hands = new Canvas { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, WinY + 4)) };
        _root.Children.Add(hands);
        foreach (double hx in new[] { 132.0, 206.0 })
        {
            var hand = Hand();
            Place(hands, hand, hx, WinY - 12);
            Animate(Move(hand, 0, 26), TranslateTransform.YProperty, 26, 0, 0.2, 0.3, new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
        }
        // "짠!" 표시선
        for (int i = 0; i < 3; i++)
        {
            double a = -60 + i * 30;
            var line = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = -14, Stroke = new SolidColorBrush(Yellow), StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = new RotateTransform(a),
            };
            Place(_root, line, 268 + i * 9, 30 + Math.Abs(i - 1) * 8);
            line.Opacity = 0;
            Fade(line, 0, 1, 1.05, 0.12);
        }
        Blink(eyes, 1.7);
    }

    /// <summary>C: 스티커처럼 착 붙고 찌그러졌다 펴진 뒤 모서리가 들림, 한 손을 흔듦.</summary>
    private void BuildC()
    {
        var sticker = new Canvas { Width = 150, Height = 150 };
        // 스티커 흰 테두리 + 그림자
        var outline = new Path
        {
            Data = BlobGeometry(130, 120), Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = 22, StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.3, Color = Navy },
        };
        Place(sticker, outline, 10, 15);
        var (body, eyes) = Blob(130);
        Place(sticker, body, 10, 15);
        // 흔드는 손
        var wave = Hand();
        wave.RenderTransformOrigin = new Point(0.2, 0.9);
        var waveRot = new RotateTransform(0);
        wave.RenderTransform = waveRot;
        Place(sticker, wave, 128, 30);
        // 들리는 모서리
        var peel = new Path
        {
            Data = Geometry.Parse("M0,30 L30,0 L30,30 Z"), Fill = new LinearGradientBrush(Color.FromRgb(0xE6, 0xE8, 0xEE), Colors.White, 45),
            Stroke = new SolidColorBrush(Color.FromArgb(60, Navy.R, Navy.G, Navy.B)), StrokeThickness = 1,
            RenderTransformOrigin = new Point(1, 1),
        };
        var peelScale = new ScaleTransform(0, 0);
        peel.RenderTransform = peelScale;
        Place(sticker, peel, 118, 118);

        Place(_root, sticker, (W - 150) / 2, (H - 150) / 2);
        sticker.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1.8, 1.8);
        var rot = new RotateTransform(-20);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rot);
        sticker.RenderTransform = group;
        sticker.Opacity = 0;

        // 착! (커졌다가 빠르게 붙음) → 찌그러졌다 펴짐
        Fade(sticker, 0, 1, 0, 0.18);
        Keys(scale, ScaleTransform.ScaleXProperty, (0, 1.8), (0.3, 1.0), (0.38, 1.1), (0.5, 0.97), (0.6, 1.0));
        Keys(scale, ScaleTransform.ScaleYProperty, (0, 1.8), (0.3, 1.0), (0.38, 0.86), (0.5, 1.04), (0.6, 1.0));
        Animate(rot, RotateTransform.AngleProperty, -20, -6, 0, 0.3, new CubicEase { EasingMode = EasingMode.EaseIn });
        // 붙을 때 충격선
        for (int i = 0; i < 3; i++)
        {
            var line = new Line { X1 = 0, Y1 = 0, X2 = -16, Y2 = 0, Stroke = new SolidColorBrush(Navy), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = new RotateTransform(-30 + i * 30), Opacity = 0 };
            Place(_root, line, 92 - Math.Abs(i - 1) * 6, 112 + i * 26);
            Keys(line, OpacityProperty, (0.3, 0), (0.34, 1), (0.8, 1), (1.0, 0));
        }
        Animate(peelScale, ScaleTransform.ScaleXProperty, 0, 1, 0.95, 0.3, new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
        Animate(peelScale, ScaleTransform.ScaleYProperty, 0, 1, 0.95, 0.3, new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
        Keys(waveRot, RotateTransform.AngleProperty, (0.9, 0), (1.1, 18), (1.3, -10), (1.5, 18), (1.7, -10), (1.9, 0));
        Blink(eyes, 1.95);
        Sparkle(268, 78, 0.7);
    }

    // ── 그림 요소 ────────────────────────────────────────────

    private void AddWindowFrame(bool fill)
    {
        var frame = new Rectangle
        {
            Width = WinW, Height = WinH, RadiusX = 18, RadiusY = 18, StrokeThickness = 6,
            Stroke = new SolidColorBrush(Navy), Fill = fill ? new SolidColorBrush(Cream) : Brushes.Transparent,
        };
        Place(_root, frame, WinX, WinY);
        if (!fill) return;
        Place(_root, new Rectangle { Width = WinW - 6, Height = 5, Fill = new SolidColorBrush(Navy) }, WinX + 3, WinY + 34);
        var dots = new[] { Teal, Yellow, Navy };
        for (int i = 0; i < 3; i++)
            Place(_root, new Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(dots[i]) }, WinX + 18 + i * 18, WinY + 13);
        // 창 아래 옅은 그림자 (맨 뒤에)
        var shadow = new Ellipse { Width = WinW + 30, Height = 16, Fill = new SolidColorBrush(Color.FromArgb(40, Navy.R, Navy.G, Navy.B)) };
        Canvas.SetLeft(shadow, WinX - 15);
        Canvas.SetTop(shadow, WinY + WinH - 2);
        _root.Children.Insert(0, shadow);
    }

    /// <summary>민트 캐릭터 (반원 머리 + 물결 아래쪽). 돌려주는 eyes는 깜빡임용.</summary>
    private static (Canvas body, ScaleTransform eyes) Blob(double w)
    {
        double h = w * 0.92;
        var c = new Canvas { Width = w, Height = h };
        c.Children.Add(new Path { Data = BlobGeometry(w, h), Fill = new SolidColorBrush(Mint), Stroke = new SolidColorBrush(Navy), StrokeThickness = 5.5, StrokeLineJoin = PenLineJoin.Round });
        var shine = new Ellipse { Width = w * 0.17, Height = w * 0.1, Fill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), RenderTransform = new RotateTransform(-20) };
        Canvas.SetLeft(shine, w * 0.2);
        Canvas.SetTop(shine, h * 0.17);
        c.Children.Add(shine);
        var eyes = new ScaleTransform(1, 1);
        var eyeRow = new Canvas { Width = w, Height = h, RenderTransformOrigin = new Point(0.5, 0.42), RenderTransform = eyes };
        foreach (double ex in new[] { 0.36, 0.6 })
        {
            var e = new Ellipse { Width = w * 0.075, Height = w * 0.11, Fill = new SolidColorBrush(Navy) };
            Canvas.SetLeft(e, w * ex);
            Canvas.SetTop(e, h * 0.36);
            eyeRow.Children.Add(e);
        }
        c.Children.Add(eyeRow);
        return (c, eyes);
    }

    private static Geometry BlobGeometry(double w, double h) => Geometry.Parse(FormattableString.Invariant(
        $"M0,{h * 0.5} A{w / 2},{h * 0.5} 0 0 1 {w},{h * 0.5} L{w},{h * 0.88} Q{w * 0.83},{h * 1.0} {w * 0.66},{h * 0.88} Q{w * 0.5},{h * 1.0} {w * 0.33},{h * 0.88} Q{w * 0.17},{h * 1.0} 0,{h * 0.88} Z"));

    private static Ellipse Hand() => new()
    {
        Width = 30, Height = 24, Fill = new SolidColorBrush(Mint), Stroke = new SolidColorBrush(Navy), StrokeThickness = 4.5,
    };

    private void Sparkle(double x, double y, double at)
    {
        var star = new Path
        {
            Data = Geometry.Parse("M0,-16 C2,-4 4,-2 16,0 C4,2 2,4 0,16 C-2,4 -4,2 -16,0 C-4,-2 -2,-4 0,-16 Z"),
            Fill = new SolidColorBrush(Yellow),
        };
        var s = new ScaleTransform(0, 0);
        var r = new RotateTransform(-40);
        var g = new TransformGroup();
        g.Children.Add(s);
        g.Children.Add(r);
        star.RenderTransform = g;
        Place(_root, star, x, y);
        Keys(s, ScaleTransform.ScaleXProperty, (at, 0), (at + 0.18, 1.25), (at + 0.32, 1.0));
        Keys(s, ScaleTransform.ScaleYProperty, (at, 0), (at + 0.18, 1.25), (at + 0.32, 1.0));
        Animate(r, RotateTransform.AngleProperty, -40, 0, at, 0.32, null);
    }

    private void Blink(ScaleTransform eyes, double at) =>
        Keys(eyes, ScaleTransform.ScaleYProperty, (at, 1), (at + 0.07, 0.1), (at + 0.15, 1));

    // ── 애니메이션 도우미 ────────────────────────────────────

    private static void Place(Canvas parent, UIElement child, double x, double y)
    {
        Canvas.SetLeft(child, x);
        Canvas.SetTop(child, y);
        parent.Children.Add(child);
    }

    private static TranslateTransform Move(UIElement e, double x, double y)
    {
        var t = new TranslateTransform(x, y);
        e.RenderTransform = t;
        return t;
    }

    private void Animate(DependencyObject target, DependencyProperty prop, double from, double to, double begin, double duration, IEasingFunction? ease)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(duration)) { BeginTime = TimeSpan.FromSeconds(begin), EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        Add(a, target, prop);
        target.SetValue(prop, from); // 시작 전 모습
    }

    private void Keys(DependencyObject target, DependencyProperty prop, params (double t, double v)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        foreach (var (t, v) in keys) a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        Add(a, target, prop);
        target.SetValue(prop, keys[0].v);
    }

    private void Fade(UIElement e, double from, double to, double begin, double duration) =>
        Animate(e, OpacityProperty, from, to, begin, duration, null);

    private int _names;

    /// <summary>스토리보드는 이름으로 대상을 찾으므로 각 대상에 이름을 붙여 등록.</summary>
    private void Add(Timeline a, DependencyObject target, DependencyProperty prop)
    {
        string name = "t" + (_names++);
        RegisterName(name, target);
        Storyboard.SetTargetName(a, name);
        Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        _story.Children.Add(a);
    }
}
