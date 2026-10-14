using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GifPin.Core;

/// <summary>
/// 전역 단축키: 동작 이름 ↔ 키 조합 글자("Ctrl+Alt+E").
/// 저장은 사람이 읽을 수 있는 글자로 하고, 등록할 때 (보조키, 가상 키 코드)로 바꾼다.
/// </summary>
public static class Hotkeys
{
    // 동작 (저장 키 = 이름, 엔진 등록 번호 = Id)
    public const string Edit = "edit", Hide = "hide", Quit = "quit", ClickThrough = "clickThrough", Effects = "effects", NextScene = "nextScene";

    public static readonly IReadOnlyList<string> Actions = new[] { Edit, Hide, ClickThrough, Effects, NextScene, Quit };

    public static int IdOf(string action) => action switch
    {
        Edit => 1, Hide => 2, Quit => 3, ClickThrough => 4, Effects => 5, NextScene => 6, _ => 0,
    };

    public static string? ActionOf(int id) => Actions.FirstOrDefault(a => IdOf(a) == id);

    public static string Default(string action) => action switch
    {
        Edit => "Ctrl+Alt+E",
        Hide => "Ctrl+Alt+H",
        Quit => "Ctrl+Alt+Q",
        _ => "",
    };

    public const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    /// <summary>저장된 단축키 표를 바로잡음: 빠진 동작은 기본값, 모르는 동작·잘못된 글자는 정리.</summary>
    public static Dictionary<string, string> Normalize(Dictionary<string, string>? map)
    {
        var result = new Dictionary<string, string>();
        foreach (var action in Actions)
        {
            string text = map != null && map.TryGetValue(action, out var t) ? t ?? "" : Default(action);
            result[action] = TryParse(text, out uint m, out uint vk) ? Format(m, vk) : "";
        }
        return result;
    }

    /// <summary>"Ctrl+Shift+F5" → 보조키와 가상 키. 빈 글자거나 형식이 틀리면 false.</summary>
    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        // "Ctrl++"처럼 + 키 자체는 쓰지 않음 (Plus로 표기)
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModCtrl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": case "windows": mods |= ModWin; break;
                default: return false;
            }
        }
        vk = KeyFromName(parts[^1]);
        return vk != 0 && IsAllowed(mods, vk);
    }

    /// <summary>보조키 없이 쓸 수 있는 건 F키뿐 (글자 키 하나만으로는 타자를 칠 때마다 걸림).</summary>
    public static bool IsAllowed(uint mods, uint vk)
    {
        if (vk == 0 || IsModifierKey(vk)) return false;
        bool fKey = vk >= 0x70 && vk <= 0x87;
        bool onlyShift = mods == ModShift;
        return fKey || (mods != 0 && !onlyShift);
    }

    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModWin) != 0) parts.Add("Win");
        parts.Add(NameOfKey(vk));
        return string.Join("+", parts);
    }

    private static readonly (uint vk, string name)[] Named =
    {
        (0x08, "Backspace"), (0x09, "Tab"), (0x0D, "Enter"), (0x13, "Pause"), (0x14, "CapsLock"), (0x1B, "Esc"),
        (0x20, "Space"), (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"),
        (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x2C, "PrintScreen"), (0x2D, "Insert"), (0x2E, "Delete"),
        (0x6A, "Num*"), (0x6B, "Num+"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/"),
        (0x90, "NumLock"), (0x91, "ScrollLock"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
    };

    public static string NameOfKey(uint vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        foreach (var (k, n) in Named) if (k == vk) return n;
        return "#" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static uint KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') return c;
        }
        foreach (var (k, n) in Named) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return k;
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3])) return (uint)(0x60 + (name[3] - '0'));
        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f >= 1 && f <= 24)
            return (uint)(0x6F + f);
        if (name.StartsWith('#') && uint.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) && hex > 0 && hex < 0xFF)
            return hex;
        return 0;
    }
}
