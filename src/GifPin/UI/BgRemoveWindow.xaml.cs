using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GifPin.Core;
using SkiaSharp;

namespace GifPin.UI;

/// <summary>배경 지우기 창: 원본·결과 나란히 미리보기, 설정 조정, 새 사진으로 저장.</summary>
public partial class BgRemoveWindow : Window
{
    private readonly AppController _app;
    private readonly PhotoItem _photo;
    private readonly string? _fromActiveId;

    private GifAnimation? _anim;
    private BitmapSource[] _orig = Array.Empty<BitmapSource>();
    private readonly Dictionary<int, BitmapSource> _resultCache = new();
    private readonly HashSet<int> _pending = new();
    private int _version, _frame, _running;
    private bool _playing = true, _picking, _saving, _ready, _updating;
    private SKColor _bg = SKColors.White;
    private CancellationTokenSource? _saveCts;

    private readonly DispatcherTimer _playTimer;
    private readonly DispatcherTimer _debounce;

    public BgRemoveWindow(AppController app, PhotoItem photo, string? fromActiveId)
    {
        _app = app;
        _photo = photo;
        _fromActiveId = fromActiveId;
        _updating = true;
        InitializeComponent();
        _updating = false;

        Title = $"배경 지우기 — {photo.Name}";
        NameBox.Text = $"{photo.Name} (배경 제거)";
        TolText.Text = $"{TolSlider.Value:0}";

        _playTimer = new DispatcherTimer(DispatcherPriority.Render);
        _playTimer.Tick += (_, _) => Advance();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RequestResult(_frame); };

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await LoadAsync();
    }

    // ── 불러오기 ─────────────────────────────────────────────

    private async Task LoadAsync()
    {
        string path = Store.ResolvePath(_photo);
        try
        {
            var (anim, frames) = await Task.Run(() =>
            {
                var a = GifAnimation.Load(path);
                var f = new BitmapSource[a.Frames.Count];
                for (int i = 0; i < f.Length; i++) f[i] = Images.ToBitmapSource(a.Frames[i]);
                return (a, f);
            });
            _anim = anim;
            _orig = frames;
        }
        catch (Exception ex)
        {
            LoadingText.Text = "이미지를 열 수 없습니다: " + ex.Message;
            return;
        }

        LoadingText.Visibility = Visibility.Collapsed;
        FrameSlider.Maximum = Math.Max(0, _orig.Length - 1);
        PlayButton.IsEnabled = _orig.Length > 1;
        FrameSlider.IsEnabled = _orig.Length > 1;

        // 이전에 배경을 지운 사진이면 그때 설정을, 아니면 테두리에서 자동 감지
        var prev = _photo.BgRemoval;
        var detected = BackgroundRemover.DetectBackground(_anim.Frames[0]);
        if (detected is SKColor d)
        {
            _bg = d;
            AutoBadge.Visibility = Visibility.Visible;
        }
        else
        {
            TransparentNotice.Visibility = Visibility.Visible;
        }
        if (prev != null && BackgroundRemover.TryParseHex(prev.Color, out var pc))
        {
            _bg = pc;
            _updating = true;
            TolSlider.Value = prev.Tolerance;
            ConnectedToggle.IsChecked = prev.ConnectedOnly;
            _updating = false;
        }
        UpdateColorUi();

        _ready = true;
        SaveButton.IsEnabled = true;
        ShowFrame(0);
        if (_orig.Length > 1) ScheduleNext();
    }

    // ── 재생 ─────────────────────────────────────────────────

    private void ShowFrame(int i)
    {
        if (_orig.Length == 0) return;
        _frame = Math.Clamp(i, 0, _orig.Length - 1);
        OrigImage.Source = _orig[_frame];
        if (_resultCache.TryGetValue(_frame, out var r)) ResultImage.Source = r;
        else RequestResult(_frame);

        _updating = true;
        FrameSlider.Value = _frame;
        _updating = false;
        FrameText.Text = $"{_frame + 1} / {_orig.Length}";
    }

    private void Advance()
    {
        _playTimer.Stop();
        if (!_playing || _orig.Length < 2) return;
        ShowFrame((_frame + 1) % _orig.Length);
        ScheduleNext();
    }

    private void ScheduleNext()
    {
        if (_anim == null || !_playing) return;
        _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _anim.DelaysMs[_frame]));
        _playTimer.Start();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        _playing = !_playing;
        PlayIcon.Text = _playing ? "" : "";
        if (_playing) ScheduleNext();
        else _playTimer.Stop();
    }

    private void FrameSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !_ready) return;
        if (_playing) Play_Click(sender, e);
        ShowFrame((int)Math.Round(e.NewValue));
    }

    private void Zoom_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string t && double.TryParse(t, out var z) && OrigZoom != null && ResultZoom != null)
        {
            OrigZoom.ScaleX = OrigZoom.ScaleY = z;
            ResultZoom.ScaleX = ResultZoom.ScaleY = z;
            var mode = z > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
            RenderOptions.SetBitmapScalingMode(OrigImage, mode);
            RenderOptions.SetBitmapScalingMode(ResultImage, mode);
        }
    }

    private void ResultBg_Checked(object sender, RoutedEventArgs e)
    {
        if (ResultBg == null || sender is not RadioButton rb) return;
        ResultBg.Background = (Brush)FindResource((rb.Tag as string) switch
        {
            "dark" => "PreviewDarkBrush",
            "light" => "PreviewLightBrush",
            _ => "CheckerBrush",
        });
    }

    // ── 결과 계산 ────────────────────────────────────────────

    private (SKColor bg, int tol, bool connected, int refine) CurrentSettings()
    {
        int refine = 1;
        foreach (var child in ((UniformGrid)Refine1.Parent).Children)
            if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is string s) refine = int.Parse(s);
        return (_bg, (int)Math.Round(TolSlider.Value), ConnectedToggle.IsChecked == true, refine);
    }

    /// <summary>설정이 바뀜: 계산해 둔 결과를 버리고 지금 프레임부터 다시 계산.</summary>
    private void Invalidate()
    {
        if (!_ready) return;
        _version++;
        _resultCache.Clear();
        _pending.Clear();
        _debounce.Stop();
        _debounce.Start();
    }

    private async void RequestResult(int frame)
    {
        if (_anim == null || _pending.Contains(frame)) return;
        _pending.Add(frame);
        int version = _version;
        var src = _anim.Frames[frame];
        var (bg, tol, connected, refine) = CurrentSettings();
        Interlocked.Increment(ref _running);
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var r = BackgroundRemover.Process(src, bg, tol, connected, refine);
                return Images.ToBitmapSource(r);
            });
            if (version != _version) return;
            _resultCache[frame] = bmp;
            if (frame == _frame) ResultImage.Source = bmp;
        }
        catch { /* 창을 닫는 중 등 */ }
        finally
        {
            Interlocked.Decrement(ref _running);
            if (version == _version) _pending.Remove(frame);
        }
    }

    private void TolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TolText != null) TolText.Text = $"{e.NewValue:0}";
        if (!_updating) Invalidate();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updating) Invalidate();
    }

    // ── 배경색 ───────────────────────────────────────────────

    private void UpdateColorUi()
    {
        Swatch.Background = new SolidColorBrush(Color.FromRgb(_bg.Red, _bg.Green, _bg.Blue));
        HexBox.Text = BackgroundRemover.ToHex(_bg);
    }

    private void SetBackground(SKColor c, bool auto)
    {
        _bg = new SKColor(c.Red, c.Green, c.Blue);
        AutoBadge.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        TransparentNotice.Visibility = Visibility.Collapsed;
        UpdateColorUi();
        Invalidate();
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        _picking = true;
        ResultImage.Cursor = Cursors.Cross;
        PickHint.Text = "원본이나 결과에서 배경을 클릭하세요";
        PickHint.Foreground = (Brush)FindResource("AccentLightBrush");
    }

    /// <summary>원본은 항상, 결과는 스포이드를 켰을 때만 클릭으로 배경색을 고른다.</summary>
    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_anim == null || sender is not Image img) return;
        if (img == ResultImage && !_picking) return;
        var pos = e.GetPosition(img);
        var frame = _anim.Frames[_frame];
        if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return;
        int x = (int)(pos.X / img.ActualWidth * frame.Width);
        int y = (int)(pos.Y / img.ActualHeight * frame.Height);
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
        var c = frame.GetPixel(x, y);
        if (c.Alpha < 16) return; // 이미 투명한 곳
        SetBackground(c, auto: false);

        _picking = false;
        ResultImage.Cursor = null;
        PickHint.Text = "클릭해서 배경색 고르기";
        PickHint.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitHex();
    }

    private void HexBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        if (BackgroundRemover.TryParseHex(HexBox.Text, out var c))
        {
            if (c.Red != _bg.Red || c.Green != _bg.Green || c.Blue != _bg.Blue) SetBackground(c, auto: false);
        }
        else UpdateColorUi();
    }

    // ── 저장 ─────────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_anim == null || _saving) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = $"{_photo.Name} (배경 제거)";

        _saving = true;
        _playing = false;
        _playTimer.Stop();
        SettingsPanel.IsEnabled = false;
        SaveButton.IsEnabled = false;
        NameBox.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "모든 프레임을 처리하는 중…";
        _saveCts = new CancellationTokenSource();

        var (bg, tol, connected, refine) = CurrentSettings();
        var frames = _anim.Frames;
        var delays = _anim.DelaysMs;
        var progress = new Progress<double>(v =>
        {
            SaveProgress.Value = v;
            ProgressText.Text = $"모든 프레임을 처리하는 중… {v * 100:0}%";
        });
        var token = _saveCts.Token;

        PhotoItem? created = null;
        Interlocked.Increment(ref _running);
        try
        {
            created = await Task.Run(() =>
            {
                var results = BackgroundRemover.ProcessAll(frames, bg, tol, connected, refine, progress, token);
                try
                {
                    Directory.CreateDirectory(Store.LibraryDir);
                    string file;
                    if (results.Count == 1)
                    {
                        file = Path.Combine(Store.LibraryDir, Store.NewId("bg") + ".png");
                        using var image = SKImage.FromBitmap(results[0]);
                        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                        using var fs = File.Create(file);
                        data.SaveTo(fs);
                    }
                    else
                    {
                        file = Path.Combine(Store.LibraryDir, Store.NewId("bg") + ".webp");
                        WebpWriter.Save(file, results, delays);
                    }
                    var p = Library.RegisterGenerated(file, name);
                    if (p == null) throw new InvalidDataException("저장한 파일을 다시 열 수 없습니다.");
                    p.SourceId = _photo.Id;
                    p.BgRemoval = new BgRemoveSettings { Color = BackgroundRemover.ToHex(bg), Tolerance = tol, ConnectedOnly = connected, Refine = refine };
                    return p;
                }
                finally
                {
                    foreach (var r in results) r.Dispose();
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "취소했습니다";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "저장하지 못했습니다: " + ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        _saving = false;
        if (created == null)
        {
            SettingsPanel.IsEnabled = true;
            SaveButton.IsEnabled = true;
            NameBox.IsEnabled = true;
            return;
        }

        _app.AddGeneratedPhoto(created);
        if (_fromActiveId != null && _app.State.Active.Exists(a => a.Id == _fromActiveId))
        {
            var answer = MessageBox.Show(this, "저장했습니다.\n\n화면에 표시 중인 GIF도 배경을 지운 사진으로 바꿀까요?\n(위치와 크기는 그대로 유지됩니다)",
                "배경 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) _app.ReplaceActive(_fromActiveId, created.Id);
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) { _saveCts?.Cancel(); return; }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _playTimer.Stop();
        _debounce.Stop();
        _saveCts?.Cancel();
        _version++;
        // 백그라운드 계산이 끝났을 때만 프레임 메모리를 바로 해제 (아니면 GC에 맡김)
        if (Volatile.Read(ref _running) == 0) _anim?.Dispose();
        base.OnClosed(e);
    }
}
