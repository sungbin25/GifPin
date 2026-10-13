using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GifPin.Core;
using SkiaSharp;

namespace GifPin.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>라이브러리 카드 하나.</summary>
public sealed class PhotoVM : Observable
{
    public PhotoItem Photo { get; }
    private ImageSource? _thumb;
    private ImageSource? _hoverFrame;
    private bool _inUse, _isSelected, _canReplace;

    public PhotoVM(PhotoItem photo) => Photo = photo;

    public string Id => Photo.Id;
    public string Name => Photo.Name;
    public string Meta => $"{Photo.Width}×{Photo.Height} · {Kind(Photo)}";
    public bool Favorite => Photo.Favorite;

    /// <summary>카드에 보이는 이미지: 마우스를 올리면 재생 중인 프레임, 아니면 썸네일.</summary>
    public ImageSource? Display => _hoverFrame ?? Thumb;

    public ImageSource? Thumb => _thumb ??= Images.LoadThumb(Photo);

    public ImageSource? HoverFrame
    {
        get => _hoverFrame;
        set { _hoverFrame = value; Raise(nameof(Display)); }
    }

    public bool InUse { get => _inUse; set => Set(ref _inUse, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool CanReplace { get => _canReplace; set => Set(ref _canReplace, value); }

    public void RefreshAll()
    {
        _thumb = null;
        Raise(nameof(Name));
        Raise(nameof(Meta));
        Raise(nameof(Favorite));
        Raise(nameof(Thumb));
        Raise(nameof(Display));
    }

    public static string Kind(PhotoItem p)
    {
        var ext = Path.GetExtension(p.File).TrimStart('.').ToUpperInvariant();
        if (ext == "JPEG") ext = "JPG";
        return p.FrameCount > 1 ? ext : Loc.T("kind.still");
    }
}

/// <summary>화면 배치 바의 타일 하나.</summary>
public sealed class ActiveVM : Observable
{
    public ActiveItem Item { get; private set; }
    public PhotoItem? Photo { get; private set; }
    public int Index { get; private set; }
    private bool _isSelected;

    public ActiveVM(ActiveItem item, PhotoItem? photo, int index)
    {
        Item = item;
        Photo = photo;
        Index = index;
    }

    public string Id => Item.Id;
    public string Name => Photo?.Name ?? Loc.T("active.noPhoto");
    public string Meta => Loc.F("active.meta", Index, Math.Round(Item.Scale * 100))
        + (Motion.IsMoving(Item.MotionMode) ? " · " + Loc.T("motion." + Item.MotionMode) : "")
        + (Layers.Normalize(Item.Layer) == Layers.Desktop ? " · " + Loc.T("layer.desktop") : "")
        + (Item.Visible ? "" : " · " + Loc.T("active.hidden"));
    public double TileOpacity => Item.Visible ? 1.0 : 0.4;
    public string EyeTip => Item.Visible ? Loc.T("active.hide") : Loc.T("active.show");
    public ImageSource? Thumb => Photo == null ? null : Images.LoadThumb(Photo);
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public void RefreshText()
    {
        Raise(nameof(Name));
        Raise(nameof(Meta));
        Raise(nameof(EyeTip));
    }

    public void Update(ActiveItem item, PhotoItem? photo, int index)
    {
        Item = item;
        Photo = photo;
        Index = index;
        Raise(nameof(Name));
        Raise(nameof(Meta));
        Raise(nameof(TileOpacity));
        Raise(nameof(EyeTip));
        Raise(nameof(Thumb));
    }
}

public static class Images
{
    private static readonly Dictionary<string, ImageSource> ThumbCache = new();

    public static ImageSource? LoadThumb(PhotoItem p)
    {
        var path = Store.ThumbPath(p);
        if (ThumbCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path) && !Library.TryCreateThumbnail(p)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            ThumbCache[path] = b;
            return b;
        }
        catch { return null; }
    }

    public static void ForgetThumb(PhotoItem p) => ThumbCache.Remove(Store.ThumbPath(p));

    /// <summary>Skia 프레임(BGRA, premultiplied) → WPF 이미지. 어느 스레드에서나 호출 가능(Freeze됨).</summary>
    public static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null,
            bmp.GetPixels(), bmp.ByteCount, bmp.RowBytes);
        src.Freeze();
        return src;
    }
}

/// <summary>WPF에서 재생할 프레임 묶음.</summary>
public sealed class WpfAnimation
{
    public BitmapSource[] Frames { get; init; } = Array.Empty<BitmapSource>();
    public int[] Delays { get; init; } = Array.Empty<int>();
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>미리보기용 애니메이션 캐시 (UI 스레드 전용, 최근 24개).</summary>
public static class AnimationCache
{
    private const int Max = 24;
    private static readonly Dictionary<string, Task<WpfAnimation?>> Cache = new();
    private static readonly LinkedList<string> Order = new();

    public static Task<WpfAnimation?> GetAsync(string path, int maxSize)
    {
        string key = path + "|" + maxSize;
        if (Cache.TryGetValue(key, out var t))
        {
            Order.Remove(key);
            Order.AddFirst(key);
            return t;
        }
        t = Task.Run(() =>
        {
            try
            {
                using var a = GifAnimation.Load(path, maxSize);
                var frames = new BitmapSource[a.Frames.Count];
                for (int i = 0; i < frames.Length; i++) frames[i] = Images.ToBitmapSource(a.Frames[i]);
                return (WpfAnimation?)new WpfAnimation { Frames = frames, Delays = a.DelaysMs.ToArray(), Width = a.Width, Height = a.Height };
            }
            catch { return null; }
        });
        Cache[key] = t;
        Order.AddFirst(key);
        while (Order.Count > Max)
        {
            Cache.Remove(Order.Last!.Value);
            Order.RemoveLast();
        }
        return t;
    }

    public static void Invalidate(string path)
    {
        foreach (var key in new List<string>(Cache.Keys))
            if (key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
            {
                Cache.Remove(key);
                Order.Remove(key);
            }
    }
}

/// <summary>ImageSource 하나를 프레임 지연에 맞춰 바꿔 주는 재생기.</summary>
public sealed class AnimPlayer
{
    private readonly Action<ImageSource?> _set;
    private readonly DispatcherTimer _timer;
    private WpfAnimation? _anim;
    private int _index, _token;

    public double Speed { get; set; } = 1;

    public AnimPlayer(Action<ImageSource?> set)
    {
        _set = set;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Next();
    }

    public async void Play(string path, int maxSize)
    {
        int token = ++_token;
        _timer.Stop();
        var anim = await AnimationCache.GetAsync(path, maxSize);
        if (token != _token || anim == null || anim.Frames.Length == 0) return;
        _anim = anim;
        _index = 0;
        _set(anim.Frames[0]);
        if (anim.Frames.Length > 1) Schedule();
    }

    public void Stop()
    {
        _token++;
        _timer.Stop();
        _anim = null;
    }

    private void Next()
    {
        if (_anim == null) { _timer.Stop(); return; }
        _index = (_index + 1) % _anim.Frames.Length;
        _set(_anim.Frames[_index]);
        Schedule();
    }

    private void Schedule()
    {
        if (_anim == null) return;
        double speed = Speed <= 0 ? 1 : Speed;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _anim.Delays[_index] / speed));
        _timer.Start();
    }
}
