using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GifPin.Core;

namespace GifPin.UI;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly AnimPlayer _hoverPlayer;
    private readonly AnimPlayer _previewPlayer;
    private readonly DispatcherTimer _toastTimer;
    private PhotoVM? _hoverVm;
    private string? _previewPath;
    private bool _updating; // 코드에서 값을 채우는 중이면 변경 이벤트 무시

    public MainWindow(AppController app)
    {
        _app = app;
        _updating = true; // InitializeComponent 중 슬라이더 기본값 이벤트가 GIF 설정을 바꾸지 않도록
        InitializeComponent();

        _hoverPlayer = new AnimPlayer(img => { if (_hoverVm != null) _hoverVm.HoverFrame = img; });
        _previewPlayer = new AnimPlayer(img => PreviewImage.Source = img);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        PhotoList.ItemsSource = _app.VisiblePhotos;
        ActiveList.ItemsSource = _app.Actives;
        _app.VisiblePhotos.CollectionChanged += (_, _) => UpdateCounts();
        _app.Actives.CollectionChanged += (_, _) => UpdateCounts();
        _app.SelectionChanged += RefreshPanel;
        _app.GeometryChanged += OnGeometryChanged;
        _app.Notice += ShowToast;
        _app.MonitorsChanged += () => { RefreshPanel(); RefreshDiagnostics(); };
        Loc.Changed += OnLanguageChanged;

        BindMotionSliders();
        BuildAnchorButtons();
        BuildHotkeyRows();
        RestoreSavedBounds();
        RefreshSettings();
        RefreshHiddenToggle();
        UpdateCounts();
        UpdateSortLabel();
        _updating = false;
        RefreshPanel();

        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += OnClosing;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OnLanguageChanged()
    {
        UpdateCounts();
        UpdateSortLabel();
        BuildHotkeyRows();
        RefreshSettings();
        RefreshPanel();
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshPanel();
    }

    public WindowBounds CurrentBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowBounds { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };
    }

    private void RestoreSavedBounds()
    {
        var b = _app.State.Settings.Window;
        if (b == null || b.Width < MinWidth || b.Height < MinHeight) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(b.Left, b.Top, b.Width, b.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsQuitting) return;
        // X 버튼 = 트레이로 숨김 (GIF는 계속 표시)
        e.Cancel = true;
        _app.State.Settings.Window = CurrentBounds();
        _app.Save();
        _hoverPlayer.Stop();
        _previewPlayer.Stop();
        _previewPath = null;
        Hide();
        _app.OnMainClosingToTray();
    }

    // ── 탭 ───────────────────────────────────────────────────

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (LibraryView == null || SettingsView == null) return; // 초기화 중
        bool lib = TabLibrary.IsChecked == true;
        LibraryView.Visibility = lib ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = lib ? Visibility.Collapsed : Visibility.Visible;
        if (!lib) RefreshSettings();
    }

    private void EditMode_Click(object sender, RoutedEventArgs e) => _app.EnterEdit();

    private void HiddenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetAllHidden(HiddenToggle.IsChecked == true);
    }

    public void RefreshHiddenToggle()
    {
        _updating = true;
        HiddenToggle.IsChecked = _app.State.Settings.AllHidden;
        _updating = false;
    }

    // ── 라이브러리 도구줄 ────────────────────────────────────

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("dlg.addTitle"),
            Filter = Library.OpenFileFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await _app.AddFilesAsync(dlg.FileNames);
    }

    private void Sprite_Click(object sender, RoutedEventArgs e) => _app.OpenSpriteImport();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await _app.AddFilesAsync(files);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _app.SetSearch(SearchBox.Text);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && PhotoList != null) _app.SetFilter(tag);
        if (SortButton != null && FilterRecent != null) SortButton.IsEnabled = FilterRecent.IsChecked != true;
    }

    private static void OpenMenuBelow(Button b)
    {
        var menu = b.ContextMenu;
        if (menu == null) return;
        menu.PlacementTarget = b;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SortButton_Click(object sender, RoutedEventArgs e) => OpenMenuBelow(SortButton);

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            _app.SetSort(tag);
            UpdateSortLabel();
        }
    }

    private void UpdateSortLabel() => SortLabel.Text = Loc.T("sort." + _app.Sort);

    private void UpdateCounts()
    {
        FilterAll.Content = Loc.F("lib.filterAll", _app.State.Library.Count);
        FilterFavorite.Content = Loc.F("lib.filterFavorite", _app.FavoriteCount);
        ActiveCount.Text = _app.Actives.Count.ToString(CultureInfo.InvariantCulture);
        bool empty = _app.VisiblePhotos.Count == 0;
        EmptyLibrary.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _app.State.Library.Count == 0 ? Loc.T("lib.empty") : Loc.T("lib.noMatch");
    }

    // ── 에셋 팩 ──────────────────────────────────────────────

    private void PackButton_Click(object sender, RoutedEventArgs e) => OpenMenuBelow(PackButton);

    private async void PackImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("pack.importTitle"), Filter = AssetPack.FileFilter };
        if (dlg.ShowDialog(this) == true) await _app.ImportPackAsync(dlg.FileName);
    }

    private async void PackExportScreen_Click(object sender, RoutedEventArgs e)
    {
        var photos = _app.State.Active.Select(a => _app.State.FindPhoto(a.PhotoId)).OfType<PhotoItem>().Distinct().ToList();
        await ExportPack(photos, withLayout: true, Loc.T("pack.defaultNameScreen"));
    }

    private async void PackExportFavorites_Click(object sender, RoutedEventArgs e) =>
        await ExportPack(_app.State.Library.Where(p => p.Favorite).ToList(), withLayout: false, Loc.T("pack.defaultNameFavorites"));

    private async void PackExportAll_Click(object sender, RoutedEventArgs e) =>
        await ExportPack(_app.State.Library.ToList(), withLayout: false, Loc.T("pack.defaultNameAll"));

    private async void CardMenu_ExportPack(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is PhotoVM vm) await ExportPack(new List<PhotoItem> { vm.Photo }, withLayout: false, vm.Name);
    }

    private async System.Threading.Tasks.Task ExportPack(List<PhotoItem> photos, bool withLayout, string defaultName)
    {
        if (photos.Count == 0)
        {
            ShowToast(Loc.T("notice.packEmpty"));
            return;
        }
        var safe = string.Concat(defaultName.Split(Path.GetInvalidFileNameChars()));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("pack.exportTitle"),
            Filter = AssetPack.FileFilter,
            FileName = (safe.Length > 0 ? safe : "GifPin pack") + ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        await _app.ExportPackAsync(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName), photos, withLayout);
    }

    // ── 사진 카드 ────────────────────────────────────────────

    private static PhotoVM? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as PhotoVM;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm == null) return;
        if (e.ClickCount == 2) _app.AddToScreen(vm.Id);
        else _app.SelectPhoto(vm.Id);
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm == null || vm == _hoverVm) return;
        StopHover();
        if (vm.Photo.FrameCount < 2) return;
        _hoverVm = vm;
        _hoverPlayer.Play(Store.ResolvePath(vm.Photo), 320);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (VmOf(sender) == _hoverVm) StopHover();
    }

    private void StopHover()
    {
        _hoverPlayer.Stop();
        if (_hoverVm != null) _hoverVm.HoverFrame = null;
        _hoverVm = null;
    }

    private void Card_Add(object sender, RoutedEventArgs e) { if (VmOf(sender) is PhotoVM vm) _app.AddToScreen(vm.Id); }

    private void Card_Replace(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is PhotoVM vm && _app.SelectedActiveId != null) _app.ReplaceActive(_app.SelectedActiveId, vm.Id);
    }

    private void Card_Star(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is PhotoVM vm) { _app.ToggleFavorite(vm.Id); UpdateCounts(); }
    }

    private void CardMenu_Add(object sender, RoutedEventArgs e) => Card_Add(sender, e);
    private void CardMenu_Replace(object sender, RoutedEventArgs e) => Card_Replace(sender, e);
    private void CardMenu_Favorite(object sender, RoutedEventArgs e) => Card_Star(sender, e);

    private void CardMenu_BgRemove(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is PhotoVM vm) _app.OpenBackgroundRemover(vm.Id, null);
    }

    private void CardMenu_OpenFolder(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not PhotoVM vm) return;
        var path = Store.ResolvePath(vm.Photo);
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void CardMenu_Remove(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not PhotoVM vm) return;
        string msg = vm.InUse ? Loc.F("card.removeInUseQuestion", vm.Name) : Loc.F("card.removeQuestion", vm.Name);
        if (MessageBox.Show(this, msg, "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (vm == _hoverVm) StopHover();
        _app.RemovePhoto(vm.Id);
        UpdateCounts();
    }

    // ── 화면 배치 바 ─────────────────────────────────────────

    private static ActiveVM? ActiveOf(object sender) => (sender as FrameworkElement)?.DataContext as ActiveVM;

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ActiveOf(sender) is ActiveVM vm) _app.SelectActive(vm.Id, flash: true);
    }

    private void Tile_Eye(object sender, RoutedEventArgs e)
    {
        if (ActiveOf(sender) is ActiveVM vm) _app.UpdateActive(vm.Id, a => a.Visible = !a.Visible);
    }

    private void Tile_Remove(object sender, RoutedEventArgs e)
    {
        if (ActiveOf(sender) is ActiveVM vm) _app.RemoveActive(vm.Id);
    }

    // ── 속성 패널 ────────────────────────────────────────────

    private void RefreshPanel()
    {
        var mode = _app.PanelMode;
        var active = mode == PanelMode.Active ? _app.SelectedActive : null;
        var photo = _app.State.FindPhoto(active?.PhotoId ?? _app.SelectedPhotoId);
        if (mode == PanelMode.None || photo == null)
        {
            PanelEmpty.Visibility = Visibility.Visible;
            PanelContent.Visibility = Visibility.Collapsed;
            _previewPlayer.Stop();
            _previewPath = null;
            PreviewImage.Source = null;
            return;
        }
        PanelEmpty.Visibility = Visibility.Collapsed;
        PanelContent.Visibility = Visibility.Visible;

        bool was = _updating;
        _updating = true;
        PanelTitle.Text = photo.Name;
        PanelInfo.Text = $"{photo.Width} × {photo.Height} · "
            + (photo.FrameCount > 1 ? Loc.F("panel.frames", photo.FrameCount) : Loc.T("kind.still"))
            + " · " + Library.FormatSize(photo.FileSize);
        PixelToggle.IsChecked = photo.Pixelated;
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(PreviewImage, photo.Pixelated ? System.Windows.Media.BitmapScalingMode.NearestNeighbor : System.Windows.Media.BitmapScalingMode.HighQuality);

        if (active != null)
        {
            int index = _app.State.Active.IndexOf(active) + 1;
            PanelSubtitle.Text = Loc.T("panel.selectedGif");
            ScreenBadge.Visibility = Visibility.Visible;
            ScreenBadgeText.Text = Loc.F("panel.screenN", index);
            ActiveProps.Visibility = Visibility.Visible;
            ActiveButtons.Visibility = Visibility.Visible;
            PhotoButtons.Visibility = Visibility.Collapsed;
            FillActiveValues(active);
            _previewPlayer.Speed = active.Speed;
        }
        else
        {
            PanelSubtitle.Text = Loc.T("panel.selectedPhoto");
            ScreenBadge.Visibility = Visibility.Collapsed;
            ActiveProps.Visibility = Visibility.Collapsed;
            ActiveButtons.Visibility = Visibility.Collapsed;
            PhotoButtons.Visibility = Visibility.Visible;
            var sel = _app.SelectedActive;
            var selPhoto = _app.State.FindPhoto(sel?.PhotoId);
            ReplaceSelectedButton.Visibility = sel != null && sel.PhotoId != photo.Id ? Visibility.Visible : Visibility.Collapsed;
            if (sel != null) ReplaceSelectedButton.Content = Loc.F("panel.replaceWith", _app.State.Active.IndexOf(sel) + 1, selPhoto?.Name);
            PreviewFlip.ScaleX = 1;
            _previewPlayer.Speed = 1;
        }
        _updating = was;

        var path = Store.ResolvePath(photo);
        if (path != _previewPath)
        {
            _previewPath = path;
            PreviewImage.Source = Images.LoadThumb(photo);
            _previewPlayer.Play(path, 320);
        }
    }

    private void FillActiveValues(ActiveItem a)
    {
        bool was = _updating;
        _updating = true;
        if (!XBox.IsKeyboardFocused) XBox.Text = a.X.ToString(CultureInfo.InvariantCulture);
        if (!YBox.IsKeyboardFocused) YBox.Text = a.Y.ToString(CultureInfo.InvariantCulture);
        ScaleSlider.Value = Math.Round(a.Scale * 100);
        ScaleText.Text = $"{a.Scale * 100:0}%";
        OpacitySlider.Value = Math.Round(a.Opacity * 100);
        OpacityText.Text = $"{a.Opacity * 100:0}%";
        Speed05.IsChecked = a.Speed < 0.75;
        Speed2.IsChecked = a.Speed > 1.5;
        Speed1.IsChecked = a.Speed >= 0.75 && a.Speed <= 1.5;
        FlipToggle.IsChecked = a.FlipX;
        PreviewFlip.ScaleX = a.FlipX ? -1 : 1;
        ClickThroughToggle.IsChecked = a.ClickThrough;

        string mode = Motion.Normalize(a.MotionMode);
        MotionNone.IsChecked = mode == Motion.None;
        MotionWalk.IsChecked = mode == Motion.Walk;
        MotionBounce.IsChecked = mode == Motion.Bounce;
        MotionFall.IsChecked = mode == Motion.Fall;
        MotionSlide.IsChecked = mode == Motion.Slide;
        GravityToggle.IsChecked = a.MotionGravity;
        UpdateMotionOptions(mode);
        bool desktop = Layers.Normalize(a.Layer) == Layers.Desktop;
        LayerTop.IsChecked = !desktop;
        LayerDesktop.IsChecked = desktop;
        LayerHint.Text = Loc.T(desktop ? "panel.layerDesktopHint" : "panel.layerTopHint");
        MotionSpeedSlider.Value = a.MotionSpeed;
        MotionSpeedText.Text = $"{a.MotionSpeed:0} px/s";
        FaceDirToggle.IsChecked = a.MotionFaceDirection;
        JumpToggle.IsChecked = a.JumpEnabled;
        JumpSliders.Visibility = a.JumpEnabled ? Visibility.Visible : Visibility.Collapsed;
        JumpHeightSlider.Value = a.JumpHeight;
        JumpHeightText.Text = $"{a.JumpHeight:0} px";
        JumpIntervalSlider.Value = a.JumpInterval;
        JumpIntervalText.Text = SecondsText(a.JumpInterval);
        JumpAngleSlider.Value = a.JumpAngle;
        JumpAngleText.Text = AngleText(a.JumpAngle);
        FrictionSlider.Value = a.SlideFriction;
        FrictionText.Text = $"{a.SlideFriction:0} / 10";

        // 랜덤 / 고정 선택
        JumpHeightRand.IsChecked = a.JumpHeightRandom;
        JumpHeightFixed.IsChecked = !a.JumpHeightRandom;
        JumpDirRand.IsChecked = a.JumpAngleRandom;
        JumpDirFixed.IsChecked = !a.JumpAngleRandom;
        SlideAutoToggle.IsChecked = a.SlideAuto;
        PushDirRand.IsChecked = a.PushDirRandom;
        PushDirFixed.IsChecked = !a.PushDirRandom;
        PushForceRand.IsChecked = a.PushForceRandom;
        PushForceFixed.IsChecked = !a.PushForceRandom;
        PushIntervalRand.IsChecked = a.PushIntervalRandom;
        PushIntervalFixed.IsChecked = !a.PushIntervalRandom;
        foreach (var b in _sliderBindings) b.Load(a);
        UpdateChoicePanels();

        var monitors = _app.MonitorList;
        MonitorRow.Visibility = monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var mon = _app.MonitorOf(a);
        MonitorLabel.Text = mon?.Label ?? Loc.T("panel.monitorUnknown");
        _updating = was;
    }

    private void OnGeometryChanged(ActiveItem a)
    {
        if (a.Id == _app.SelectedActiveId && _app.PanelMode == PanelMode.Active) FillActiveValues(a);
    }

    private void WithSelected(Action<ActiveItem> change)
    {
        if (_updating || _app.SelectedActiveId == null) return;
        _app.UpdateActive(_app.SelectedActiveId, change);
    }

    private void PosBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitPosition();
    }

    private void PosBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitPosition();

    private void CommitPosition()
    {
        if (_updating || _app.SelectedActive is not ActiveItem cur) return;
        if (!int.TryParse(XBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(YBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            FillActiveValues(cur);
            return;
        }
        if (x != cur.X || y != cur.Y) _app.Relocate(cur.Id, x, y);
    }

    private void BuildAnchorButtons()
    {
        string[] keys = { "anchor.tl", "anchor.tc", "anchor.tr", "anchor.ml", "anchor.mc", "anchor.mr", "anchor.bl", "anchor.bc", "anchor.br" };
        for (int i = 0; i < 9; i++)
        {
            int index = i;
            var b = new Button
            {
                Width = 12,
                Height = 7,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                Background = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0),
            };
            b.SetResourceReference(ToolTipProperty, "s." + keys[i]);
            b.Click += (_, _) => PlaceAt(index);
            AnchorGrid.Children.Add(b);
        }
    }

    /// <summary>9분할 위치: GIF가 있는 모니터의 작업 영역 기준, 가장자리에서 24px 띄움.</summary>
    private void PlaceAt(int index)
    {
        var a = _app.SelectedActive;
        if (a == null) return;
        var (w, h) = _app.ShownSize(a);
        var work = Native.WorkAreaAt(a.X + w / 2, a.Y + h / 2);
        const int m = 24;
        int col = index % 3, row = index / 3;
        int x = col == 0 ? work.Left + m : col == 1 ? (work.Left + work.Right - w) / 2 : work.Right - w - m;
        int y = row == 0 ? work.Top + m : row == 1 ? (work.Top + work.Bottom - h) / 2 : work.Bottom - h - m;
        _app.Relocate(a.Id, x, y);
    }

    private void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MonitorButton.ContextMenu;
        menu.Items.Clear();
        var current = _app.SelectedActive is ActiveItem a ? _app.MonitorOf(a) : null;
        foreach (var m in _app.MonitorList)
        {
            int index = m.Index;
            var item = new MenuItem { Header = m.Label + (current?.Index == m.Index ? "  ✓" : "") };
            item.Click += (_, _) => _app.MoveSelectedToMonitor(index);
            menu.Items.Add(item);
        }
        OpenMenuBelow(MonitorButton);
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText == null) return;
        ScaleText.Text = $"{e.NewValue:0}%";
        if (!_updating) _app.ScaleSelectedTo(e.NewValue / 100.0);
    }

    private void ResetScale_Click(object sender, RoutedEventArgs e) => _app.ScaleSelectedTo(1.0);

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText == null) return;
        OpacityText.Text = $"{e.NewValue:0}%";
        double v = e.NewValue / 100.0;
        WithSelected(a => a.Opacity = v);
    }

    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            if (_previewPlayer != null && !_updating) _previewPlayer.Speed = speed;
            WithSelected(a => a.Speed = speed);
        }
    }

    private void FlipToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = FlipToggle.IsChecked == true;
        PreviewFlip.ScaleX = on ? -1 : 1;
        WithSelected(a => a.FlipX = on);
    }

    private void ClickThroughToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = ClickThroughToggle.IsChecked == true;
        WithSelected(a => a.ClickThrough = on);
    }

    private void PixelToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || _app.SelectedPhotoId == null) return;
        _app.SetPixelated(_app.SelectedPhotoId, PixelToggle.IsChecked == true);
        RefreshPanel();
    }

    private void Motion_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string mode || SlideOptions == null) return;
        UpdateMotionOptions(mode);
        if (_updating) return;
        // 미끄러짐은 마우스로 던져야 하므로 클릭 통과를 끈다
        bool turnOffClickThrough = mode == Motion.Slide && _app.SelectedActive is { ClickThrough: true };
        WithSelected(a =>
        {
            a.MotionMode = mode;
            if (turnOffClickThrough) a.ClickThrough = false;
        });
        if (turnOffClickThrough)
        {
            ClickThroughToggle.IsChecked = false;
            ShowToast(Loc.T("notice.slideClickThroughOff"));
        }
    }

    /// <summary>움직임 모드에 맞는 옵션만 보이기.</summary>
    private void UpdateMotionOptions(string mode)
    {
        mode = Motion.Normalize(mode);
        MotionOptions.Visibility = mode == Motion.None ? Visibility.Collapsed : Visibility.Visible;
        MotionSpeedRow.Visibility = mode == Motion.Slide ? Visibility.Collapsed : Visibility.Visible;
        JumpOptions.Visibility = mode == Motion.Fall ? Visibility.Visible : Visibility.Collapsed;
        GravityRow.Visibility = mode is Motion.Walk or Motion.Fall ? Visibility.Visible : Visibility.Collapsed;
        bool gravity = GravityToggle.IsChecked != false;
        GravityHint.Text = Loc.T(gravity ? "panel.gravityOnHint" : (mode == Motion.Fall ? "panel.gravityOffJumpHint" : "panel.gravityOffWalkHint"));
        SlideOptions.Visibility = mode == Motion.Slide ? Visibility.Visible : Visibility.Collapsed;
        // 스스로 움직이는 중에는 위치 직접 입력 대신 9분할·모니터 이동만
        XBox.IsEnabled = YBox.IsEnabled = !Motion.IsAutonomous(mode);
    }

    // ── 움직임 세부 설정: 슬라이더 묶음 · 랜덤/고정 선택 ─────────────────

    private sealed record SliderBinding(
        System.Windows.Controls.Slider Slider, TextBlock Text, Func<double, string> Format,
        Func<ActiveItem, double> Get, Action<ActiveItem, double> Set)
    {
        public void Load(ActiveItem a)
        {
            Slider.Value = Get(a);
            Text.Text = Format(Slider.Value);
        }
    }

    private readonly List<SliderBinding> _sliderBindings = new();

    private static string Px(double v) => $"{v:0} px";
    private static string PxPerSec(double v) => $"{v:0} px/s";

    /// <summary>새 슬라이더들을 ActiveItem 값에 연결. min/max 짝은 서로 넘지 않게 맞춘다.</summary>
    private void BindMotionSliders()
    {
        SliderBinding Bind(System.Windows.Controls.Slider slider, TextBlock text, Func<double, string> fmt,
            Func<ActiveItem, double> get, Action<ActiveItem, double> set,
            System.Windows.Controls.Slider? keepAtLeast = null, System.Windows.Controls.Slider? keepAtMost = null)
        {
            var b = new SliderBinding(slider, text, fmt, get, set);
            slider.ValueChanged += (_, e) =>
            {
                double v = e.NewValue;
                text.Text = fmt(v);
                if (_updating) return;
                // 최소가 최대를 넘거나 최대가 최소보다 작아지면 짝을 같이 움직임
                if (keepAtLeast != null && keepAtLeast.Value < v) keepAtLeast.Value = v;
                if (keepAtMost != null && keepAtMost.Value > v) keepAtMost.Value = v;
                WithSelected(a => set(a, v));
            };
            _sliderBindings.Add(b);
            return b;
        }

        Bind(JumpHeightMinSlider, JumpHeightMinText, Px, a => a.JumpHeightMin, (a, v) => a.JumpHeightMin = v, keepAtLeast: JumpHeightMaxSlider);
        Bind(JumpHeightMaxSlider, JumpHeightMaxText, Px, a => a.JumpHeightMax, (a, v) => a.JumpHeightMax = v, keepAtMost: JumpHeightMinSlider);
        Bind(JumpAngleRangeSlider, JumpAngleRangeText, v => $"± {v:0}°", a => a.JumpAngleRange, (a, v) => a.JumpAngleRange = v);
        Bind(PushAngleSlider, PushAngleText, PushAngleText_, a => a.PushAngle, (a, v) => a.PushAngle = v);
        Bind(PushForceSlider, PushForceText, PxPerSec, a => a.PushForce, (a, v) => a.PushForce = v);
        Bind(PushForceMinSlider, PushForceMinText, PxPerSec, a => a.PushForceMin, (a, v) => a.PushForceMin = v, keepAtLeast: PushForceMaxSlider);
        Bind(PushForceMaxSlider, PushForceMaxText, PxPerSec, a => a.PushForceMax, (a, v) => a.PushForceMax = v, keepAtMost: PushForceMinSlider);
        Bind(PushIntervalSlider, PushIntervalText, SecondsText, a => a.PushInterval, (a, v) => a.PushInterval = v);
        Bind(PushIntervalMinSlider, PushIntervalMinText, SecondsText, a => a.PushIntervalMin, (a, v) => a.PushIntervalMin = v, keepAtLeast: PushIntervalMaxSlider);
        Bind(PushIntervalMaxSlider, PushIntervalMaxText, SecondsText, a => a.PushIntervalMax, (a, v) => a.PushIntervalMax = v, keepAtMost: PushIntervalMinSlider);
    }

    /// <summary>0° 오른쪽, 90° 위 … 화살표로 함께 표시.</summary>
    private static string PushAngleText_(double deg)
    {
        string[] arrows = { "→", "↗", "↑", "↖", "←", "↙", "↓", "↘" };
        int i = (int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8;
        return $"{deg:0}° {arrows[i]}";
    }

    /// <summary>랜덤/고정 선택에 맞는 슬라이더만 보이기.</summary>
    private void UpdateChoicePanels()
    {
        static Visibility V(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        JumpHeightFixedPanel.Visibility = V(JumpHeightRand.IsChecked != true);
        JumpHeightRandPanel.Visibility = V(JumpHeightRand.IsChecked == true);
        JumpDirFixedPanel.Visibility = V(JumpDirRand.IsChecked != true);
        JumpDirRandPanel.Visibility = V(JumpDirRand.IsChecked == true);
        AutoPushPanel.Visibility = V(SlideAutoToggle.IsChecked == true);
        PushDirFixedPanel.Visibility = V(PushDirFixed.IsChecked == true);
        PushForceFixedPanel.Visibility = V(PushForceFixed.IsChecked == true);
        PushForceRandPanel.Visibility = V(PushForceFixed.IsChecked != true);
        PushIntervalFixedPanel.Visibility = V(PushIntervalFixed.IsChecked == true);
        PushIntervalRandPanel.Visibility = V(PushIntervalFixed.IsChecked != true);
    }

    private void MotionChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag || PushIntervalRandPanel == null) return;
        UpdateChoicePanels();
        var parts = tag.Split(':');
        bool random = parts[1] == "random";
        switch (parts[0])
        {
            case "jumpHeight": WithSelected(a => a.JumpHeightRandom = random); break;
            case "jumpDir": WithSelected(a => a.JumpAngleRandom = random); break;
            case "pushDir": WithSelected(a => a.PushDirRandom = random); break;
            case "pushForce": WithSelected(a => a.PushForceRandom = random); break;
            case "pushInterval": WithSelected(a => a.PushIntervalRandom = random); break;
        }
    }

    private void SlideAuto_Click(object sender, RoutedEventArgs e)
    {
        bool on = SlideAutoToggle.IsChecked == true;
        UpdateChoicePanels();
        WithSelected(a => a.SlideAuto = on);
    }

    private void Gravity_Click(object sender, RoutedEventArgs e)
    {
        bool on = GravityToggle.IsChecked == true;
        if (_app.SelectedActive is ActiveItem a) UpdateMotionOptions(a.MotionMode);
        WithSelected(x => x.MotionGravity = on);
    }

    private void Layer_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string layer || LayerHint == null) return;
        LayerHint.Text = Loc.T(layer == Layers.Desktop ? "panel.layerDesktopHint" : "panel.layerTopHint");
        WithSelected(a => a.Layer = Layers.Normalize(layer));
    }

    private static string SecondsText(double seconds) => Loc.F("unit.seconds", seconds.ToString("0.0#", CultureInfo.CurrentCulture));

    private static string AngleText(double deg) =>
        Math.Abs(deg) < 0.5 ? Loc.T("panel.angleVertical") : deg > 0 ? $"{deg:0}° →" : $"← {-deg:0}°";

    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        bool on = JumpToggle.IsChecked == true;
        JumpSliders.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        WithSelected(a => a.JumpEnabled = on);
    }

    private void JumpHeight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JumpHeightText == null) return;
        double v = Math.Round(e.NewValue / 10) * 10;
        JumpHeightText.Text = $"{v:0} px";
        WithSelected(a => a.JumpHeight = v);
    }

    private void JumpInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JumpIntervalText == null) return;
        double v = Math.Round(e.NewValue * 2) / 2;
        JumpIntervalText.Text = SecondsText(v);
        WithSelected(a => a.JumpInterval = v);
    }

    private void JumpAngle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JumpAngleText == null) return;
        double v = Math.Round(e.NewValue);
        JumpAngleText.Text = AngleText(v);
        WithSelected(a => a.JumpAngle = v);
    }

    private void Friction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FrictionText == null) return;
        double v = Math.Round(e.NewValue);
        FrictionText.Text = $"{v:0} / 10";
        WithSelected(a => a.SlideFriction = v);
    }

    private static string DelayText(double ms) =>
        ms < 1 ? Loc.T("set.immediately") : Loc.F("unit.seconds", (ms / 1000).ToString("0.0#", CultureInfo.CurrentCulture));

    private void HoldSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoldText == null) return;
        HoldText.Text = DelayText(e.NewValue);
        if (!_updating && ResumeSlider != null) _app.SetPointerTiming((int)e.NewValue, (int)ResumeSlider.Value);
    }

    private void ResumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ResumeText == null) return;
        ResumeText.Text = DelayText(e.NewValue);
        if (!_updating && HoldSlider != null) _app.SetPointerTiming((int)HoldSlider.Value, (int)e.NewValue);
    }

    private void MotionSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MotionSpeedText == null) return;
        MotionSpeedText.Text = $"{e.NewValue:0} px/s";
        double v = Math.Round(e.NewValue);
        WithSelected(a => a.MotionSpeed = v);
    }

    private void FaceDir_Click(object sender, RoutedEventArgs e)
    {
        bool on = FaceDirToggle.IsChecked == true;
        WithSelected(a => a.MotionFaceDirection = on);
    }

    private void BgRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedPhotoId == null) return;
        _app.OpenBackgroundRemover(_app.SelectedPhotoId, _app.PanelMode == PanelMode.Active ? _app.SelectedActiveId : null);
    }

    private void RemoveFromScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedActiveId != null) _app.RemoveActive(_app.SelectedActiveId);
    }

    private void AddToScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedPhotoId != null) _app.AddToScreen(_app.SelectedPhotoId);
    }

    private void ReplaceSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedActiveId != null && _app.SelectedPhotoId != null) _app.ReplaceActive(_app.SelectedActiveId, _app.SelectedPhotoId);
    }

    // ── 설정 탭 ──────────────────────────────────────────────

    private void RefreshSettings()
    {
        bool was = _updating;
        _updating = true;
        var s = _app.State.Settings;
        LangEn.IsChecked = Loc.Language == Loc.English;
        LangKo.IsChecked = Loc.Language == Loc.Korean;
        StartWithWindowsToggle.IsChecked = s.StartWithWindows;
        OpenOnStartToggle.IsChecked = s.OpenWindowOnStart;
        CopyOnAddToggle.IsChecked = s.CopyOnAdd;
        HoldSlider.Value = s.HoldToGrabMs;
        HoldText.Text = DelayText(s.HoldToGrabMs);
        ResumeSlider.Value = s.ResumeDelayMs;
        ResumeText.Text = DelayText(s.ResumeDelayMs);
        DataFolderText.Text = Store.DataDir;
        VersionText.Text = Loc.F("set.version", AppController.AppVersion);
        RefreshDiagnostics();
        _updating = was;
    }

    private void RefreshDiagnostics()
    {
        var mons = _app.MonitorList;
        DiagText.Text =
            $"GifPin {AppController.AppVersion} · {Environment.OSVersion.VersionString}\n" +
            Loc.F("diag.summary", _app.State.Library.Count, _app.State.Active.Count, mons.Count) + "\n" +
            Loc.T("diag.logFile") + ": " + Log.FilePath;
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string lang) return;
        _app.SetLanguage(lang);
    }

    private void BuildHotkeyRows()
    {
        HotkeyRows.Children.Clear();
        var rows = new (string titleKey, string keys)[]
        {
            ("hotkey.edit", "Ctrl+Alt+E"),
            ("hotkey.hide", "Ctrl+Alt+H"),
            ("hotkey.quit", "Ctrl+Alt+Q"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var dock = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };
            var keys = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in rows[i].keys.Split('+'))
            {
                keys.Children.Add(new Border
                {
                    Margin = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(8, 3, 8, 3),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BorderStrongBrush"),
                    Background = (System.Windows.Media.Brush)FindResource("CardBrush"),
                    Child = new TextBlock { Text = k, FontSize = 12, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont") },
                });
            }
            DockPanel.SetDock(keys, Dock.Right);
            dock.Children.Add(keys);
            dock.Children.Add(new TextBlock { Text = Loc.T(rows[i].titleKey), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            HotkeyRows.Children.Add(dock);
            if (i < rows.Length - 1)
                HotkeyRows.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)FindResource("LineBrush") });
        }
        if (_app.Engine.HotkeyFailures.Count > 0)
            HotkeyRows.Children.Add(new TextBlock
            {
                Text = Loc.F("hotkey.failed", string.Join(", ", _app.Engine.HotkeyFailures)),
                Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
                Margin = new Thickness(18, 0, 18, 12),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        if (!_updating) _app.SetStartWithWindows(StartWithWindowsToggle.IsChecked == true);
    }

    private void OpenOnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.State.Settings.OpenWindowOnStart = OpenOnStartToggle.IsChecked == true;
        _app.Save();
    }

    private void CopyOnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.State.Settings.CopyOnAdd = CopyOnAddToggle.IsChecked == true;
        _app.Save();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.DataDir);
        Process.Start("explorer.exe", $"\"{Store.DataDir}\"");
    }

    private void RecoverGather_Click(object sender, RoutedEventArgs e) => _app.GatherAllToCenter();
    private void RecoverShow_Click(object sender, RoutedEventArgs e) { _app.ShowAll(); RefreshHiddenToggle(); }
    private void RecoverClick_Click(object sender, RoutedEventArgs e) => _app.DisableClickThroughAll();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        Process.Start("explorer.exe", $"\"{Log.Folder}\"");
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_app.BuildDiagnostics());
            ShowToast(Loc.T("diag.copied"));
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard copy failed", ex);
        }
    }

    // ── 토스트 ───────────────────────────────────────────────

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
