using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GifPin.Core;

namespace GifPin.UI;

public sealed class CollisionSettingsWindow : Window
{
    public CollisionSettingsWindow(AppController app, string id)
    {
        var item = app.State.Active.FirstOrDefault(a => a.Id == id)?.Clone() ?? new ActiveItem();
        item.Normalize();
        Title = Loc.T("collision.settings"); Width = 720; Height = 760; MinWidth = 620; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont"); FontSize = 13;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var cancel = new Button { Content = Loc.T("common.cancel"), MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = Loc.T("common.save"), MinWidth = 90, IsDefault = true }; buttons.Children.Add(save);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var enabled = Toggle(panel, "collision.enabled", item.CollisionEnabled);
        panel.Children.Add(new TextBlock { Text = Loc.T("collision.hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var size = Number(panel, "collision.size", 25, 100, item.CollisionSize, 1);
        var effects = Toggle(panel, "collision.effects", item.FxCollisionEnabled);
        var options = new StackPanel(); panel.Children.Add(options);
        var threshold = Number(options, "collision.threshold", 20, 3000, item.FxCollisionStrongSpeed, 10);
        var columns = new Grid { Margin = new Thickness(0, 8, 0, 12) };
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition()); options.Children.Add(columns);
        var weak = new Profile(this, app, "collision.weak", item.FxCollisionWeak);
        var strong = new Profile(this, app, "collision.strong", item.FxCollisionStrong);
        columns.Children.Add(weak.Panel); columns.Children.Add(strong.Panel); Grid.SetColumn(strong.Panel, 1);
        var images = new Button { Content = Loc.T("collision.images"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
        images.Click += (_, _) => new ParticleImagePicker(app, id, enableContinuous: false) { Owner = this }.ShowDialog();
        options.Children.Add(images);
        options.Children.Add(new TextBlock { Text = Loc.T("collision.imagesHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        options.IsEnabled = effects.IsChecked == true;
        effects.Click += (_, _) => options.IsEnabled = effects.IsChecked == true;
        save.Click += (_, _) =>
        {
            app.UpdateActive(id, a =>
            {
                a.CollisionEnabled = enabled.IsChecked == true; a.CollisionSize = size.Value;
                a.FxCollisionEnabled = effects.IsChecked == true; a.FxCollisionStrongSpeed = threshold.Value;
                a.FxCollisionWeak = weak.Value; a.FxCollisionStrong = strong.Value;
            });
            app.SelectActive(id, flash: false); DialogResult = true;
        };
    }

    private static CheckBox Toggle(StackPanel parent, string key, bool value)
    {
        var box = new CheckBox { Content = Loc.T(key), IsChecked = value, Margin = new Thickness(0, 8, 0, 8) };
        parent.Children.Add(box); return box;
    }

    private static Slider Number(StackPanel parent, string key, double min, double max, double value, double step)
    {
        var label = new TextBlock { Margin = new Thickness(0, 8, 0, 4) }; parent.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5 };
        void Update() => label.Text = $"{Loc.T(key)}  {slider.Value:0.##}";
        slider.ValueChanged += (_, _) => Update(); Update(); parent.Children.Add(slider); return slider;
    }

    private sealed class Profile
    {
        public StackPanel Panel { get; } = new() { Margin = new Thickness(0, 0, 18, 0) };
        private readonly CheckBox _enabled;
        private readonly ComboBox _kind;
        private readonly Slider _count, _size, _life, _speed;
        private string _color;
        public CollisionEffect Value => new() { Enabled = _enabled.IsChecked == true, Kind = (string)_kind.SelectedItem, Color = _color, Count = (int)_count.Value, Size = _size.Value, Life = _life.Value, Speed = _speed.Value };

        public Profile(Window owner, AppController app, string title, CollisionEffect value)
        {
            _color = value.Color;
            Panel.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            _enabled = Toggle(Panel, "collision.profileEnabled", value.Enabled);
            var fields = new StackPanel { IsEnabled = value.Enabled }; Panel.Children.Add(fields);
            _enabled.Click += (_, _) => fields.IsEnabled = _enabled.IsChecked == true;
            fields.Children.Add(new TextBlock { Text = Loc.T("collision.kind"), Margin = new Thickness(0, 4, 0, 4) });
            _kind = new ComboBox { ItemsSource = Fx.Kinds, SelectedItem = value.Kind };
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new KindLabel() });
            _kind.ItemTemplate = new DataTemplate { VisualTree = factory }; fields.Children.Add(_kind);
            var color = new Button { Content = Loc.T("collision.color") + "  " + _color, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            color.Click += async (_, _) =>
            {
                var picked = await ColorPicker.PickAsync(owner, ColorPicker.TryParse(_color, out var c) ? c : Colors.White, app.State.Settings.RecentColors);
                if (picked is not Color selected) return;
                _color = $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";
                app.AddRecentColor(_color); color.Content = Loc.T("collision.color") + "  " + _color;
            };
            fields.Children.Add(color);
            _count = Number(fields, "collision.count", 1, 80, value.Count, 1);
            _size = Number(fields, "collision.particleSize", 4, 48, value.Size, 1);
            _life = Number(fields, "collision.life", .2, 3, value.Life, .1);
            _speed = Number(fields, "collision.speed", 20, 240, value.Speed, 5);
        }
    }

    private sealed class KindLabel : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string kind = (string)value;
            return Loc.T("fx.kind" + char.ToUpperInvariant(kind[0]) + kind.Substring(1));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
