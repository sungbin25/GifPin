using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GifPin.Core;

namespace GifPin.UI;

/// <summary>편집 모드 동안 화면 위쪽 가운데에 떠 있는 작은 툴바.</summary>
public partial class EditToolbar : Window
{
    private readonly AppController _app;
    private readonly DispatcherTimer _topmostTimer;

    public EditToolbar(AppController app)
    {
        _app = app;
        InitializeComponent();

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        _app.GeometryChanged += OnGeometryChanged;
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 2;
            RefreshSelection();
            Activate();
            KeepOnTop();
            _topmostTimer.Start();
        };
        PreviewKeyDown += OnKey;
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _app.GeometryChanged -= OnGeometryChanged;
        base.OnClosed(e);
        // Alt+F4 등으로 닫힌 경우 = 저장하고 끝내기
        if (_app.IsEditing) _app.FinishEdit(save: true);
    }

    private void KeepOnTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void RefreshSelection()
    {
        var a = _app.SelectedActive;
        var p = _app.State.FindPhoto(a?.PhotoId);
        SelName.Text = p?.Name ?? Loc.T("edit.noGif");
        SelThumb.Source = p != null ? Images.LoadThumb(p) : null;
        ScaleText.Text = a != null ? $"{a.Scale * 100:0}%" : "–";
    }

    public void RefreshList() => RefreshSelection();

    private void OnGeometryChanged(ActiveItem a)
    {
        if (a.Id == _app.SelectedActiveId) ScaleText.Text = $"{a.Scale * 100:0}%";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: _app.NudgeSelected(-step, 0); break;
            case Key.Right: _app.NudgeSelected(step, 0); break;
            case Key.Up: _app.NudgeSelected(0, -step); break;
            case Key.Down: _app.NudgeSelected(0, step); break;
            case Key.Enter: _app.FinishEdit(save: true); break;
            case Key.Escape: _app.FinishEdit(save: false); break;
            case Key.Delete:
                if (_app.SelectedActiveId != null) _app.RemoveActive(_app.SelectedActiveId);
                if (_app.State.Active.Count > 0) _app.SelectActive(_app.State.Active[^1].Id, flash: false);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove(); // 툴바가 GIF를 가리면 옮길 수 있게
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = PickButton.ContextMenu;
        menu.Items.Clear();
        int i = 1;
        foreach (var a in _app.State.Active)
        {
            var p = _app.State.FindPhoto(a.PhotoId);
            var id = a.Id;
            var item = new MenuItem
            {
                Header = Loc.F("panel.screenN", i++) + " · " + (p?.Name ?? Loc.T("active.noPhoto")) + (a.Id == _app.SelectedActiveId ? "  ✓" : ""),
            };
            item.Click += (_, _) => _app.SelectActive(id, flash: false);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = PickButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Smaller_Click(object sender, RoutedEventArgs e) => _app.ScaleSelected(1 / 1.1);
    private void Bigger_Click(object sender, RoutedEventArgs e) => _app.ScaleSelected(1.1);
    private void Reset_Click(object sender, RoutedEventArgs e) => _app.ScaleSelectedTo(1.0);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _app.FinishEdit(save: false);
    private void Done_Click(object sender, RoutedEventArgs e) => _app.FinishEdit(save: true);
}
