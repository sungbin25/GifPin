using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static GifPin.Core.Native;

namespace GifPin.Core;

/// <summary>모니터 하나. 좌표는 모두 물리 픽셀(가상 화면 기준).</summary>
public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Device { get; init; } = "";
    public bool Primary { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public int WorkLeft { get; init; }
    public int WorkTop { get; init; }
    public int WorkRight { get; init; }
    public int WorkBottom { get; init; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public string Label => Loc.F(Primary ? "monitor.labelPrimary" : "monitor.label", Index, Width, Height);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public static class Monitors
{
    /// <summary>연결된 모니터 목록. 주 모니터가 1번, 나머지는 왼쪽부터.</summary>
    public static List<MonitorInfo> List()
    {
        var raw = new List<(MONITORINFOEXW info, IntPtr h)>();
        MonitorEnumProc proc = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoEx(h, ref mi)) raw.Add((mi, h));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        var ordered = raw
            .OrderByDescending(m => (m.info.dwFlags & MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.info.rcMonitor.Left)
            .ThenBy(m => m.info.rcMonitor.Top)
            .ToList();

        var list = new List<MonitorInfo>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i].info;
            list.Add(new MonitorInfo
            {
                Index = i + 1,
                Device = m.szDevice ?? "",
                Primary = (m.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Left = m.rcMonitor.Left, Top = m.rcMonitor.Top, Right = m.rcMonitor.Right, Bottom = m.rcMonitor.Bottom,
                WorkLeft = m.rcWork.Left, WorkTop = m.rcWork.Top, WorkRight = m.rcWork.Right, WorkBottom = m.rcWork.Bottom,
            });
        }
        if (list.Count == 0)
        {
            // 열거 실패 시 주 모니터 하나로 가정
            var w = WorkAreaAt(1, 1);
            list.Add(new MonitorInfo
            {
                Index = 1, Primary = true,
                Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Bottom,
                WorkLeft = w.Left, WorkTop = w.Top, WorkRight = w.Right, WorkBottom = w.Bottom,
            });
        }
        return list;
    }

    public static MonitorInfo Primary(IReadOnlyList<MonitorInfo> list) => list.FirstOrDefault(m => m.Primary) ?? list[0];

    /// <summary>GIF 가운데가 놓인 모니터 (없으면 null = 화면 밖).</summary>
    public static MonitorInfo? Containing(IReadOnlyList<MonitorInfo> list, int x, int y, int w, int h)
    {
        int cx = x + w / 2, cy = y + h / 2;
        return list.FirstOrDefault(m => m.Contains(cx, cy));
    }

    /// <summary>GIF가 어느 모니터에서든 충분히(40×40px 이상) 보이는지.</summary>
    public static bool IsReachable(IReadOnlyList<MonitorInfo> list, int x, int y, int w, int h)
    {
        foreach (var m in list)
        {
            int ix = Math.Min(x + w, m.WorkRight) - Math.Max(x, m.WorkLeft);
            int iy = Math.Min(y + h, m.WorkBottom) - Math.Max(y, m.WorkTop);
            if (ix >= Math.Min(40, w) && iy >= Math.Min(40, h)) return true;
        }
        return false;
    }

    /// <summary>다른 모니터로 옮길 때: 작업 영역 안에서의 상대 위치(가운데 기준)를 유지한다.</summary>
    public static (int x, int y) MoveTo(MonitorInfo? from, MonitorInfo to, int x, int y, int w, int h)
    {
        double fx = 0.9, fy = 0.9; // 기준 모니터를 모르면 오른쪽 아래 근처
        if (from != null)
        {
            int fw = Math.Max(1, from.WorkRight - from.WorkLeft), fh = Math.Max(1, from.WorkBottom - from.WorkTop);
            fx = Math.Clamp((x + w / 2.0 - from.WorkLeft) / fw, 0, 1);
            fy = Math.Clamp((y + h / 2.0 - from.WorkTop) / fh, 0, 1);
        }
        int tw = to.WorkRight - to.WorkLeft, th = to.WorkBottom - to.WorkTop;
        int nx = (int)Math.Round(to.WorkLeft + fx * tw - w / 2.0);
        int ny = (int)Math.Round(to.WorkTop + fy * th - h / 2.0);
        nx = Math.Clamp(nx, to.WorkLeft, Math.Max(to.WorkLeft, to.WorkRight - w));
        ny = Math.Clamp(ny, to.WorkTop, Math.Max(to.WorkTop, to.WorkBottom - h));
        return (nx, ny);
    }
}
