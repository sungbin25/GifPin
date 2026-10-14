using System;
using Microsoft.Win32;

namespace GifPin.Core;

/// <summary>
/// 설치 프로그램이 남긴 정보 (HKCU\Software\GifPin): 설치할 때 고른 언어와 설치 시각.
/// 새로 설치(또는 다시 설치)할 때마다 한 번, 앱 언어를 설치 프로그램에서 고른 언어로 맞춘다.
/// 그 뒤에 앱 설정에서 바꾼 언어는 다음 설치 전까지 그대로 유지.
/// </summary>
public static class InstallerInfo
{
    private const string Key = @"Software\GifPin";

    /// <summary>적용할 언어가 있으면 ("en"/"ko", 설치 표시) 돌려줌. 이미 적용한 설치면 null.</summary>
    public static (string language, string stamp)? PendingLanguage(string? appliedStamp)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("InstallLanguage") is not string lang || key.GetValue("InstallStamp") is not string stamp) return null;
            if (string.IsNullOrEmpty(stamp) || stamp == appliedStamp) return null;
            string code = lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";
            return (code, stamp);
        }
        catch { return null; }
    }

    /// <summary>점검용: 설치 프로그램이 쓴 것처럼 기록.</summary>
    internal static void WriteForTest(string language, string stamp)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("InstallLanguage", language);
        key.SetValue("InstallStamp", stamp);
    }

    internal static void ClearForTest()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false); } catch { }
    }
}
