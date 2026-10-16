using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GifPin.UI;

/// <summary>The approved transparent wordmark, animated from small embedded layers without a video decoder.</summary>
internal sealed class BrandIntro : Canvas
{
    private const double Unit = .38, MotionUnit = Unit / 1.05;
    private readonly List<Layer> _letters = new();
    private readonly Layer _character, _arcs;

    private sealed class Layer
    {
        public required Image Image;
        public required ScaleTransform Scale;
        public required RotateTransform Rotation;
        public required TranslateTransform Offset;
        public double Width;
        public void Pose(double dx, double dy, double sx = 1, double sy = 1, double rotation = 0, double opacity = 1)
        {
            Offset.X = dx * MotionUnit; Offset.Y = dy * MotionUnit;
            Scale.ScaleX = sx; Scale.ScaleY = sy; Rotation.Angle = -rotation; Image.Opacity = opacity;
        }
    }

    private static Uri Resource(string name) => new($"pack://application:,,,/{typeof(BrandIntro).Assembly.GetName().Name};component/Assets/BrandIntro/{name}");

    public BrandIntro()
    {
        Width = 640; Height = 160; IsHitTestVisible = false;
        using var stream = Application.GetResourceStream(Resource("layers.json"))!.Stream;
        using var document = JsonDocument.Parse(stream);
        _character = AddLayer(document.RootElement.GetProperty("character"));
        foreach (var letter in document.RootElement.GetProperty("letters").EnumerateArray()) _letters.Add(AddLayer(letter));
        _arcs = AddLayer(document.RootElement.GetProperty("arcs"));
        Seek(0);
    }

    private Layer AddLayer(JsonElement value)
    {
        double w = value.GetProperty("w").GetDouble() * Unit, h = value.GetProperty("h").GetDouble() * Unit;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = Resource(value.GetProperty("name").GetString()!); bitmap.EndInit(); bitmap.Freeze();
        var image = new Image { Source = bitmap, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var scale = new ScaleTransform(1, 1, w / 2, h);
        var rotate = new RotateTransform(0, w / 2, h);
        var offset = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(rotate); transforms.Children.Add(offset);
        image.RenderTransform = transforms;
        SetLeft(image, (value.GetProperty("x").GetDouble() - 885) * Unit + Width / 2);
        SetTop(image, (value.GetProperty("y").GetDouble() - 275) * Unit + 18);
        Children.Add(image);
        return new Layer { Image = image, Scale = scale, Rotation = rotate, Offset = offset, Width = w };
    }

    private static double Clamp(double n) => Math.Clamp(n, 0, 1);
    private static double Ease(double n) => 1 - Math.Pow(1 - Clamp(n), 4);
    private static double Pulse(double t, double start, double frequency = 32, double decay = 13)
    {
        double q = t - start;
        return q >= 0 && q < .55 ? Math.Exp(-decay * q) * Math.Sin(frequency * q) : 0;
    }

    public void Seek(double t)
    {
        for (int i = 0; i < _letters.Count; i++)
        {
            bool left = i < 4; int j = left ? i : i - 4;
            double dx = left ? -1450 * (1 - Ease(t / .38)) : 1450 * (1 - Ease((t - .48) / .34));
            double u = Clamp((t - (left ? 0 : .48)) / (left ? .38 : .34));
            double stretch = (left ? .09 : .10) * Math.Sin(Math.PI * u);
            double sx = 1 + stretch, sy = 1 - stretch * .55, angle = 0;
            if (left)
            {
                double p = Pulse(t, .37 + j * .012);
                dx += 18 * p; sx += .11 * p; sy -= .08 * p; angle += 1.3 * p;
            }
            else if (t >= .82)
            {
                double q = t - .82;
                dx = q < .6 ? -24 * Math.Exp(-13 * q) * Math.Cos(27 * q) : 0;
            }
            double impact = .82 + (left ? (3 - i) * .017 : j * .018), age = t - impact;
            if (age >= 0 && age < .6)
            {
                double squash = Math.Exp(-12 * age) * Math.Cos(30 * age);
                sx -= .17 * squash; sy += .13 * squash;
                if (left) dx -= 25 * Math.Exp(-12 * age) * Math.Sin(30 * age);
                angle += (left ? 1 : -1) * 2.2 * Pulse(t, impact);
            }
            if (i == 3) { double p = Pulse(t, 1.55, 38, 20); sx += .06 * p; sy -= .07 * p; }
            if (i is 3 or 4) dx += (i == 3 ? 1 : -1) * _letters[i].Width / MotionUnit * (1 - sx) / 2;
            if (t >= 1.8) { dx = angle = 0; sx = sy = 1; }
            _letters[i].Pose(dx, 0, sx, sy, angle);
        }
        if (t < 1.15) _character.Pose(0, 170, .65, .65, opacity: 0);
        else
        {
            double u = Clamp((t - 1.15) / .40);
            double dy = 170 * (1 - u) - 245 * Math.Sin(Math.PI * u);
            double sx = .65 + .35 * u - .12 * Math.Sin(Math.PI * u), sy = .65 + .35 * u + .16 * Math.Sin(Math.PI * u);
            double angle = -8 * Math.Sin(Math.PI * u);
            if (t >= 1.55)
            {
                double q = t - 1.55, p = Math.Exp(-20 * q) * Math.Cos(38 * q);
                dy = q < .16 ? -9 * Math.Sin(Math.PI * Clamp(q / .16)) : 0;
                sx = 1 + .14 * p; sy = 1 - .16 * p; angle = 0;
            }
            if (t >= 1.8) { dy = angle = 0; sx = sy = 1; }
            _character.Pose(10 * Math.Sin(Math.PI * u), dy, sx, sy, angle, Clamp((t - 1.15) / .07));
        }
        _arcs.Pose(0, 0, opacity: Clamp((t - 1.65) / .12));
    }
}
