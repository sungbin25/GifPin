using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GifPin.UI;

internal static class SplashPreview
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var variant in new[] { SplashWindow.Variant.A, SplashWindow.Variant.B, SplashWindow.Variant.C })
        {
            int finished = 0;
            var window = new SplashWindow(variant) { ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Finished += () => finished++;
            window.Show(); window.UpdateLayout();
            foreach (double t in new[] { .25, .84, 1.3, 1.85, 2.3 })
            {
                window.SeekPreview(t); window.UpdateLayout();
                var pixels = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                pixels.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                using var file = File.Create(Path.Combine(output, $"splash-{variant}-{t:0.00}.png")); encoder.Save(file);
                if (t == 1.85)
                {
                    var data = new byte[pixels.PixelWidth * pixels.PixelHeight * 4]; pixels.CopyPixels(data, pixels.PixelWidth * 4, 0);
                    int title = 0, character = 0;
                    for (int y = 0; y < pixels.PixelHeight; y++)
                    for (int x = 0; x < pixels.PixelWidth; x++)
                        if (data[(y * pixels.PixelWidth + x) * 4 + 3] > 100) { if (y < 190) title++; else character++; }
                    if (title < 1000 || character < 1000) throw new Exception("Title or character missing: " + variant);
                }
            }
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (finished != 1 || window.IsVisible) throw new Exception("Splash skip failed: " + variant);
        }
        // Check completion on the actual shared rendering/storyboard clock, without loading user state.
        int completions = 0;
        var timed = new SplashWindow(SplashWindow.Variant.A) { ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        timed.Finished += () => { completions++; frame.Continue = false; };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timed.Show(); timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
        if (completions != 1 || timed.IsVisible || watch.Elapsed.TotalSeconds < 2.3) { timed.Close(); throw new Exception("Splash timed completion failed"); }
        Console.WriteLine("PASS: embedded title assets, all three layouts, click skip and timed shared-clock completion");
    }
}
