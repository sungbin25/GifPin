using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GifPin.Core;
using GifPin.UI;
using SkiaSharp;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--splash-preview") { SplashPreview.Run(Path.GetFullPath(args[1])); return 0; }
            if (args.Length != 2) throw new ArgumentException("Usage: GifPin.VideoTests <FFmpeg directory> <test output directory>");
            string tools = args[0] == "--download-tools" ? VideoTools.EnsureAsync(null, default).GetAwaiter().GetResult() : Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            CollisionTests.Run();
            CollisionTests.Engine(Path.GetFullPath("tests/sample.gif")).GetAwaiter().GetResult();
            RunAsync(tools, output).GetAwaiter().GetResult();
            Ui(tools, output);
            SplashPreview.Run(output);
            Console.WriteLine("PASS: video conversion, transparency, timing, cancellation, validation, preview and UI resources");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunAsync(string tools, string output)
    {
        string source = Path.Combine(output, "한글 video sample.mp4");
        // Red box on a green background, then blue: distinguish the requested segment from the start.
        var start = new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-y", "-v", "error", "-f", "lavfi", "-i",
            "color=c=0x00ff00:s=96x64:r=24:d=2,drawbox=x=32:y=16:w=32:h=32:color=red:t=fill",
            "-f", "lavfi", "-i", "color=c=blue:s=96x64:r=24:d=2,drawbox=x=32:y=16:w=32:h=32:color=red:t=fill",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0", "-c:v", "libx264", "-pix_fmt", "yuv420p", source }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "Fixture generation failed: " + error);
        }
        var info = await VideoConverter.ProbeAsync(tools, source, default);
        Check(info.Width == 96 && info.Height == 64 && Math.Abs(info.Duration - 4) < .1, "Metadata mismatch");
        var options = new VideoOptions(.5, 1.5, 64, 24);
        var preview = await VideoConverter.PreviewAsync(tools, source, options, info, default);
        string color;
        try
        {
            Check(preview.frames.Count == 24, "Preview frame count");
            var detected = BackgroundRemover.DetectBackground(preview.frames[0]);
            Check(detected != null && detected.Value.Green > 200 && detected.Value.Red < 30, "Automatic green detection");
            color = BackgroundRemover.ToHex(detected!.Value);
        }
        finally { foreach (var f in preview.frames) f.Dispose(); }
        string removed = Path.Combine(output, "removed.webp"), kept = Path.Combine(output, "kept.webp");
        if (File.Exists(removed)) File.Delete(removed);
        if (File.Exists(kept)) File.Delete(kept);
        await VideoConverter.ConvertAsync(tools, source, removed, options, info,
            new BgRemoveSettings { Color = color, Tolerance = 15, ConnectedOnly = true, Refine = 1 }, null, default);
        using (var anim = GifAnimation.Load(removed))
        {
            Check(anim.Frames.Count == 24 && anim.Width == 64 && anim.Height == 43, "Converted frame size/count");
            Check(anim.DelaysMs.Sum() == 1000, "24 fps timing drift");
            foreach (var frame in anim.Frames)
            {
                Check(frame.GetPixel(0, 0).Alpha == 0, "Background is not transparent");
                Check(frame.GetPixel(32, 21).Alpha > 245 && frame.GetPixel(32, 21).Red > 180, "Foreground damaged");
            }
        }
        // Select the blue section and keep its background.
        await VideoConverter.ConvertAsync(tools, source, kept, new VideoOptions(2.5, 3.5, 96, 15), info, null, null, default);
        using (var anim = GifAnimation.Load(kept))
        {
            Check(anim.Frames.Count == 15 && anim.DelaysMs.Sum() == 1000, "Kept clip timing");
            Check(anim.Frames[0].GetPixel(0, 0).Blue > 200 && anim.Frames[0].GetPixel(0, 0).Alpha == 255, "Wrong selected interval/background");
        }
        // The ANIM chunk has loop count zero: infinite repeat.
        byte[] bytes = File.ReadAllBytes(removed);
        Check(System.Text.Encoding.ASCII.GetString(bytes, 30, 4) == "ANIM" && bytes[42] == 0 && bytes[43] == 0, "Loop metadata");
        string whole = Path.Combine(output, "whole.webp");
        if (File.Exists(whole)) File.Delete(whole);
        await VideoConverter.ConvertAsync(tools, source, whole, new VideoOptions(0, info.Duration, 64, 30), info, null, null, default);
        using (var anim = GifAnimation.Load(whole))
            Check(anim.Frames.Count == 120 && anim.DelaysMs.Sum() == 4000, "Whole video / 30 fps timing");
        string tiny = Path.Combine(output, "tiny.webp");
        if (File.Exists(tiny)) File.Delete(tiny);
        await VideoConverter.ConvertAsync(tools, source, tiny, new VideoOptions(0, .1, 64, 1), info, null, null, default);
        using (var anim = GifAnimation.Load(tiny)) Check(anim.Frames.Count == 1, "Short clip lost its only frame");
        using (var sample = new SKBitmap(100, 50))
        {
            sample.Erase(SKColors.Lime); sample.SetPixel(50, 25, SKColors.Red);
            Check(!VideoImportWindow.TryPickColor(sample, new System.Windows.Point(100, 10), 200, 200, out _), "Eyedropper sampled letterbox margin");
            Check(VideoImportWindow.TryPickColor(sample, new System.Windows.Point(100, 100), 200, 200, out var picked) && picked == SKColors.Red, "Eyedropper coordinate mapping");
            using var result = BackgroundRemover.Process(sample, picked, 1, false, 0);
            Check(result.GetPixel(50, 25).Alpha == 0 && result.GetPixel(0, 0).Alpha == 255, "Manual color removal");
        }
        string cancelled = Path.Combine(output, "cancelled.webp");
        using (var cts = new CancellationTokenSource())
        {
            bool wasCancelled = false;
            var progress = new ImmediateProgress(_ => cts.Cancel());
            try { await VideoConverter.ConvertAsync(tools, source, cancelled, new VideoOptions(0, 4, 96, 30), info, null, progress, cts.Token); }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check(wasCancelled && !File.Exists(cancelled) && !Directory.GetFiles(output, "*.partial").Any(), "Cancellation left output or temporary frames");
        }
        foreach (var invalid in new[] { new VideoOptions(-1, 1, 64, 15), new VideoOptions(2, 1, 64, 15), new VideoOptions(0, 8, 64, 15), new VideoOptions(double.NaN, 1, 64, 15) })
        {
            bool rejected = false; try { invalid.Validate(info); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid range accepted");
        }
        var rotated = VideoConverter.ParseInfo("{\"streams\":[{\"width\":1920,\"height\":1080,\"sample_aspect_ratio\":\"1:1\",\"side_data_list\":[{\"rotation\":-90}]}],\"format\":{\"duration\":\"10\"}}");
        Check(rotated.Width == 1080 && rotated.Height == 1920, "Portrait rotation ignored");
        Check(VideoConverter.IsVideo("CLIP.MP4") && !VideoConverter.IsVideo("image.png"), "Import routing");
        string broken = Path.Combine(output, "broken.mp4"); File.WriteAllText(broken, "not a video");
        bool badRejected = false;
        try { await VideoConverter.ProbeAsync(tools, broken, default); } catch (InvalidDataException) { badRejected = true; }
        Check(badRejected, "Corrupt video accepted");
        Console.WriteLine("PASS: actual MP4 -> WebP integration checks");
    }

    private sealed class ImmediateProgress(Action<double> action) : IProgress<double> { public void Report(double value) => action(value); }

    private static void Ui(string tools, string output)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "VideoTools"); Directory.CreateDirectory(bundled);
        foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe", "verified.txt" })
        {
            string dest = Path.Combine(bundled, name);
            if (!File.Exists(dest)) File.Copy(Path.Combine(tools, name), dest);
        }
        // Load only the resource dictionary; constructing GifPin.App would queue its production startup.
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("GifPin.AppResources")!;
        var xml = System.Xml.Linq.XDocument.Load(resource);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            xml.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        Loc.SetLanguage("ko"); AppController.ApplyLanguageResources();
        var state = new AppState(); AppController.ApplyThemeFrom(state.Settings);
        var controller = new AppController(state);
        CollisionTests.Ui(controller, output);
        var window = new VideoImportWindow(controller);
        // Render the view off-screen, without starting GifPin or reading/writing the user's state.
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -30000; window.Top = -30000;
        window.Show(); window.UpdateLayout();
        Check(window.Title == Loc.T("video.title"), "Localized title missing");
        var load = typeof(VideoImportWindow).GetMethod("LoadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Pump((Task)load.Invoke(window, new object[] { Path.Combine(output, "한글 video sample.mp4") })!);
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("RemoveToggle");
        toggle.IsChecked = true; toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        window.UpdateLayout();
        var resultImage = (System.Windows.Controls.Image)window.FindName("ResultImage");
        var resultBitmap = (System.Windows.Media.Imaging.BitmapSource)resultImage.Source;
        var pixel = new byte[4]; resultBitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Check(pixel[3] == 0, "UI preview did not remove the detected background");
        Check(((System.Windows.Controls.Button)window.FindName("SaveButton")).IsEnabled, "Save not enabled after preview");
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var file = File.Create(Path.Combine(output, "video-window.png"))) png.Save(file);
        window.Close();
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var watch = Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(60)) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        Check(task.IsCompleted, "UI operation timed out"); task.GetAwaiter().GetResult();
    }

}
