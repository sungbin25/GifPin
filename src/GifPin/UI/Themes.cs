using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GifPin.Core;
using Microsoft.Win32;

namespace GifPin.UI;

/// <summary>
/// 화면 테마: 색 이름(…Brush)마다 값을 정해 앱 리소스를 바꾼다. XAML은 DynamicResource로 연결돼 있어 창을 다시 열지 않아도 바로 바뀜.
/// 값 형식: "#RRGGBB" / "#AARRGGBB", 세로 그라데이션 "v:#위,#아래", 가로 그라데이션 "h:#왼쪽,#오른쪽".
/// </summary>
public static class Themes
{
    public const string System = "system", Dark = "dark", Light = "light", Amber = "amber", Mint = "mint", Neon = "neon", Xp = "xp", Classic = "classic";

    /// <summary>설정 화면에 보이는 순서.</summary>
    public static readonly IReadOnlyList<string> All = new[] { System, Dark, Light, Amber, Mint, Neon, Xp, Classic };

    /// <summary>예전 "미드나잇"(다크와 거의 같았음)은 앰버로 바뀜.</summary>
    public static string Normalize(string? name) => name == "midnight" ? Amber : name != null && All.Contains(name) ? name : Dark;

    private sealed record Theme(bool IsDark, string Font, double RadiusM, double RadiusL, double RadiusXL, Dictionary<string, string> Colors);

    // ── 기본(다크) 색 ──
    private static readonly Dictionary<string, string> DarkColors = new()
    {
        ["Bg"] = "#0F1115", ["Chrome"] = "#0B0D11", ["Panel"] = "#13161C", ["Card"] = "#171A21", ["Input"] = "#0F1115",
        ["Line"] = "#1E222B", ["Border"] = "#2A2F3A", ["BorderStrong"] = "#343A47",
        ["Text"] = "#E8EAF0", ["TextDim"] = "#9AA3B2", ["TextMuted"] = "#8C95A6", ["Label"] = "#C9CED8",
        ["Accent"] = "#4C8DFF", ["AccentSoft"] = "#264C8DFF", ["AccentText"] = "#0A0F1A", ["AccentLight"] = "#8FB6FF",
        ["SelectedBg"] = "#172038", ["SegmentOn"] = "#2A3040",
        ["Green"] = "#3DD68C", ["GreenText"] = "#6FE3A8", ["Warn"] = "#FFB35C", ["WarnText"] = "#FFC888",
        ["Danger"] = "#FF9AA6", ["DangerBorder"] = "#4A2B31",
        ["HoverOverlay"] = "#FFFFFF", ["BtnBg"] = "#1A1E26", ["BtnSoftBg"] = "#222838", ["BtnSoftBorder"] = "#34406A", ["BtnSoftText"] = "#BFD4FF",
        ["WarnBg"] = "#24FFB35C", ["StrongText"] = "#FFFFFF",
        ["ChromeText"] = "#9AA3B2", ["ChromeTextActive"] = "#FFFFFF", ["ChromeAccent"] = "#4C8DFF",
        ["SegmentBox"] = "#161920", ["SegmentBoxBorder"] = "#22262F", ["Track"] = "#2F3542", ["Knob"] = "#9AA3B2", ["KnobOn"] = "#FFFFFF",
        ["SliderThumb"] = "#FFFFFF", ["ScrollThumb"] = "#343A47",
        ["MenuBg"] = "#15181F", ["MenuBorder"] = "#2E3441", ["MenuHover"] = "#222838",
        ["CardBorder"] = "#22262F", ["CardHoverBorder"] = "#3A4152", ["BottomBar"] = "#0C0E12", ["CountBadge"] = "#1F2A3F",
        ["ToastBg"] = "#1E2330", ["ToastBorder"] = "#34406A", ["GreenBadgeBg"] = "#243DD68C", ["ToolbarBg"] = "#12151B",
        ["CheckerA"] = "#1A1E26", ["CheckerB"] = "#232833",
    };

    private static Dictionary<string, string> From(Dictionary<string, string> baseColors, Dictionary<string, string> overrides)
    {
        var d = new Dictionary<string, string>(baseColors);
        foreach (var kv in overrides) d[kv.Key] = kv.Value;
        return d;
    }

    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["Bg"] = "#F4F5F7", ["Chrome"] = "#FFFFFF", ["Panel"] = "#FFFFFF", ["Card"] = "#FFFFFF", ["Input"] = "#FFFFFF",
        ["Line"] = "#E3E6EB", ["Border"] = "#D3D8E0", ["BorderStrong"] = "#BFC6D1",
        ["Text"] = "#1B1F27", ["TextDim"] = "#4B5363", ["TextMuted"] = "#6B7383", ["Label"] = "#2E3440",
        ["Accent"] = "#2F6FEB", ["AccentSoft"] = "#262F6FEB", ["AccentText"] = "#FFFFFF", ["AccentLight"] = "#1F5BD0",
        ["SelectedBg"] = "#E6EEFD", ["SegmentOn"] = "#FFFFFF",
        ["Green"] = "#1E9E62", ["GreenText"] = "#157A4B", ["Warn"] = "#D9822B", ["WarnText"] = "#A65C10",
        ["Danger"] = "#C93545", ["DangerBorder"] = "#F0C2C8",
        ["HoverOverlay"] = "#000000", ["BtnBg"] = "#FFFFFF", ["BtnSoftBg"] = "#E6EEFD", ["BtnSoftBorder"] = "#B7CDF7", ["BtnSoftText"] = "#1F5BD0",
        ["WarnBg"] = "#26D9822B", ["StrongText"] = "#0E1116",
        ["ChromeText"] = "#4B5363", ["ChromeTextActive"] = "#0E1116", ["ChromeAccent"] = "#2F6FEB",
        ["SegmentBox"] = "#ECEEF2", ["SegmentBoxBorder"] = "#E0E3E8", ["Track"] = "#CDD3DC", ["Knob"] = "#FFFFFF", ["KnobOn"] = "#FFFFFF",
        ["SliderThumb"] = "#FFFFFF", ["ScrollThumb"] = "#C5CBD5",
        ["MenuBg"] = "#FFFFFF", ["MenuBorder"] = "#D3D8E0", ["MenuHover"] = "#EEF2F8",
        ["CardBorder"] = "#E3E6EB", ["CardHoverBorder"] = "#BFC6D1", ["BottomBar"] = "#FAFBFC", ["CountBadge"] = "#E6EEFD",
        ["ToastBg"] = "#FFFFFF", ["ToastBorder"] = "#B7CDF7", ["GreenBadgeBg"] = "#261E9E62", ["ToolbarBg"] = "#FFFFFF",
        ["CheckerA"] = "#FFFFFF", ["CheckerB"] = "#E9ECF1",
    };

    private static readonly Dictionary<string, Theme> Defs = new()
    {
        [Dark] = new(true, "Segoe UI, Malgun Gothic", 6, 8, 12, DarkColors),
        [Light] = new(false, "Segoe UI, Malgun Gothic", 6, 8, 12, LightColors),
        // 앰버: 따뜻한 숯색 바탕에 호박색 강조 — 차가운 남색 계열인 다크와 확실히 다르게
        [Amber] = new(true, "Segoe UI, Malgun Gothic", 6, 8, 12, new()
        {
            ["Bg"] = "#14110F", ["Chrome"] = "#0F0C0A", ["Panel"] = "#1B1714", ["Card"] = "#221D19", ["Input"] = "#120F0D",
            ["Line"] = "#2C2520", ["Border"] = "#3E342C", ["BorderStrong"] = "#54473C",
            ["Text"] = "#F4ECE3", ["TextDim"] = "#C9B8A6", ["TextMuted"] = "#AD9A87", ["Label"] = "#E3D5C6",
            ["Accent"] = "#FFA53D", ["AccentSoft"] = "#26FFA53D", ["AccentText"] = "#1E1206", ["AccentLight"] = "#FFC57E",
            ["SelectedBg"] = "#33241A", ["SegmentOn"] = "#3A2F27",
            ["Green"] = "#7ED58A", ["GreenText"] = "#A4E6AC", ["Warn"] = "#FFB35C", ["WarnText"] = "#FFCB8E",
            ["Danger"] = "#FF8F80", ["DangerBorder"] = "#5A2D27",
            ["HoverOverlay"] = "#FFE6C8", ["BtnBg"] = "#26201B", ["BtnSoftBg"] = "#3A2A1A", ["BtnSoftBorder"] = "#7A5428", ["BtnSoftText"] = "#FFD2A0",
            ["WarnBg"] = "#24FFB35C", ["StrongText"] = "#FFFFFF",
            ["ChromeText"] = "#C9B8A6", ["ChromeTextActive"] = "#FFF6EC", ["ChromeAccent"] = "#FFA53D",
            ["SegmentBox"] = "#17130F", ["SegmentBoxBorder"] = "#2E2620", ["Track"] = "#4A3E34", ["Knob"] = "#D8C6B2", ["KnobOn"] = "#1E1206",
            ["SliderThumb"] = "#FFF6EC", ["ScrollThumb"] = "#54473C",
            ["MenuBg"] = "#1E1915", ["MenuBorder"] = "#4A3E34", ["MenuHover"] = "#33281F",
            ["CardBorder"] = "#2E2620", ["CardHoverBorder"] = "#6A5646", ["BottomBar"] = "#0F0C0A", ["CountBadge"] = "#3A2A1A",
            ["ToastBg"] = "#26201B", ["ToastBorder"] = "#7A5428", ["GreenBadgeBg"] = "#247ED58A", ["ToolbarBg"] = "#1B1714",
            ["CheckerA"] = "#221D19", ["CheckerB"] = "#2C2520",
        }),
        [Mint] = new(false, "Segoe UI, Malgun Gothic", 8, 10, 14, From(LightColors, new()
        {
            ["Bg"] = "#EEF8F5", ["Line"] = "#D7ECE6", ["Border"] = "#BFE0D7", ["BorderStrong"] = "#9FCFC2",
            ["Text"] = "#17302B", ["TextDim"] = "#3E5E57", ["TextMuted"] = "#5E7D76", ["Label"] = "#24423C",
            ["Accent"] = "#13A58A", ["AccentSoft"] = "#2613A58A", ["AccentLight"] = "#0E8A73", ["SelectedBg"] = "#DDF4EE",
            ["StrongText"] = "#0E2420", ["Green"] = "#13A58A", ["GreenText"] = "#0E8A73", ["Warn"] = "#E8913A", ["WarnText"] = "#B5651A",
            ["Danger"] = "#E0577A", ["DangerBorder"] = "#F6C6D3",
            ["BtnSoftBg"] = "#FFE3EC", ["BtnSoftBorder"] = "#FFB8CD", ["BtnSoftText"] = "#D03F6E", ["WarnBg"] = "#26E8913A",
            ["ChromeText"] = "#3E5E57", ["ChromeTextActive"] = "#0E2420", ["ChromeAccent"] = "#FF6F9E",
            ["SegmentBox"] = "#E4F3EF", ["SegmentBoxBorder"] = "#D3EAE3", ["Track"] = "#C3DCD5", ["ScrollThumb"] = "#B5D6CD",
            ["MenuBorder"] = "#BFE0D7", ["MenuHover"] = "#E6F6F1", ["CardBorder"] = "#D7ECE6", ["CardHoverBorder"] = "#9FCFC2",
            ["BottomBar"] = "#F7FCFA", ["CountBadge"] = "#DDF4EE", ["ToastBorder"] = "#9FCFC2", ["GreenBadgeBg"] = "#2613A58A",
            ["CheckerB"] = "#E7F4F0",
        })),
        [Neon] = new(true, "Segoe UI, Malgun Gothic", 6, 8, 12, new()
        {
            ["Bg"] = "#0D0B1A", ["Chrome"] = "#090713", ["Panel"] = "#120F24", ["Card"] = "#17132D", ["Input"] = "#0B0918",
            ["Line"] = "#221C3D", ["Border"] = "#2E2650", ["BorderStrong"] = "#3D3368",
            ["Text"] = "#EEE9FF", ["TextDim"] = "#A99FD0", ["TextMuted"] = "#8D84B5", ["Label"] = "#D3CCF2",
            ["Accent"] = "#FF3EA5", ["AccentSoft"] = "#26FF3EA5", ["AccentText"] = "#1A0612", ["AccentLight"] = "#39E8FF",
            ["SelectedBg"] = "#2A1238", ["SegmentOn"] = "#2E2552",
            ["Green"] = "#39E8C0", ["GreenText"] = "#7FF5D9", ["Warn"] = "#FFC34D", ["WarnText"] = "#FFD98A",
            ["Danger"] = "#FF7A9C", ["DangerBorder"] = "#4D2238",
            ["HoverOverlay"] = "#FFFFFF", ["BtnBg"] = "#1C1736", ["BtnSoftBg"] = "#102A3A", ["BtnSoftBorder"] = "#1E6F86", ["BtnSoftText"] = "#8CF3FF",
            ["WarnBg"] = "#24FFC34D", ["StrongText"] = "#FFFFFF",
            ["ChromeText"] = "#A99FD0", ["ChromeTextActive"] = "#FFFFFF", ["ChromeAccent"] = "h:#FF3EA5,#39E8FF",
            ["SegmentBox"] = "#120F24", ["SegmentBoxBorder"] = "#2A2347", ["Track"] = "#2E2650", ["Knob"] = "#A99FD0", ["KnobOn"] = "#FFFFFF",
            ["SliderThumb"] = "#FFFFFF", ["ScrollThumb"] = "#3D3368",
            ["MenuBg"] = "#141029", ["MenuBorder"] = "#3D3368", ["MenuHover"] = "#2A1F4A",
            ["CardBorder"] = "#241D42", ["CardHoverBorder"] = "#5A3F8C", ["BottomBar"] = "#090713", ["CountBadge"] = "#2A1F4A",
            ["ToastBg"] = "#1C1736", ["ToastBorder"] = "#FF3EA5", ["GreenBadgeBg"] = "#2439E8C0", ["ToolbarBg"] = "#120F24",
            ["CheckerA"] = "#17132D", ["CheckerB"] = "#211B3D",
        }),
        // Windows XP (Luna): 베이지 창, 파란 그라데이션 상단, 흰→베이지 버튼, 남색 테두리, Tahoma
        [Xp] = new(false, "Tahoma, Malgun Gothic", 3, 3, 4, new()
        {
            ["Bg"] = "#ECE9D8", ["Chrome"] = "v:#3A8DF6,#0B55D6", ["Panel"] = "#F5F4EA", ["Card"] = "#FFFFFF", ["Input"] = "#FFFFFF",
            ["Line"] = "#D6D2C2", ["Border"] = "#7F9DB9", ["BorderStrong"] = "#003C74",
            ["Text"] = "#000000", ["TextDim"] = "#3C3C3C", ["TextMuted"] = "#6D6D6D", ["Label"] = "#1A1A1A",
            ["Accent"] = "v:#3A8DF6,#1C5FD3", ["AccentSoft"] = "#26316AC5", ["AccentText"] = "#FFFFFF", ["AccentLight"] = "#0B4FBF",
            ["SelectedBg"] = "#C1D2EE", ["SegmentOn"] = "v:#FFFFFF,#E3E1D3",
            ["Green"] = "#2E9B2E", ["GreenText"] = "#1F7A1F", ["Warn"] = "#E07B00", ["WarnText"] = "#A85C00",
            ["Danger"] = "#C0262D", ["DangerBorder"] = "#E6A6A9",
            ["HoverOverlay"] = "#316AC5", ["BtnBg"] = "v:#FFFFFF,#E3E1D3", ["BtnSoftBg"] = "v:#F4F8FE,#D9E5F7", ["BtnSoftBorder"] = "#003C74", ["BtnSoftText"] = "#0B3A80",
            ["WarnBg"] = "#26E07B00", ["StrongText"] = "#000000",
            ["ChromeText"] = "#DCE8FF", ["ChromeTextActive"] = "#FFFFFF", ["ChromeAccent"] = "#FFB733",
            ["SegmentBox"] = "#F5F4EA", ["SegmentBoxBorder"] = "#7F9DB9", ["Track"] = "#ACA899", ["Knob"] = "#FFFFFF", ["KnobOn"] = "#FFFFFF",
            ["SliderThumb"] = "v:#FFFFFF,#E3E1D3", ["ScrollThumb"] = "h:#C8D7F7,#A9C2EE",
            ["MenuBg"] = "#FFFFFF", ["MenuBorder"] = "#ACA899", ["MenuHover"] = "#C1D2EE",
            ["CardBorder"] = "#D6D2C2", ["CardHoverBorder"] = "#7F9DB9", ["BottomBar"] = "#F5F4EA", ["CountBadge"] = "#C1D2EE",
            ["ToastBg"] = "#FFFFE1", ["ToastBorder"] = "#000000", ["GreenBadgeBg"] = "#262E9B2E", ["ToolbarBg"] = "#ECE9D8",
            ["CheckerA"] = "#FFFFFF", ["CheckerB"] = "#E6E3D3",
        }),
        // 클래식 (Windows 98): 회색 각진 창, 남색 그라데이션 상단, 모서리 없음
        [Classic] = new(false, "Tahoma, Gulim", 0, 0, 0, new()
        {
            ["Bg"] = "#C0C0C0", ["Chrome"] = "h:#000080,#1084D0", ["Panel"] = "#C0C0C0", ["Card"] = "#D4D0C8", ["Input"] = "#FFFFFF",
            ["Line"] = "#808080", ["Border"] = "#808080", ["BorderStrong"] = "#404040",
            ["Text"] = "#000000", ["TextDim"] = "#202020", ["TextMuted"] = "#404040", ["Label"] = "#000000",
            ["Accent"] = "#000080", ["AccentSoft"] = "#33000080", ["AccentText"] = "#FFFFFF", ["AccentLight"] = "#000080",
            ["SelectedBg"] = "#A6CAF0", ["SegmentOn"] = "#FFFFFF",
            ["Green"] = "#008000", ["GreenText"] = "#006000", ["Warn"] = "#806000", ["WarnText"] = "#604000",
            ["Danger"] = "#A00000", ["DangerBorder"] = "#800000",
            ["HoverOverlay"] = "#000000", ["BtnBg"] = "#D4D0C8", ["BtnSoftBg"] = "#D4D0C8", ["BtnSoftBorder"] = "#404040", ["BtnSoftText"] = "#000080",
            ["WarnBg"] = "#33FFFF00", ["StrongText"] = "#000000",
            ["ChromeText"] = "#D4D0C8", ["ChromeTextActive"] = "#FFFFFF", ["ChromeAccent"] = "#FFFFFF",
            ["SegmentBox"] = "#C0C0C0", ["SegmentBoxBorder"] = "#808080", ["Track"] = "#808080", ["Knob"] = "#FFFFFF", ["KnobOn"] = "#FFFFFF",
            ["SliderThumb"] = "#D4D0C8", ["ScrollThumb"] = "#A0A0A0",
            ["MenuBg"] = "#D4D0C8", ["MenuBorder"] = "#404040", ["MenuHover"] = "#A6CAF0",
            ["CardBorder"] = "#808080", ["CardHoverBorder"] = "#000000", ["BottomBar"] = "#C0C0C0", ["CountBadge"] = "#FFFFFF",
            ["ToastBg"] = "#FFFFE1", ["ToastBorder"] = "#000000", ["GreenBadgeBg"] = "#3300A000", ["ToolbarBg"] = "#C0C0C0",
            ["CheckerA"] = "#FFFFFF", ["CheckerB"] = "#D4D0C8",
        }),
    };

    /// <summary>지금 적용된 테마 (system이면 실제로 고른 dark/light).</summary>
    public static string Current { get; private set; } = Dark;
    public static bool IsDark { get; private set; } = true;

    /// <summary>Windows "앱 모드"가 라이트인지.</summary>
    public static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static string Resolve(string name) => Normalize(name) == System ? (SystemUsesLight() ? Light : Dark) : Normalize(name);

    /// <summary>테마를 앱 전체에 적용 (열려 있는 창 포함).</summary>
    public static void Apply(string name)
    {
        string resolved = Resolve(name);
        var t = Defs[resolved];
        var colors = WithContrast(t.Colors, t.IsDark);
        var res = Application.Current.Resources;
        foreach (var (key, spec) in colors)
        {
            if (key is "CheckerA" or "CheckerB") continue;
            res[key + "Brush"] = MakeBrush(spec);
        }
        res["CheckerBrush"] = Checker(t.Colors["CheckerA"], t.Colors["CheckerB"]);
        res["UiFont"] = new FontFamily(t.Font);
        res["RadiusM"] = new CornerRadius(t.RadiusM);
        res["RadiusL"] = new CornerRadius(t.RadiusL);
        res["RadiusXL"] = new CornerRadius(t.RadiusXL);
        Current = resolved;
        IsDark = t.IsDark;
        Native.DarkTitleBars = t.IsDark;
        foreach (Window w in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) Native.UseDarkTitleBar(hwnd);
        }
        Log.Info($"Theme: {name}{(resolved != name ? $" ({resolved})" : "")}");
    }

    // ── 대비 보정 ────────────────────────────────────────────
    // 테마 색을 그대로 쓰면 어두운 테마에서 테두리·스위치·아이콘이 바탕에 묻힐 수 있음.
    // 기준보다 대비가 낮으면 그 테마의 "글자색과 강조색을 섞은 색" 쪽으로 조금씩 옮겨 테마 색감을 살린 채 잘 보이게 한다.

    /// <summary>(색 이름, 비교할 바탕, 최소 대비). 아이콘·글자는 4.5, 조작 요소 테두리·스위치는 3, 구분선은 낮게.</summary>
    private static readonly (string key, string against, double min)[] ContrastRules =
    {
        ("TextMuted", "Panel", 4.5), ("TextDim", "Panel", 6.0), ("Label", "Panel", 7.0), ("ChromeText", "Chrome", 4.5),
        ("Border", "Panel", 2.2), ("BorderStrong", "Panel", 3.0), ("SegmentBoxBorder", "Panel", 1.7),
        ("Track", "Panel", 2.0), ("Knob", "Track", 3.0), ("CardBorder", "Bg", 1.5), ("CardHoverBorder", "Bg", 2.5),
        ("ScrollThumb", "Bg", 2.0), ("MenuBorder", "MenuBg", 2.2), ("Line", "Panel", 1.25),
    };

    /// <summary>밝은 테마는 글자·아이콘 대비만 보정 (테두리를 진하게 하면 오히려 무거워 보임). 흰 스위치 손잡이도 그대로.</summary>
    private static readonly HashSet<string> DarkOnlyRules = new() { "Border", "SegmentBoxBorder", "Track", "Knob", "CardBorder", "CardHoverBorder", "ScrollThumb", "MenuBorder", "Line" };

    /// <summary>보정한 색 표 + 파생 색 (Icon: 아이콘, AnchorDot: 위치 버튼 점).</summary>
    private static Dictionary<string, string> WithContrast(Dictionary<string, string> src, bool isDark)
    {
        var c = new Dictionary<string, string>(src);
        Color Solid(string key) => FirstColor(c[key]);
        // 대비를 높일 때 향하는 색: 글자색 75% + 강조색 25% (테마 색이 묻어나게)
        Color target = Mix(Solid("Text"), FirstColor(c["Accent"]), 0.25);
        foreach (var (key, against, min) in ContrastRules)
        {
            if (!c.ContainsKey(key) || IsGradient(c[key]) || IsGradient(c[against])) continue;
            if (!isDark && DarkOnlyRules.Contains(key)) continue;
            var fixedColor = EnsureContrast(Solid(key), Solid(against), min, target);
            c[key] = ToHex(fixedColor);
        }
        // 아이콘: 강조색과 글자색의 중간 (테마 색이 보이면서 바탕과 대비 4.5 이상)
        var panel = IsGradient(c["Panel"]) ? FirstColor(c["Panel"]) : Solid("Panel");
        c["Icon"] = ToHex(EnsureContrast(Mix(FirstColor(c["Accent"]), Solid("Text"), 0.45), panel, 4.5, Solid("Text")));
        c["AnchorDot"] = ToHex(EnsureContrast(Mix(panel, FirstColor(c["Accent"]), 0.45), panel, 3.0, target));
        return c;
    }

    private static bool IsGradient(string spec) => spec.Length > 2 && spec[1] == ':';
    private static Color FirstColor(string spec) => ParseColor(IsGradient(spec) ? spec[2..].Split(',')[0] : spec);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Lum(Color c)
    {
        static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 대비율 (반투명 색은 바탕 위에 섞은 결과로).</summary>
    internal static double Contrast(Color fg, Color bg)
    {
        if (fg.A < 255) fg = Mix(Color.FromRgb(bg.R, bg.G, bg.B), Color.FromRgb(fg.R, fg.G, fg.B), fg.A / 255.0);
        double a = Lum(fg), b = Lum(bg);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>대비가 min이 될 때까지 color를 target 쪽으로 옮김 (target이 바탕과 비슷하면 흑/백 쪽으로).</summary>
    private static Color EnsureContrast(Color color, Color against, double min, Color target)
    {
        if (color.A < 255) color = Mix(Color.FromRgb(against.R, against.G, against.B), Color.FromRgb(color.R, color.G, color.B), color.A / 255.0);
        if (Contrast(color, against) >= min) return color;
        if (Contrast(target, against) < min) target = Lum(against) < 0.4 ? Colors.White : Colors.Black;
        for (int i = 1; i <= 40; i++)
        {
            var c = Mix(color, target, i / 40.0);
            if (Contrast(c, against) >= min) return c;
        }
        return target;
    }

    /// <summary>점검용: 보정 뒤 각 테마의 대비가 기준을 넘는지 (넘지 못한 것 목록).</summary>
    internal static List<string> ContrastReport()
    {
        var bad = new List<string>();
        foreach (var (name, t) in Defs)
        {
            var c = WithContrast(t.Colors, t.IsDark);
            foreach (var (key, against, min) in ContrastRules)
            {
                if (IsGradient(c[key]) || IsGradient(c[against]) || (!t.IsDark && DarkOnlyRules.Contains(key))) continue;
                double r = Contrast(ParseColor(c[key]), ParseColor(c[against]));
                if (r < min - 0.05) bad.Add($"{name}.{key} {r:0.0} < {min}");
            }
            double icon = Contrast(ParseColor(c["Icon"]), FirstColor(c["Panel"]));
            if (icon < 4.45) bad.Add($"{name}.Icon {icon:0.0}");
        }
        return bad;
    }

    /// <summary>설정 화면의 미리보기 색: 바탕, 카드, 강조, 글자.</summary>
    public static (Brush bg, Brush card, Brush accent, Brush text) Preview(string name)
    {
        var c = Defs[Resolve(name)].Colors;
        return (MakeBrush(c["Bg"]), MakeBrush(c["Card"]), MakeBrush(c["Accent"]), MakeBrush(c["Text"]));
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex.Trim());

    internal static Brush MakeBrush(string spec)
    {
        Brush b;
        if (spec.Length > 2 && spec[1] == ':')
        {
            var parts = spec[2..].Split(',');
            bool vertical = spec[0] == 'v';
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
            for (int i = 0; i < parts.Length; i++)
                g.GradientStops.Add(new GradientStop(ParseColor(parts[i]), parts.Length == 1 ? 0 : (double)i / (parts.Length - 1)));
            b = g;
        }
        else b = new SolidColorBrush(ParseColor(spec));
        b.Freeze();
        return b;
    }

    private static Brush Checker(string a, string b)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(MakeBrush(a), null, Geometry.Parse("M0,0 H16 V16 H0 Z")));
        group.Children.Add(new GeometryDrawing(MakeBrush(b), null, Geometry.Parse("M0,0 H8 V8 H0 Z M8,8 H16 V16 H8 Z")));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    /// <summary>점검용: 모든 테마의 색 이름이 다크와 같은 목록인지.</summary>
    internal static string? CheckDefinitions()
    {
        foreach (var (name, t) in Defs)
        {
            var missing = DarkColors.Keys.Except(t.Colors.Keys).ToList();
            if (missing.Count > 0) return $"{name}: missing {string.Join(", ", missing)}";
            foreach (var spec in t.Colors.Values) MakeBrush(spec);
        }
        return null;
    }
}
