using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private bool _previewAnimated;
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
        _app.AutostartChanged += RefreshSettings; // 트레이 메뉴에서 바꾼 경우
        _app.SettingsChanged += RefreshSettings;
        _app.ScenesChanged += BuildSceneRows;
        _app.ScenesChanged += RefreshSceneButton;
        _app.HotkeysChanged += BuildHotkeyRows;
        _app.RuleStateChanged += RefreshRuleStatus;
        _app.UpdateStatusChanged += RefreshUpdateStatus;
        _app.ThemeChanged += OnLanguageChanged; // 코드로 만든 줄·메뉴도 새 색으로 다시 만듦
        PreviewKeyDown += OnPreviewKeyDownForHotkey;
        Deactivated += (_, _) => CancelHotkeyCapture();
        Loc.Changed += OnLanguageChanged;

        BindMotionSliders();
        BindEffects();
        BuildAnchorButtons();
        BuildHotkeyRows();
        BuildSceneRows();
        RefreshSceneButton();
        BuildThemePicker();
        RestoreSavedBounds();
        RefreshSettings();
        RefreshHiddenToggle();
        UpdateCounts();
        UpdateSortLabel();
        _updating = false;
        RefreshPanel();

        IsVisibleChanged += (_, _) => UpdatePerfTimer();
        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += OnClosing;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OnLanguageChanged()
    {
        BuildThemePicker();
        UpdateCounts();
        UpdateSortLabel();
        BuildHotkeyRows();
        BuildSceneRows();
        RefreshSceneButton();
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
        UpdatePerfTimer();
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
            Filter = Library.OpenFileFilter + "|" + VideoConverter.Filter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await _app.AddFilesAsync(dlg.FileNames);
    }

    private void Sprite_Click(object sender, RoutedEventArgs e) => _app.OpenSpriteImport();
    private void Video_Click(object sender, RoutedEventArgs e) => _app.OpenVideoImport();

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

    private void PackButton_Click(object sender, RoutedEventArgs e)
    {
        BuildRecentPacksMenu();
        OpenMenuBelow(PackButton);
    }

    /// <summary>팩 메뉴의 "최근 팩": 최근에 가져오거나 내보낸 팩을 바로 다시 가져옴.</summary>
    private int BuildRecentPacksMenu()
    {
        if (PackButton.ContextMenu is not ContextMenu menu) return 0;
        foreach (var old in menu.Items.OfType<MenuItem>().Where(m => Equals(m.Tag, "recentPacks")).ToList()) menu.Items.Remove(old);
        var recent = _app.RecentPacks();
        if (recent.Count == 0) return 0;
        var sub = new MenuItem { Header = Loc.T("pack.recent"), Tag = "recentPacks" };
        foreach (var path in recent)
        {
            string p = path;
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(p).Replace("_", "__"), ToolTip = p };
            item.Click += async (_, _) => await _app.ImportPackAsync(p);
            sub.Items.Add(item);
        }
        menu.Items.Insert(1, sub);
        return recent.Count;
    }

    internal int RecentPacksMenuForTest() => BuildRecentPacksMenu();

    private async void PackImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("pack.importTitle"), Filter = AssetPack.ImportFilter, InitialDirectory = _app.PackFolder(),
        };
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
            Filter = AssetPack.ExportFilter,
            DefaultExt = AssetPack.Extension,
            AddExtension = true,
            InitialDirectory = _app.PackFolder(),
            FileName = (safe.Length > 0 ? safe : "GifPin pack") + AssetPack.Extension,
        };
        if (dlg.ShowDialog(this) != true) return;
        string file = dlg.FileName;
        if (!file.EndsWith(AssetPack.Extension, StringComparison.OrdinalIgnoreCase)) file += AssetPack.Extension; // 늘 .gifpin으로
        await _app.ExportPackAsync(file, Path.GetFileNameWithoutExtension(file), photos, withLayout);
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
        if (vm.Photo.FrameCount < 2 || !_app.State.Settings.LibraryPreview) return;
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
            StyleButton.Visibility = Visibility.Visible;
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
            StyleButton.Visibility = Visibility.Collapsed;
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
        bool animate = _app.State.Settings.LibraryPreview;
        if (path != _previewPath || animate != _previewAnimated)
        {
            _previewPath = path;
            _previewAnimated = animate;
            _previewPlayer.Stop();
            PreviewImage.Source = Images.LoadThumb(photo);
            if (animate) _previewPlayer.Play(path, 320); // 끄면 정지 그림만 (메모리·CPU 절약)
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
        LoadEffects(a);

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
                BorderThickness = new Thickness(0),
            };
            b.SetResourceReference(BackgroundProperty, "AnchorDotBrush"); // 테마가 바뀌어도 따라감
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

    // ── 효과 레이어: 컨트롤의 Tag = ActiveItem 속성 이름 ───────────────

    private readonly List<ToggleButton> _fxToggles = new();
    private readonly List<ToggleButton> _fxKindToggles = new(); // 파티클 모양 (여러 개 선택)

    private static string KindOf(ToggleButton t) => ((string)t.Tag).Substring("FxParticleKind+".Length);

    /// <summary>파티클 모양 켜고 끄기. 하나는 늘 남아 있어야 함. 여러 개면 같은 비율로 섞임.</summary>
    private void FxKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        var kinds = _fxKindToggles.Where(t => t.IsChecked == true).Select(KindOf).ToList();
        if (kinds.Count == 0) { tb.IsChecked = true; return; } // 마지막 하나는 끌 수 없음
        string value = string.Join(",", kinds);
        WithSelected(a => a.FxParticleKind = value);
        if (_app.SelectedActive is ActiveItem a) UpdateEffectPanels(a);
    }

    /// <summary>"모두 끄기"가 끄는 효과 (세부 옵션 스위치는 그대로 둠).</summary>
    private static readonly string[] MainEffects =
        { "FxFloat", "FxBreathe", "FxSway", "FxSpin", "FxBlink", "FxSquash", "FxDust", "FxTrail", "FxShadow", "FxGlow", "FxParticles" };

    private void PickParticleImages_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedActiveId is not string id) return;
        var picker = new ParticleImagePicker(_app, id) { Owner = this };
        picker.ShowDialog();
        if (_app.SelectedActive is ActiveItem a)
        {
            bool was = _updating;
            _updating = true;
            LoadEffects(a);
            _updating = was;
        }
    }
    private readonly List<RadioButton> _fxChoices = new();
    private readonly List<System.Windows.Controls.Slider> _fxSliders = new();

    /// <summary>슬라이더 값 표시 형식.</summary>
    private static string FxFormat(string prop, double v) => prop switch
    {
        "FxFloatAmp" or "FxParticleSize" => $"{v:0} px",
        "FxGlowSize" or "FxTrailSpacing" => $"{v:0} px",
        "FxFloatPeriod" or "FxBreathePeriod" or "FxSwayPeriod" or "FxBlinkPeriod" or "FxParticleLife" => SecondsText(Math.Round(v, 1)),
        "FxSwayAngle" => $"± {v:0}°",
        "FxSpinSpeed" => $"{v:0}°/s",
        "FxTrailCount" => $"{v:0}",
        "FxParticleRate" => Loc.F("fx.perSecond", v.ToString("0", CultureInfo.CurrentCulture)),
        _ => $"{v:0}%",
    };

    private static System.Reflection.PropertyInfo? FxProp(string name) => typeof(ActiveItem).GetProperty(name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>효과 패널의 스위치·선택·슬라이더를 ActiveItem 속성에 연결.</summary>
    private void BindEffects()
    {
        foreach (var d in Descendants(FxPanel))
        {
            switch (d)
            {
                case RadioButton rb when rb.Tag is string t && t.StartsWith("Fx"): _fxChoices.Add(rb); break;
                case ToggleButton kb when kb.Tag is string t && t.StartsWith("FxParticleKind+"): _fxKindToggles.Add(kb); break;
                case ToggleButton tb when tb.Tag is string t && t.StartsWith("Fx"): _fxToggles.Add(tb); break;
                case System.Windows.Controls.Slider sl when sl.Tag is string prop:
                    _fxSliders.Add(sl);
                    sl.ValueChanged += (_, e) =>
                    {
                        if (FindName(prop + "Text") is TextBlock text) text.Text = FxFormat(prop, e.NewValue);
                        if (_updating) return;
                        double v = Math.Round(e.NewValue, 2);
                        WithSelected(a => FxProp(prop)?.SetValue(a, v));
                    };
                    break;
            }
        }
    }

    private void LoadEffects(ActiveItem a)
    {
        foreach (var tb in _fxToggles)
            tb.IsChecked = FxProp((string)tb.Tag)?.GetValue(a) is true;
        foreach (var rb in _fxChoices)
        {
            var parts = ((string)rb.Tag).Split('=', 2);
            var value = FxProp(parts[0])?.GetValue(a);
            rb.IsChecked = string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), parts[1], StringComparison.OrdinalIgnoreCase);
        }
        var kinds = Fx.ParticleKinds(a);
        foreach (var kb in _fxKindToggles) kb.IsChecked = Array.IndexOf(kinds, KindOf(kb)) >= 0;
        BuildGlowColors(a);
        BuildParticleColors(a);
        foreach (var sl in _fxSliders)
        {
            var prop = (string)sl.Tag;
            if (FxProp(prop)?.GetValue(a) is double v) sl.Value = v;
            if (FindName(prop + "Text") is TextBlock text) text.Text = FxFormat(prop, sl.Value);
        }
        UpdateEffectPanels(a);
    }

    /// <summary>켜진 효과의 세부 설정만 보이고, 상황 안내를 맞춘다.</summary>
    private void UpdateEffectPanels(ActiveItem a)
    {
        foreach (var tb in _fxToggles)
            if (FindName((string)tb.Tag + "Opts") is FrameworkElement opts)
                opts.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FxClickHint.Visibility = a.FxParticleMode == Fx.Click && a.ClickThrough ? Visibility.Visible : Visibility.Collapsed;

        // 내 이미지 파티클
        var kindList = Fx.ParticleKinds(a);
        bool image = Array.IndexOf(kindList, Fx.Image) >= 0;
        bool onlyImage = image && kindList.Length == 1;
        ParticleImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        // 색은 기본 모양(하트·별 등)이나 물들인 이미지에 쓰임
        ParticleColorPanel.Visibility = onlyImage && a.FxParticleKeepColors ? Visibility.Collapsed : Visibility.Visible;
        ParticleCpuWarn.Visibility = image && a.FxParticleAnimate ? Visibility.Visible : Visibility.Collapsed;
        if (image)
        {
            var photos = _app.ParticlePhotos(a);
            ParticleImageCount.Text = photos.Count == 0 ? Loc.T("fx.imagesNone") : Loc.F("fx.imagesCount", photos.Count);
            ParticleImageThumbs.Children.Clear();
            foreach (var ph in photos)
                ParticleImageThumbs.Children.Add(new Border
                {
                    Width = 34, Height = 34, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(6),
                    Background = (System.Windows.Media.Brush)FindResource("CheckerBrush"),
                    ToolTip = ph.Name,
                    Child = new Image { Source = Images.LoadThumb(ph), Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(2) },
                });
        }
        FxMotionHint.Visibility = (a.FxSquash || a.FxDust || a.FxTrail) && !Motion.IsMoving(a.MotionMode) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FxToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string prop) return;
        bool on = tb.IsChecked == true;
        WithSelected(a => FxProp(prop)?.SetValue(a, on));
        if (_app.SelectedActive is ActiveItem a) UpdateEffectPanels(a);
    }

    private void FxChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string tag || FxClickHint == null) return;
        var parts = tag.Split('=', 2);
        var pi = FxProp(parts[0]);
        if (pi == null) return;
        object value = pi.PropertyType == typeof(bool) ? bool.Parse(parts[1]) : parts[1];
        WithSelected(a => pi.SetValue(a, value));
        if (_app.SelectedActive is ActiveItem a) UpdateEffectPanels(a);
    }

    private void FxAllOff_Click(object sender, RoutedEventArgs e)
    {
        WithSelected(a =>
        {
            foreach (var prop in MainEffects) FxProp(prop)?.SetValue(a, false);
        });
        if (_app.SelectedActive is ActiveItem a)
        {
            bool was = _updating;
            _updating = true;
            LoadEffects(a);
            _updating = was;
        }
    }

    private void FxFps_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string t) return;
        var s = _app.State.Settings;
        _app.SetEffectsSettings(int.Parse(t, CultureInfo.InvariantCulture), s.PauseEffectsFullscreen, s.PauseEffectsOnBattery, s.AutoLightEffects, s.AutoLightCpuPercent);
    }

    private void FxPause_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var s = _app.State.Settings;
        _app.SetEffectsSettings(s.EffectsFps, PauseFxFullscreenToggle.IsChecked == true, PauseFxBatteryToggle.IsChecked == true,
            AutoLightToggle.IsChecked == true, s.AutoLightCpuPercent);
        AutoLightOptions.Visibility = AutoLightToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoLightCpu_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AutoLightCpuText == null) return;
        AutoLightCpuText.Text = $"{e.NewValue:0}%";
        if (_updating) return;
        var s = _app.State.Settings;
        _app.SetEffectsSettings(s.EffectsFps, s.PauseEffectsFullscreen, s.PauseEffectsOnBattery, s.AutoLightEffects, (int)e.NewValue);
    }

    /// <summary>설정 탭: 지금 절약 모드인지와 다른 프로그램의 CPU 사용률.</summary>
    private void RefreshLightStatus()
    {
        if (AutoLightStatus == null) return;
        AutoLightStatus.Text = Loc.F(_app.Engine.LightMode ? "set.autoLightOn" : "set.autoLightOff", Math.Round(_app.Engine.OtherCpuPercent));
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
        var autostart = Autostart.GetStatus(); // 설치 프로그램이나 Windows 설정에서 바뀌었을 수 있으니 실제 상태로
        StartWithWindowsToggle.IsChecked = autostart == Autostart.Status.On;
        AutostartBlockedText.Visibility = autostart == Autostart.Status.BlockedByWindows ? Visibility.Visible : Visibility.Collapsed;
        OpenOnStartToggle.IsChecked = s.OpenWindowOnStart;
        CopyOnAddToggle.IsChecked = s.CopyOnAdd;
        Fps30.IsChecked = s.EffectsFps < 45;
        Fps60.IsChecked = s.EffectsFps >= 45;
        PauseFxFullscreenToggle.IsChecked = s.PauseEffectsFullscreen;
        AutoLightToggle.IsChecked = s.AutoLightEffects;
        AutoLightOptions.Visibility = s.AutoLightEffects ? Visibility.Visible : Visibility.Collapsed;
        AutoLightCpuSlider.Value = s.AutoLightCpuPercent;
        AutoLightCpuText.Text = $"{s.AutoLightCpuPercent}%";
        RefreshLightStatus();
        PauseFxBatteryToggle.IsChecked = s.PauseEffectsOnBattery;
        EffectsOffToggle.IsChecked = s.EffectsOff;
        HideFromCaptureToggle.IsChecked = s.HideFromCapture;
        HoverFadeToggle.IsChecked = s.HoverFade;
        HoverFadeOptions.Visibility = s.HoverFade ? Visibility.Visible : Visibility.Collapsed;
        HoverFadeSlider.Value = s.HoverFadeOpacity;
        HoverFadeText.Text = $"{s.HoverFadeOpacity}%";
        NewItemSizeSlider.Value = s.NewItemSize;
        NewItemSizeText.Text = $"{s.NewItemSize} px";
        var template = s.NewItemTemplate ?? new ActiveItem();
        NewLayerTop.IsChecked = Layers.Normalize(template.Layer) == Layers.Top;
        NewLayerDesktop.IsChecked = Layers.Normalize(template.Layer) == Layers.Desktop;
        NewClickThroughToggle.IsChecked = template.ClickThrough;
        TemplateSummaryText.Text = _app.TemplateSummary();
        RuleForeground.IsChecked = !s.HideForAppsWhileRunning;
        RuleRunning.IsChecked = s.HideForAppsWhileRunning;
        BuildAppRuleRows();
        RefreshRuleStatus();
        AutostartDelaySlider.Value = s.AutostartDelaySec;
        AutostartDelayText.Text = DelayText(s.AutostartDelaySec * 1000);
        CheckUpdatesToggle.IsChecked = s.CheckUpdates;
        StartupAnimationToggle.IsChecked = s.StartupAnimation;
        RefreshUpdateStatus();
        PerfSaver.IsChecked = s.PerfMode == PerfModes.Saver;
        PerfBalanced.IsChecked = s.PerfMode == PerfModes.Balanced;
        PerfQuality.IsChecked = s.PerfMode == PerfModes.Quality;
        PerfCustom.IsChecked = s.PerfMode == PerfModes.Custom;
        PerfModeHint.Text = Loc.T("perf.hint." + s.PerfMode);
        ParticleCapSlider.Value = s.MaxParticlesTotal;
        ParticleCapText.Text = $"{s.MaxParticlesTotal}";
        GifCapNone.IsChecked = s.GifFpsCap == 0;
        GifCap30.IsChecked = s.GifFpsCap == 30;
        GifCap20.IsChecked = s.GifFpsCap == 20;
        QualityLow.IsChecked = s.RenderQuality == "low";
        QualityMedium.IsChecked = s.RenderQuality == "medium";
        QualityHigh.IsChecked = s.RenderQuality == "high";
        MemorySlider.Value = s.MemoryBudgetMB;
        MemoryText.Text = $"{s.MemoryBudgetMB} MB";
        UnloadHiddenToggle.IsChecked = s.UnloadHidden;
        PauseOccludedToggle.IsChecked = s.PauseOccluded;
        PauseLockedToggle.IsChecked = s.PauseWhenLocked;
        LibraryPreviewToggle.IsChecked = s.LibraryPreview;
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

    // ── 단축키 지정 ──────────────────────────────────────────

    private string? _capturingAction;
    private Button? _capturingButton;

    private System.Windows.Media.Brush Res(string key) => (System.Windows.Media.Brush)FindResource(key);

    private FrameworkElement KeyChips(string text)
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        if (string.IsNullOrEmpty(text))
        {
            keys.Children.Add(new TextBlock { Text = Loc.T("hotkey.none"), Foreground = Res("TextMutedBrush"), FontSize = 12 });
            return keys;
        }
        foreach (var k in text.Split('+'))
        {
            keys.Children.Add(new Border
            {
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 2),
                BorderBrush = Res("BorderStrongBrush"),
                Background = Res("CardBrush"),
                Child = new TextBlock { Text = k, FontSize = 12, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Foreground = Res("TextBrush") },
            });
        }
        return keys;
    }

    private void BuildHotkeyRows()
    {
        _capturingAction = null;
        _capturingButton = null;
        HotkeyRows.Children.Clear();
        var actions = Hotkeys.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            string action = actions[i];
            var dock = new DockPanel { Margin = new Thickness(18, 8, 18, 8) };
            var button = new Button
            {
                MinWidth = 180, Height = 32, Padding = new Thickness(6, 0, 10, 0), Tag = action,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Content = KeyChips(_app.HotkeyText(action)),
                ToolTip = Loc.T("hotkey.changeTip"),
            };
            button.Click += HotkeyButton_Click;
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
            dock.Children.Add(new TextBlock { Text = Loc.T("hotkey." + action), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            HotkeyRows.Children.Add(dock);
            if (i < actions.Count - 1)
                HotkeyRows.Children.Add(new Border { Height = 1, Background = Res("LineBrush") });
        }
        HotkeyMessage.Text = _app.Engine.HotkeyFailures.Count > 0
            ? Loc.F("hotkey.failed", string.Join(", ", _app.Engine.HotkeyFailures)) : "";
        HotkeyMessage.Foreground = Res(_app.Engine.HotkeyFailures.Count > 0 ? "DangerBrush" : "TextMutedBrush");
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string action) return;
        CancelHotkeyCapture();
        _capturingAction = action;
        _capturingButton = b;
        _app.SuspendHotkeys(); // 지금 단축키를 누르면 그 동작이 실행되지 않게 잠시 해제
        b.Content = new TextBlock { Text = Loc.T("hotkey.pressKeys"), Foreground = Res("AccentLightBrush"), FontSize = 12 };
        HotkeyMessage.Text = Loc.T("hotkey.captureHint");
        HotkeyMessage.Foreground = Res("TextMutedBrush");
        b.Focus();
    }

    private void CancelHotkeyCapture()
    {
        if (_capturingAction == null) return;
        _capturingAction = null;
        _app.ResumeHotkeys();
        BuildHotkeyRows();
    }

    private void OnPreviewKeyDownForHotkey(object sender, KeyEventArgs e)
    {
        if (_capturingAction == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        string action = _capturingAction;
        if (key == Key.Escape && mods == ModifierKeys.None) { CancelHotkeyCapture(); return; }
        if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None)
        {
            _capturingAction = null;
            _app.SetHotkey(action, "");
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (Hotkeys.IsModifierKey(vk) || key is Key.LWin or Key.RWin) return; // 보조키만 누른 상태 → 계속 기다림
        uint m = 0;
        if ((mods & ModifierKeys.Control) != 0) m |= Hotkeys.ModCtrl;
        if ((mods & ModifierKeys.Alt) != 0) m |= Hotkeys.ModAlt;
        if ((mods & ModifierKeys.Shift) != 0) m |= Hotkeys.ModShift;
        if ((mods & ModifierKeys.Windows) != 0) m |= Hotkeys.ModWin;
        if (!Hotkeys.IsAllowed(m, vk))
        {
            HotkeyMessage.Text = Loc.T("hotkey.needModifier");
            HotkeyMessage.Foreground = Res("WarnTextBrush");
            return;
        }
        string text = Hotkeys.Format(m, vk);
        _capturingAction = null;
        if (!_app.SetHotkey(action, text))
        {
            _app.ResumeHotkeys();
            BuildHotkeyRows();
            HotkeyMessage.Text = Loc.F("hotkey.inUse", text);
            HotkeyMessage.Foreground = Res("WarnTextBrush");
        }
        else if (_app.Engine.HotkeyFailures.Contains(text))
        {
            HotkeyMessage.Text = Loc.F("hotkey.failed", text);
            HotkeyMessage.Foreground = Res("DangerBrush");
        }
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _capturingAction = null;
        _app.ResetHotkeys();
    }

    // ── 화면 표시 / 효과 끄기 ────────────────────────────────

    private void HideFromCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetHideFromCapture(HideFromCaptureToggle.IsChecked == true);
    }

    private void HoverFade_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = HoverFadeToggle.IsChecked == true;
        HoverFadeOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _app.SetHoverFade(on, (int)HoverFadeSlider.Value);
    }

    private void HoverFadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverFadeText == null) return;
        HoverFadeText.Text = $"{e.NewValue:0}%";
        if (_updating) return;
        _app.SetHoverFade(_app.State.Settings.HoverFade, (int)e.NewValue);
    }

    private void EffectsOff_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetEffectsOff(EffectsOffToggle.IsChecked == true);
    }

    // ── 성능 ─────────────────────────────────────────────────

    private void PerfMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string mode) return;
        _app.SetPerfMode(mode);
    }

    private void ParticleCap_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParticleCapText == null) return;
        ParticleCapText.Text = $"{e.NewValue:0}";
        if (_updating) return;
        int v = (int)e.NewValue;
        _app.SetPerfOption(s => s.MaxParticlesTotal = v);
    }

    private void GifCap_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string t) return;
        int v = int.Parse(t, CultureInfo.InvariantCulture);
        _app.SetPerfOption(s => s.GifFpsCap = v);
    }

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string q) return;
        _app.SetPerfOption(s => s.RenderQuality = q);
    }

    private void Memory_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryText == null) return;
        MemoryText.Text = $"{e.NewValue:0} MB";
        if (_updating) return;
        int v = (int)e.NewValue;
        _app.SetPerfOption(s => s.MemoryBudgetMB = v);
    }

    private void PerfToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool unload = UnloadHiddenToggle.IsChecked == true, occluded = PauseOccludedToggle.IsChecked == true, locked = PauseLockedToggle.IsChecked == true;
        _app.SetPerfOption(s => { s.UnloadHidden = unload; s.PauseOccluded = occluded; s.PauseWhenLocked = locked; });
    }

    private void LibraryPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetLibraryPreview(LibraryPreviewToggle.IsChecked == true);
    }

    // 성능 모니터: 설정 탭이 보이는 동안 1초마다
    private DispatcherTimer? _perfTimer;
    private bool _perfBusy;

    private void UpdatePerfTimer()
    {
        bool want = IsVisible && SettingsView.Visibility == Visibility.Visible;
        if (want && _perfTimer == null)
        {
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _perfTimer.Tick += async (_, _) =>
            {
                if (_perfBusy) return;
                _perfBusy = true;
                try { PerfMonitorText.Text = await _app.PerfReportAsync(); }
                catch (Exception ex) { Log.Warn("Performance monitor failed", ex); }
                finally { _perfBusy = false; }
                RefreshLightStatus();
            };
            _perfTimer.Start();
        }
        else if (!want && _perfTimer != null)
        {
            _perfTimer.Stop();
            _perfTimer = null;
        }
    }

    // ── 외곽 빛·파티클 색 (팔레트·스포이드·여러 색) ──────────────

    private static readonly string[] PresetColors = { "#FFFFFF", "#FFD54F", "#FF8A80", "#F48FB1", "#CE93D8", "#81D4FA", "#A5D6A7", "#FFAB40" };

    private static string NormHex(string? hex) => ColorPicker.TryParse(hex, out var c) ? ColorPicker.ToHex(c) : "";

    private static FrameworkElement Dot(System.Windows.Media.Brush fill) => new System.Windows.Shapes.Ellipse
    {
        Width = 14, Height = 14, Fill = fill, Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 1,
    };

    private static System.Windows.Media.Brush HexBrush(string hex) =>
        new System.Windows.Media.SolidColorBrush(ColorPicker.TryParse(hex, out var c) ? c : System.Windows.Media.Colors.White);

    private Button ColorToolButton(string glyph, string tip, Func<Task> onClick)
    {
        var b = new Button
        {
            Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(1), BorderThickness = new Thickness(0), ToolTip = tip,
            Content = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 13 },
        };
        b.SetResourceReference(ForegroundProperty, "IconBrush");
        b.Click += async (_, _) => await onClick();
        return b;
    }

    /// <summary>색 고르기 창 또는 스포이드로 색 하나 (취소면 null).</summary>
    private async Task<string?> AskColor(string initial, bool eyedropper)
    {
        System.Windows.Media.Color? c = eyedropper
            ? await ColorPicker.PickFromScreenAsync(this)
            : await ColorPicker.PickAsync(this, ColorPicker.TryParse(initial, out var ic) ? ic : System.Windows.Media.Colors.White, _app.State.Settings.RecentColors);
        if (c is not System.Windows.Media.Color picked) return null;
        string hex = ColorPicker.ToHex(picked);
        _app.AddRecentColor(hex);
        return hex;
    }

    /// <summary>외곽 빛 색: 기본 8색 + (고른 색이 그 밖이면) 그 색 + 팔레트 + 스포이드.</summary>
    private void BuildGlowColors(ActiveItem a)
    {
        if (GlowColorRow == null) return;
        GlowColorRow.Children.Clear();
        string cur = NormHex(a.FxGlowColor);
        var colors = PresetColors.ToList();
        if (cur.Length > 0 && !colors.Contains(cur)) colors.Add(cur);
        foreach (var hex in colors)
        {
            string value = hex;
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "FxGlowColorPick", Padding = new Thickness(0), Height = 26, Width = 28,
                Content = Dot(HexBrush(hex)), ToolTip = hex, IsChecked = hex == cur,
            };
            rb.Checked += (_, _) => { if (!_updating) WithSelected(x => x.FxGlowColor = value); };
            GlowColorRow.Children.Add(rb);
        }
        GlowColorRow.Children.Add(ColorToolButton("\uE790", Loc.T("color.paletteTip"), async () =>
        {
            if (await AskColor(cur, eyedropper: false) is string hex) { WithSelected(x => x.FxGlowColor = hex); RefreshColorRows(); }
        }));
        GlowColorRow.Children.Add(ColorToolButton("\uEF3C", Loc.T("color.eyedropperTip"), async () =>
        {
            if (await AskColor(cur, eyedropper: true) is string hex) { WithSelected(x => x.FxGlowColor = hex); RefreshColorRows(); }
        }));
    }

    /// <summary>
    /// 파티클 색: 무지개 / 여러 색 중복 선택(고른 색끼리 섞여 나옴) / 내 색 추가(팔레트·스포이드).
    /// 모든 색을 끄면 무지개로.
    /// </summary>
    private void BuildParticleColors(ActiveItem a)
    {
        if (ParticleColorRow == null) return;
        ParticleColorRow.Children.Clear();
        var selected = Fx.ParticleColors(a.FxParticleColor).ToList();
        bool rainbow = selected.Count == 0;
        var toggleStyle = (Style)FindResource("SegmentToggle");

        var rainbowBrush = new System.Windows.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (hex, off) in new[] { ("#FF8A80", 0.0), ("#FFD54F", 0.33), ("#81D4FA", 0.66), ("#CE93D8", 1.0) })
            rainbowBrush.GradientStops.Add(new System.Windows.Media.GradientStop(((System.Windows.Media.SolidColorBrush)HexBrush(hex)).Color, off));
        var rb = new ToggleButton { Style = toggleStyle, Padding = new Thickness(0), Height = 26, Width = 28, Content = Dot(rainbowBrush), IsChecked = rainbow, ToolTip = Loc.T("fx.rainbow") };
        rb.Click += (_, _) => SetParticleColors(new List<string>());
        ParticleColorRow.Children.Add(rb);

        var colors = PresetColors.ToList();
        foreach (var hex in selected) if (!colors.Contains(hex)) colors.Add(hex); // 내가 추가한 색
        foreach (var hex in colors)
        {
            string value = hex;
            var tb = new ToggleButton { Style = toggleStyle, Padding = new Thickness(0), Height = 26, Width = 28, Content = Dot(HexBrush(hex)), IsChecked = selected.Contains(hex), ToolTip = hex };
            tb.Click += (_, _) =>
            {
                var list = Fx.ParticleColors(_app.SelectedActive?.FxParticleColor).ToList();
                if (tb.IsChecked == true) { if (!list.Contains(value)) list.Add(value); }
                else list.Remove(value);
                SetParticleColors(list);
            };
            ParticleColorRow.Children.Add(tb);
        }
        ParticleColorRow.Children.Add(ColorToolButton("\uE790", Loc.T("color.paletteTip"), async () =>
        {
            if (await AskColor(selected.LastOrDefault() ?? "#FFFFFF", eyedropper: false) is string hex) AddParticleColor(hex);
        }));
        ParticleColorRow.Children.Add(ColorToolButton("\uEF3C", Loc.T("color.eyedropperTip"), async () =>
        {
            if (await AskColor("#FFFFFF", eyedropper: true) is string hex) AddParticleColor(hex);
        }));
    }

    private void AddParticleColor(string hex)
    {
        var list = Fx.ParticleColors(_app.SelectedActive?.FxParticleColor).ToList();
        if (!list.Contains(hex)) list.Add(hex);
        SetParticleColors(list);
    }

    private void SetParticleColors(List<string> list)
    {
        string value = list.Count == 0 ? Fx.Rainbow : string.Join(",", list);
        WithSelected(x => x.FxParticleColor = value);
        RefreshColorRows();
    }

    private void RefreshColorRows()
    {
        if (_app.SelectedActive is not ActiveItem a) return;
        bool was = _updating;
        _updating = true;
        BuildGlowColors(a);
        BuildParticleColors(a);
        _updating = was;
    }

    // ── 테마 ─────────────────────────────────────────────────

    /// <summary>테마 고르기: 테마마다 작은 미리보기(바탕·카드·강조색)와 이름.</summary>
    private void BuildThemePicker()
    {
        if (ThemePicker == null) return;
        ThemePicker.Children.Clear();
        string current = _app.State.Settings.Theme;
        foreach (var name in Themes.All)
        {
            var (bg, card, accent, text) = Themes.Preview(name);
            var preview = new Border
            {
                Width = 104, Height = 58, CornerRadius = new CornerRadius(6), Background = bg,
                BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), ClipToBounds = true,
            };
            var inner = new Grid();
            inner.Children.Add(new Border { Height = 10, VerticalAlignment = VerticalAlignment.Top, Background = name == Themes.Xp || name == Themes.Classic ? Themes.Preview(name).accent : System.Windows.Media.Brushes.Transparent });
            inner.Children.Add(new Border
            {
                Margin = new Thickness(8, 16, 8, 8), CornerRadius = new CornerRadius(4), Background = card,
                Child = new StackPanel
                {
                    Margin = new Thickness(6, 5, 6, 0),
                    Children =
                    {
                        new Border { Height = 4, Width = 40, HorizontalAlignment = HorizontalAlignment.Left, Background = text, Opacity = 0.7, CornerRadius = new CornerRadius(2) },
                        new Border { Height = 8, Width = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0), Background = accent, CornerRadius = new CornerRadius(3) },
                    },
                },
            });
            if (name == Themes.Custom)
            {
                // 사용자 지정: 오른쪽 아래에 팔레트 표시
                var badge = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 4, 4),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = accent,
                    Child = new TextBlock { Text = "\uE790", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = card, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                inner.Children.Add(badge);
            }
            preview.Child = inner;
            string labelText = Loc.T("theme." + name);
            var label = new TextBlock { Text = labelText, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var stack = new StackPanel { Margin = new Thickness(2) };
            stack.Children.Add(preview);
            stack.Children.Add(label);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "theme", Content = stack, Height = double.NaN,
                Padding = new Thickness(6), Margin = new Thickness(0, 0, 6, 6), IsChecked = name == current, Tag = name,
                ToolTip = Loc.T("theme." + name + "Tip"),
            };
            rb.Checked += (_, _) =>
            {
                if (_updating || rb.Tag is not string t || t == _app.State.Settings.Theme) return;
                if (t == Themes.Custom) _customEditorOpen = true;
                Dispatcher.InvokeAsync(() => _app.SetTheme(t));
            };
            ThemePicker.Children.Add(rb);
        }
        BuildCustomThemePanel();
    }

    private bool? _customEditorOpen;

    /// <summary>
    /// 사용자 지정 테마: 출발 테마를 고르고, UI 색을 하나씩 바꿈. 색 칸을 누르면 팔레트(스포이드 포함)가 열리고
    /// 바꾸는 즉시 앱 전체에 적용됨. 직접 고른 색에는 점이 붙고 ↶ 버튼으로 하나씩 되돌릴 수 있음.
    /// </summary>
    private void BuildCustomThemePanel()
    {
        if (CustomThemePanel == null) return;
        CustomThemePanel.Children.Clear();
        var st = _app.State.Settings;
        bool open = _customEditorOpen ??= st.Theme == Themes.Custom;

        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Style = (Style)FindResource("SegmentToggle"), IsChecked = open, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "\uE790", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) },
                    new TextBlock { Text = Loc.T("customTheme.title"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = open ? "  \uE70E" : "  \uE70D", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        toggle.Click += (_, _) => { _customEditorOpen = toggle.IsChecked == true; BuildCustomThemePanel(); };
        CustomThemePanel.Children.Add(toggle);
        if (!open) return;

        CustomThemePanel.Children.Add(new TextBlock { Text = Loc.T("customTheme.desc"), Style = (Style)FindResource("Hint"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });

        // 출발 테마 + 모두 되돌리기
        var baseRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var resetAll = new Button { Content = Loc.T("customTheme.resetAll"), Height = 28, IsEnabled = st.CustomThemeColors.Count > 0 };
        resetAll.Click += (_, _) => Dispatcher.InvokeAsync(_app.ResetCustomColors);
        DockPanel.SetDock(resetAll, Dock.Right);
        baseRow.Children.Add(resetAll);
        var baseBox = new Border { Style = (Style)FindResource("SegmentBox"), HorizontalAlignment = HorizontalAlignment.Left };
        var bases = new WrapPanel();
        foreach (var b in Themes.Bases)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "customBase", Tag = b, Content = Loc.T("theme." + b),
                IsChecked = st.CustomThemeBase == b, Padding = new Thickness(9, 0, 9, 0),
            };
            rb.Checked += (_, _) => { if (!_updating && rb.Tag is string v && v != _app.State.Settings.CustomThemeBase) Dispatcher.InvokeAsync(() => _app.SetCustomBase(v)); };
            bases.Children.Add(rb);
        }
        baseBox.Child = bases;
        var baseLabel = new TextBlock { Text = Loc.T("customTheme.base"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        baseLabel.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        var baseLeft = new StackPanel { Orientation = Orientation.Horizontal };
        baseLeft.Children.Add(baseLabel);
        baseLeft.Children.Add(baseBox);
        baseRow.Children.Add(baseLeft);
        CustomThemePanel.Children.Add(baseRow);

        var effective = Themes.CustomEffective();
        foreach (var (group, keys) in Themes.EditableGroups)
        {
            var header = new TextBlock { Text = Loc.T("customTheme.group." + group), Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 12, 0, 6) };
            CustomThemePanel.Children.Add(header);
            var wrap = new WrapPanel();
            foreach (var key in keys) wrap.Children.Add(CustomColorItem(key, effective.TryGetValue(key, out var spec) ? spec : "#000000", st.CustomThemeColors.ContainsKey(key)));
            CustomThemePanel.Children.Add(wrap);
        }
    }

    /// <summary>색 하나: [색 칸] 이름 (•) [↶]</summary>
    private FrameworkElement CustomColorItem(string key, string spec, bool overridden)
    {
        var swatch = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = new Border { Background = Themes.MakeBrush(spec), CornerRadius = new CornerRadius(5) },
        };
        swatch.SetResourceReference(Border.BackgroundProperty, "CheckerBrush"); // 반투명 색은 무늬 위에
        swatch.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        var name = new TextBlock { Text = Loc.T("themeColor." + key), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (overridden) name.FontWeight = FontWeights.SemiBold;
        var content = new DockPanel { LastChildFill = true, Width = 214 - 30 - 16 }; // 왼쪽 정렬 (버튼은 내용을 가운데에 둠)
        content.Children.Add(swatch);
        content.Children.Add(name);
        var pick = new Button
        {
            Content = content, Height = 36, Padding = new Thickness(5, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = Loc.T("themeColor." + key + "Tip") + "\n" + spec, BorderThickness = new Thickness(overridden ? 1.5 : 1),
        };
        if (overridden) pick.SetResourceReference(Button.BorderBrushProperty, "AccentBrush");
        pick.Click += async (_, _) =>
        {
            System.Windows.Media.Color current;
            try { current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(FirstHex(spec)); }
            catch { current = System.Windows.Media.Colors.White; }
            var picked = await ColorPicker.PickAsync(this, System.Windows.Media.Color.FromRgb(current.R, current.G, current.B), _app.State.Settings.RecentColors);
            if (picked is not System.Windows.Media.Color p) return;
            string hex = ColorPicker.ToHex(p);
            _app.AddRecentColor(hex);
            // 반투명 색(선택 배경·배지 등)은 원래 투명도를 유지
            if (current.A < 255) hex = $"#{current.A:X2}{hex[1..]}";
            _app.SetCustomColor(key, hex);
        };
        var cell = new DockPanel { Width = 214, Margin = new Thickness(0, 0, 6, 6) };
        {
            // 되돌리기 칸은 늘 자리를 차지 (줄이 가지런하게), 직접 고른 색일 때만 보임
            var undo = new Button
            {
                Visibility = overridden ? Visibility.Visible : Visibility.Hidden,
                Style = (Style)FindResource("BtnIcon"), Width = 28, Height = 36, Margin = new Thickness(2, 0, 0, 0), ToolTip = Loc.T("customTheme.resetOne"),
                Content = new TextBlock { Text = "\uE7A7", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 13 },
            };
            undo.Click += (_, _) => Dispatcher.InvokeAsync(() => _app.SetCustomColor(key, null));
            DockPanel.SetDock(undo, Dock.Right);
            cell.Children.Add(undo);
        }
        cell.Children.Add(pick);
        return cell;
    }

    private static string FirstHex(string spec) => spec.Length > 2 && spec[1] == ':' ? spec[2..].Split(',')[0] : spec;

    /// <summary>점검용: 사용자 지정 색 편집기를 펼침.</summary>
    internal int OpenCustomEditorForTest()
    {
        _customEditorOpen = true;
        BuildCustomThemePanel();
        return CustomThemePanel.Children.OfType<WrapPanel>().Sum(w => w.Children.Count);
    }

    /// <summary>점검용: 설정 화면을 테마 부분까지 내림 (스크린샷용).</summary>
    internal void ScrollToThemeForTest()
    {
        UpdateLayout();
        if (SettingsView.Content is UIElement content)
        {
            var y = CustomThemePanel.TranslatePoint(new Point(0, 0), content).Y;
            SettingsView.ScrollToVerticalOffset(Math.Max(0, y - 40));
        }
        UpdateLayout();
    }

    // ── 자동 실행 지연 / 업데이트 ─────────────────────────────

    private void AutostartDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AutostartDelayText == null) return;
        AutostartDelayText.Text = DelayText(e.NewValue * 1000);
        if (_updating) return;
        _app.SetAutostartDelay((int)e.NewValue);
    }

    private void StartupAnimation_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetStartupAnimation(StartupAnimationToggle.IsChecked == true);
    }

    private void SplashPreview_Click(object sender, RoutedEventArgs e)
    {
        SplashPreviewButton.IsEnabled = false;
        var w = _app.PreviewSplash(then: () => SplashPreviewButton.IsEnabled = true);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetCheckUpdates(CheckUpdatesToggle.IsChecked == true);
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e) => await _app.CheckForUpdatesAsync(manual: true);

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e) => AppController.OpenUrl(_app.LatestRelease?.Url ?? Updates.ReleasesPage);

    private void RefreshUpdateStatus()
    {
        if (UpdateStatusText == null) return;
        var r = _app.LatestRelease;
        CheckNowButton.IsEnabled = !_app.CheckingUpdates;
        DownloadUpdateButton.Visibility = r is { IsNewer: true } ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText.Foreground = Res(r is { IsNewer: true } ? "GreenTextBrush" : "TextMutedBrush");
        UpdateStatusText.Text = _app.CheckingUpdates ? Loc.T("update.checking")
            : r is { IsNewer: true } ? Loc.F("update.newVersion", r.Version, AppController.AppVersion)
            : r != null ? Loc.F("update.upToDate", AppController.AppVersion)
            : _app.State.Settings.LastUpdateCheck is DateTimeOffset t ? Loc.F("update.lastChecked", t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
            : Loc.F("update.current", AppController.AppVersion);
    }

    // ── 백업·복원·초기화 ─────────────────────────────────────

    private async void BackupCreate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Backup.DefaultFileName(),
            Filter = Loc.T("backup.filter") + "|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        ShowToast(Loc.T("notice.backingUp"));
        await _app.CreateBackupAsync(dlg.FileName);
    }

    private async void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("backup.filter") + "|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, Loc.T("backup.restoreQuestion"), "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _app.RestoreBackupAsync(dlg.FileName);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("backup.resetQuestion"), "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _app.ResetSettings();
    }

    // ── 새 GIF 기본값 ────────────────────────────────────────

    private void NewItemSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NewItemSizeText == null) return;
        NewItemSizeText.Text = $"{e.NewValue:0} px";
        if (_updating) return;
        _app.SetNewItemSize((int)e.NewValue);
    }

    private void NewLayer_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string layer) return;
        _app.ChangeTemplate(t => t.Layer = Layers.Normalize(layer));
    }

    private void NewClickThrough_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = NewClickThroughToggle.IsChecked == true;
        _app.ChangeTemplate(t => t.ClickThrough = on);
    }

    /// <summary>"GIF 설정 가져오기": 화면에 떠 있는 GIF 목록에서 하나를 골라 그 설정을 새 GIF 기본값으로.</summary>
    private void TemplateFromSelected_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_app.State.Active.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("newItem.noneOnScreen"), IsEnabled = false });
        int index = 1;
        foreach (var a in _app.State.Active)
        {
            var photo = _app.State.FindPhoto(a.PhotoId);
            string activeId = a.Id;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var thumb = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                Background = (System.Windows.Media.Brush)FindResource("CheckerBrush"), ClipToBounds = true,
            };
            if (photo != null && Images.LoadThumb(photo) is System.Windows.Media.ImageSource img)
                thumb.Child = new Image { Source = img, Stretch = System.Windows.Media.Stretch.Uniform };
            row.Children.Add(thumb);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"{index}. {photo?.Name ?? "?"}", MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis });
            int fx = a.EnabledEffectCount();
            text.Children.Add(new TextBlock
            {
                Text = Loc.T("motion." + Motion.Normalize(a.MotionMode)) + " · " + (fx == 0 ? Loc.T("newItem.noEffects") : Loc.F("newItem.effectsN", fx))
                       + " · " + Loc.T(Layers.Normalize(a.Layer) == Layers.Desktop ? "layer.desktop" : "layer.top"),
                FontSize = 11.5, Foreground = Res("TextMutedBrush"),
            });
            row.Children.Add(text);
            menu.Items.Add(MenuEntryContent(row, () => _app.TemplateFromActive(activeId)));
            index++;
        }
        OpenMenu(TemplateFromButton, menu);
    }

    private static MenuItem MenuEntryContent(object header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ── 라이브러리 상단: 장면 ────────────────────────────────

    private void RefreshSceneButton()
    {
        if (SceneButtonText == null) return;
        var cur = _app.CurrentScene;
        SceneButtonText.Text = cur != null ? cur.Name : Loc.T("set.scenes");
    }

    private void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var sc in _app.State.Scenes)
        {
            string sid = sc.Id;
            var row = new DockPanel();
            var info = new TextBlock
            {
                Text = Loc.F("lib.sceneCount", sc.Items.Count) + (sc.AutoAt != null ? " · " + sc.AutoAt : ""),
                FontSize = 11.5, Foreground = Res("TextMutedBrush"), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(info, Dock.Right);
            row.Children.Add(info);
            row.Children.Add(new TextBlock { Text = sc.Name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = MenuEntryContent(row, () => _app.ApplyScene(sid));
            item.IsChecked = sc.Id == _app.State.CurrentSceneId;
            menu.Items.Add(item);
        }
        if (_app.State.Scenes.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("lib.noScenes"), IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(Loc.T("tray.saveScene"), () => SaveScene_Click(this, new RoutedEventArgs())));
        var cur = _app.CurrentScene;
        if (cur != null)
        {
            string cid = cur.Id;
            menu.Items.Add(MenuEntry(Loc.F("lib.overwriteScene", cur.Name), () => _app.OverwriteScene(cid)));
        }
        var del = new MenuItem { Header = Loc.T("lib.deleteScene"), IsEnabled = _app.State.Scenes.Count > 0 };
        foreach (var sc in _app.State.Scenes)
        {
            string sid = sc.Id, name = sc.Name;
            del.Items.Add(MenuEntry(name, () =>
            {
                if (MessageBox.Show(this, Loc.F("scene.deleteQuestion", name), "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _app.DeleteScene(sid);
            }));
        }
        menu.Items.Add(del);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(Loc.T("lib.sceneSettings"), () => { TabSettings.IsChecked = true; }));
        OpenMenu(SceneButton, menu);
    }

    private void TemplateReset_Click(object sender, RoutedEventArgs e) => _app.ClearTemplate();

    // ── 프로그램별 숨기기 ────────────────────────────────────

    private void RuleMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton rb || rb.Tag is not string mode) return;
        _app.SetAppRuleMode(mode == "run");
    }

    private void BuildAppRuleRows()
    {
        AppRuleRows.Children.Clear();
        foreach (var exe in _app.State.Settings.HideForApps)
        {
            AppRuleRows.Children.Add(new Border { Height = 1, Background = Res("LineBrush") });
            var dock = new DockPanel { Margin = new Thickness(18, 8, 18, 8) };
            string name = exe;
            var remove = new Button { Content = Loc.T("rules.remove"), Height = 28, Padding = new Thickness(10, 0, 10, 0), FontSize = 12 };
            remove.Click += (_, _) => _app.RemoveAppRule(name);
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            dock.Children.Add(new TextBlock { Text = exe, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center });
            AppRuleRows.Children.Add(dock);
        }
    }

    private void RefreshRuleStatus()
    {
        if (RuleStatusText == null) return;
        var by = _app.Engine.RuleHiddenBy;
        RuleStatusText.Text = _app.State.Settings.HideForApps.Count == 0 ? Loc.T("rules.empty")
            : by != null ? Loc.F("rules.hiddenNow", by) : Loc.T("rules.notNow");
        RuleStatusText.Foreground = Res(by != null ? "WarnTextBrush" : "TextMutedBrush");
    }

    private void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var apps = AppRules.RunningWindowedApps();
        foreach (var (exe, title) in apps)
        {
            string name = exe;
            var item = new MenuItem { Header = $"{exe}  —  {(title.Length > 40 ? title[..40] + "…" : title)}" };
            item.Click += (_, _) => _app.AddAppRule(name);
            menu.Items.Add(item);
        }
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("rules.noneRunning"), IsEnabled = false });
        menu.PlacementTarget = AddRunningAppButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("rules.exeFilter") + "|*.exe", Title = Loc.T("rules.browse") };
        if (dlg.ShowDialog(this) == true) _app.AddAppRule(dlg.FileName);
    }

    // ── 설정 복사·붙여넣기 / 효과 프리셋 ───────────────────────

    /// <summary>점검용: 라이브러리/설정 탭 전환.</summary>
    internal void SetTabForTest(bool settings)
    {
        if (settings) TabSettings.IsChecked = true;
        else TabLibrary.IsChecked = true;
    }

    private ContextMenu? _lastMenu;

    /// <summary>점검용: 라이브러리·설정의 팝업 메뉴를 모두 열어 보고(하위 메뉴 포함) 닫음.</summary>
    internal int SmokeMenus()
    {
        int opened = 0;
        void Try(Action open)
        {
            open();
            if (_lastMenu == null) throw new Exception("menu did not open");
            foreach (var mi in _lastMenu.Items.OfType<MenuItem>().Where(m => m.HasItems))
            {
                mi.IsSubmenuOpen = true;
                mi.UpdateLayout();
                mi.IsSubmenuOpen = false;
            }
            _lastMenu.IsOpen = false;
            _lastMenu = null;
            opened++;
        }
        Try(() => SceneButton_Click(this, new RoutedEventArgs()));
        Try(() => TemplateFromSelected_Click(this, new RoutedEventArgs()));
        if (_app.SelectedActiveId != null)
        {
            Try(() => PresetButton_Click(this, new RoutedEventArgs()));
            Try(() => StyleButton_Click(this, new RoutedEventArgs()));
        }
        return opened;
    }

    private void OpenMenu(Button target, ContextMenu menu)
    {
        _lastMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void StyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedActiveId is not string id) return;
        bool has = _app.CopiedStyle != null;
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(Loc.T("style.copy"), () => _app.CopyStyle(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(Loc.T("style.paste"), () => _app.PasteStyle(id, effectsOnly: false), has));
        menu.Items.Add(MenuEntry(Loc.T("style.pasteEffects"), () => _app.PasteStyle(id, effectsOnly: true), has));
        menu.Items.Add(MenuEntry(Loc.T("style.pasteAll"), () => _app.PasteStyleToAll(effectsOnly: false), has && _app.State.Active.Count > 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(Loc.T("style.asDefault"), () => _app.TemplateFromSelected()));
        OpenMenu(StyleButton, menu);
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.SelectedActiveId is not string id) return;
        var menu = new ContextMenu();
        foreach (var preset in _app.AllPresets())
        {
            string pid = preset.Id;
            menu.Items.Add(MenuEntry((preset.BuiltIn ? "✦ " : "") + preset.Name, () => _app.ApplyPreset(id, pid)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(Loc.T("preset.save"), () =>
        {
            var name = TextPrompt.Ask(this, Loc.T("preset.save"), Loc.T("preset.nameLabel"), Loc.F("preset.defaultName", _app.State.EffectPresets.Count + 1));
            if (name != null) _app.SavePreset(id, name);
        }));
        var del = new MenuItem { Header = Loc.T("preset.delete"), IsEnabled = _app.State.EffectPresets.Count > 0 };
        foreach (var preset in _app.State.EffectPresets.ToList())
        {
            string pid = preset.Id;
            del.Items.Add(MenuEntry(preset.Name, () => _app.DeletePreset(pid)));
        }
        menu.Items.Add(del);
        OpenMenu(PresetButton, menu);
    }

    // ── 장면 ─────────────────────────────────────────────────

    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (_app.State.Active.Count == 0) { ShowToast(Loc.T("notice.sceneEmpty")); return; }
        var name = TextPrompt.Ask(this, Loc.T("scene.saveTitle"), Loc.T("scene.nameLabel"), Loc.F("scene.defaultName", _app.State.Scenes.Count + 1));
        if (name != null) _app.SaveScene(name);
    }

    private void BuildSceneRows()
    {
        SceneRows.Children.Clear();
        if (_app.State.Scenes.Count == 0) return;
        foreach (var sc in _app.State.Scenes)
        {
            SceneRows.Children.Add(new Border { Height = 1, Background = Res("LineBrush") });
            string id = sc.Id;
            var dock = new DockPanel { Margin = new Thickness(18, 10, 18, 10) };

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // 자동 전환 시각: [없음 | 매일] + 숫자를 돌리는 시계
            var (h0, m0) = sc.AutoAt is string at ? (int.Parse(at[..2]), int.Parse(at[3..])) : (9, 0);
            var wheel = new TimeWheel(h0, m0) { Margin = new Thickness(8, 0, 0, 0), Visibility = sc.AutoAt != null ? Visibility.Visible : Visibility.Collapsed };
            wheel.TimeCommitted += (hh, mm) => _app.SetSceneTime(id, $"{hh:00}:{mm:00}", quiet: true);
            var seg = new Border { Style = (Style)FindResource("SegmentBox"), VerticalAlignment = VerticalAlignment.Center };
            var segRow = new StackPanel { Orientation = Orientation.Horizontal };
            var none = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "sceneTime" + id, Content = Loc.T("scene.noTime"), Padding = new Thickness(10, 0, 10, 0), IsChecked = sc.AutoAt == null };
            var daily = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "sceneTime" + id, Content = Loc.T("scene.autoAt"), Padding = new Thickness(10, 0, 10, 0), IsChecked = sc.AutoAt != null };
            none.Checked += (_, _) => { wheel.Visibility = Visibility.Collapsed; _app.SetSceneTime(id, null, quiet: true); };
            daily.Checked += (_, _) => { wheel.Visibility = Visibility.Visible; _app.SetSceneTime(id, $"{wheel.Hour:00}:{wheel.Minute:00}", quiet: true); };
            segRow.Children.Add(none);
            segRow.Children.Add(daily);
            seg.Child = segRow;
            seg.ToolTip = Loc.T("scene.autoAtTip");
            right.Children.Add(seg);
            right.Children.Add(wheel);
            var apply = new Button { Content = Loc.T("scene.apply"), Height = 30, MinWidth = 70, Margin = new Thickness(10, 0, 0, 0), Style = (Style)FindResource("BtnSoftAccent") };
            apply.Click += (_, _) => _app.ApplyScene(id);
            right.Children.Add(apply);
            var more = new Button
            {
                Content = new TextBlock { Text = "\uE712", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 13 },
                Width = 32, Height = 30, Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("scene.more"),
            };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = Loc.T("scene.rename") };
            rename.Click += (_, _) =>
            {
                var n = TextPrompt.Ask(this, Loc.T("scene.rename"), Loc.T("scene.nameLabel"), sc.Name);
                if (n != null) _app.RenameScene(id, n);
            };
            var overwrite = new MenuItem { Header = Loc.T("scene.overwrite") };
            overwrite.Click += (_, _) => _app.OverwriteScene(id);
            var delete = new MenuItem { Header = Loc.T("scene.delete") };
            delete.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Loc.F("scene.deleteQuestion", sc.Name), "GifPin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _app.DeleteScene(id);
            };
            menu.Items.Add(rename);
            menu.Items.Add(overwrite);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            more.ContextMenu = menu;
            more.Click += (_, _) => { menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            right.Children.Add(more);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            if (sc.Id == _app.State.CurrentSceneId)
                title.Children.Add(new TextBlock { Text = "● ", Foreground = Res("AccentBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = sc.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(title);
            left.Children.Add(new TextBlock
            {
                Text = Loc.F("scene.info", sc.Items.Count, sc.SavedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)),
                Foreground = Res("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            });
            dock.Children.Add(left);
            SceneRows.Children.Add(dock);
        }
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetStartWithWindows(StartWithWindowsToggle.IsChecked == true);
        RefreshSettings();
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
