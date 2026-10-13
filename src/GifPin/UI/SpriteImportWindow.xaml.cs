using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GifPin.Core;
using SkiaSharp;

namespace GifPin.UI;

/// <summary>스프라이트 시트 또는 프레임 폴더 → 애니메이션(WebP)으로 라이브러리에 추가.</summary>
public partial class SpriteImportWindow : Window
{
    private readonly AppController _app;
    private readonly DispatcherTimer _play;
    private readonly DispatcherTimer _debounce;

    private string? _sheetPath;
    private SKBitmap? _sheet;
    private List<string> _folderFiles = new();
    private string? _folderPath;

    private List<SKBitmap> _frames = new();
    private BitmapSource[] _preview = Array.Empty<BitmapSource>();
    private int _index, _version;
    private bool _updating, _saving;

    public SpriteImportWindow(AppController app)
    {
        _app = app;
        _updating = true;
        InitializeComponent();
        _updating = false;

        _play = new DispatcherTimer(DispatcherPriority.Render);
        _play.Tick += (_, _) => NextFrame();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RebuildAsync(); };

        FpsText.Text = $"{FpsSlider.Value:0} fps";
        UpdateMode();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
    }

    private bool SheetMode => ModeSheet.IsChecked == true;

    private void UpdateMode()
    {
        if (SheetOptions == null || PickButton == null) return;
        SheetOptions.Visibility = SheetMode ? Visibility.Visible : Visibility.Collapsed;
        PickButton.Content = SheetMode ? Loc.T("sprite.pickImage") : Loc.T("sprite.pickFolder");
        SourceText.Text = SheetMode ? (_sheetPath ?? Loc.T("sprite.noSource")) : (_folderPath ?? Loc.T("sprite.noSource"));
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        UpdateMode();
        Schedule();
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (SheetMode)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("sprite.pickImage"), Filter = Library.OpenFileFilter };
            if (dlg.ShowDialog(this) == true) LoadSheet(dlg.FileName);
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("sprite.pickFolder") };
            if (dlg.ShowDialog(this) == true) LoadFolder(dlg.FolderName);
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (Directory.Exists(paths[0]))
        {
            ModeFolder.IsChecked = true;
            LoadFolder(paths[0]);
        }
        else
        {
            ModeSheet.IsChecked = true;
            LoadSheet(paths[0]);
        }
    }

    private void LoadSheet(string path)
    {
        try
        {
            _sheet?.Dispose();
            _sheet = SpriteBuilder.LoadBgra(path);
            _sheetPath = path;
            var (cols, rows) = SpriteBuilder.GuessGrid(_sheet.Width, _sheet.Height);
            _updating = true;
            ColsBox.Text = cols.ToString(CultureInfo.InvariantCulture);
            RowsBox.Text = rows.ToString(CultureInfo.InvariantCulture);
            CountBox.Text = "";
            NameBox.Text = Path.GetFileNameWithoutExtension(path);
            _updating = false;
            PixelToggle.IsChecked = Math.Max(_sheet.Width / Math.Max(1, cols), _sheet.Height / Math.Max(1, rows)) <= 128;
            UpdateMode();
            Schedule();
        }
        catch (Exception ex)
        {
            Log.Warn($"Sprite sheet load failed: {path}", ex);
            StatusText.Text = ex.Message;
        }
    }

    private void LoadFolder(string folder)
    {
        _folderPath = folder;
        _folderFiles = SpriteBuilder.ListFrameFiles(folder);
        _updating = true;
        NameBox.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _updating = false;
        UpdateMode();
        if (_folderFiles.Count == 0) StatusText.Text = Loc.T("err.noFrames");
        Schedule();
    }

    private void Option_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_updating) Schedule();
    }

    private void Fps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || FpsText == null) return; // InitializeComponent 중에는 타이머가 아직 없음
        FpsText.Text = $"{e.NewValue:0} fps";
        RestartPlayback();
    }

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private async Task RebuildAsync()
    {
        int version = ++_version;
        StatusText.Text = "";
        List<SKBitmap>? frames = null;
        try
        {
            if (SheetMode)
            {
                if (_sheet == null) { ClearFrames(); return; }
                int cols = ParseInt(ColsBox.Text, 1), rows = ParseInt(RowsBox.Text, 1), count = ParseInt(CountBox.Text, 0);
                var sheet = _sheet;
                frames = await Task.Run(() => SpriteBuilder.FromSheet(sheet, cols, rows, count));
            }
            else
            {
                if (_folderFiles.Count == 0) { ClearFrames(); return; }
                var files = _folderFiles.ToList();
                frames = await Task.Run(() => SpriteBuilder.FromFiles(files));
            }
            var preview = await Task.Run(() => frames.Select(Images.ToBitmapSource).ToArray());
            if (version != _version) { foreach (var f in frames) f.Dispose(); return; }

            foreach (var f in _frames) f.Dispose();
            _frames = frames;
            _preview = preview;
            _index = 0;
            PreviewHint.Visibility = Visibility.Collapsed;
            InfoText.Text = Loc.F("sprite.info", _frames.Count, _frames[0].Width, _frames[0].Height);
            SaveButton.IsEnabled = true;
            RestartPlayback();
        }
        catch (Exception ex)
        {
            if (frames != null) foreach (var f in frames) f.Dispose();
            if (version == _version)
            {
                ClearFrames();
                StatusText.Text = ex.Message;
            }
        }
    }

    private void ClearFrames()
    {
        foreach (var f in _frames) f.Dispose();
        _frames = new List<SKBitmap>();
        _preview = Array.Empty<BitmapSource>();
        _play.Stop();
        PreviewImage.Source = null;
        PreviewHint.Visibility = Visibility.Visible;
        InfoText.Text = "";
        SaveButton.IsEnabled = false;
    }

    private void RestartPlayback()
    {
        if (_play == null) return;
        _play.Stop();
        if (_preview.Length == 0) return;
        PreviewImage.Source = _preview[Math.Min(_index, _preview.Length - 1)];
        if (_preview.Length < 2) return;
        _play.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, FpsSlider.Value));
        _play.Start();
    }

    private void NextFrame()
    {
        if (_preview.Length == 0) { _play.Stop(); return; }
        _index = (_index + 1) % _preview.Length;
        PreviewImage.Source = _preview[_index];
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count == 0 || _saving) return;
        _saving = true;
        Options.IsEnabled = false;
        SaveButton.IsEnabled = false;
        StatusText.Text = Loc.T("sprite.saving");

        var frames = _frames;
        double fps = FpsSlider.Value;
        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? "sprite" : NameBox.Text.Trim();
        bool pixel = PixelToggle.IsChecked == true;
        try
        {
            var photo = await Task.Run(() =>
            {
                Directory.CreateDirectory(Store.LibraryDir);
                var file = Path.Combine(Store.LibraryDir, Store.NewId("sp") + ".webp");
                SpriteBuilder.Save(frames, fps, file);
                return Library.RegisterGenerated(file, name);
            });
            if (photo == null) throw new InvalidDataException(Loc.T("err.unsupportedImage"));
            photo.Pixelated = pixel;
            _app.AddGeneratedPhoto(photo);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Sprite import failed", ex);
            StatusText.Text = Loc.F("sprite.failed", ex.Message);
            Options.IsEnabled = true;
            SaveButton.IsEnabled = true;
            _saving = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _play.Stop();
        _debounce.Stop();
        _version++;
        if (!_saving)
        {
            foreach (var f in _frames) f.Dispose();
            _sheet?.Dispose();
        }
        base.OnClosed(e);
    }
}
