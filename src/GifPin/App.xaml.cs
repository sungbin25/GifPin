using System;
using System.Linq;
using System.Threading;
using System.Windows;
using GifPin.Core;
using GifPin.UI;

namespace GifPin;

public partial class App : Application
{
    private const string MutexName = "GifPin.SingleInstance.v1";
    private const string ShowEventName = "GifPin.ShowMainWindow.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 하나만 실행: 이미 실행 중이면 그쪽 메인 창을 열고 종료
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.InvokeAsync(() => _controller?.ShowMain());
        }) { IsBackground = true, Name = "GifPin.ShowListener" };
        listener.Start();

        bool smoke = Environment.GetEnvironmentVariable("GIFPIN_SMOKETEST") == "1";
        DispatcherUnhandledException += (_, ex) =>
        {
            if (smoke)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "smoke.err"), ex.Exception.ToString());
                Environment.Exit(2);
            }
            Log.Error("Unhandled UI exception", ex.Exception);
            MessageBox.Show(Loc.T("app.unexpectedError") + "\n\n" + ex.Exception.Message + "\n\n" + Loc.T("app.seeLog"),
                "GifPin", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception (fatal)", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        var state = Store.Load();
        Loc.SetLanguage(state.Settings.Language);
        AppController.ApplyLanguageResources();
        bool fromAutostart = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool showWindow = !fromAutostart || state.Settings.OpenWindowOnStart;

        _controller = new AppController(state);
        _controller.Start(showWindow);

        if (smoke)
        {
            // CI 점검용: 화면을 띄운 뒤 몇 초 동안 오류 없이 돌면 성공 표시를 남기고 종료
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                string report = "startup ok";
                var gif = Environment.GetEnvironmentVariable("GIFPIN_SMOKE_GIF");
                try
                {
                    if (!string.IsNullOrEmpty(gif)) report += "\n" + SmokeTest.RunPipeline(gif);
                    var assets = Environment.GetEnvironmentVariable("GIFPIN_SMOKE_ASSETS");
                    if (!string.IsNullOrEmpty(assets)) report += "\n" + SmokeTest.RunV03(assets);
                    report += "\n" + SmokeUi(_controller);
                    report += "\n" + await SmokeMouse(_controller);
                    report += $"\nactive overlays: {_controller.State.Active.Count}";
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "smoke.err"), ex.ToString());
                    Environment.Exit(3);
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "smoke.ok"), report);
                _controller.Quit();
            };
            t.Start();
        }
    }

    /// <summary>CI 점검: 모든 창을 실제로 열고 닫아 XAML·리소스 오류를 잡는다.</summary>
    private static string SmokeUi(AppController app)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var lang in new[] { Loc.Korean, Loc.English })
        {
            app.SetLanguage(lang);
            sb.Append($"lang {lang}: '{Loc.T("tab.library")}' ");
        }
        var sprite = new SpriteImportWindow(app);
        sprite.Show();
        sprite.Close();
        sb.Append("· sprite window ok ");
        var photo = app.State.Library.FirstOrDefault();
        if (photo != null)
        {
            var bg = new BgRemoveWindow(app, photo, null);
            bg.Show();
            bg.Close();
            sb.Append("· bg window ok ");
        }
        if (app.State.Active.Count > 0)
        {
            app.UpdateActive(app.State.Active[0].Id, a => a.MotionMode = Motion.Walk);
            app.EnterEdit();
            app.FinishEdit(save: true);
            app.GatherAllToCenter();
            app.ShowAll();
            sb.Append("· edit mode + recovery ok ");
        }
        sb.Append($"· monitors {app.MonitorList.Count} · log {(System.IO.File.Exists(Log.FilePath) ? "written" : "missing")}");
        if (!System.IO.File.Exists(Log.FilePath)) throw new Exception("log file was not written");
        return sb.ToString();
    }

    /// <summary>CI 점검: 마우스 동작을 흉내 내어 끌기·던지기·움직이는 GIF 잡기를 확인.</summary>
    private static async System.Threading.Tasks.Task<string> SmokeMouse(AppController app)
    {
        if (app.State.Active.Count == 0) return "mouse skipped (nothing on screen)";
        var id = app.State.Active[0].Id;
        var eng = app.Engine;
        var mon = Monitors.Primary(app.MonitorList);
        var (w, h) = app.ShownSize(app.State.Active[0]);
        async System.Threading.Tasks.Task<OverlayProbe> P() => await eng.ProbeAsync(id) ?? throw new Exception("overlay missing");
        static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();
        static System.Threading.Tasks.Task Wait(int ms) => System.Threading.Tasks.Task.Delay(ms);
        var sb = new System.Text.StringBuilder("mouse:");

        // 1) 움직임 없음: 끈 만큼 정확히 옮겨지고 저장됨
        app.UpdateActive(id, a => { a.MotionMode = Motion.None; a.ClickThrough = false; a.Visible = true; });
        int x0 = (mon.WorkLeft + mon.WorkRight) / 2 - w / 2, y0 = (mon.WorkTop + mon.WorkBottom) / 2 - h / 2;
        app.Relocate(id, x0, y0);
        await Wait(300);
        long b = Now();
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, x0 + 10, y0 + 10, b, 0);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Move, x0 + 30, y0 + 25, b, 150);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Move, x0 + 50, y0 + 40, b, 300);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, x0 + 50, y0 + 40, b, 500);
        await Wait(300);
        var p = await P();
        if (p.Item.X != x0 + 40 || p.Item.Y != y0 + 30) throw new Exception($"drag moved to {p.Item.X},{p.Item.Y}, expected {x0 + 40},{y0 + 30}");
        if (app.State.Active[0].X != x0 + 40) throw new Exception("dragged position was not saved");
        if (p.Pressed || p.MotionRunning) throw new Exception("drag did not finish cleanly");
        sb.Append(" drag ok ·");

        // 2) 미끄러짐: 빠르게 던지면 놓은 뒤에도 계속 가다가 화면 안에서 멈춤
        app.UpdateActive(id, a => a.MotionMode = Motion.Slide);
        app.Relocate(id, x0, y0);
        await Wait(300);
        b = Now();
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, x0 + 10, y0 + 10, b, 0);
        for (int i = 1; i <= 5; i++)
            eng.SimulatePointer(id, OverlayEngine.PointerAction.Move, x0 + 10 + 30 * i, y0 + 10 + 6 * i, b, 16 * i);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, x0 + 160, y0 + 40, b, 90);
        await Wait(120);
        p = await P();
        if (!p.Sliding || p.Item.X <= x0 + 150) throw new Exception($"throw did not slide (x={p.Item.X}, sliding={p.Sliding})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (p.Sliding && sw.ElapsedMilliseconds < 15000) { await Wait(250); p = await P(); }
        if (p.Sliding) throw new Exception("slide never stopped");
        if (p.Item.X < mon.WorkLeft - 1 || p.Item.X + w > mon.WorkRight + 1 || p.Item.Y < mon.WorkTop - 1 || p.Item.Y + h > mon.WorkBottom + 1)
            throw new Exception($"slide stopped off screen at {p.Item.X},{p.Item.Y}");
        await Wait(200);
        if (app.State.Active[0].X != p.Item.X) throw new Exception("slide stop position was not saved");
        sb.Append($" slide {sw.ElapsedMilliseconds / 1000.0:0.0}s ·");

        // 3) 걷는 GIF: 짧게 누르면 그대로, 오래 누르면 멈추고 잡힘, 놓으면 잠시 뒤 다시 걷기
        app.SetPointerTiming(200, 500);
        app.UpdateActive(id, a => a.MotionMode = Motion.Walk);
        await Wait(400);
        p = await P();
        if (!p.MotionRunning) throw new Exception("walk is not running");
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        await Wait(60);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        await Wait(250);
        p = await P();
        if (!p.MotionRunning || p.Held) throw new Exception("a short click stopped the walk");

        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        await Wait(120);
        p = await P();
        if (!p.MotionRunning) throw new Exception("walk stopped before the hold time");
        await Wait(250);
        p = await P();
        if (p.MotionRunning || !p.Held || !p.Pressed) throw new Exception($"hold did not catch the GIF (running={p.MotionRunning} held={p.Held})");
        int heldX = p.Item.X;
        await Wait(250);
        p = await P();
        if (p.Item.X != heldX) throw new Exception("caught GIF kept moving");
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        await Wait(200);
        p = await P();
        if (p.MotionRunning || !p.Held) throw new Exception("walk restarted before the resume delay");
        await Wait(700);
        p = await P();
        if (!p.MotionRunning || p.Held) throw new Exception("walk did not restart after the resume delay");
        sb.Append(" hold/resume ok ·");

        // 4) 중력 끔: 놓은 높이(y0)에서 수평으로 걷기
        app.UpdateActive(id, a => { a.MotionMode = Motion.Walk; a.MotionGravity = false; });
        app.Relocate(id, x0, y0);
        await Wait(600);
        p = await P();
        if (!p.MotionRunning || p.Item.Y != y0 || p.Item.X == x0) throw new Exception($"walk without gravity: at {p.Item.X},{p.Item.Y}, expected y={y0} and moving");
        sb.Append(" no-gravity walk ok ·");

        // 5) 미끄러짐 자동 밀기: 0.5초마다 랜덤 방향으로 밀리고, 멈춰 있을 때는 누르자마자 잡힘
        app.UpdateActive(id, a =>
        {
            a.MotionMode = Motion.Slide; a.MotionGravity = true; a.SlideAuto = true;
            a.PushDirRandom = true; a.PushForceRandom = true; a.PushForceMin = 400; a.PushForceMax = 800;
            a.PushIntervalRandom = false; a.PushInterval = 0.5;
        });
        app.Relocate(id, x0, y0);
        await Wait(3000);
        p = await P();
        if (p.Pushes < 2 || !p.MotionRunning) throw new Exception($"auto push: {p.Pushes} pushes, running={p.MotionRunning}");
        if (p.Item.X < mon.WorkLeft - 1 || p.Item.X + w > mon.WorkRight + 1 || p.Item.Y < mon.WorkTop - 1 || p.Item.Y + h > mon.WorkBottom + 1)
            throw new Exception($"auto push went off screen: {p.Item.X},{p.Item.Y}");
        sw.Restart();
        while (p.Sliding && sw.ElapsedMilliseconds < 5000) { await Wait(20); p = await P(); }
        if (!p.Sliding)
        {
            eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
            await Wait(60);
            var g = await P();
            eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
            if (!g.Pressed || g.MotionRunning) throw new Exception("resting auto-slide GIF was not grabbed right away");
        }
        app.UpdateActive(id, a => { a.SlideAuto = false; a.MotionMode = Motion.None; });
        sb.Append($" auto push x{p.Pushes} ·");

        // 6) 효과 레이어: 떨어지기 + 점프와 함께 여러 효과 → 타이머가 돌고, 파티클·찌그러짐이 생기고, 창에 여백이 생김
        app.SetEffectsSettings(60, pauseFullscreen: false, pauseOnBattery: false);
        app.UpdateActive(id, a =>
        {
            a.MotionMode = Motion.Fall; a.MotionGravity = true; a.JumpEnabled = true; a.JumpInterval = 0.5; a.JumpHeight = 120;
            a.JumpAngleRandom = false; a.JumpHeightRandom = false; a.JumpAngle = 0;
            a.FxFloat = true; a.FxSquash = true; a.FxDust = true; a.FxShadow = true; a.FxTrail = true;
            a.FxGlow = true; a.FxGlowPulse = true; a.FxBreathe = true; a.FxSway = true; a.FxBlink = true;
            a.FxParticles = true; a.FxParticleMode = Fx.Always; a.FxParticleRate = 20; a.FxParticleKind = Fx.Heart;
        });
        app.Relocate(id, x0, y0);
        await Wait(2500);
        p = await P();
        if (!p.FxRunning || p.Particles == 0 || p.Squashes == 0) throw new Exception($"effects: running={p.FxRunning} particles={p.Particles} squashes={p.Squashes}");
        if (p.WindowWidth <= w || p.WindowHeight <= h) throw new Exception($"effects window not padded: {p.WindowWidth}x{p.WindowHeight} for {w}x{h}");
        int fxParticles = p.Particles, fxSquashes = p.Squashes;

        // 여백이 있어도 끌기는 정확히 끈 만큼
        app.UpdateActive(id, a => { a.MotionMode = Motion.None; a.FxParticleKind = Fx.Star; });
        app.Relocate(id, x0, y0);
        await Wait(300);
        b = Now();
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, x0 + 10, y0 + 10, b, 0);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Move, x0 + 50, y0 + 40, b, 300);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, x0 + 50, y0 + 40, b, 500);
        await Wait(300);
        p = await P();
        if (p.Item.X != x0 + 40 || p.Item.Y != y0 + 30) throw new Exception($"drag with effects moved to {p.Item.X},{p.Item.Y}, expected {x0 + 40},{y0 + 30}");

        // 클릭할 때 터지는 파티클
        app.UpdateActive(id, a => { a.FxParticleMode = Fx.Click; a.FxFloat = false; a.FxBreathe = false; a.FxSway = false; a.FxBlink = false; a.FxGlowPulse = false; });
        await Wait(2200); // 이전 파티클이 사라질 때까지
        int before = (await P()).Particles;
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Down, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        eng.SimulatePointer(id, OverlayEngine.PointerAction.Up, p.Item.X + 5, p.Item.Y + 5, Now(), 0);
        await Wait(100);
        p = await P();
        if (p.Particles < before + 8 || !p.FxRunning) throw new Exception($"click burst: {before} -> {p.Particles} particles");

        // 모두 끄면 여백·타이머가 사라짐
        app.UpdateActive(id, a =>
        {
            a.FxFloat = a.FxBreathe = a.FxSway = a.FxSpin = a.FxBlink = a.FxSquash = a.FxDust = a.FxTrail = a.FxShadow = a.FxGlow = a.FxParticles = false;
        });
        await Wait(2500);
        p = await P();
        if (p.FxRunning || p.WindowWidth != w || p.WindowHeight != h) throw new Exception($"effects off: running={p.FxRunning} window {p.WindowWidth}x{p.WindowHeight} vs {w}x{h}");
        sb.Append($" effects ok ({fxParticles} particles, {fxSquashes} squashes, burst) ·");

        // 7) 표시 층: 바탕화면이면 항상 위(topmost)가 아니고, 맨 위로 되돌리면 다시 topmost
        app.UpdateActive(id, a => { a.MotionMode = Motion.None; a.MotionGravity = true; a.Layer = Layers.Desktop; });
        await Wait(400);
        p = await P();
        if (p.Topmost) throw new Exception("desktop layer is still topmost");
        app.UpdateActive(id, a => a.Layer = Layers.Top);
        await Wait(400);
        p = await P();
        if (!p.Topmost) throw new Exception("top layer is not topmost");
        sb.Append(" layers ok");

        app.UpdateActive(id, a => { a.MotionMode = Motion.None; a.ClickThrough = true; });
        return sb.ToString();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
