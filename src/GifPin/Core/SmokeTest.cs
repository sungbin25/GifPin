using System;
using System.Collections.Generic;
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

    /// <summary>v0.3 기능 점검: APNG, 스프라이트 시트, 프레임 폴더, 에셋 팩, 움직임, 번역 사전.</summary>
    public static string RunV03(string assetsDir)
    {
        var report = new System.Text.StringBuilder();

        // APNG: 4프레임, 프레임마다 이전 프레임을 지움(dispose=background)
        string apng = Path.Combine(assetsDir, "sample.apng");
        if (!ApngDecoder.IsApng(apng)) throw new Exception("APNG not detected");
        using (var a = GifAnimation.Load(apng))
        {
            if (a.Frames.Count != 4) throw new Exception($"APNG frames {a.Frames.Count} != 4");
            var last = a.Frames[3];
            if (last.GetPixel(50, 24).Alpha < 200) throw new Exception("APNG last frame missing its dot");
            if (last.GetPixel(14, 24).Alpha != 0) throw new Exception("APNG dispose not applied (old dot still visible)");
            report.Append($"apng {a.Frames.Count} frames ok · ");
        }

        // 스프라이트 시트: 64×16 → 4칸 자동 추정 → WebP 저장 → 다시 읽기
        string strip = Path.Combine(assetsDir, "strip.png");
        using (var sheet = SpriteBuilder.LoadBgra(strip))
        {
            var (cols, rows) = SpriteBuilder.GuessGrid(sheet.Width, sheet.Height);
            if (cols != 4 || rows != 1) throw new Exception($"grid guess {cols}x{rows} != 4x1");
            var frames = SpriteBuilder.FromSheet(sheet, cols, rows, 0);
            string outFile = Path.Combine(Store.DataDir, "smoke-sprite.webp");
            try { SpriteBuilder.Save(frames, 8, outFile); }
            finally { foreach (var f in frames) f.Dispose(); }
            using var back = GifAnimation.Load(outFile);
            if (back.Frames.Count != 4 || back.Width != 16) throw new Exception($"sprite webp {back.Frames.Count} frames {back.Width}px");
            if (back.DelaysMs[0] != 125) throw new Exception($"sprite delay {back.DelaysMs[0]} != 125");
            report.Append("sprite sheet ok · ");
        }

        // 프레임 폴더: f1, f2, f10 순서 (이름의 숫자 크기 순)
        var files = SpriteBuilder.ListFrameFiles(Path.Combine(assetsDir, "frames"));
        var order = string.Join(",", files.Select(Path.GetFileNameWithoutExtension));
        if (order != "f1,f2,f10") throw new Exception($"frame order {order}");
        var ff = SpriteBuilder.FromFiles(files);
        if (ff.Count != 3) throw new Exception("frame folder count");
        foreach (var f in ff) f.Dispose();
        report.Append("frame folder ok · ");

        // 에셋 팩: 내보내기 → 풀기 (배치 포함)
        var photos = new List<PhotoItem>
        {
            new() { Id = "pa", Name = "apng sample", File = apng, Width = 64, Height = 48, FrameCount = 4, Favorite = true },
            new() { Id = "pb", Name = "strip", File = strip, Width = 64, Height = 16, FrameCount = 1, Pixelated = true },
        };
        var layout = new List<ActiveItem> { new() { Id = "x", PhotoId = "pa", X = 100, Y = 100, Scale = 2, MotionMode = Motion.Bounce } };
        string zip = Path.Combine(Store.DataDir, "smoke-pack.zip");
        AssetPack.Export(zip, "smoke pack", photos, layout);
        using (var ex = AssetPack.Extract(zip))
        {
            if (ex.Files.Count != 2) throw new Exception($"pack files {ex.Files.Count} != 2");
            if (ex.Layout.Count != 1 || ex.Layout[0].MotionMode != Motion.Bounce) throw new Exception("pack layout lost");
            if (!ex.Files.Any(f => f.item.Favorite && f.item.Name == "apng sample")) throw new Exception("pack metadata lost");
            if (!ex.Files.All(f => File.Exists(f.path))) throw new Exception("pack files not extracted");
            report.Append("asset pack ok · ");
        }

        // 움직임: 걷기는 바닥에 서서 오른쪽 벽에 닿으면 방향을 바꾼다
        var m = new MotionState();
        m.Reset(Motion.Walk, 600, 0, 0);
        for (int i = 0; i < 200; i++) m.Step(0.05, 50, 50, 0, 0, 400, 300);
        if (m.Y != 250) throw new Exception($"walk not on floor: y={m.Y}");
        if (!m.FacingLeft && m.X < 340) throw new Exception("walk never turned");
        var fall = new MotionState();
        fall.Reset(Motion.Fall, 100, 0, 0);
        for (int i = 0; i < 400; i++) fall.Step(0.02, 50, 50, 0, 0, 400, 300);
        if (Math.Abs(fall.Y - 250) > 0.5 || fall.Vy != 0) throw new Exception($"fall did not settle: y={fall.Y} vy={fall.Vy}");
        report.Append("motion ok · ");

        // 번역 사전: 모든 키에 영어·한국어가 있어야 함
        var missing = Strings.All.Where(kv => string.IsNullOrWhiteSpace(kv.Value.en) || string.IsNullOrWhiteSpace(kv.Value.ko)).Select(kv => kv.Key).ToList();
        if (missing.Count > 0) throw new Exception("untranslated: " + string.Join(", ", missing));
        report.Append($"strings {Strings.All.Count} ok");
        return report.ToString();
    }
}
