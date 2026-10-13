using System;
using Microsoft.Win32;

namespace GifPin.Core;

/// <summary>Windows 시작 시 자동 실행: HKCU\...\Run 의 "GifPin" 값.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GifPin";

    /// <summary>레지스트리에 등록되어 있는지 (설치 프로그램에서 켰을 수도 있음).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* 권한 문제 등은 무시 */ }
    }
}
