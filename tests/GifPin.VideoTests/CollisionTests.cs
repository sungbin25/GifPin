using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GifPin.Core;
using GifPin.UI;

internal static class CollisionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("Collision: " + message); }
    private static CollisionBody Body(string mode, double x, double vx = 0, double previous = double.NaN) =>
        new() { Mode = mode, X = x, PreviousX = double.IsNaN(previous) ? x : previous, Width = 50, Height = 50, Vx = vx };

    public static void Run()
    {
        BounceRecovery();
        var walker = Body(Motion.Walk, 60, 120, 0); var wall = Body(Motion.None, 80);
        var hits = Collisions.Resolve(new[] { walker, wall });
        Check(hits.Count == 1 && walker.Vx == -120 && walker.X + 50 <= wall.X, "walker must turn at contact");
        var a = Body(Motion.Slide, 60, 100, 0); var b = Body(Motion.Slide, 80);
        hits = Collisions.Resolve(new[] { a, b });
        Check(hits.Count == 1 && Math.Abs(a.Vx - 5) < .001 && Math.Abs(b.Vx - 95) < .001, "momentum transfer to resting slide");
        Check(a.X + 50 <= b.X, "separate bodies");
        a = Body(Motion.Slide, 20, 100); b = Body(Motion.Slide, 60, -100);
        a.Vy = b.Vy = 20;
        Collisions.Resolve(new[] { a, b });
        Check(a.Vx < 0 && b.Vx > 0 && a.Vy == 20 && b.Vy == 20, "head-on bounce preserves tangential velocity");
        Check(Math.Abs(a.Vx + b.Vx) < .001 && a.Vx * a.Vx + b.Vx * b.Vx <= 20000, "momentum / energy");
        a = Body(Motion.Slide, 600, 6000, 0); b = Body(Motion.None, 100); b.Width = 2;
        hits = Collisions.Resolve(new[] { a, b });
        Check(hits.Count == 1 && a.Vx < 0 && a.X < 51, "fast throw cannot tunnel through thin image");
        a = Body(Motion.Slide, 10, -100); b = Body(Motion.Slide, 50, 100);
        Check(Collisions.Resolve(new[] { a, b }).Count == 0 && a.Vx == -100, "separating overlap must not bounce again");
        var crowd = new[] { Body(Motion.Slide, 0, 200), Body(Motion.Slide, 40), Body(Motion.None, 80) };
        for (int i = 0; i < 100; i++) Collisions.Resolve(crowd);
        Check(double.IsFinite(crowd[0].X) && crowd[0].X + 50 <= crowd[1].X + .1 && crowd[1].X + 50 <= 80.1, "crowded contacts settle");
        var motion = new MotionState(); motion.Reset(Motion.Slide, new MotionParams(), 0, 0);
        motion.Collide(10, 0, -300, 0);
        Check(motion.IsSliding && motion.FacingLeft, "wake and facing");
        var old = JsonSerializer.Deserialize<ActiveItem>("{}")!; old.Normalize();
        Check(!old.CollisionEnabled, "existing layouts opt out");
        old.CollisionEnabled = true; old.FxCollisionStrong = new CollisionEffect { Count = 31, Kind = Fx.Heart, Color = "#123456" };
        var copy = old.Clone(); copy.FxCollisionStrong = copy.FxCollisionStrong with { Count = 4 };
        Check(old.FxCollisionStrong.Count == 31, "clones isolate profile edits");
        var roundtrip = JsonSerializer.Deserialize<ActiveItem>(JsonSerializer.Serialize(old))!;
        Check(roundtrip.FxCollisionStrong == old.FxCollisionStrong, "settings roundtrip");
        old.CollisionSize = double.NaN; old.FxCollisionWeak = null!; old.Normalize();
        Check(old.CollisionSize == 100 && old.FxCollisionWeak != null, "invalid settings normalized");
        using var fx = new EffectState(seed: 1);
        var settings = new ActiveItem { CollisionEnabled = true };
        fx.CollisionBurst(settings, 100, 50, 25, 0, 0);
        Check(fx.ParticleCount == 8 && fx.IsAnimated(settings, false), "weak burst without continuous particles");
        fx.ClearTransient(); fx.CollisionBurst(settings, 500, 50, 25, 0, 0);
        Check(fx.ParticleCount == 24 && EffectState.ComputePads(settings, 50, 50).Left > 20, "strong burst and padding");
        for (int i = 0; i < 40; i++) fx.Advance(.1, settings, 0, 0, 50, 50, null);
        Check(fx.ParticleCount == 0 && !fx.IsAnimated(settings, false), "burst expires and clock stops");
        fx.ParticleBudget = 3; fx.CollisionBurst(settings, 500, 0, 0, 0, 0);
        Check(fx.ParticleCount == 3, "global particle budget");
        fx.ClearTransient(); settings.FxCollisionEnabled = false; fx.CollisionBurst(settings, 500, 0, 0, 0, 0);
        Check(fx.ParticleCount == 0, "effect off");
        settings.FxCollisionEnabled = true; settings.FxCollisionWeak = settings.FxCollisionWeak with { Enabled = false };
        fx.CollisionBurst(settings, 100, 0, 0, 0, 0); Check(fx.ParticleCount == 0, "individual profile off");
        settings.FxCollisionStrong = settings.FxCollisionStrong with { Kind = Fx.Image }; settings.FxParticleImages = "custom";
        Check(Fx.UsesParticleImages(settings), "collision images loaded without regular particles");
        Console.WriteLine("PASS: collision solver, high-speed sweep, persistence, effect strength and budgets");
    }

    private static void BounceRecovery()
    {
        static double Speed(MotionState m) => Math.Sqrt(m.Vx * m.Vx + m.Vy * m.Vy);
        static void Tick(MotionState m, int frames, double dt = .1)
        {
            for (int i = 0; i < frames; i++) m.Step(dt, 50, 50, -1000000, -1000000, 1000000, 1000000);
        }
        var m = new MotionState(); m.Reset(Motion.Bounce, new MotionParams { Speed = 120 }, 0, 0);
        double target = Speed(m);
        Tick(m, 100); Check(Math.Abs(Speed(m) - target) < 1e-9, "normal bounce unchanged");
        m.Collide(0, 0, -600, 800);
        Tick(m, 30); Check(Math.Abs(Speed(m) - 1000) < 1e-8, "hold accelerated speed for three seconds");
        Tick(m, 1); Check(Speed(m) < 1000 && Speed(m) > target && Math.Abs(m.Vx / m.Vy + .75) < 1e-9, "gradual recovery preserves direction");
        Tick(m, 80); Check(Math.Abs(Speed(m) - target) < .1, "accelerated bounce returns to configured speed");
        m.Collide(0, 0, 6, -8); Tick(m, 30);
        Check(Math.Abs(Speed(m) - 10) < 1e-8, "hold slowed speed");
        Tick(m, 1); Check(Speed(m) > 10 && Speed(m) < target, "slowed bounce accelerates gradually");
        m.Collide(0, 0, 30, 40); Tick(m, 25);
        m.Collide(0, 0, -30, 40); Tick(m, 25);
        Check(Math.Abs(Speed(m) - 50) < 1e-8, "second image contact restarts quiet interval");
        // A slow wall contact must count even though it is below the particle/impact threshold.
        m.X = 0; m.Y = 100; m.Vx = -10; m.Vy = 0;
        m.Step(.1, 50, 50, 0, 0, 1000, 1000); Tick(m, 25);
        Check(Math.Abs(Speed(m) - 10) < 1e-8 && m.Vx > 0, "slow wall contact restarts delay and preserves reflected direction");
        m.Collide(0, 0, 0, 0); Tick(m, 30);
        Check(Speed(m) < 1e-8, "stopped bounce waits before recovering");
        Tick(m, 1); Check(Speed(m) > 0 && m.Vx > 0, "zero speed safely recovers using last direction");
        var fine = new MotionState(); fine.Reset(Motion.Bounce, new MotionParams { Speed = 120 }, 0, 0);
        var coarse = new MotionState(); coarse.Reset(Motion.Bounce, new MotionParams { Speed = 120 }, 0, 0);
        fine.Collide(0, 0, 600, 800); coarse.Collide(0, 0, 600, 800);
        Tick(fine, 300, 1.0 / 60); Tick(coarse, 50);
        Check(Math.Abs(Speed(fine) - Speed(coarse)) < 1e-7, "recovery independent of frame rate");
        m.Reset(Motion.Slide, new MotionParams(), 0, 0); m.Collide(0, 0, 100, 0); Tick(m, 60);
        Check(Speed(m) == 0, "slide still stops, without bounce recovery");
        Console.WriteLine("PASS: bounce speed hold, smooth recovery, repeated/slow-wall contacts, zero speed and frame timing");
    }

    public static async Task Engine(string gif)
    {
        using var engine = new OverlayEngine(); engine.Start(); engine.ForceSystemPause(true); engine.ForceLightMode(false);
        engine.SetEffectsSettings(60, false, false, false);
        engine.SetPerformance(400, 30, "medium", 256, false, false, false);
        var a = new ActiveItem { Id = "collision-test-a", X = 200, Y = 220, MotionMode = Motion.Walk, MotionSpeed = 300, MotionGravity = false, CollisionEnabled = true };
        var b = new ActiveItem { Id = "collision-test-b", X = 400, Y = 220, CollisionEnabled = true };
        engine.Sync(a, gif); engine.Sync(b, gif);
        async Task<OverlayProbe> Wait(string id, Func<OverlayProbe, bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 5000)
            {
                var p = await engine.ProbeAsync(id);
                if (p != null && predicate(p)) return p;
                await Task.Delay(15);
            }
            throw new Exception("Collision engine timeout: " + id);
        }
        await Wait(a.Id, p => p.AnimWidth > 0); await Wait(b.Id, p => p.AnimWidth > 0);
        engine.ForceSystemPause(false);
        var hit = await Wait(a.Id, p => p.ImageCollisions > 0);
        Check(hit.VelocityX < 0 && hit.Particles > 0, $"real overlay turns and emits particles: vx={hit.VelocityX}, particles={hit.Particles}, light={hit.LightMode}, paused={hit.FxPaused}, active={hit.Shown}, fx={hit.FxRunning}");
        engine.ForceSystemPause(true); await engine.ProbeAsync(a.Id);
        b.CollisionEnabled = false; engine.Sync(b, gif); engine.Relocate(a.Id, 200, 220); engine.ForceSystemPause(false);
        var crossed = await Wait(a.Id, p => p.Item.X > 500);
        Check(crossed.ImageCollisions == hit.ImageCollisions && crossed.VelocityX > 0, "disabled image excluded");
        engine.ForceSystemPause(true); await engine.ProbeAsync(a.Id);
        a.MotionMode = Motion.Slide; a.SlideAuto = true; a.PushIntervalRandom = false; a.PushInterval = .5;
        a.PushForceRandom = false; a.PushForce = 1200; a.PushDirRandom = false; a.PushAngle = 0;
        a.FxCollisionEnabled = false; b.FxCollisionEnabled = false;
        b.CollisionEnabled = true; b.MotionMode = Motion.Slide;
        engine.Sync(a, gif); engine.Sync(b, gif); engine.Relocate(a.Id, 200, 220); engine.Relocate(b.Id, 400, 220);
        engine.ForceSystemPause(false);
        var sliding = await Wait(b.Id, p => p.Sliding && p.VelocityX > 100);
        Check(sliding.MotionRunning && sliding.Particles == 0, "resting overlay wakes, effects can be disabled");
        engine.Remove(a.Id); engine.Remove(b.Id);
        await engine.ProbeAsync(a.Id);
        Console.WriteLine("PASS: real overlay walk/slide collision, wake-up, particles and per-image opt-out");
    }

    public static void Ui(AppController app, string output)
    {
        var item = new ActiveItem { Id = "collision-ui", CollisionEnabled = true }; app.State.Active.Add(item);
        var window = new CollisionSettingsWindow(app, item.Id) { ShowActivated = false, ShowInTaskbar = false, Left = -30000, Top = -30000, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        window.Show(); window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "collision-window.png"))) png.Save(file);
        window.Close(); app.State.Active.Remove(item);
    }
}
