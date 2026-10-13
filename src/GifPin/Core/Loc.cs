using System;
using System.Collections.Generic;
using System.Globalization;

namespace GifPin.Core;

/// <summary>
/// UI 문구 번역. 기본 영어, 설정에서 한국어 선택.
/// 코드: Loc.T("key") / Loc.F("key", args).  XAML: {DynamicResource s.key} (UI 쪽에서 리소스로 복사해 둠).
/// 문구 목록은 Strings.cs.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Korean = "ko";

    public static string Language { get; private set; } = English;

    /// <summary>언어가 바뀜 (UI 스레드에서 발생).</summary>
    public static event Action? Changed;

    public static string Normalize(string? lang) => lang == Korean ? Korean : English;

    public static void SetLanguage(string? lang)
    {
        var l = Normalize(lang);
        if (l == Language && _initialized) return;
        _initialized = true;
        Language = l;
        Changed?.Invoke();
    }
    private static bool _initialized;

    public static string T(string key)
    {
        if (!Strings.All.TryGetValue(key, out var v)) return key;
        return Language == Korean ? v.ko : v.en;
    }

    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    /// <summary>현재 언어의 모든 문구 (UI 리소스 사전 채우기용).</summary>
    public static IEnumerable<KeyValuePair<string, string>> All()
    {
        foreach (var kv in Strings.All)
            yield return new KeyValuePair<string, string>(kv.Key, Language == Korean ? kv.Value.ko : kv.Value.en);
    }
}
