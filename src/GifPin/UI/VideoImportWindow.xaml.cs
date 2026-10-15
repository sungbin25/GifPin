using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GifPin.Core;
using SkiaSharp;

namespace GifPin.UI;

public partial class VideoImportWindow : Window
{
    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private List<SKBitmap> _frames = new();
    private readonly Dictionary<int, BitmapSource> _results = new();
    private string? _path, _tools;
    private VideoInfo? _info;
    private VideoOptions? _previewOptions;
    private CancellationTokenSource? _cts;
    private bool _ready, _busy, _closing, _updating, _picking, _auto = true;
    private int _frame;
    private double _previewFps;
    private SKColor _color = SKColors.White;

    public VideoImportWindow(AppController app, string? path = null)
    {
        _app = app;
        InitializeComponent();
        _ready = true;
        _timer.Tick += (_, _) => ShowFrame((_frame + 1) % Math.Max(1, _frames.Count));
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => { if (path != null) await LoadAsync(path); };
        DragOver += (_, e) => { e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) =>
        {
            if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadAsync(files[0]);
            e.Handled = true;
        };
        UpdateColor(); Options_Changed(this, new RoutedEventArgs());
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true; StopPlayback(); _cts = new CancellationTokenSource();
        Options.IsEnabled = SaveButton.IsEnabled = PlayButton.IsEnabled = FrameSlider.IsEnabled = false;
        Progress.Value = 0;
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("common.cancelled"); }
        catch (Exception ex) { StatusText.Text = Loc.T("video.failed") + " " + ex.Message; Log.Warn("Video import failed", ex); }
        finally
        {
            _cts.Dispose(); _cts = null; _busy = false;
            Options.IsEnabled = true;
            PlayButton.IsEnabled = FrameSlider.IsEnabled = _frames.Count > 1;
            UpdateButtons();
            if (_closing) Close();
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = VideoConverter.Filter, Title = Loc.T("video.pick") };
        if (dialog.ShowDialog(this) == true) await LoadAsync(dialog.FileName);
    }

    private Task LoadAsync(string path) => RunAsync(async token =>
    {
        if (!VideoConverter.IsVideo(path) || !File.Exists(path)) throw new InvalidDataException(Loc.T("video.cannotRead"));
        StatusText.Text = Loc.T("video.preparingTools");
        _tools = await VideoTools.EnsureAsync(new Progress<double>(p => Progress.Value = p), token);
        StatusText.Text = Loc.T("video.reading");
        var info = await VideoConverter.ProbeAsync(_tools, path, token);
        ClearFrames(); _previewOptions = null;
        _path = path; _info = info;
        _updating = true;
        StartBox.Text = "0"; EndBox.Text = info.Duration.ToString("0.###", CultureInfo.InvariantCulture);
        NameBox.Text = Path.GetFileNameWithoutExtension(path);
        _updating = false;
        SourceText.Text = path; SourceText.ToolTip = path;
        InfoText.Text = $"{info.Width} × {info.Height} · {info.Duration:0.###} s";
        await BuildPreviewAsync(token);
    });

    private VideoOptions ReadOptions()
    {
        static double Seconds(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double n) ? n
            : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : double.NaN;
        var options = new VideoOptions(Seconds(StartBox.Text), Seconds(EndBox.Text),
            int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag, CultureInfo.InvariantCulture), (int)FpsSlider.Value);
        options.Validate(_info ?? throw new InvalidOperationException(Loc.T("video.noSource")));
        return options;
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        FpsText.Text = $"{FpsSlider.Value:0} fps";
        StopPlayback(); UpdateButtons();
        if (_previewOptions != null) StatusText.Text = Loc.T("video.refreshHint");
    }

    private void UpdateButtons()
    {
        PreviewButton.IsEnabled = _info != null && !_busy;
        bool matches = false;
        try { matches = _previewOptions != null && ReadOptions() == _previewOptions; } catch (ArgumentException) { }
        SaveButton.IsEnabled = !_busy && matches && _frames.Count > 0;
    }

    private void Whole_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        StartBox.Text = "0"; EndBox.Text = _info.Duration.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunAsync(BuildPreviewAsync);

    private async Task BuildPreviewAsync(CancellationToken token)
    {
        var options = ReadOptions();
        StatusText.Text = Loc.T("video.previewing"); Progress.IsIndeterminate = true;
        try
        {
            var preview = await Task.Run(() => VideoConverter.PreviewAsync(_tools!, _path!, options, _info!, token), token);
            ClearFrames(); _frames = preview.frames; _previewFps = preview.fps; _previewOptions = options;
            if (_auto) DetectColor(0);
            FrameSlider.Maximum = Math.Max(0, _frames.Count - 1);
            ShowFrame(0);
            StatusText.Text = Loc.F("video.previewReady", _frames.Count);
        }
        finally { Progress.IsIndeterminate = false; }
    }

    private BgRemoveSettings? BackgroundSettings() => RemoveToggle.IsChecked != true ? null : new BgRemoveSettings
    {
        Color = BackgroundRemover.ToHex(_color), Tolerance = (int)ToleranceSlider.Value,
        ConnectedOnly = ConnectedToggle.IsChecked == true, Refine = Math.Max(0, RefineBox.SelectedIndex),
    };

    private void ShowFrame(int index)
    {
        if (_frames.Count == 0) return;
        _frame = Math.Clamp(index, 0, _frames.Count - 1);
        var source = _frames[_frame];
        OriginalImage.Source = Images.ToBitmapSource(source);
        if (!_results.TryGetValue(_frame, out var result))
        {
            var bg = BackgroundSettings();
            if (bg == null) result = (BitmapSource)OriginalImage.Source;
            else
            {
                // Scale edge refinement to the smaller preview image.
                int outputWidth = VideoConverter.Dimensions(_info!, _previewOptions!.Size).w;
                int refine = bg.Refine == 0 ? 0 : Math.Max(1, (int)Math.Round(bg.Refine * source.Width / (double)outputWidth));
                using var processed = BackgroundRemover.Process(source, _color, bg.Tolerance, bg.ConnectedOnly, refine);
                result = Images.ToBitmapSource(processed);
            }
            _results[_frame] = result;
        }
        ResultImage.Source = result;
        _updating = true; FrameSlider.Value = _frame; _updating = false;
        FrameText.Text = $"{_previewOptions!.Start + _frame / _previewFps:0.##} s · {_frame + 1} / {_frames.Count}";
    }

    private void Frame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating || _busy) return;
        StopPlayback(); ShowFrame((int)e.NewValue);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled) { StopPlayback(); return; }
        if (_frames.Count < 2) return;
        _timer.Interval = TimeSpan.FromSeconds(1 / _previewFps); _timer.Start();
        PlayButton.Content = Loc.T("video.pause");
    }

    private void StopPlayback() { _timer.Stop(); if (_ready) PlayButton.Content = Loc.T("video.play"); }

    private void Background_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        BackgroundOptions.IsEnabled = RemoveToggle.IsChecked == true;
        if (!BackgroundOptions.IsEnabled) { _picking = false; OriginalImage.Cursor = null; }
        ToleranceText.Text = $"{ToleranceSlider.Value:0}";
        _results.Clear(); ShowFrame(_frame);
    }

    private void DetectColor(int frame)
    {
        if (_frames.Count == 0) return;
        if (BackgroundRemover.DetectBackground(_frames[frame]) is SKColor color)
        { _color = color; UpdateColor(); }
        else StatusText.Text = Loc.T("bg.alreadyTransparent");
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback(); _auto = true; _picking = false; OriginalImage.Cursor = null;
        DetectColor(_frame); _results.Clear(); ShowFrame(_frame);
    }

    private void UpdateColor()
    {
        Swatch.Background = new SolidColorBrush(Color.FromRgb(_color.Red, _color.Green, _color.Blue));
        ColorText.Text = BackgroundRemover.ToHex(_color) + (_auto ? " · " + Loc.T("video.auto") : "");
        ToleranceText.Text = $"{ToleranceSlider.Value:0}";
    }

    private void Eyedropper_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback(); _picking = true; OriginalImage.Cursor = Cursors.Cross;
        StatusText.Text = Loc.T("video.pickHint");
    }

    private void PickColor_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_picking || _busy || _frames.Count == 0) return;
        var image = _frames[_frame]; var point = e.GetPosition(OriginalImage);
        if (!TryPickColor(image, point, OriginalImage.ActualWidth, OriginalImage.ActualHeight, out var color)) return;
        _color = color; _auto = false; _picking = false; OriginalImage.Cursor = null;
        UpdateColor(); _results.Clear(); ShowFrame(_frame); StatusText.Text = Loc.T("video.colorPicked");
    }

    internal static bool TryPickColor(SKBitmap image, Point point, double width, double height, out SKColor color)
    {
        color = default;
        // Uniform stretching may leave letterboxing. Ignore those margins.
        double scale = Math.Min(width / image.Width, height / image.Height);
        if (!double.IsFinite(scale) || scale <= 0) return false;
        double x = (point.X - (width - image.Width * scale) / 2) / scale;
        double y = (point.Y - (height - image.Height * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return false;
        color = image.GetPixel((int)x, (int)y);
        return color.Alpha >= 16;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async token =>
        {
            var options = ReadOptions(); var background = BackgroundSettings();
            string name = string.IsNullOrWhiteSpace(NameBox.Text) ? Path.GetFileNameWithoutExtension(_path!) : NameBox.Text.Trim();
            Directory.CreateDirectory(Store.LibraryDir);
            string output = Path.Combine(Store.LibraryDir, Store.NewId("video") + ".webp");
            PhotoItem? photo = null;
            bool added = false;
            try
            {
                StatusText.Text = Loc.T("video.converting");
                var progress = new Progress<double>(p => { Progress.Value = p; StatusText.Text = Loc.F("video.progress", Math.Round(p * 100)); });
                await Task.Run(() => VideoConverter.ConvertAsync(_tools!, _path!, output, options, _info!, background, progress, token), token);
                token.ThrowIfCancellationRequested();
                photo = await Task.Run(() => Library.RegisterGenerated(output, name), token);
                token.ThrowIfCancellationRequested();
                if (photo == null) throw new InvalidDataException(Loc.T("video.failed"));
                photo.BgRemoval = background;
                _app.AddGeneratedPhoto(photo); added = true; _closing = true;
            }
            finally
            {
                if (!added)
                {
                    if (photo != null) Library.DeleteFiles(photo);
                    else if (File.Exists(output)) File.Delete(output);
                }
            }
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; _closing = true; _cts?.Cancel(); }
        base.OnClosing(e);
    }

    private void ClearFrames()
    {
        StopPlayback(); OriginalImage.Source = ResultImage.Source = null; _results.Clear();
        foreach (var frame in _frames) frame.Dispose(); _frames.Clear(); _frame = 0;
    }

    protected override void OnClosed(EventArgs e) { ClearFrames(); base.OnClosed(e); }
}
