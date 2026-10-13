using System;
using Microsoft.Win32;

namespace GifPin.Core;

/// <summary>
/// Windows 시작 시 자동 실행: HKCU\...\Run 의 "GifPin" 값.
/// Windows 11의 "시작 프로그램" 설정(작업 관리자 포함)에서 끈 경우는 StartupApproved에 "꺼짐"으로 기록되어
/// Run 값이 있어도 실행되지 않으므로 그 상태도 함께 확인한다.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "GifPin";

    public enum Status { Off, On, BlockedByWindows }

    public static Status GetStatus()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return Status.Off;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // 첫 바이트가 홀수(03, 07 …)면 Windows 설정에서 꺼 둔 상태, 짝수(02, 06)이거나 값이 없으면 허용
            if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) return Status.BlockedByWindows;
            return Status.On;
        }
        catch { return Status.Off; }
    }

    /// <summary>실제로 다음 로그인 때 실행되는지.</summary>
    public static bool IsEnabled() => GetStatus() == Status.On;

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --tray");
                // 사용자가 GifPin에서 직접 켰으므로 Windows 설정에서 꺼 둔 표시도 "허용"으로 되돌림
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1)
                {
                    var allowed = new byte[12];
                    allowed[0] = 0x02;
                    approved.SetValue(ValueName, allowed, RegistryValueKind.Binary);
                }
                Log.Info("Start with Windows: on");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Start with Windows: off");
            }
        }
        catch (Exception ex) { Log.Warn("Could not change Start with Windows", ex); }
    }

    /// <summary>점검용: Windows 설정에서 끈 것처럼 표시.</summary>
    internal static void MarkBlockedForTest()
    {
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        var blocked = new byte[12];
        blocked[0] = 0x03;
        approved.SetValue(ValueName, blocked, RegistryValueKind.Binary);
    }
}
