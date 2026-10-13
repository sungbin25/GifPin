using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// GIF·애니메이션 WebP·APNG의 프레임을 미리 디코딩해 보관한다.
/// 각 프레임은 UpdateLayeredWindow가 요구하는 32비트 BGRA, premultiplied alpha 형식이다.
///
/// 메모리 절약:
///  - 원하는 크기(화면에 보이는 크기 등)가 원본보다 작으면 프레임마다 디코딩 직후 바로 줄여서 저장한다.
///    (원본 크기 프레임은 다음 프레임을 합성하는 데 필요한 동안만 잠깐 들고 있음)
///  - 전체가 메모리 예산을 넘으면 해상도를 더 낮추고, 그래도 넘으면 프레임을 건너뛴다(시간은 합쳐서 속도 유지).
///  - 캔버스가 터무니없이 크면(조작된 파일 등) 디코딩하지 않는다.
/// </summary>
internal sealed unsafe class GifAnimation : IDisposable
{
    /// <summary>애니메이션 하나에 쓸 수 있는 최대 메모리 (기본).</summary>
    public const long DefaultBudget = 384L * 1024 * 1024;
    /// <summary>이보다 큰 캔버스(픽셀 수)는 열지 않음: 약 8000×5000.</summary>
    public const long MaxCanvasPixels = 40_000_000;
    private const int MinLongSide = 64; // 예산 때문에 줄이더라도 이보다 작게는 안 줄임

    /// <summary>저장된 프레임 크기.</summary>
    public int Width { get; internal init; }
    public int Height { get; internal init; }
    /// <summary>원본 이미지 크기 (화면 크기 계산은 이것을 기준으로).</summary>
    public int SourceWidth { get; internal init; }
    public int SourceHeight { get; internal init; }
    /// <summary>예산 때문에 건너뛴 프레임 간격 (1 = 모두 사용).</summary>
    public int FrameStep { get; internal init; } = 1;
    public List<SKBitmap> Frames { get; } = new();
    public List<int> DelaysMs { get; } = new();
    public bool IsAnimated => Frames.Count > 1;
    public long ByteSize
    {
        get
        {
            long n = 0;
            foreach (var f in Frames) n += (long)f.Width * f.Height * 4;
            return n;
        }
    }

    /// <summary>파일을 디코딩한다. maxSize &gt; 0이면 긴 변이 maxSize를 넘지 않게 줄인다.</summary>
    public static GifAnimation Load(string path, int maxSize = 0) => Load(path, maxSize, maxSize, DefaultBudget);

    /// <summary>프레임을 maxWidth × maxHeight 안에 들어가게(비율 유지) 줄여서, budget 바이트 안에서 디코딩.</summary>
    public static GifAnimation Load(string path, int maxWidth, int maxHeight, long budget)
    {
        if (ApngDecoder.IsApng(path))
        {
            var probe = ApngDecoder.Probe(path) ?? throw new InvalidDataException(Loc.T("err.unsupportedImage"));
            var plan = Plan(probe.width, probe.height, probe.frames, maxWidth, maxHeight, budget);
            return ApngDecoder.Decode(path, plan.w, plan.h, plan.step); // SkiaSharp은 APNG 첫 장만 읽으므로 직접 처리
        }

        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException(Loc.T("err.unsupportedImage"));
        int srcW = codec.Info.Width, srcH = codec.Info.Height;
        int count = Math.Max(1, codec.FrameCount);
        var p = Plan(srcW, srcH, count, maxWidth, maxHeight, budget);
        return DecodeCodec(codec, srcW, srcH, count, p.w, p.h, p.step);
    }

    /// <summary>정해진 크기·간격으로 디코딩 (점검용).</summary>
    internal static GifAnimation LoadPlanned(string path, int w, int h, int step)
    {
        if (ApngDecoder.IsApng(path)) return ApngDecoder.Decode(path, w, h, step);
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException(Loc.T("err.unsupportedImage"));
        return DecodeCodec(codec, codec.Info.Width, codec.Info.Height, Math.Max(1, codec.FrameCount), w, h, step);
    }

    /// <summary>
    /// 저장 크기와 건너뛰기 간격을 정한다.
    /// 1) 원하는 크기에 맞춰 축소, 2) 예산을 넘으면 더 축소(긴 변 64px까지), 3) 그래도 넘으면 프레임 건너뛰기.
    /// </summary>
    internal static (int w, int h, int step) Plan(int srcW, int srcH, int frames, int maxW, int maxH, long budget)
    {
        if (srcW <= 0 || srcH <= 0) throw new InvalidDataException(Loc.T("err.unsupportedImage"));
        if ((long)srcW * srcH > MaxCanvasPixels) throw new InvalidDataException(Loc.T("err.imageTooLarge"));
        frames = Math.Max(1, frames);
        budget = Math.Max(budget, 1L << 20);

        double k = 1;
        if (maxW > 0 && srcW > maxW) k = Math.Min(k, (double)maxW / srcW);
        if (maxH > 0 && srcH > maxH) k = Math.Min(k, (double)maxH / srcH);

        double bytes = frames * (srcW * k) * (srcH * k) * 4;
        if (bytes > budget)
        {
            double fit = k * Math.Sqrt(budget / bytes);
            double floor = Math.Min(1, (double)MinLongSide / Math.Max(srcW, srcH));
            k = Math.Max(fit, Math.Min(k, floor));
        }
        int w = Math.Max(1, (int)Math.Round(srcW * k)), h = Math.Max(1, (int)Math.Round(srcH * k));
        double perFrame = (double)w * h * 4;
        int step = (int)Math.Max(1, Math.Ceiling(frames * perFrame / budget));
        return (w, h, step);
    }

    private static GifAnimation DecodeCodec(SKCodec codec, int srcW, int srcH, int count, int tw, int th, int step)
    {
        var info = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var target = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
        bool scale = tw != srcW || th != srcH;
        var anim = new GifAnimation { Width = tw, Height = th, SourceWidth = srcW, SourceHeight = srcH, FrameStep = step };
        SKCodecFrameInfo[] infos = codec.FrameInfo;

        // 어떤 원본 프레임이 나중에 합성 바탕으로 쓰이는지 (마지막으로 쓰이는 프레임 번호)
        var lastUse = new Dictionary<int, int>();
        for (int i = 0; i < count && i < infos.Length; i++)
            if (infos[i].RequiredFrame >= 0) lastUse[infos[i].RequiredFrame] = i;
        var full = new Dictionary<int, SKBitmap>(); // 아직 필요한 원본 크기 프레임
        var stored = new HashSet<SKBitmap>();        // anim.Frames에 그대로 들어간 원본 (중복 해제 방지)

        try
        {
            for (int i = 0; i < count; i++)
            {
                var bmp = new SKBitmap(info);
                int required = i < infos.Length ? infos[i].RequiredFrame : -1;
                if (required >= 0 && full.TryGetValue(required, out var src))
                    Buffer.MemoryCopy((void*)src.GetPixels(), (void*)bmp.GetPixels(), bmp.ByteCount, src.ByteCount);
                else
                {
                    bmp.Erase(SKColors.Transparent);
                    required = -1;
                }

                var result = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, required));
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    bmp.Dispose();
                    if (i == 0) throw new InvalidDataException(Loc.F("err.firstFrame", result));
                    break; // 뒤쪽 프레임이 깨졌으면 정상 프레임까지만 사용
                }

                int d = i < infos.Length ? infos[i].Duration : 100;
                d = d <= 10 ? 100 : d; // 브라우저와 동일: 0~10ms는 100ms로 취급
                bool keep = i % step == 0;
                if (keep)
                {
                    if (scale) anim.Frames.Add(bmp.Resize(target, SKFilterQuality.Medium) ?? bmp.Copy());
                    else { anim.Frames.Add(bmp); stored.Add(bmp); }
                    anim.DelaysMs.Add(d);
                }
                else if (anim.DelaysMs.Count > 0) anim.DelaysMs[^1] += d; // 건너뛴 프레임 시간은 앞 프레임에 더함

                // 나중에 바탕으로 쓰일 프레임만 원본 크기로 남겨 둠
                if (lastUse.TryGetValue(i, out int until) && until > i) full[i] = bmp;
                else if (!stored.Contains(bmp)) bmp.Dispose();

                // 더는 안 쓰는 원본 정리
                foreach (var key in new List<int>(full.Keys))
                    if (lastUse[key] <= i)
                    {
                        var b = full[key];
                        full.Remove(key);
                        if (!stored.Contains(b)) b.Dispose();
                    }
            }
        }
        catch
        {
            anim.Dispose();
            throw;
        }
        finally
        {
            foreach (var b in full.Values) if (!stored.Contains(b)) b.Dispose();
        }
        return anim;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
        Frames.Clear();
    }
}
