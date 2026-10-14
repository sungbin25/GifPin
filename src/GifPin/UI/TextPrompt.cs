using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using GifPin.Core;

namespace GifPin.UI;

/// <summary>이름 하나를 입력받는 작은 창 (장면·프리셋 이름 등). 취소하면 null.</summary>
public sealed class TextPrompt : Window
{
    private readonly TextBox _box;

    private TextPrompt(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("BgBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), Foreground = (System.Windows.Media.Brush)FindResource("LabelBrush") });
        _box = new TextBox { Text = initial, Height = 32, MaxLength = 60 };
        root.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Loc.T("common.save"), Height = 32, MinWidth = 84, IsDefault = true, Style = (Style)FindResource("BtnPrimary") };
        var cancel = new Button { Content = Loc.T("common.cancel"), Height = 32, MinWidth = 84, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_box.Text)) { _box.Focus(); return; }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    /// <summary>owner가 없거나 보이지 않으면 화면 가운데에 띄움.</summary>
    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var w = new TextPrompt(title, label, initial);
        if (owner != null && owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w._box.Text.Trim() : null;
    }

    /// <summary>점검용: 창을 만들어 띄웠다 닫기만 함.</summary>
    internal static void SmokeOpenClose()
    {
        var w = new TextPrompt("test", "label", "value");
        w.Show();
        w.Close();
    }
}
