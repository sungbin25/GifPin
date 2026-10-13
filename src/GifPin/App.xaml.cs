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
            MessageBox.Show("예기치 못한 오류가 발생했습니다.\n\n" + ex.Exception.Message, "GifPin", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var state = Store.Load();
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

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
