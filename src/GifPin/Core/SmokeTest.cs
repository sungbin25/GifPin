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
        var layout = new List<ActiveItem> { new() { Id = "x", PhotoId = "pa", X = 100, Y = 100, Scale = 2, MotionMode = Motion.Bounce, FxGlow = true, FxGlowColor = "#81D4FA", FxParticles = true, FxParticleKind = Fx.Heart } };
        string zip = Path.Combine(Store.DataDir, "smoke-pack.zip");
        AssetPack.Export(zip, "smoke pack", photos, layout);
        using (var ex = AssetPack.Extract(zip))
        {
            if (ex.Files.Count != 2) throw new Exception($"pack files {ex.Files.Count} != 2");
            if (ex.Layout.Count != 1 || ex.Layout[0].MotionMode != Motion.Bounce) throw new Exception("pack layout lost");
            var st = ex.Layout[0].Settings;
            if (st == null || !st.FxGlow || st.FxGlowColor != "#81D4FA" || st.FxParticleKind != Fx.Heart) throw new Exception("pack lost effect settings");
            if (!ex.Files.Any(f => f.item.Favorite && f.item.Name == "apng sample")) throw new Exception("pack metadata lost");
            if (!ex.Files.All(f => File.Exists(f.path))) throw new Exception("pack files not extracted");
            report.Append("asset pack ok · ");
        }

        report.Append(RunPhysics()).Append(" · ");
        report.Append(RunEffects()).Append(" · ");

        // 번역 사전: 모든 키에 영어·한국어가 있어야 함
        var missing = Strings.All.Where(kv => string.IsNullOrWhiteSpace(kv.Value.en) || string.IsNullOrWhiteSpace(kv.Value.ko)).Select(kv => kv.Key).ToList();
        if (missing.Count > 0) throw new Exception("untranslated: " + string.Join(", ", missing));
        report.Append($"strings {Strings.All.Count} ok");
        return report.ToString();
    }

    private static MotionParams Params(double speed = Motion.DefaultSpeed, bool jump = false, double height = 100, double interval = 0.5, double angle = 0, double friction = Motion.DefaultFriction, bool gravity = true)
        => new() { Speed = speed, JumpEnabled = jump, JumpHeight = height, JumpInterval = interval, JumpAngle = angle, Friction = friction, Gravity = gravity };

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

        // 중력 끔 + 걷기: 배치한 높이(y=100)에서 수평으로만 이동
        var wg = new MotionState();
        wg.Reset(Motion.Walk, Params(300, gravity: false), 50, 100);
        double wMinX = double.MaxValue, wMaxX = 0;
        for (int i = 0; i < 300; i++)
        {
            wg.Step(0.02, W, H, 0, 0, R, B);
            if (Math.Abs(wg.Y - 100) > 0.01) throw new Exception($"walk without gravity changed height: y={wg.Y}");
            wMinX = Math.Min(wMinX, wg.X); wMaxX = Math.Max(wMaxX, wg.X);
        }
        if (wMaxX < MaxX - 1 || wMinX > 1) throw new Exception($"walk without gravity did not cross the screen: {wMinX:0}..{wMaxX:0}");

        // 중력 끔 + 점프: y=120을 땅으로 삼아 뛰었다가 다시 120으로 내려옴 (바닥 250까지 떨어지지 않음)
        var jg = new MotionState();
        jg.Reset(Motion.Fall, Params(20, jump: true, height: 80, interval: 0.4, gravity: false), 200, 120);
        double jTop = double.MaxValue, jLow = 0;
        for (int i = 0; i < 400; i++)
        {
            jg.Step(0.01, W, H, 0, 0, R, B);
            jTop = Math.Min(jTop, jg.Y); jLow = Math.Max(jLow, jg.Y);
        }
        if (jg.Jumps < 2) throw new Exception($"jump without gravity: only {jg.Jumps} jumps");
        if (jLow > 120.5) throw new Exception($"jump without gravity fell below its ground: y={jLow:0.0}");
        if (Math.Abs(jTop - 40) > 8) throw new Exception($"jump without gravity: top y={jTop:0.0}, expected ~40");
        // 떨어뜨린 높이가 새 땅
        jg.Release(200, 60, 0, 0);
        for (int i = 0; i < 30; i++) jg.Step(0.01, W, H, 0, 0, R, B);
        if (jg.Y > 60.5) throw new Exception($"released GIF fell below the new ground: y={jg.Y:0.0}");

        // 랜덤 점프: 방향은 좌우가 섞이고, 높이는 범위 안에서 매번 달라짐
        var rj = new MotionState(seed: 7);
        rj.Reset(Motion.Fall, Params(20, jump: true, interval: 0.3) with
        {
            JumpAngleRandom = true, JumpAngleRange = 60, JumpHeightRandom = true, JumpHeightMin = 40, JumpHeightMax = 140,
        }, 175, Floor);
        int lefts = 0, rights = 0, lastJumps = 0;
        double peak = Floor, minPeak = double.MaxValue, maxPeak = 0;
        for (int i = 0; i < 4000; i++)
        {
            rj.Step(0.01, W, H, 0, 0, R, B);
            if (rj.Jumps != lastJumps)
            {
                if (lastJumps > 0) { minPeak = Math.Min(minPeak, Floor - peak); maxPeak = Math.Max(maxPeak, Floor - peak); }
                lastJumps = rj.Jumps; peak = Floor;
                if (rj.Vx < -1) lefts++; else if (rj.Vx > 1) rights++;
            }
            peak = Math.Min(peak, rj.Y);
            if (rj.X < -0.5 || rj.X > MaxX + 0.5) throw new Exception($"random jump left the screen: x={rj.X:0}");
        }
        if (rj.Jumps < 8 || lefts == 0 || rights == 0) throw new Exception($"random jump direction: jumps={rj.Jumps} left={lefts} right={rights}");
        if (minPeak < 40 - 8 || maxPeak > 140 + 8 || maxPeak - minPeak < 20) throw new Exception($"random jump height {minPeak:0}..{maxPeak:0}, expected varied within 40..140");

        // 미끄러짐 자동 밀기 (고정): 오른쪽(0°)으로 400px/s, 0.5초마다
        var ap = new MotionState(seed: 1);
        ap.Reset(Motion.Slide, Params() with
        {
            SlideAuto = true, PushDirRandom = false, PushAngle = 0, PushForceRandom = false, PushForce = 400,
            PushIntervalRandom = false, PushInterval = 0.5,
        }, 100, 100);
        double t = 0;
        while (ap.Pushes == 0 && t < 2) { ap.Step(0.01, W, H, 0, 0, R, B); t += 0.01; }
        if (ap.Pushes != 1 || Math.Abs(t - 0.5) > 0.03) throw new Exception($"fixed push came at {t:0.00}s (pushes={ap.Pushes})");
        if (ap.Vx < 300 || Math.Abs(ap.Vy) > 0.001) throw new Exception($"fixed push direction/force wrong: vx={ap.Vx:0} vy={ap.Vy:0}");

        // 미끄러짐 자동 밀기 (랜덤): 여러 번 밀리고, 방향이 섞이고, 화면 밖으로 나가지 않음
        var ar = new MotionState(seed: 3);
        ar.Reset(Motion.Slide, Params() with
        {
            SlideAuto = true, PushForceMin = 300, PushForceMax = 900, PushIntervalMin = 0.5, PushIntervalMax = 1.0,
        }, 150, 120);
        bool anyLeft = false, anyRight = false, anyUp = false, anyDown = false;
        int lastPushes = 0;
        for (int i = 0; i < 3000; i++)
        {
            ar.Step(0.01, W, H, 0, 0, R, B);
            if (ar.Pushes != lastPushes)
            {
                lastPushes = ar.Pushes;
                double force = Math.Sqrt(ar.Vx * ar.Vx + ar.Vy * ar.Vy);
                if (force < 290 || force > 910) throw new Exception($"random push force {force:0} outside 300..900");
                anyLeft |= ar.Vx < 0; anyRight |= ar.Vx > 0; anyUp |= ar.Vy < 0; anyDown |= ar.Vy > 0;
            }
            if (ar.X < -0.5 || ar.X > MaxX + 0.5 || ar.Y < -0.5 || ar.Y > Floor + 0.5) throw new Exception($"auto slide left the screen: {ar.X:0},{ar.Y:0}");
        }
        if (ar.Pushes < 5 || !(anyLeft && anyRight && anyUp && anyDown)) throw new Exception($"random pushes: {ar.Pushes}, dirs L{anyLeft} R{anyRight} U{anyUp} D{anyDown}");

        // 미끄러짐: 빠르게 던지면 벽에 여러 번 튕기다 멈춤, 느리게 던지면 금방 멈춤
        var sFast = Slide(2500, 900, out int fastBounces, out double fastTime);
        var sSlow = Slide(150, 0, out int slowBounces, out double slowTime);
        if (fastBounces < 2) throw new Exception($"fast throw bounced {fastBounces} times");
        if (slowBounces != 0 || slowTime >= fastTime) throw new Exception($"slow throw: bounces={slowBounces} time={slowTime:0.00} vs fast {fastTime:0.00}");
        var still = new MotionState();
        still.Reset(Motion.Slide, Params(), 100, 100);
        still.Release(100, 100, 3, 0);
        if (still.IsSliding) throw new Exception("tiny throw should not slide");

        return $"motion ok (jump top {minY:0}, random jumps {minPeak:0}-{maxPeak:0}px L{lefts}/R{rights}, auto pushes {ar.Pushes}, fast slide {fastBounces} bounces/{fastTime:0.0}s, slow {slowTime:0.0}s)";

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

    /// <summary>효과 계산 점검 (그리기 제외): 여백, 파티클 양, 충돌 반응, 잔상, 클릭 터뜨리기.</summary>
    public static string RunEffects()
    {
        var a = new ActiveItem();
        if (EffectState.ComputePads(a, 100, 50) != Pads.Zero) throw new Exception("pads without effects");
        a.FxSpin = true;
        var pads = EffectState.ComputePads(a, 100, 50);
        double need = (Math.Sqrt(100 * 100 + 50 * 50) - 50) / 2;
        if (pads.Top < need || pads.Left < need - 30) throw new Exception($"spin pads too small: {pads}");
        a.FxSpin = false;

        // 항상: 초당 10개 → 1초에 약 10개
        a.FxParticles = true; a.FxParticleMode = Fx.Always; a.FxParticleRate = 10; a.FxParticleLife = 2;
        var fx = new EffectState(seed: 5);
        for (int i = 0; i < 100; i++) fx.Advance(0.01, a, 100, 100, 64, 64, null);
        if (fx.ParticleCount < 8 || fx.ParticleCount > 12) throw new Exception($"always: {fx.ParticleCount} particles after 1 s at 10/s");
        int always = fx.ParticleCount;

        // 움직일 때만: 가만히 있으면 0, 움직이면 생김
        a.FxParticleMode = Fx.Moving;
        var mv = new EffectState(seed: 6);
        for (int i = 0; i < 100; i++) mv.Advance(0.01, a, 100, 100, 64, 64, null);
        if (mv.ParticleCount != 0) throw new Exception("moving-only particles appeared while still");
        for (int i = 0; i < 100; i++) mv.Advance(0.01, a, 100 + i * 3, 100, 64, 64, null); // 300 px/s
        if (mv.ParticleCount == 0) throw new Exception("moving-only particles did not appear while moving");

        // 클릭: 한꺼번에 터짐, 시간이 지나면 사라짐
        a.FxParticleMode = Fx.Click;
        var ck = new EffectState(seed: 7);
        ck.Burst(a, 100, 100, 64, 64);
        if (ck.ParticleCount < 8) throw new Exception($"click burst made {ck.ParticleCount} particles");
        for (int i = 0; i < 400; i++) ck.Advance(0.01, a, 100, 100, 64, 64, null);
        if (ck.ParticleCount != 0) throw new Exception("burst particles never faded");
        a.FxParticles = false;

        // 떨어지기 + 찌그러짐 + 먼지: 착지할 때 반응하고, 잠시 뒤 원래대로
        a.FxSquash = true; a.FxDust = true;
        var m = new MotionState();
        m.Reset(Motion.Fall, new MotionParams { Speed = 20, JumpEnabled = false }, 100, 0);
        var sq = new EffectState(seed: 8);
        for (int i = 0; i < 200; i++)
        {
            m.Step(0.01, 50, 50, 0, 0, 400, 300);
            sq.Advance(0.01, a, m.X, m.Y, 50, 50, m);
        }
        if (sq.Squashes < 1 || sq.DustPuffs < 1) throw new Exception($"landing: squashes={sq.Squashes} dust={sq.DustPuffs}");
        for (int i = 0; i < 150; i++) { m.Step(0.01, 50, 50, 0, 0, 400, 300); sq.Advance(0.01, a, m.X, m.Y, 50, 50, m); }
        if (sq.SquashActive) throw new Exception("squash never settled");

        // 잔상: 움직이는 동안 개수만큼 쌓이고 멈추면 사라짐
        a.FxTrail = true; a.FxTrailCount = 5;
        var tr = new EffectState(seed: 9);
        for (int i = 0; i < 60; i++) tr.Advance(0.01, a, 100 + i * 4, 100, 50, 50, null);
        if (tr.TrailCount != 5) throw new Exception($"trail holds {tr.TrailCount}, expected 5");
        for (int i = 0; i < 60; i++) tr.Advance(0.01, a, 340, 100, 50, 50, null);
        if (tr.TrailCount != 0) throw new Exception("trail did not clear after stopping");

        // 범위 바로잡기
        var bad = new ActiveItem { FxParticleKind = "???", FxGlowColor = "nope", FxTrailCount = 99, FxParticleColor = "#12" };
        Fx.Normalize(bad);
        if (bad.FxParticleKind != Fx.Sparkle || bad.FxGlowColor != "#FFD54F" || bad.FxTrailCount != 10 || bad.FxParticleColor != Fx.Rainbow)
            throw new Exception("effect settings were not normalized");

        return $"effects ok (spin pad {pads.Top}, {always} particles/s, landing squash+dust, trail 5)";
    }
}
