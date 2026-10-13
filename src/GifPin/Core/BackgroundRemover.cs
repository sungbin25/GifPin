using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace GifPin.Core;

/// <summary>
/// 단색 배경 지우기.
///  1) 배경색과의 색 거리(0~100)가 허용 오차 이하인 픽셀 = 배경 후보
///  2) "가장자리에서 이어진 부분만"이면 이미지 테두리에서 시작해 상하좌우로 이어진 후보만 지움 (윤곽선 안쪽 흰색 보존)
///  3) 다듬기 폭 안의 경계 픽셀은 배경색에 가까운 만큼 반투명하게 하고 섞인 배경색 성분을 제거 (흰 테두리 제거)
/// </summary>
public static class BackgroundRemover
{
    /// <summary>다듬기 구간: 허용 오차보다 이만큼(0~100 척도) 더 먼 색까지 서서히 불투명해짐.</summary>
    private const double EdgeRange = 30;

    public static SKBitmap Process(SKBitmap src, SKColor bg, int tolerance, bool connectedOnly, int refine)
    {
        int w = src.Width, h = src.Height, n = w * h;
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte[] px = new byte[n * 4];
        CopyOut(src, px);

        // 1) 색 거리
        var dist = new float[n];
        var candidate = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            byte a = px[o + 3];
            if (a == 0) { dist[i] = 0; candidate[i] = true; continue; }
            // premultiplied → straight
            double b = px[o] * 255.0 / a, g = px[o + 1] * 255.0 / a, r = px[o + 2] * 255.0 / a;
            double dr = r - bg.Red, dg = g - bg.Green, db = b - bg.Blue;
            dist[i] = (float)(Math.Sqrt(dr * dr + dg * dg + db * db) / (Math.Sqrt(3) * 255) * 100);
            candidate[i] = dist[i] <= tolerance;
        }

        // 2) 지울 영역
        var removed = new bool[n];
        if (connectedOnly)
        {
            var stack = new Stack<int>();
            void Seed(int i) { if (candidate[i] && !removed[i]) { removed[i] = true; stack.Push(i); } }
            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                if (x > 0) Seed(i - 1);
                if (x < w - 1) Seed(i + 1);
                if (y > 0) Seed(i - w);
                if (y < h - 1) Seed(i + w);
            }
        }
        else
        {
            Array.Copy(candidate, removed, n);
        }

        // 3) 다듬기: 지운 영역에서 refine px 안쪽 경계 고리
        var ring = new bool[n];
        if (refine > 0)
        {
            var frontier = (bool[])removed.Clone();
            for (int step = 0; step < refine; step++)
            {
                var next = (bool[])frontier.Clone();
                for (int i = 0; i < n; i++)
                {
                    if (frontier[i]) continue;
                    int x = i % w, y = i / w;
                    bool near = (x > 0 && frontier[i - 1]) || (x < w - 1 && frontier[i + 1])
                             || (y > 0 && frontier[i - w]) || (y < h - 1 && frontier[i + w]);
                    if (near) { next[i] = true; ring[i] = true; }
                }
                frontier = next;
            }
        }

        // 결과 쓰기
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            if (removed[i]) { px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0; continue; }
            if (!ring[i] || px[o + 3] == 0) continue;

            double t = Math.Clamp((dist[i] - tolerance) / EdgeRange, 0, 1); // 0 = 배경색, 1 = 전경
            if (t >= 1) continue;
            double srcA = px[o + 3] / 255.0;
            double alpha = srcA * t;
            if (alpha < 0.02) { px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0; continue; }

            // straight 색에서 배경색 성분 제거: c = t*fg + (1-t)*bg → fg = (c - (1-t)*bg) / t
            double b = px[o] / srcA, g = px[o + 1] / srcA, r = px[o + 2] / srcA;
            double fb = Math.Clamp((b - (1 - t) * bg.Blue) / t, 0, 255);
            double fg = Math.Clamp((g - (1 - t) * bg.Green) / t, 0, 255);
            double fr = Math.Clamp((r - (1 - t) * bg.Red) / t, 0, 255);
            px[o] = (byte)Math.Round(fb * alpha);
            px[o + 1] = (byte)Math.Round(fg * alpha);
            px[o + 2] = (byte)Math.Round(fr * alpha);
            px[o + 3] = (byte)Math.Round(alpha * 255);
        }

        CopyIn(px, dst);
        return dst;
    }

    /// <summary>모든 프레임을 병렬 처리. progress는 0~1.</summary>
    public static List<SKBitmap> ProcessAll(IReadOnlyList<SKBitmap> frames, SKColor bg, int tolerance, bool connectedOnly,
        int refine, IProgress<double>? progress, CancellationToken token)
    {
        var results = new SKBitmap[frames.Count];
        int done = 0;
        try
        {
            Parallel.For(0, frames.Count, new ParallelOptions { CancellationToken = token }, i =>
            {
                results[i] = Process(frames[i], bg, tolerance, connectedOnly, refine);
                progress?.Report(Interlocked.Increment(ref done) / (double)frames.Count);
            });
        }
        catch
        {
            foreach (var r in results) r?.Dispose();
            throw;
        }
        return results.ToList();
    }

    /// <summary>
    /// 이미지 테두리에서 가장 많이 나오는 색을 배경색으로 추정.
    /// 테두리의 절반 이상이 이미 투명하면 null (이미 배경이 투명함).
    /// </summary>
    public static SKColor? DetectBackground(SKBitmap frame)
    {
        int w = frame.Width, h = frame.Height;
        var counts = new Dictionary<uint, int>();
        int transparent = 0, total = 0;
        void Count(int x, int y)
        {
            var c = frame.GetPixel(x, y);
            total++;
            if (c.Alpha < 16) { transparent++; return; }
            uint key = (uint)((c.Red & 0xF8) << 16 | (c.Green & 0xF8) << 8 | (c.Blue & 0xF8)); // 비슷한 색끼리 묶음
            counts[key] = counts.TryGetValue(key, out var v) ? v + 1 : 1;
        }
        for (int x = 0; x < w; x++) { Count(x, 0); Count(x, h - 1); }
        for (int y = 1; y < h - 1; y++) { Count(0, y); Count(w - 1, y); }
        if (total == 0 || transparent * 2 >= total || counts.Count == 0) return null;

        uint best = counts.OrderByDescending(kv => kv.Value).First().Key;
        // 묶음 안의 실제 평균색을 쓰지 않고, 테두리에서 그 묶음에 속한 첫 픽셀 색을 쓴다
        for (int x = 0; x < w; x++)
        {
            foreach (int y in new[] { 0, h - 1 })
            {
                var c = frame.GetPixel(x, y);
                if (c.Alpha >= 16 && (uint)((c.Red & 0xF8) << 16 | (c.Green & 0xF8) << 8 | (c.Blue & 0xF8)) == best)
                    return new SKColor(c.Red, c.Green, c.Blue);
            }
        }
        return new SKColor((byte)(best >> 16), (byte)(best >> 8), (byte)best);
    }

    private static unsafe void CopyOut(SKBitmap src, byte[] px)
    {
        if (src.ColorType == SKColorType.Bgra8888 && src.AlphaType == SKAlphaType.Premul && src.RowBytes == src.Width * 4)
        {
            fixed (byte* p = px) Buffer.MemoryCopy((void*)src.GetPixels(), p, px.Length, px.Length);
            return;
        }
        using var tmp = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        src.CopyTo(tmp, SKColorType.Bgra8888);
        fixed (byte* p = px) Buffer.MemoryCopy((void*)tmp.GetPixels(), p, px.Length, px.Length);
    }

    private static unsafe void CopyIn(byte[] px, SKBitmap dst)
    {
        fixed (byte* p = px) Buffer.MemoryCopy(p, (void*)dst.GetPixels(), px.Length, px.Length);
    }

    public static string ToHex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    public static bool TryParseHex(string? s, out SKColor color)
    {
        color = SKColors.White;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
