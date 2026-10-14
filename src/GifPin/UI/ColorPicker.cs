using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using GifPin.Core;

namespace GifPin.UI;

/// <summary>
/// 색 고르기 창: 팔레트(72색) · 최근 색 · 색상/채도/밝기 · #코드 입력 · 스포이드.
/// 모달이 아닌 창으로 띄우고 결과는 Task로 받는다 (스포이드를 쓰는 동안 창을 숨겼다 다시 보여야 해서).
/// </summary>
public sealed class ColorPicker : Window
{
    private readonly TaskCompletionSource<Color?> _result = new();
    private readonly Border _preview;
    private readonly TextBox _hexBox;
    private readonly Slider _hue, _sat, _val;
    private readonly IReadOnlyList<string> _recent;
    private Color _color;
    private bool _syncing;

    private Brush R(string key) => (Brush)FindResource(key);

    private ColorPicker(Color initial, IReadOnlyList<string> recent)
    {
        _color = initial;
        _recent = recent;
        Title = Loc.T("color.title");
        Width = 404;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(16) };

        // 팔레트: 12가지 색상 × 5단계 밝기 + 무채색 12단계
        root.Children.Add(Label(Loc.T("color.palette")));
        var grid = new UniformGrid { Columns = 12, Margin = new Thickness(0, 4, 0, 0) };
        (double s, double v)[] tones = { (0.30, 1.0), (0.55, 1.0), (0.85, 1.0), (0.90, 0.78), (0.90, 0.52) };
        foreach (var (s, v) in tones)
            for (int i = 0; i < 12; i++) grid.Children.Add(Swatch(FromHsv(i * 30, s, v)));
        for (int i = 0; i < 12; i++)
        {
            byte g = (byte)Math.Round(255 * (1 - i / 11.0));
            grid.Children.Add(Swatch(Color.FromRgb(g, g, g)));
        }
        root.Children.Add(grid);

        if (_recent.Count > 0)
        {
            root.Children.Add(Label(Loc.T("color.recent"), top: 10));
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var hex in _recent) if (TryParse(hex, out var c)) row.Children.Add(Swatch(c));
            root.Children.Add(row);
        }

        // 색상 / 채도 / 밝기
        _hue = Slider(0, 360);
        _sat = Slider(0, 100);
        _val = Slider(0, 100);
        var hueTrack = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) hueTrack.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        root.Children.Add(SliderRow(Loc.T("color.hue"), _hue, hueTrack));
        root.Children.Add(SliderRow(Loc.T("color.saturation"), _sat, null));
        root.Children.Add(SliderRow(Loc.T("color.brightness"), _val, null));
        _hue.ValueChanged += (_, _) => FromSliders();
        _sat.ValueChanged += (_, _) => FromSliders();
        _val.ValueChanged += (_, _) => FromSliders();

        // 미리보기 + 코드 + 스포이드
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _preview = new Border { Width = 44, Height = 32, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        _preview.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        DockPanel.SetDock(_preview, Dock.Left);
        bottom.Children.Add(_preview);
        var picker = new Button { Height = 32, Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc.T("color.eyedropperTip") };
        picker.Content = IconText("", Loc.T("color.eyedropper"));
        picker.Click += async (_, _) => await UseEyedropper();
        DockPanel.SetDock(picker, Dock.Right);
        bottom.Children.Add(picker);
        _hexBox = new TextBox { Height = 32, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Consolas"), MaxLength = 7, VerticalContentAlignment = VerticalAlignment.Center };
        _hexBox.TextChanged += (_, _) =>
        {
            if (_syncing || !TryParse(_hexBox.Text, out var c)) return;
            SetColor(c, fromHex: true);
        };
        bottom.Children.Add(_hexBox);
        root.Children.Add(bottom);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Loc.T("color.ok"), Height = 32, MinWidth = 84, IsDefault = true, Style = (Style)FindResource("BtnPrimary") };
        var cancel = new Button { Content = Loc.T("common.cancel"), Height = 32, MinWidth = 84, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { _result.TrySetResult(_color); Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Closed += (_, _) => _result.TrySetResult(null);
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        SetColor(initial, fromHex: false);
    }

    /// <summary>색 고르기 창을 띄우고, 고른 색(취소면 null)을 돌려줌.</summary>
    public static Task<Color?> PickAsync(Window? owner, Color initial, IReadOnlyList<string> recent)
    {
        var w = new ColorPicker(initial, recent);
        if (owner != null && owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        return w._result.Task;
    }

    /// <summary>
    /// 스포이드만 바로 쓰기: GifPin 창을 잠시 내리고(아래에 있는 것을 고를 수 있게) 화면에서 색을 고른 뒤 되돌림.
    /// </summary>
    public static async Task<Color?> PickFromScreenAsync(Window? hide)
    {
        var state = hide?.WindowState ?? WindowState.Normal;
        bool wasVisible = hide?.IsVisible == true;
        if (wasVisible) hide!.WindowState = WindowState.Minimized;
        await Task.Delay(250); // 창이 내려가는 애니메이션이 끝나고 찍히도록
        Color? c;
        try { c = ScreenEyedropper.Pick(); }
        catch (Exception ex)
        {
            Log.Warn("Eyedropper failed", ex);
            c = null;
        }
        if (wasVisible)
        {
            hide!.WindowState = state;
            hide.Activate();
        }
        return c;
    }

    private async Task UseEyedropper()
    {
        var owner = Owner;
        Hide();
        var c = await PickFromScreenAsync(owner);
        Show();
        Activate();
        if (c is Color picked) SetColor(picked, fromHex: false);
    }

    // ── 색 바꾸기 ────────────────────────────────────────────

    private void SetColor(Color c, bool fromHex)
    {
        _color = Color.FromRgb(c.R, c.G, c.B);
        _syncing = true;
        var (h, s, v) = ToHsv(_color);
        _hue.Value = h;
        _sat.Value = s * 100;
        _val.Value = v * 100;
        if (!fromHex) _hexBox.Text = ToHex(_color);
        _syncing = false;
        _preview.Background = new SolidColorBrush(_color);
    }

    private void FromSliders()
    {
        if (_syncing) return;
        _color = FromHsv(_hue.Value, _sat.Value / 100, _val.Value / 100);
        _syncing = true;
        _hexBox.Text = ToHex(_color);
        _syncing = false;
        _preview.Background = new SolidColorBrush(_color);
    }

    // ── 화면 요소 ────────────────────────────────────────────

    private TextBlock Label(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return t;
    }

    private Button Swatch(Color c)
    {
        var b = new Button
        {
            Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(1), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, ToolTip = ToHex(c),
            Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), BorderThickness = new Thickness(1) },
        };
        b.Click += (_, _) => SetColor(c, fromHex: false);
        b.MouseDoubleClick += (_, _) => { SetColor(c, fromHex: false); _result.TrySetResult(_color); Close(); };
        return b;
    }

    private static Slider Slider(double min, double max) => new() { Minimum = min, Maximum = max, Margin = new Thickness(0, 2, 0, 0) };

    private FrameworkElement SliderRow(string label, Slider slider, Brush? track)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Label(label));
        if (track != null)
            panel.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = track, Margin = new Thickness(6, 6, 6, 0) });
        panel.Children.Add(slider);
        return panel;
    }

    private static StackPanel IconText(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        p.Children.Add(icon);
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    // ── 색 변환 ──────────────────────────────────────────────

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string h = text.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// <summary>점검용: 창을 띄웠다 닫기.</summary>
    internal static void SmokeOpenClose()
    {
        var w = new ColorPicker(Colors.Orange, new[] { "#FF0000" });
        w.Show();
        w.UpdateLayout();
        w.Close();
    }
}
