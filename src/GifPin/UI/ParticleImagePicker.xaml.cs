using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using GifPin.Core;

namespace GifPin.UI;

/// <summary>파티클로 쓸 사진 고르기 (라이브러리에서 여러 장, PC에서 바로 추가 가능).</summary>
public partial class ParticleImagePicker : Window
{
    private readonly AppController _app;
    private readonly string _activeId;
    private readonly bool _enableContinuous;
    private readonly List<string> _selected; // 고른 순서 유지

    public ParticleImagePicker(AppController app, string activeId, bool enableContinuous = true)
    {
        _enableContinuous = enableContinuous;
        _app = app;
        _activeId = activeId;
        var a = app.State.Active.FirstOrDefault(x => x.Id == activeId);
        _selected = a != null ? Fx.ParticleImageIds(a).Where(id => app.State.FindPhoto(id) != null).ToList() : new List<string>();
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFilesAsync(files);
        };
        BuildTiles();
    }

    private void BuildTiles()
    {
        Tiles.Children.Clear();
        // 라이브러리에서 이미 고른 것은 앞쪽에
        var photos = _app.State.Library
            .OrderBy(p => _selected.IndexOf(p.Id) is int i && i >= 0 ? i : int.MaxValue)
            .ThenByDescending(p => p.AddedAt)
            .ToList();
        foreach (var p in photos)
        {
            var stack = new StackPanel();
            stack.Children.Add(new Image
            {
                Source = Images.LoadThumb(p),
                Height = 74,
                Stretch = Stretch.Uniform,
            });
            stack.Children.Add(new TextBlock
            {
                Text = p.Name,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (p.FrameCount > 1)
                stack.Children.Add(new TextBlock { Text = "GIF", FontSize = 9, Foreground = (Brush)FindResource("AccentTextBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            var tile = new ToggleButton
            {
                Style = (Style)FindResource("PickTile"),
                Content = stack,
                Tag = p.Id,
                IsChecked = _selected.Contains(p.Id),
                ToolTip = p.Name,
            };
            tile.Click += Tile_Click;
            Tiles.Children.Add(tile);
        }
        EmptyText.Visibility = photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string id) return;
        if (t.IsChecked == true)
        {
            if (_selected.Count >= Fx.MaxParticleImages)
            {
                t.IsChecked = false;
                CountText.Text = Loc.F("picker.max", Fx.MaxParticleImages);
                return;
            }
            if (!_selected.Contains(id)) _selected.Add(id);
        }
        else _selected.Remove(id);
        UpdateCount();
    }

    private void UpdateCount() => CountText.Text = Loc.F("picker.selected", _selected.Count, Fx.MaxParticleImages);

    private async void AddFromPc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("picker.addFromPc"), Filter = Library.OpenFileFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
    }

    /// <summary>PC의 파일을 라이브러리에 추가하고, 새로 들어온 사진은 바로 고른 상태로.</summary>
    private async System.Threading.Tasks.Task AddFilesAsync(IEnumerable<string> files)
    {
        var before = _app.State.Library.Select(p => p.Id).ToHashSet();
        await _app.AddFilesAsync(files.Where(f => !AssetPack.IsPackFile(f)));
        foreach (var p in _app.State.Library.Where(p => !before.Contains(p.Id)))
            if (_selected.Count < Fx.MaxParticleImages && !_selected.Contains(p.Id)) _selected.Add(p.Id);
        BuildTiles();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string value = string.Join(",", _selected);
        _app.UpdateActive(_activeId, a =>
        {
            a.FxParticleImages = value;
            if (_selected.Count > 0 && _enableContinuous)
            {
                a.FxParticles = true;
                // 이미 고른 모양(하트 등)은 그대로 두고 "내 이미지"를 함께 추가 → 같은 비율로 섞임
                var kinds = Fx.ParticleKinds(a).ToList();
                if (!kinds.Contains(Fx.Image)) kinds.Add(Fx.Image);
                a.FxParticleKind = string.Join(",", kinds);
            }
        });
        _app.SelectActive(_activeId, flash: false); // PC에서 추가하면 메인 창 선택이 바뀌었을 수 있음
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _app.SelectActive(_activeId, flash: false);
        DialogResult = false;
    }
}
