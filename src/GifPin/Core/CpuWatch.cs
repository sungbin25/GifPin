using System;
using System.Runtime.InteropServices;

namespace GifPin.Core;

/// <summary>
/// 다른 프로그램(GifPin 제외)이 쓰는 CPU 비율을 잰다. GifPin 자신의 사용량은 빼므로
/// 효과를 줄였다 늘렸다 하며 스스로 흔들리지 않는다.
/// </summary>
internal sealed class CpuSampler
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private long _idle, _kernel, _user, _own;
    private bool _has;

    /// <summary>지난번 이후 다른 프로그램의 CPU 사용률 (0~100). 처음 한 번은 null.</summary>
    public double? SampleOthersPercent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
        GetProcessTimes(GetCurrentProcess(), out _, out _, out long pk, out long pu);
        long own = pk + pu;
        double? result = null;
        if (_has)
        {
            double total = (kernel - _kernel) + (user - _user); // kernel 시간에는 idle이 포함됨
            double busy = total - (idle - _idle);
            double mine = own - _own;
            if (total > 0) result = Math.Clamp((busy - mine) / total * 100, 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user; _own = own; _has = true;
        return result;
    }
}

/// <summary>
/// CPU 사용률이 기준 이상으로 몇 번 연속이면 "바쁨"(게임 등), 기준보다 15% 낮게 몇 번 연속이면 다시 "여유".
/// 잠깐 튀는 값에는 반응하지 않는다.
/// </summary>
internal sealed class BusyDetector
{
    public int ThresholdPercent { get; set; } = 80;
    public int SamplesToEnter { get; set; } = 3;  // 1.5초 간격 → 약 4.5초
    public int SamplesToExit { get; set; } = 4;   // 약 6초
    public bool Busy { get; private set; }
    private int _high, _low;

    public bool Feed(double othersPercent)
    {
        if (othersPercent >= ThresholdPercent) { _high++; _low = 0; }
        else if (othersPercent < ThresholdPercent - 15) { _low++; _high = 0; }
        else { _high = 0; _low = 0; }
        if (!Busy && _high >= SamplesToEnter) Busy = true;
        else if (Busy && _low >= SamplesToExit) Busy = false;
        return Busy;
    }

    public void Reset() { Busy = false; _high = _low = 0; }
}
