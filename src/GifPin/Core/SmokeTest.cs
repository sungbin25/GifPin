using System;
using System.IO;
using System.Linq;

namespace GifPin.Core;

/// <summary>
/// CI 점검용 (GIFPIN_SMOKETEST=1): 실제 파이프라인을 한 번 돌려 본다.
/// 디코딩 → 배경 감지 → 배경 지우기 → 애니메이션 WebP 저장 → 다시 디코딩해 프레임 수·투명도 확인.
/// </summary>
public static class SmokeTest
{
    public static string RunPipeline(string gifPath)
    {
        using var anim = GifAnimation.Load(gifPath);
        var bg = BackgroundRemover.DetectBackground(anim.Frames[0])
                 ?? throw new Exception("배경색을 감지하지 못했습니다");
        var results = BackgroundRemover.ProcessAll(anim.Frames, bg, 15, true, 1, null, default);
        string outFile = Path.Combine(Store.DataDir, "smoke.webp");
        try
        {
            WebpWriter.Save(outFile, results, anim.DelaysMs);
        }
        finally
        {
            foreach (var r in results) r.Dispose();
        }

        using var back = GifAnimation.Load(outFile);
        if (back.Frames.Count != anim.Frames.Count)
            throw new Exception($"WebP 프레임 수 불일치: {back.Frames.Count} != {anim.Frames.Count}");
        var corner = back.Frames[0].GetPixel(0, 0);
        if (corner.Alpha != 0) throw new Exception($"배경이 투명해지지 않음 (모서리 알파 {corner.Alpha})");
        var center = back.Frames[0].GetPixel(back.Width / 2, back.Height / 2 + 12);
        if (center.Alpha < 200) throw new Exception($"윤곽선 안쪽 흰색이 지워짐 (가운데 알파 {center.Alpha})");

        return $"pipeline ok: {anim.Frames.Count} frames, bg {BackgroundRemover.ToHex(bg)}, webp {new FileInfo(outFile).Length} bytes, delays {string.Join(",", back.DelaysMs.Take(3))}";
    }
}
