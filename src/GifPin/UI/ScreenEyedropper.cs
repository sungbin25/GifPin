using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GifPin.Core;

namespace GifPin.UI;

/// <summary>
/// 스포이드: 화면 전체를 한 번 찍어 둔 뒤, 그 위를 덮는 거의 투명한 창에서 마우스 아래 픽셀 색을 고른다.
/// 확대경(15×15 픽셀을 8배)과 색 코드를 마우스 옆에 보여 주고, 클릭하면 그 색, Esc·오른쪽 클릭이면 취소.
/// 여러 모니터·배율이 섞여 있어도 실제 픽셀 좌표(GetCursorPos)로 찍어 둔 그림에서 읽으므로 정확하다.
/// </summary>
public sealed class ScreenEyedropper : Window
{
    private const int Zoom = 8, Span = 15;
    private readonly byte[] _pixels;
    private readonly int _vx, _vy, _vw, _vh;
    private readonly BitmapSource _shot;
    private readonly Image _lens;
    private readonly Border _swatch, _panel;
    private readonly TextBlock _hex;
    private readonly Canvas _canvas;
    public Color? Result { get; private set; }

    private ScreenEyedropper(byte[] pixels, BitmapSource shot, int vx, int vy, int vw, int vh)
    {
        _pixels = pixels;
        _shot = shot;
        (_vx, _vy, _vw, _vh) = (vx, vy, vw, vh);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // 거의 투명하지만 마우스는 받음
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        Left = 0; Top = 0; Width = 100; Height = 100; // 실제 위치·크기는 SourceInitialized에서 픽셀 단위로

        _lens = new Image { Width = Span * Zoom, Height = Span * Zoom, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_lens, BitmapScalingMode.NearestNeighbor);
        var cross = new Border
        {
            Width = Zoom + 2, Height = Zoom + 2, BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) },
        };
        _swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        _hex = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 4) };
        info.Children.Add(_swatch);
        info.Children.Add(_hex);
        var lensBox = new Grid { ClipToBounds = true };
        lensBox.Children.Add(_lens);
        lensBox.Children.Add(cross);
        var stack = new StackPanel();
        stack.Children.Add(new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Margin = new Thickness(6, 6, 6, 0), Child = lensBox });
        stack.Children.Add(info);
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("color.eyedropperHint"), Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD8)), FontSize = 11,
            Margin = new Thickness(6, 0, 6, 6), MaxWidth = Span * Zoom, TextWrapping = TextWrapping.Wrap,
        });
        _panel = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 15, 17, 21)), CornerRadius = new CornerRadius(8), Child = stack, IsHitTestVisible = false };
        _canvas = new Canvas();
        _canvas.Children.Add(_panel);
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _vx, _vy, _vw, _vh, 0x0040 /* SWP_SHOWWINDOW */);
        };
        Loaded += (_, _) => { Activate(); Focus(); UpdateAt(); };
        MouseMove += (_, _) => UpdateAt();
        MouseLeftButtonUp += (_, _) => { Result = ColorAtCursor(); Close(); };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>화면에서 색 하나 고르기 (취소하면 null). 부르기 전에 가리고 싶은 창은 미리 숨길 것.</summary>
    public static Color? Pick()
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return null;
        byte[] pixels;
        BitmapSource source;
        using (var bmp = new System.Drawing.Bitmap(vw, vh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(vx, vy, 0, 0, new System.Drawing.Size(vw, vh), System.Drawing.CopyPixelOperation.SourceCopy);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, vw, vh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                pixels = new byte[data.Stride * vh];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                source = BitmapSource.Create(vw, vh, 96, 96, PixelFormats.Bgra32, null, pixels, data.Stride);
                source.Freeze();
            }
            finally { bmp.UnlockBits(data); }
        }
        var w = new ScreenEyedropper(pixels, source, vx, vy, vw, vh);
        w.ShowDialog();
        return w.Result;
    }

    private Color ColorAt(int px, int py)
    {
        px = Math.Clamp(px, 0, _vw - 1);
        py = Math.Clamp(py, 0, _vh - 1);
        int i = (py * _vw + px) * 4;
        return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    private (int px, int py) CursorPixel()
    {
        Native.GetCursorPos(out var p);
        return (p.X - _vx, p.Y - _vy);
    }

    private Color ColorAtCursor()
    {
        var (px, py) = CursorPixel();
        return ColorAt(px, py);
    }

    private void UpdateAt()
    {
        var (px, py) = CursorPixel();
        var c = ColorAt(px, py);
        _swatch.Background = new SolidColorBrush(c);
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        int half = Span / 2;
        int x0 = Math.Clamp(px - half, 0, Math.Max(0, _vw - Span)), y0 = Math.Clamp(py - half, 0, Math.Max(0, _vh - Span));
        _lens.Source = new CroppedBitmap(_shot, new Int32Rect(x0, y0, Math.Min(Span, _vw), Math.Min(Span, _vh)));
        // 확대경은 마우스 오른쪽 아래 (화면 끝이면 반대쪽)
        var pos = Mouse.GetPosition(this);
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double pw = _panel.DesiredSize.Width, ph = _panel.DesiredSize.Height;
        double x = pos.X + 24, y = pos.Y + 24;
        if (x + pw > ActualWidth) x = pos.X - 24 - pw;
        if (y + ph > ActualHeight) y = pos.Y - 24 - ph;
        Canvas.SetLeft(_panel, Math.Max(0, x));
        Canvas.SetTop(_panel, Math.Max(0, y));
    }
}
