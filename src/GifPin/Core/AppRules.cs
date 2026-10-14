using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using static GifPin.Core.Native;

namespace GifPin.Core;

/// <summary>
/// 프로그램별 숨기기: 지정한 프로그램이 앞에 있을 때(또는 실행 중일 때) GIF를 모두 숨긴다.
/// 엔진 스레드에서 1.5초마다, 그리고 앞 창이 바뀔 때마다 확인.
/// </summary>
internal sealed class AppRules
{
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase); // 확장자 없는 이름 (Process.ProcessName과 같은 형식)
    private bool _whileRunning;
    private uint _lastPid;
    private string? _lastName;
    private readonly int _ownPid = Environment.ProcessId;

    /// <summary>"C:\\…\\POWERPNT.EXE", "powerpnt" → "POWERPNT.EXE" 형식 (표시·저장용).</summary>
    public static string NormalizeName(string name)
    {
        name = Path.GetFileName(name.Trim().Trim('"'));
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name;
    }

    private static string Stem(string name) => Path.GetFileNameWithoutExtension(NormalizeName(name));

    public bool Enabled => _names.Count > 0;

    public void Configure(IEnumerable<string> names, bool whileRunning)
    {
        _names = new HashSet<string>(names.Select(Stem), StringComparer.OrdinalIgnoreCase);
        _whileRunning = whileRunning;
    }

    /// <summary>숨겨야 하면 그 프로그램 이름, 아니면 null. keepIfOwn: 앞 창이 GifPin 자신이면 지난 결과 유지.</summary>
    public string? Check(string? previous)
    {
        if (_names.Count == 0) return null;
        if (_whileRunning)
        {
            foreach (var n in _names)
            {
                var procs = Process.GetProcessesByName(n);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) return n + ".exe";
            }
            return null;
        }

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return previous; // 창 전환 중
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return previous;
        if (pid == _ownPid) return previous; // GifPin 창(설정 등)을 눌렀을 때는 그대로
        string? name = NameOf(pid);
        return name != null && _names.Contains(name) ? name + ".exe" : null;
    }

    private string? NameOf(uint pid)
    {
        if (pid == _lastPid) return _lastName;
        string? name = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch { /* 이미 끝난 프로세스·권한 없음 */ }
        _lastPid = pid;
        _lastName = name;
        return name;
    }

    /// <summary>창이 있는 실행 중인 프로그램 목록 (설정 화면의 "실행 중인 프로그램에서 추가").</summary>
    public static List<(string exe, string title)> RunningWindowedApps()
    {
        var list = new List<(string, string)>();
        int own = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != own && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    list.Add((p.ProcessName + ".exe", p.MainWindowTitle));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
