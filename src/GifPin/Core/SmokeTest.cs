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

        report.Append(RunPhysics()).Append(" · ");

        // 번역 사전: 모든 키에 영어·한국어가 있어야 함
        var missing = Strings.All.Where(kv => string.IsNullOrWhiteSpace(kv.Value.en) || string.IsNullOrWhiteSpace(kv.Value.ko)).Select(kv => kv.Key).ToList();
        if (missing.Count > 0) throw new Exception("untranslated: " + string.Join(", ", missing));
        report.Append($"strings {Strings.All.Count} ok");
        return report.ToString();
    }

    private static MotionParams Params(double speed = Motion.DefaultSpeed, bool jump = false, double height = 100, double interval = 0.5, double angle = 0, double friction = Motion.DefaultFriction)
        => new(speed, jump, height, interval, angle, friction);

    /// <summary>움직임 계산 점검 (화면 400×300, GIF 50×50 → 바닥 y=250, 오른쪽 끝 x=350).</summary>
    public static string RunPhysics()
    {
        const int W = 50, H = 50, R = 400, B = 300, Floor = 250, MaxX = 350;

        // 걷기: 공중에서 시작해도 바닥으로 내려와 걷고, 벽에서 돌아선다
        var m = new MotionState();
        m.Reset(Motion.Walk, Params(600), 0, 0);
        bool turned = false;
        for (int i = 0; i < 200; i++)
        {
            m.Step(0.05, W, H, 0, 0, R, B);
            turned |= m.FacingLeft;
            if (m.X < -0.5 || m.X > MaxX + 0.5) throw new Exception($"walk left the screen: x={m.X}");
        }
        if (Math.Abs(m.Y - Floor) > 0.5) throw new Exception($"walk not on floor: y={m.Y}");
        if (!turned) throw new Exception("walk never turned");

        // 떨어지기 (점프 끔): 바닥에 멈춤
        var f = new MotionState();
        f.Reset(Motion.Fall, Params(100), 0, 0);
        for (int i = 0; i < 400; i++) f.Step(0.02, W, H, 0, 0, R, B);
        if (Math.Abs(f.Y - Floor) > 0.5 || f.Vy != 0) throw new Exception($"fall did not settle: y={f.Y} vy={f.Vy}");
        if (f.Jumps != 0) throw new Exception("fall jumped while jumping is off");

        // 떨어지기 + 수직 점프: 높이 100 → 최고점 y≈150, 좌우로는 거의 안 움직임
        var j = new MotionState();
        j.Reset(Motion.Fall, Params(20, jump: true, height: 100, interval: 0.5), 200, 0);
        double minY = double.MaxValue, landedX = -1;
        for (int i = 0; i < 400; i++)
        {
            j.Step(0.01, W, H, 0, 0, R, B);
            if (j.Jumps > 0) minY = Math.Min(minY, j.Y);
            if (j.Jumps == 0 && j.Vy == 0 && Math.Abs(j.Y - Floor) < 0.5) landedX = j.X;
        }
        if (j.Jumps < 1) throw new Exception("fall never jumped");
        if (Math.Abs(minY - (Floor - 100)) > 8) throw new Exception($"jump height wrong: top y={minY:0.0}, expected ~{Floor - 100}");

        // 비스듬히 점프 (+40°): 오른쪽으로 가다가 벽에서 방향을 바꾸고, 화면 밖으로 나가지 않음
        var a = new MotionState();
        a.Reset(Motion.Fall, Params(20, jump: true, height: 120, interval: 0.3, angle: 40), 100, Floor);
        double maxX = 0; bool wentLeft = false;
        for (int i = 0; i < 2000; i++)
        {
            a.Step(0.01, W, H, 0, 0, R, B);
            maxX = Math.Max(maxX, a.X);
            if (a.Vx < -1) wentLeft = true;
            if (a.X < -0.5 || a.X > MaxX + 0.5 || a.Y < -0.5 || a.Y > Floor + 0.5) throw new Exception($"angled jump left the screen: {a.X:0},{a.Y:0}");
        }
        if (a.Jumps < 3 || maxX < MaxX - 1 || !wentLeft) throw new Exception($"angled jump: jumps={a.Jumps} maxX={maxX:0} turned={wentLeft}");

        // 미끄러짐: 빠르게 던지면 벽에 여러 번 튕기다 멈춤, 느리게 던지면 금방 멈춤
        var sFast = Slide(2500, 900, out int fastBounces, out double fastTime);
        var sSlow = Slide(150, 0, out int slowBounces, out double slowTime);
        if (fastBounces < 2) throw new Exception($"fast throw bounced {fastBounces} times");
        if (slowBounces != 0 || slowTime >= fastTime) throw new Exception($"slow throw: bounces={slowBounces} time={slowTime:0.00} vs fast {fastTime:0.00}");
        var still = new MotionState();
        still.Reset(Motion.Slide, Params(), 100, 100);
        still.Release(100, 100, 3, 0);
        if (still.IsSliding) throw new Exception("tiny throw should not slide");

        return $"motion ok (jump top {minY:0}, fast slide {fastBounces} bounces/{fastTime:0.0}s, slow {slowTime:0.0}s)";

        static MotionState Slide(double vx, double vy, out int bounces, out double time)
        {
            var s = new MotionState();
            s.Reset(Motion.Slide, Params(friction: Motion.DefaultFriction), 100, 100);
            s.Release(100, 100, vx, vy);
            bounces = 0; time = 0;
            double px = s.Vx, py = s.Vy;
            while (s.IsSliding && time < 30)
            {
                s.Step(0.01, W, H, 0, 0, R, B);
                time += 0.01;
                if (Math.Sign(s.Vx) != Math.Sign(px) && s.Vx != 0 || Math.Sign(s.Vy) != Math.Sign(py) && s.Vy != 0) bounces++;
                px = s.Vx; py = s.Vy;
                if (s.X < -0.5 || s.X > MaxX + 0.5 || s.Y < -0.5 || s.Y > Floor + 0.5) throw new Exception($"slide left the screen: {s.X:0},{s.Y:0}");
            }
            if (s.IsSliding) throw new Exception("slide never stopped");
            return s;
        }
    }
}
