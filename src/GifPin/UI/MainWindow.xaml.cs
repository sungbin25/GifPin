using System;
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
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        PhotoList.ItemsSource = _app.VisiblePhotos;
        ActiveList.ItemsSource = _app.Actives;
        _app.VisiblePhotos.CollectionChanged += (_, _) => UpdateCounts();
        _app.Actives.CollectionChanged += (_, _) => UpdateCounts();
        _app.SelectionChanged += RefreshPanel;
        _app.GeometryChanged += OnGeometryChanged;
        _app.Notice += ShowToast;

        BuildAnchorButtons();
        BuildHotkeyRows();
        RestoreBounds();
        RefreshSettings();
        RefreshHiddenToggle();
        UpdateCounts();
        _updating = false;
        RefreshPanel();

        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += OnClosing;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
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

    private void RestoreBounds()
    {
        var b = _app.State.Settings.Window;
        if (b == null || b.Width < MinWidth || b.Height < MinHeight) return;
        // 화면 밖이면 무시
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
            Title = "라이브러리에 추가할 사진 선택",
            Filter = Library.OpenFileFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await _app.AddFilesAsync(dlg.FileNames);
    }

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

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = SortButton.ContextMenu;
        menu.PlacementTarget = SortButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            _app.SetSort(tag);
            SortLabel.Text = mi.Header as string ?? "";
        }
    }

    private void UpdateCounts()
    {
        FilterAll.Content = $"전체 {_app.State.Library.Count}";
        FilterFavorite.Content = $"즐겨찾기 {_app.FavoriteCount}";
        ActiveCount.Text = _app.Actives.Count.ToString(CultureInfo.InvariantCulture);
        bool empty = _app.VisiblePhotos.Count == 0;
        EmptyLibrary.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _app.State.Library.Count == 0
            ? "GIF 파일을 이 창에 끌어다 놓거나 'PC에서 추가'를 누르세요"
            : "조건에 맞는 사진이 없습니다";
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
        string msg = vm.InUse
            ? $"\"{vm.Name}\"은(는) 지금 화면에 표시 중입니다.\n화면에서 내리고 라이브러리에서 제거할까요?"
            : $"\"{vm.Name}\"을(를) 라이브러리에서 제거할까요?";
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

        _updating = true;
        PanelTitle.Text = photo.Name;
        PanelInfo.Text = $"{photo.Width} × {photo.Height} · {(photo.FrameCount > 1 ? photo.FrameCount + "프레임" : "정지 이미지")} · {Library.FormatSize(photo.FileSize)}";

        if (active != null)
        {
            int index = _app.State.Active.IndexOf(active) + 1;
            PanelSubtitle.Text = "선택한 GIF";
            ScreenBadge.Visibility = Visibility.Visible;
            ScreenBadgeText.Text = $"화면 {index}";
            ActiveProps.Visibility = Visibility.Visible;
            ActiveButtons.Visibility = Visibility.Visible;
            PhotoButtons.Visibility = Visibility.Collapsed;
            FillActiveValues(active);
            PreviewFlip.ScaleX = active.FlipX ? -1 : 1;
            _previewPlayer.Speed = active.Speed;
        }
        else
        {
            PanelSubtitle.Text = "선택한 사진";
            ScreenBadge.Visibility = Visibility.Collapsed;
            ActiveProps.Visibility = Visibility.Collapsed;
            ActiveButtons.Visibility = Visibility.Collapsed;
            PhotoButtons.Visibility = Visibility.Visible;
            var sel = _app.SelectedActive;
            var selPhoto = _app.State.FindPhoto(sel?.PhotoId);
            ReplaceSelectedButton.Visibility = sel != null && sel.PhotoId != photo.Id ? Visibility.Visible : Visibility.Collapsed;
            ReplaceSelectedButton.Content = $"화면 {_app.State.Active.IndexOf(sel!) + 1}의 \"{selPhoto?.Name}\"와 교체";
            PreviewFlip.ScaleX = 1;
            _previewPlayer.Speed = 1;
        }
        _updating = false;

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
        ClickThroughToggle.IsChecked = a.ClickThrough;
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
        if (!int.TryParse(XBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(YBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            if (_app.SelectedActive is ActiveItem cur) FillActiveValues(cur);
            return;
        }
        WithSelected(a => { a.X = x; a.Y = y; });
    }

    private void BuildAnchorButtons()
    {
        string[] names = { "왼쪽 위", "위 가운데", "오른쪽 위", "왼쪽 가운데", "가운데", "오른쪽 가운데", "왼쪽 아래", "아래 가운데", "오른쪽 아래" };
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
                ToolTip = names[i] + "에 배치",
            };
            b.Click += (_, _) => PlaceAt(index);
            AnchorGrid.Children.Add(b);
        }
    }

    /// <summary>9분할 위치: GIF가 있는 모니터의 작업 영역 기준, 가장자리에서 24px 띄움.</summary>
    private void PlaceAt(int index)
    {
        var a = _app.SelectedActive;
        var p = _app.State.FindPhoto(a?.PhotoId);
        if (a == null || p == null) return;
        int w = (int)Math.Round(p.Width * a.Scale), h = (int)Math.Round(p.Height * a.Scale);
        var work = Native.WorkAreaAt(a.X + w / 2, a.Y + h / 2);
        const int m = 24;
        int col = index % 3, row = index / 3;
        int x = col == 0 ? work.Left + m : col == 1 ? (work.Left + work.Right - w) / 2 : work.Right - w - m;
        int y = row == 0 ? work.Top + m : row == 1 ? (work.Top + work.Bottom - h) / 2 : work.Bottom - h - m;
        _app.UpdateActive(a.Id, it => { it.X = x; it.Y = y; });
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
        _updating = true;
        var s = _app.State.Settings;
        StartWithWindowsToggle.IsChecked = s.StartWithWindows;
        OpenOnStartToggle.IsChecked = s.OpenWindowOnStart;
        CopyOnAddToggle.IsChecked = s.CopyOnAdd;
        DataFolderText.Text = Store.DataDir;
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = $"GifPin {ver?.ToString(3)} · 창을 닫아도 트레이에서 계속 실행됩니다. 완전히 끄려면 트레이 메뉴의 \"종료\" 또는 Ctrl+Alt+Q를 누르세요.";
        _updating = false;
    }

    private void BuildHotkeyRows()
    {
        var rows = new (string title, string keys)[]
        {
            ("편집 모드 켜기 / 끄기", "Ctrl+Alt+E"),
            ("모두 숨기기 / 보이기", "Ctrl+Alt+H"),
            ("GifPin 종료", "Ctrl+Alt+Q"),
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
            dock.Children.Add(new TextBlock { Text = rows[i].title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            HotkeyRows.Children.Add(dock);
            if (i < rows.Length - 1)
                HotkeyRows.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)FindResource("LineBrush") });
        }
        if (_app.Engine.HotkeyFailures.Count > 0)
            HotkeyRows.Children.Add(new TextBlock
            {
                Text = "다른 프로그램이 사용 중이라 등록하지 못한 단축키: " + string.Join(", ", _app.Engine.HotkeyFailures),
                Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
                Margin = new Thickness(18, 0, 18, 12),
                FontSize = 12,
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

    // ── 토스트 ───────────────────────────────────────────────

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
