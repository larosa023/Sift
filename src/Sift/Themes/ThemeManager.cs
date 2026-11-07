using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Sift.Core;

namespace Sift.Themes;

public static class ThemeManager
{
    public const string WindowsAccent = "windows";

    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Windows accent", WindowsAccent),
        ("Indigo", "#6366F1"),
        ("Sky", "#0EA5E9"),
        ("Teal", "#14B8A6"),
        ("Emerald", "#22C55E"),
        ("Amber", "#F59E0B"),
        ("Orange", "#F97316"),
        ("Rose", "#F43F5E"),
        ("Violet", "#A855F7"),
        ("Slate", "#64748B"),
    };

    public static bool IsDark { get; private set; }
    public static ThemeInfo Current { get; private set; } = ThemeCatalog.Find("dark");
    public static string CurrentThemeId { get; private set; } = ThemeCatalog.SystemId;
    public static string CurrentAccent { get; private set; } = "#6366F1";
    public static event Action? ThemeChanged;

    private static bool _watching;

    /// <param name="themeId">A ThemeCatalog id, or "system" to follow Windows light/dark.</param>
    /// <param name="accent">A hex colour, or "windows" for the Windows accent colour.</param>
    public static void Apply(string themeId, string accent)
    {
        CurrentThemeId = string.IsNullOrWhiteSpace(themeId) ? ThemeCatalog.SystemId : themeId.Trim().ToLowerInvariant();
        CurrentAccent = string.IsNullOrWhiteSpace(accent) ? "#6366F1" : accent;

        var theme = Resolve(CurrentThemeId);
        Current = theme;
        IsDark = theme.IsDark;

        var dict = new ResourceDictionary();
        foreach (var (key, hex) in theme.Colors)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            dict[key] = b;
        }

        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);

        ApplyAccent(CurrentAccent);
        foreach (Window w in app.Windows) ApplyFrame(w);

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                bool followsSystem = CurrentThemeId == ThemeCatalog.SystemId || CurrentAccent == WindowsAccent;
                if (followsSystem && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                    app.Dispatcher.InvokeAsync(() => Apply(CurrentThemeId, CurrentAccent));
            };
        }

        ThemeChanged?.Invoke();
    }

    public static ThemeInfo Resolve(string id) =>
        id == ThemeCatalog.SystemId ? ThemeCatalog.Find(SystemPrefersDark() ? "dark" : "light") : ThemeCatalog.Find(id);

    public static Color AccentColor(string accent)
    {
        if (accent == WindowsAccent) return WindowsAccentColor() ?? Color.FromRgb(0x00, 0x78, 0xD4);
        try { return (Color)ColorConverter.ConvertFromString(accent); }
        catch { return Color.FromRgb(0x63, 0x66, 0xF1); }
    }

    private static void ApplyAccent(string accent)
    {
        var c = AccentColor(accent);
        var r = Application.Current.Resources;
        r["AccentColor"] = c;
        r["AccentBrush"] = Freeze(new SolidColorBrush(c));
        r["AccentHoverBrush"] = Freeze(new SolidColorBrush(Shift(c, IsDark ? 0.14 : -0.10)));
        r["AccentPressedBrush"] = Freeze(new SolidColorBrush(Shift(c, IsDark ? -0.08 : -0.18)));
        r["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x33 : (byte)0x22, c.R, c.G, c.B)));
        r["AccentTextBrush"] = Freeze(new SolidColorBrush(Luminance(c) > 0.6 ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White));
        r["AccentForegroundBrush"] = Freeze(new SolidColorBrush(IsDark ? Shift(c, 0.25) : Shift(c, -0.15)));
    }

    /// <summary>The user's Windows accent colour (Settings › Personalization › Colors).</summary>
    public static Color? WindowsAccentColor()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
            {
                uint v = unchecked((uint)abgr);
                return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static void ApplyFrame(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetDarkFrame(hwnd, IsDark);
        Native.SetRoundedCorners(hwnd);
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Lighten (positive) or darken (negative) a color.</summary>
    public static Color Shift(Color c, double amount)
    {
        byte F(byte v) => amount >= 0
            ? (byte)Math.Clamp(v + (255 - v) * amount, 0, 255)
            : (byte)Math.Clamp(v * (1 + amount), 0, 255);
        return Color.FromArgb(c.A, F(c.R), F(c.G), F(c.B));
    }
}
