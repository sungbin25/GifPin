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
    private Action<bool>? _startNow;

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
                Dispatcher.InvokeAsync(() =>
                {
                    if (_controller == null) _startNow?.Invoke(true); // 자동 실행 대기 중에 직접 실행함 → 바로 시작
                    else _controller.ShowMain();
                });
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
        // 설치 프로그램에서 고른 언어 (새로 설치했을 때 한 번)
        if (InstallerInfo.PendingLanguage(state.Settings.AppliedInstallStamp) is { } fromInstaller)
        {
            state.Settings.Language = fromInstaller.language;
            state.Settings.AppliedInstallStamp = fromInstaller.stamp;
            Log.Info($"Language from installer: {fromInstaller.language}");
            Store.SaveNow(state);
        }
        Loc.SetLanguage(state.Settings.Language);
        AppController.ApplyLanguageResources();
        Themes.Apply(state.Settings.Theme);
        bool fromAutostart = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool showWindow = !fromAutostart || state.Settings.OpenWindowOnStart;

        _startNow = show =>
        {
            if (_controller != null) return;
            _startNow = null;
            _controller = new AppController(state);
            _controller.Start(show);
        };

        // Windows 시작 시 자동 실행: 설정한 만큼 기다렸다가 시작 (로그인 직후 다른 프로그램과 겹치지 않게)
        int delay = fromAutostart && !smoke ? Math.Clamp(state.Settings.AutostartDelaySec, 0, 300) : 0;
        if (delay > 0)
        {
            Log.Info($"Started with Windows: waiting {delay} s before showing GIFs");
            var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            wait.Tick += (_, _) => { wait.Stop(); _startNow?.Invoke(showWindow); };
            wait.Start();
            return;
        }
        _startNow(showWindow);
        if (_controller == null) return;

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
                    report += "\n" + await SmokeConvenience(_controller);
                    report += "\n" + await SmokePerformance(_controller);
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
            var picker = new ParticleImagePicker(app, app.State.Active[0].Id);
            picker.Show();
            picker.Close();
            sb.Append("· particle picker ok ");
            app.UpdateActive(app.State.Active[0].Id, a => a.MotionMode = Motion.Walk);
            app.EnterEdit();
            app.FinishEdit(save: true);
            app.GatherAllToCenter();
            app.ShowAll();
            sb.Append("· edit mode + recovery ok ");
        }
        // Windows 시작 시 자동 실행: 켜기 → Windows 설정에서 꺼진 것으로 표시 → 다시 켜면 허용 → 끄기 (원래 상태로 되돌림)
        bool hadAutostart = Autostart.IsEnabled();
        app.SetStartWithWindows(true);
        if (Autostart.GetStatus() != Autostart.Status.On) throw new Exception("autostart did not turn on");
        Autostart.MarkBlockedForTest();
        if (Autostart.GetStatus() != Autostart.Status.BlockedByWindows) throw new Exception("blocked autostart not detected");
        app.SetStartWithWindows(true);
        if (Autostart.GetStatus() != Autostart.Status.On) throw new Exception("autostart was not re-allowed");
        app.SetStartWithWindows(false);
        if (Autostart.GetStatus() != Autostart.Status.Off) throw new Exception("autostart did not turn off");
        if (hadAutostart) app.SetStartWithWindows(true);
        sb.Append("· autostart ok ");
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

        // 3-1) 다시 실행한 경우: 저장된 "걷기"로 GIF 창을 새로 만들면 바로 걸어야 함 (예전엔 모드를 한 번 바꿔야 움직였음)
        eng.Remove(id);
        await Wait(200);
        app.UpdateActive(id, a => { }); // 저장된 설정 그대로 창을 다시 만듦 (앱 시작과 같은 경로)
        await Wait(1200);
        p = await P();
        int restartX = p.Item.X;
        await Wait(500);
        p = await P();
        if (!p.MotionRunning || p.Item.X == restartX) throw new Exception($"walk did not start after re-creating the overlay (running={p.MotionRunning}, x {restartX} -> {p.Item.X})");
        sb.Append(" walk after restart ok ·");

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
        app.SetEffectsSettings(60, pauseFullscreen: false, pauseOnBattery: false, autoLight: false, cpuPercent: 80);
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
        int fxParticles = p.Particles, fxSquashes = p.Squashes, fxFps = p.FramesPerSecond;
        // 공용 프레임 박자: 60fps 설정이면 실제로 고르게 45fps 이상 (예전 창별 타이머는 약 32fps)
        if (fxFps < 45) throw new Exception($"effects ran at {fxFps} fps ({eng.FramePacing}), expected about 60");

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

        // 내 이미지(GIF) 파티클: 라이브러리 사진을 파티클로 불러와 움직이며 그림
        string photoId = app.State.Active[0].PhotoId;
        app.UpdateActive(id, a =>
        {
            a.FxParticleMode = Fx.Always; a.FxParticleKind = Fx.Image; a.FxParticleImages = photoId;
            a.FxParticleAnimate = true; a.FxParticleRate = 15; a.FxTrailSpacing = 30;
        });
        await Wait(1500);
        p = await P();
        if (p.Sprites != 1 || p.Particles == 0) throw new Exception($"image particles on screen: sprites={p.Sprites} particles={p.Particles}");
        app.UpdateActive(id, a => { a.FxParticleKind = Fx.Sparkle; a.FxParticleImages = ""; });
        await Wait(300);
        p = await P();
        if (p.Sprites != 0) throw new Exception("particle images were not released");

        // 절약 모드: 켜지면 새 파티클이 멈추고 날던 것은 사라짐, 끄면 다시 나옴
        app.UpdateActive(id, a => { a.FxParticleKind = "heart,star"; a.FxParticleMode = Fx.Always; a.FxParticleRate = 20; a.FxParticleLife = 1; });
        await Wait(800);
        if ((await P()).Particles == 0) throw new Exception("mixed-shape particles did not appear");
        eng.ForceLightMode(true);
        await Wait(1800);
        p = await P();
        if (!p.LightMode || p.Particles != 0) throw new Exception($"light mode: on={p.LightMode} particles={p.Particles}");
        eng.ForceLightMode(null);
        await Wait(800);
        p = await P();
        if (p.LightMode || p.Particles == 0) throw new Exception($"after light mode: on={p.LightMode} particles={p.Particles}");

        // 모두 끄면 여백·타이머가 사라짐
        app.UpdateActive(id, a =>
        {
            a.FxFloat = a.FxBreathe = a.FxSway = a.FxSpin = a.FxBlink = a.FxSquash = a.FxDust = a.FxTrail = a.FxShadow = a.FxGlow = a.FxParticles = false;
        });
        await Wait(2500);
        p = await P();
        if (p.FxRunning || p.WindowWidth != w || p.WindowHeight != h) throw new Exception($"effects off: running={p.FxRunning} window {p.WindowWidth}x{p.WindowHeight} vs {w}x{h}");
        sb.Append($" effects ok ({fxParticles} particles, {fxSquashes} squashes, {fxFps} fps via {eng.FramePacing}, burst) ·");

        // 7) 성능: 화면 크기로 디코딩, 같은 GIF 공유, 정확한 재생 속도, 슬라이더 중 재생 유지, 빛 캐시, 바뀐 부분만 보내기
        app.UpdateActive(id, a => { a.Scale = 1.5; a.MotionMode = Motion.None; a.Speed = 1; });
        var src = app.State.FindPhoto(app.State.Active[0].PhotoId)!;
        // 큰 GIF(384px)를 만들어 작게 띄우면 작게 디코딩, 키우면 원본 크기로 다시 디코딩
        string bigPath = System.IO.Path.Combine(Store.LibraryDir, "smoke-big.webp");
        System.IO.Directory.CreateDirectory(Store.LibraryDir);
        using (var sample = GifAnimation.Load(Store.ResolvePath(src)))
        {
            var info = new SkiaSharp.SKImageInfo(384, 384, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            var bigFrames = sample.Frames.Select(f => f.Resize(info, SkiaSharp.SKFilterQuality.Medium)).ToList();
            try { SpriteBuilder.Save(bigFrames, 10, bigPath); }
            finally { foreach (var f in bigFrames) f.Dispose(); }
        }
        var bigPhoto = Library.RegisterGenerated(bigPath, "smoke big") ?? throw new Exception("big test GIF not registered");
        app.AddGeneratedPhoto(bigPhoto);
        app.AddToScreen(bigPhoto.Id);
        string bigId = app.State.Active[^1].Id;
        app.UpdateActive(bigId, a => a.Scale = 0.25);
        await Wait(1500);
        var bp = await eng.ProbeAsync(bigId) ?? throw new Exception("big overlay missing");
        if (bp.AnimWidth >= 384 || bp.AnimWidth < 96) throw new Exception($"quarter-size 384px GIF was decoded at {bp.AnimWidth}px");
        int halfWidth = bp.AnimWidth;
        app.UpdateActive(bigId, a => a.Scale = 1);
        await Wait(1500);
        bp = await eng.ProbeAsync(bigId) ?? throw new Exception("big overlay missing");
        if (bp.AnimWidth != 384) throw new Exception($"enlarged GIF stayed at {bp.AnimWidth}px");
        app.RemoveActive(bigId);
        app.SelectActive(id, flash: false);

        // 같은 사진을 하나 더 띄우면 디코딩은 하나를 함께 씀
        var before2 = await eng.AnimStatsAsync();
        app.AddToScreen(src.Id);
        var second = app.State.Active[^1];
        app.UpdateActive(second.Id, a => a.Scale = 1.5);
        await Wait(1200);
        var shared = await eng.AnimStatsAsync();
        app.RemoveActive(second.Id);
        if (shared.count != before2.count) throw new Exception($"same GIF decoded twice ({before2.count} -> {shared.count})");

        // 재생 속도: 장면 간격대로 (샘플은 90ms → 2초에 약 22장)
        long g0 = (await P()).GifFrames;
        await Wait(2000);
        long g1 = (await P()).GifFrames;
        int perSecond = (int)((g1 - g0) / 2);
        if (g1 - g0 < 18 || g1 - g0 > 26) throw new Exception($"GIF advanced {g1 - g0} frames in 2 s (expected about 22)");

        // 슬라이더를 끄는 동안(설정이 계속 바뀜)에도 멈추지 않음
        long s0 = (await P()).GifFrames;
        for (int i = 0; i < 20; i++) { app.UpdateActive(id, a => a.Opacity = 0.5 + i * 0.02); await Wait(50); }
        long s1 = (await P()).GifFrames;
        app.UpdateActive(id, a => a.Opacity = 1);
        if (s1 - s0 < 8) throw new Exception($"GIF stalled while settings changed ({s1 - s0} frames in 1 s)");

        // 외곽 빛은 장면별로 한 번만 만들고 재사용, 효과 창은 바뀐 부분만 화면에 보냄
        app.UpdateActive(id, a =>
        {
            a.FxGlow = true; a.FxGlowPulse = true; a.FxTrail = true; a.FxTrailCount = 10; a.FxTrailSpacing = 40;
            a.FxParticles = true; a.FxParticleMode = Fx.Always; a.FxParticleRate = 2; a.FxParticleKind = Fx.Dot;
        });
        await Wait(2500);
        p = await P();
        if (p.GlowHits < p.GlowBuilds * 3 || p.GlowBuilds > 12) throw new Exception($"glow cache: {p.GlowBuilds} builds, {p.GlowHits} hits");
        long fullArea = (long)p.WindowWidth * p.WindowHeight;
        if (p.PushedArea <= 0 || p.PushedArea > fullArea * 0.6) throw new Exception($"pushed {p.PushedArea} of {fullArea} pixels");
        app.UpdateActive(id, a => { a.FxGlow = false; a.FxTrail = false; a.FxParticles = false; });
        sb.Append($" perf ok (quarter-size decode {halfWidth}px, shared, {perSecond} frames/s, glow {p.GlowBuilds}/{p.GlowHits}, pushed {p.PushedArea * 100 / Math.Max(1, fullArea)}%) ·");

        // 8) 표시 층: 바탕화면이면 항상 위(topmost)가 아니고, 맨 위로 되돌리면 다시 topmost
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

    /// <summary>CI 점검 (v0.5): 녹화 숨기기, 마우스 흐리게, 모든 효과 끄기, 장면, 단축키.</summary>
    private static async System.Threading.Tasks.Task<string> SmokeConvenience(AppController app)
    {
        if (app.State.Active.Count == 0) return "convenience skipped (nothing on screen)";
        var eng = app.Engine;
        var id = app.State.Active[0].Id;
        async System.Threading.Tasks.Task<OverlayProbe> P(string which) => await eng.ProbeAsync(which) ?? throw new Exception("overlay missing");
        static System.Threading.Tasks.Task Wait(int ms) => System.Threading.Tasks.Task.Delay(ms);
        var sb = new System.Text.StringBuilder("v0.5:");
        TextPrompt.SmokeOpenClose();

        // 1) 화면 녹화·공유에서 숨기기
        app.SetHideFromCapture(true);
        await Wait(200);
        if (!(await P(id)).CaptureExcluded) throw new Exception("overlay was not excluded from capture");
        app.SetHideFromCapture(false);
        await Wait(200);
        if ((await P(id)).CaptureExcluded) throw new Exception("capture exclusion was not removed");
        sb.Append(" capture ok ·");

        // 2) 마우스를 올리면 흐리게: 클릭 통과 GIF 위면 20%로, 벗어나면 다시 100%
        app.UpdateActive(id, a => { a.ClickThrough = true; a.Visible = true; a.MotionMode = Motion.None; });
        var item = app.State.Active[0];
        var (w, h) = app.ShownSize(item);
        app.SetHoverFade(true, 20);
        eng.SimulateHover(item.X + w / 2, item.Y + h / 2);
        await Wait(500);
        var p = await P(id);
        if (Math.Abs(p.FadeOpacity - 0.2) > 0.01) throw new Exception($"hover fade: opacity {p.FadeOpacity:0.00}, expected 0.20");
        eng.SimulateHover(item.X - 500, item.Y - 500);
        await Wait(500);
        p = await P(id);
        if (p.FadeOpacity < 0.99) throw new Exception($"fade did not come back: {p.FadeOpacity:0.00}");
        app.SetHoverFade(false, 20);
        eng.SimulateHover(null, null);
        sb.Append(" hover fade ok ·");

        // 3) 효과 끄기: 날던 파티클·잔상은 바로 사라지고 새로 안 생김, 둥실둥실(대기 모션)은 계속, 다시 켜면 파티클이 나옴
        app.UpdateActive(id, a => { a.FxFloat = true; a.FxTrail = true; a.FxShadow = true; a.FxParticles = true; a.FxParticleMode = Fx.Always; a.FxParticleRate = 20; a.FxParticleKind = Fx.Dot; });
        await Wait(800);
        var fxOn = await P(id);
        if (fxOn.Particles == 0) throw new Exception("particles did not start before effects-off");
        app.SetEffectsOff(true);
        await Wait(250);
        var fxOff = await P(id);
        if (fxOff.Particles != 0) throw new Exception($"effects-off left {fxOff.Particles} particles on screen");
        if (!fxOff.FxRunning || fxOff.FxPaused) throw new Exception("effects-off stopped the idle motion (float)");
        if (fxOff.WindowHeight >= fxOn.WindowHeight && fxOn.WindowHeight > h + 40) throw new Exception("effects-off kept the large effect padding");
        await Wait(600);
        if ((await P(id)).Particles != 0) throw new Exception("particles were created while effects were off");
        app.SetEffectsOff(false);
        await Wait(800);
        if ((await P(id)).Particles == 0) throw new Exception("particles did not come back after effects-on");
        app.UpdateActive(id, a => { a.FxFloat = a.FxTrail = a.FxShadow = a.FxParticles = false; });
        sb.Append(" effects toggle ok ·");

        // 4) 장면: A(지금) / B(하나 더) 저장 → A 적용 → 다음 장면 = B → 매일 시각에 자동 전환
        int countA = app.State.Active.Count;
        var sceneA = app.SaveScene("smoke A");
        app.AddToScreen(app.State.Active[0].PhotoId);
        int countB = app.State.Active.Count;
        var sceneB = app.SaveScene("smoke B");
        app.ApplyScene(sceneA.Id);
        await Wait(300);
        if (app.State.Active.Count != countA || app.State.CurrentSceneId != sceneA.Id) throw new Exception($"scene A: {app.State.Active.Count} GIFs, expected {countA}");
        foreach (var x in app.State.Active) if (await eng.ProbeAsync(x.Id) == null) throw new Exception("scene GIF has no overlay");
        app.ApplyNextScene();
        await Wait(300);
        if (app.State.Active.Count != countB || app.State.CurrentSceneId != sceneB.Id) throw new Exception($"next scene: {app.State.Active.Count} GIFs, expected {countB}");
        if (!app.SetSceneTime(sceneA.Id, "7:5") || sceneA.AutoAt != "07:05") throw new Exception($"scene time not normalized: {sceneA.AutoAt}");
        if (app.SetSceneTime(sceneA.Id, "25:00")) throw new Exception("invalid scene time accepted");
        var today = DateTime.Today;
        app.SetSceneClockForTest(today.AddHours(7).AddMinutes(4));
        app.CheckSceneSchedule(today.AddHours(7).AddMinutes(6));
        if (app.State.CurrentSceneId != sceneA.Id) throw new Exception("scheduled scene was not applied");
        // 숫자를 돌리는 시계: 시를 3칸, 분을 -5칸(0에서 거꾸로) 돌리면 그 시각으로 저장, 목록은 다시 만들지 않음
        var wheel = new GifPin.UI.TimeWheel(23, 2);
        wheel.TimeCommitted += (hh, mm) => app.SetSceneTime(sceneA.Id, $"{hh:00}:{mm:00}", quiet: true);
        wheel.Spin(hour: true, 3);
        wheel.Spin(hour: false, -5);
        wheel.CommitNow();
        if (sceneA.AutoAt != "02:57") throw new Exception($"time wheel: {sceneA.AutoAt}, expected 02:57");
        app.SetSceneTime(sceneA.Id, null, quiet: true);
        if (sceneA.AutoAt != null) throw new Exception("scene time 'none' was not saved");
        // 새 GIF 기본값을 화면의 특정 GIF에서 가져오기
        app.UpdateActive(app.State.Active[^1].Id, x => x.MotionMode = Motion.Bounce);
        if (!app.TemplateFromActive(app.State.Active[^1].Id) || app.State.Settings.NewItemTemplate?.MotionMode != Motion.Bounce) throw new Exception("template from GIF");
        app.ClearTemplate();
        if (app.CurrentScene?.Id != sceneA.Id) throw new Exception("current scene");
        app.DeleteScene(sceneB.Id);
        app.DeleteScene(sceneA.Id);
        id = app.State.Active[0].Id;
        sb.Append($" scenes ok ({countA}/{countB}) ·");

        // 5) 단축키 지정: 새 조합 등록, 겹치면 거절, 기본값 복원
        if (!Hotkeys.TryParse("Ctrl+Shift+F9", out uint m, out uint vk) || Hotkeys.Format(m, vk) != "Ctrl+Shift+F9") throw new Exception("hotkey parse/format");
        if (Hotkeys.TryParse("A", out _, out _) || Hotkeys.TryParse("Shift+A", out _, out _)) throw new Exception("hotkey without modifier accepted");
        if (!app.SetHotkey(Hotkeys.NextScene, "Ctrl+Alt+F9")) throw new Exception("hotkey not set");
        if (app.SetHotkey(Hotkeys.Effects, "ctrl+alt+f9")) throw new Exception("duplicate hotkey accepted");
        if (app.HotkeyText(Hotkeys.NextScene) != "Ctrl+Alt+F9") throw new Exception("hotkey text");
        app.ResetHotkeys();
        if (app.HotkeyText(Hotkeys.NextScene) != "" || app.HotkeyText(Hotkeys.Edit) != "Ctrl+Alt+E") throw new Exception("hotkeys not reset");
        sb.Append($" hotkeys ok (failures: {eng.HotkeyFailures.Count}) ·");

        // 6) 새 GIF 기본값: 크기 제한 + 층·클릭 통과·효과가 새 GIF에 적용됨
        var big = app.State.Library.FirstOrDefault(ph => Math.Max(ph.Width, ph.Height) >= 300) ?? throw new Exception("no large photo for new-GIF test");
        app.SetNewItemSize(120);
        app.ChangeTemplate(t => { t.Layer = Layers.Desktop; t.ClickThrough = false; t.FxFloat = true; });
        app.AddToScreen(big.Id);
        var added = app.State.Active[^1];
        var (aw, ah) = app.ShownSize(added);
        if (Math.Max(aw, ah) > 121 || added.Layer != Layers.Desktop || added.ClickThrough || !added.FxFloat)
            throw new Exception($"new GIF defaults: {aw}x{ah} layer={added.Layer} click-through={added.ClickThrough} float={added.FxFloat}");
        app.ClearTemplate();
        app.SetNewItemSize(400);
        app.AddToScreen(big.Id);
        var plain = app.State.Active[^1];
        if (plain.Layer != Layers.Top || !plain.ClickThrough || plain.FxFloat) throw new Exception("template was not cleared");
        app.RemoveActive(plain.Id);
        sb.Append(" new-GIF defaults ok ·");

        // 7) 설정 복사·붙여넣기 (효과만), 프리셋 적용·저장
        app.UpdateActive(id, x => { x.FxGlow = true; x.FxGlowColor = "#00FF00"; x.MotionMode = Motion.None; });
        app.CopyStyle(id);
        int keepX = added.X;
        app.PasteStyle(added.Id, effectsOnly: true);
        added = app.State.Active.First(x => x.Id == added.Id);
        if (!added.FxGlow || added.FxGlowColor != "#00FF00" || added.Layer != Layers.Desktop || added.X != keepX) throw new Exception("effects-only paste");
        app.ApplyPreset(added.Id, "builtin:love");
        added = app.State.Active.First(x => x.Id == added.Id);
        if (!added.FxParticles || added.FxParticleKind != Fx.Heart || added.FxGlow) throw new Exception("built-in preset was not applied");
        var preset = app.SavePreset(added.Id, "smoke preset") ?? throw new Exception("preset not saved");
        app.UpdateActive(id, x => x.FxGlow = false);
        app.ApplyPreset(id, preset.Id);
        if (!app.State.Active.First(x => x.Id == id).FxParticles) throw new Exception("saved preset was not applied");
        app.DeletePreset(preset.Id);
        app.UpdateActive(id, x => { x.FxParticles = false; x.FxBreathe = false; });
        app.RemoveActive(added.Id);
        sb.Append(" copy/presets ok ·");

        // 8) 프로그램별 숨기기: "실행 중" 규칙에 GifPin 자신을 넣으면 숨고, 빼면 다시 보임
        app.SetAppRuleMode(true);
        app.AddAppRule(System.IO.Path.GetFileName(Environment.ProcessPath ?? "GifPin.exe"));
        await Wait(600);
        p = await P(id);
        if (eng.RuleHiddenBy == null || p.Shown) throw new Exception($"program rule did not hide GIFs (by={eng.RuleHiddenBy}, shown={p.Shown})");
        app.RemoveAppRule(app.State.Settings.HideForApps[0]);
        await Wait(600);
        p = await P(id);
        if (eng.RuleHiddenBy != null || !p.Shown) throw new Exception("GIFs did not come back after removing the rule");
        app.SetAppRuleMode(false);
        sb.Append(" program rules ok ·");

        // 9) 업데이트: 버전 비교, 실제 확인은 결과만 기록 (GitHub API 제한으로 실패할 수 있음)
        if (!Updates.IsNewer("0.10.0", "0.9.3") || Updates.IsNewer("0.5.2", "0.5.2") || Updates.IsNewer("0.5.1", "0.5.2") || !Updates.IsNewer("1.0", "0.9.9"))
            throw new Exception("version comparison");
        await app.CheckForUpdatesAsync(manual: true);
        sb.Append($" update check: {(app.LatestRelease is { } rel ? $"latest {rel.Version}{(rel.IsNewer ? " (newer)" : "")}" : "unavailable")} ·");

        // 10) 백업 → 바꾸기 → 복원하면 백업할 때로 돌아감 (복원 전 자동 백업도 생김)
        app.SetHoverFade(false, 33);
        app.SetAutostartDelay(30);
        if (app.State.Settings.AutostartDelaySec != 30) throw new Exception("autostart delay not saved");
        int libCount = app.State.Library.Count, activeCount = app.State.Active.Count;
        string zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gifpin-smoke-backup.zip");
        if (!await app.CreateBackupAsync(zip) || !System.IO.File.Exists(zip)) throw new Exception("backup file was not created");
        app.SetHoverFade(false, 60);
        app.AddToScreen(app.State.Library[0].Id);
        app.SaveScene("after backup");
        if (!await app.RestoreBackupAsync(zip)) throw new Exception("restore failed");
        await Wait(800);
        if (app.State.Library.Count != libCount || app.State.Active.Count != activeCount) throw new Exception($"restore: {app.State.Library.Count}/{app.State.Active.Count} vs {libCount}/{activeCount}");
        if (app.State.Settings.HoverFadeOpacity != 33 || app.State.Scenes.Any(sc => sc.Name == "after backup")) throw new Exception("restore did not bring back the backed-up settings");
        foreach (var x in app.State.Active)
            if (await eng.ProbeAsync(x.Id) is not { Shown: true } && x.Visible) throw new Exception("restored GIF is not on screen");
        if (!System.IO.Directory.Exists(Backup.BackupDir) || System.IO.Directory.GetFiles(Backup.BackupDir, "auto-before-restore-*.zip").Length == 0)
            throw new Exception("automatic backup before restore is missing");
        try { Backup.Restore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "not-a-backup.zip")); throw new Exception("missing file restored"); }
        catch (System.IO.FileNotFoundException) { }
        sb.Append($" backup/restore ok ({libCount} photos) ·");

        // 11) 설정 초기화: 옵션은 기본값, 언어·라이브러리는 그대로
        string lang = app.State.Settings.Language;
        app.SetNewItemSize(999);
        app.SetHideFromCapture(true);
        app.ResetSettings();
        if (app.State.Settings.NewItemSize != 400 || app.State.Settings.HideFromCapture || app.State.Settings.AutostartDelaySec != 0 || app.State.Settings.Language != lang)
            throw new Exception("settings reset");
        if (app.State.Library.Count != libCount) throw new Exception("reset touched the library");
        await Wait(200);
        if ((await P(app.State.Active[0].Id)).CaptureExcluded) throw new Exception("reset did not reach the overlays");
        sb.Append(" reset ok");
        return sb.ToString();
    }

    /// <summary>CI 점검 (v0.6): 성능 모드, 전체 파티클 상한, GIF 그리기 제한, 가려짐·잠금 멈춤, 숨긴 GIF 메모리 해제, 성능 모니터.</summary>
    private static async System.Threading.Tasks.Task<string> SmokePerformance(AppController app)
    {
        if (app.State.Active.Count == 0) return "performance skipped (nothing on screen)";
        var eng = app.Engine;
        async System.Threading.Tasks.Task<OverlayProbe> P(string which) => await eng.ProbeAsync(which) ?? throw new Exception("overlay missing");
        static System.Threading.Tasks.Task Wait(int ms) => System.Threading.Tasks.Task.Delay(ms);
        var sb = new System.Text.StringBuilder("v0.6:");
        var s = app.State.Settings;
        string id = app.State.Active[0].Id;
        var mon = Monitors.Primary(app.MonitorList);

        // 1) 성능 모드: 절전 → 값이 바뀌고 화질이 낮아짐, 하나를 바꾸면 직접 설정, 균형으로 돌아가기
        app.SetPerfMode(PerfModes.Saver);
        await Wait(200);
        if (s.EffectsFps != 30 || s.MaxParticlesTotal != 150 || s.GifFpsCap != 20 || (await P(id)).Quality != "Low")
            throw new Exception($"saver mode: fps {s.EffectsFps}, particles {s.MaxParticlesTotal}, gif cap {s.GifFpsCap}, quality {(await P(id)).Quality}");
        app.SetPerfOption(x => x.MaxParticlesTotal = 500);
        if (s.PerfMode != PerfModes.Custom) throw new Exception("changing an option did not switch to custom");
        app.SetPerfMode(PerfModes.Balanced);
        if (s.PerfMode != PerfModes.Balanced || PerfModes.Detect(s) != PerfModes.Balanced || s.EffectsFps != 60) throw new Exception("balanced mode");
        sb.Append(" modes ok ·");

        // 2) 전체 파티클 상한: GIF 두 개가 많이 뿜어도 합계는 상한 이하
        app.SetPerfOption(x => x.MaxParticlesTotal = 40);
        app.AddToScreen(app.State.Active[0].PhotoId);
        string id2 = app.State.Active[^1].Id;
        foreach (var which in new[] { id, id2 })
            app.UpdateActive(which, a => { a.Visible = true; a.MotionMode = Motion.None; a.FxParticles = true; a.FxParticleMode = Fx.Always; a.FxParticleRate = 40; a.FxParticleLife = 3; a.FxParticleKind = Fx.Dot; });
        app.Relocate(id2, mon.WorkLeft + 100, mon.WorkTop + 100);
        await Wait(2000);
        int p1 = (await P(id)).Particles, p2 = (await P(id2)).Particles;
        if (p1 + p2 > 40 || p1 + p2 < 20) throw new Exception($"particle cap: {p1} + {p2} with limit 40");
        app.RemoveActive(id2);
        app.UpdateActive(id, a => a.FxParticles = false);
        app.SetPerfMode(PerfModes.Balanced);
        sb.Append($" particle cap ok ({p1}+{p2}/40) ·");

        // 3) GIF 그리기 제한: 50fps GIF를 초당 20번까지만 그림 (재생 속도는 그대로)
        string fastPath = System.IO.Path.Combine(Store.LibraryDir, "smoke-fast.webp");
        using (var sample = GifAnimation.Load(Store.ResolvePath(app.State.FindPhoto(app.State.Active[0].PhotoId)!)))
            SpriteBuilder.Save(sample.Frames, 50, fastPath);
        var fastPhoto = Library.RegisterGenerated(fastPath, "smoke fast") ?? throw new Exception("fast GIF not registered");
        app.AddGeneratedPhoto(fastPhoto);
        app.AddToScreen(fastPhoto.Id);
        string fastId = app.State.Active[^1].Id;
        app.SetPerfOption(x => x.GifFpsCap = 20);
        await Wait(500);
        var f0 = await P(fastId);
        await Wait(2000);
        var f1 = await P(fastId);
        long frames = f1.GifFrames - f0.GifFrames, draws = f1.GifDraws - f0.GifDraws;
        if (frames < 70 || draws > 46) throw new Exception($"gif cap 20: {frames} frames, {draws} draws in 2 s");
        app.SetPerfOption(x => x.GifFpsCap = 0);
        await Wait(300);
        f0 = await P(fastId);
        await Wait(2000);
        f1 = await P(fastId);
        long drawsFree = f1.GifDraws - f0.GifDraws;
        if (drawsFree < 70) throw new Exception($"no gif cap: only {drawsFree} draws in 2 s");
        sb.Append($" gif cap ok ({draws} vs {drawsFree} draws / {frames} frames) ·");

        // 4) 가려진 바탕화면 GIF 멈춤: 창으로 덮으면 멈추고, 치우면 다시 재생
        app.UpdateActive(fastId, a => a.Layer = Layers.Desktop);
        // 화면 오른쪽 아래 구석 (CI 컴퓨터의 터미널 창 등 다른 창이 덜 덮는 곳)
        var (fw, fh) = app.ShownSize(app.State.Active.First(x => x.Id == fastId));
        int gx = mon.WorkRight - fw - 4, gy = mon.WorkBottom - fh - 4;
        app.Relocate(fastId, gx, gy);
        // GifPin 자신의 창(메인 창)이 그 자리를 덮지 않게 잠시 최소화
        var minimized = Current.Windows.OfType<Window>().Where(x => x.IsVisible && x.WindowState != WindowState.Minimized).ToList();
        foreach (var x in minimized) x.WindowState = WindowState.Minimized;
        await Wait(500);
        eng.CheckOcclusionNow();
        await Wait(300);
        if ((await P(fastId)).Occluded) throw new Exception("desktop GIF counted as covered before any window covered it: " + await eng.DescribeWindowsAboveAsync(fastId));
        var cover = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Left = gx - 150, Top = gy - 150, Width = fw + 300, Height = fh + 300, Background = System.Windows.Media.Brushes.DimGray,
        };
        cover.Show();
        await Wait(400);
        eng.CheckOcclusionNow();
        await Wait(300);
        var c = await P(fastId);
        if (!c.Occluded || c.GifPlaying) throw new Exception($"covered desktop GIF: occluded={c.Occluded} playing={c.GifPlaying}");
        cover.Close();
        await Wait(400);
        eng.CheckOcclusionNow();
        await Wait(300);
        c = await P(fastId);
        if (c.Occluded || !c.GifPlaying) throw new Exception($"uncovered desktop GIF: occluded={c.Occluded} playing={c.GifPlaying}");
        foreach (var x in minimized) x.WindowState = WindowState.Normal;
        sb.Append(" occlusion ok ·");

        // 5) 화면 잠금처럼 멈춤 → 풀리면 다시 재생
        eng.ForceSystemPause(true);
        await Wait(300);
        if ((await P(fastId)).GifPlaying) throw new Exception("GIF kept playing while paused for lock");
        eng.ForceSystemPause(null);
        await Wait(300);
        if (!(await P(fastId)).GifPlaying) throw new Exception("GIF did not resume after lock");
        sb.Append(" lock pause ok ·");

        // 6) 숨긴 GIF 메모리 해제 → 다시 보이면 불러옴
        var before = await eng.AnimStatsAsync();
        app.UpdateActive(fastId, a => a.Visible = false);
        await Wait(300);
        eng.UnloadHiddenNowForTest(); // 첫 확인은 숨긴 시각만 기록
        await Wait(100);
        eng.UnloadHiddenNowForTest();
        await Wait(300);
        var u = await P(fastId);
        var after = await eng.AnimStatsAsync();
        if (!u.Unloaded || after.bytes >= before.bytes) throw new Exception($"hidden GIF not unloaded (unloaded={u.Unloaded}, {before.bytes} -> {after.bytes} bytes)");
        app.UpdateActive(fastId, a => a.Visible = true);
        await Wait(1500);
        u = await P(fastId);
        if (u.Unloaded || !u.Shown || !u.GifPlaying) throw new Exception($"GIF not reloaded when shown (unloaded={u.Unloaded} shown={u.Shown})");
        sb.Append($" unload ok ({(before.bytes - after.bytes) / 1024} KB freed) ·");

        // 7) 성능 모니터, 미리보기 끄기
        string report = await app.PerfReportAsync();
        if (!report.Contains("CPU")) throw new Exception("performance report");
        app.SetLibraryPreview(false);
        app.SelectPhoto(fastPhoto.Id);
        app.SetLibraryPreview(true);
        app.RemoveActive(fastId);
        sb.Append(" monitor ok ·");

        // 8) 팝업 메뉴(장면·GIF 설정 가져오기·프리셋·설정 복사)와 하위 메뉴가 열리는지 — 장면이 있어야 삭제 하위 메뉴가 생김
        var tmpScene = app.SaveScene("menu test");
        if (app.State.Active.Count > 0) app.SelectActive(app.State.Active[0].Id, flash: false);
        var main = Current.Windows.OfType<GifPin.UI.MainWindow>().FirstOrDefault() ?? throw new Exception("main window missing");
        int menus = main.SmokeMenus();
        app.DeleteScene(tmpScene.Id);
        sb.Append($" menus ok ({menus}) ·");

        // 9) 테마: 모든 테마의 색 정의가 빠짐없고, 바꿔도 창이 다시 그려짐 (코드로 만든 줄 포함)
        if (Themes.CheckDefinitions() is string themeError) throw new Exception("theme definitions: " + themeError);
        string shots = System.IO.Path.Combine(Store.DataDir, "theme-shots");
        System.IO.Directory.CreateDirectory(shots);
        main.WindowState = WindowState.Normal;
        main.Width = 1280; main.Height = 860;
        foreach (var t in Themes.All)
        {
            app.SetTheme(t);
            foreach (var tab in new[] { "library", "settings" })
            {
                main.SetTabForTest(tab == "settings");
                await System.Threading.Tasks.Task.Delay(250);
                main.UpdateLayout();
                if (Current.Resources["BgBrush"] is not System.Windows.Media.Brush) throw new Exception($"theme {t}: background missing");
                SaveShot(main, System.IO.Path.Combine(shots, $"{t}-{tab}.png"));
            }
        }
        main.SetTabForTest(false);
        var lowContrast = Themes.ContrastReport();
        if (lowContrast.Count > 0) throw new Exception("low contrast after correction: " + string.Join(", ", lowContrast));
        if (Themes.Normalize("midnight") != Themes.Amber) throw new Exception("old midnight theme not mapped to amber");
        app.SetTheme(Themes.System);
        app.FollowSystemTheme();
        if (Themes.Current != Themes.Resolve(Themes.System)) throw new Exception($"system theme: showing {Themes.Current}, Windows wants {Themes.Resolve(Themes.System)}");
        app.SetTheme(Themes.Light);
        if (Themes.IsDark || Native.DarkTitleBars) throw new Exception("light theme still dark");
        app.SetTheme(Themes.Dark);
        if (!Themes.IsDark) throw new Exception("dark theme not dark");
        sb.Append($" themes ok ({Themes.All.Count}) ·");

        // 10) 설치 프로그램에서 고른 언어: 새 설치 표시면 적용 대상, 이미 적용한 설치면 아님
        InstallerInfo.WriteForTest("korean", "smoke-1");
        if (InstallerInfo.PendingLanguage(null) is not { language: "ko" }) throw new Exception("installer language (korean) not read");
        if (InstallerInfo.PendingLanguage("smoke-1") != null) throw new Exception("installer language applied twice");
        InstallerInfo.WriteForTest("english", "smoke-2");
        if (InstallerInfo.PendingLanguage("smoke-1") is not { language: "en" }) throw new Exception("installer language (english) not read");
        InstallerInfo.ClearForTest();
        sb.Append(" installer language ok ·");

        // 11) 외곽 빛 300%: 100%보다 빛 그림이 진함 / 파티클 여러 색: 고른 색만 나옴 / 색 고르기 창
        GifPin.UI.ColorPicker.SmokeOpenClose();
        if (Fx.NormalizeColorList("#ff0000, #00ff00,bad,#FF0000") != "#FF0000,#00FF00" || Fx.NormalizeColorList("") != Fx.Rainbow)
            throw new Exception("particle color list: " + Fx.NormalizeColorList("#ff0000, #00ff00,bad,#FF0000"));
        string gid = app.State.Active[0].Id;
        app.UpdateActive(gid, x => { x.Visible = true; x.FxGlow = true; x.FxGlowColor = "#00FF00"; x.FxGlowStrength = 300; x.FxGlowPulse = false; });
        if (app.State.Active[0].FxGlowStrength != 300) throw new Exception("glow strength above 100% was clamped");
        app.UpdateActive(gid, x => { x.FxParticles = true; x.FxParticleMode = Fx.Always; x.FxParticleRate = 30; x.FxParticleKind = Fx.Dot; x.FxParticleColor = "#FF0000,#0000FF"; });
        await System.Threading.Tasks.Task.Delay(1000);
        var colorsSeen = await eng.ParticleColorsAsync(gid);
        if (colorsSeen.Count == 0 || colorsSeen.Any(c => c != 0xFFFF0000u && c != 0xFF0000FFu) || colorsSeen.Distinct().Count() < 2)
            throw new Exception("particle colors: " + string.Join(",", colorsSeen.Distinct().Select(c => c.ToString("X8"))));
        app.UpdateActive(gid, x => { x.FxGlow = false; x.FxParticles = false; x.FxParticleColor = Fx.Rainbow; });
        sb.Append(" glow/colors ok");
        return sb.ToString();
    }

    /// <summary>점검용: 창 내용을 PNG로 저장 (테마 확인용 스크린샷).</summary>
    private static void SaveShot(Window w, string path)
    {
        if (w.Content is not FrameworkElement root || root.ActualWidth < 10) return;
        int pw = (int)root.ActualWidth, ph = (int)root.ActualHeight;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, pw, ph);
            dc.DrawRectangle(w.Background, null, r); // 창 바탕색 (내용만 그리면 빠짐)
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, r);
        }
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
