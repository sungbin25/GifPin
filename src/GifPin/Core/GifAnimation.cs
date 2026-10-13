using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// GIF(또는 애니메이션 WebP)의 모든 프레임을 미리 디코딩해 보관한다.
/// 각 프레임은 UpdateLayeredWindow가 요구하는 32비트 BGRA, premultiplied alpha 형식이다.
/// </summary>
internal sealed unsafe class GifAnimation : IDisposable
{
    public int Width { get; internal init; }
    public int Height { get; internal init; }
    public List<SKBitmap> Frames { get; } = new();
    public List<int> DelaysMs { get; } = new();
    public bool IsAnimated => Frames.Count > 1;

    /// <summary>파일을 디코딩한다. maxSize &gt; 0이면 긴 변이 maxSize를 넘지 않게 줄인다(미리보기용).</summary>
    public static GifAnimation Load(string path, int maxSize = 0)
    {
        var anim = LoadFull(path);
        if (maxSize <= 0 || Math.Max(anim.Width, anim.Height) <= maxSize) return anim;

        float k = (float)maxSize / Math.Max(anim.Width, anim.Height);
        var info = new SKImageInfo(Math.Max(1, (int)(anim.Width * k)), Math.Max(1, (int)(anim.Height * k)),
            SKColorType.Bgra8888, SKAlphaType.Premul);
        var scaled = new GifAnimation { Width = info.Width, Height = info.Height };
        foreach (var f in anim.Frames)
            scaled.Frames.Add(f.Resize(info, SKFilterQuality.Medium) ?? f.Copy());
        scaled.DelaysMs.AddRange(anim.DelaysMs);
        anim.Dispose();
        return scaled;
    }

    private static GifAnimation LoadFull(string path)
    {
        if (ApngDecoder.IsApng(path)) return ApngDecoder.Decode(path); // SkiaSharp은 APNG 첫 장만 읽음

        using var codec = SKCodec.Create(path)
            ?? throw new InvalidDataException(Loc.T("err.unsupportedImage"));

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var anim = new GifAnimation { Width = info.Width, Height = info.Height };
        SKCodecFrameInfo[] frameInfos = codec.FrameInfo;
        int count = Math.Max(1, codec.FrameCount);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var bmp = new SKBitmap(info);
                int required = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;

                // GIF disposal 규칙: 이 프레임이 의존하는 이전 프레임 위에 덧그린다.
                if (required >= 0 && required < anim.Frames.Count)
                {
                    var src = anim.Frames[required];
                    Buffer.MemoryCopy((void*)src.GetPixels(), (void*)bmp.GetPixels(), bmp.ByteCount, src.ByteCount);
                }
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

                anim.Frames.Add(bmp);
                int d = i < frameInfos.Length ? frameInfos[i].Duration : 100;
                anim.DelaysMs.Add(d <= 10 ? 100 : d); // 브라우저와 동일: 0~10ms는 100ms로 취급
            }
        }
        catch
        {
            anim.Dispose();
            throw;
        }
        return anim;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
        Frames.Clear();
    }
}
