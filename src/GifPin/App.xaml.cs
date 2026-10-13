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
            t.Tick += (_, _) =>
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

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
