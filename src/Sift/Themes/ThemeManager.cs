using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Sift.Core;

namespace Sift.Themes;

public static class ThemeManager
{
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Indigo", "#6366F1"),
        ("Sky", "#0EA5E9"),
        ("Teal", "#14B8A6"),
        ("Emerald", "#22C55E"),
        ("Amber", "#F59E0B"),
        ("Rose", "#F43F5E"),
        ("Violet", "#A855F7"),
        ("Slate", "#64748B"),
    };

    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    private static ThemeMode _mode;
    private static bool _watching;

    public static void Apply(ThemeMode mode, string accentHex)
    {
        _mode = mode;
        bool dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;

        var app = Application.Current;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Sift;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);

        ApplyAccent(accentHex);

        foreach (Window w in app.Windows) ApplyFrame(w);

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _mode == ThemeMode.System)
                    app.Dispatcher.InvokeAsync(() => Apply(_mode, CurrentAccentHex));
            };
        }

        ThemeChanged?.Invoke();
    }

    public static string CurrentAccentHex { get; private set; } = "#6366F1";

    public static void ApplyAccent(string hex)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); }
        catch { c = Color.FromRgb(0x63, 0x66, 0xF1); hex = "#6366F1"; }
        CurrentAccentHex = hex;

        var r = Application.Current.Resources;
        r["AccentColor"] = c;
        r["AccentBrush"] = Freeze(new SolidColorBrush(c));
        r["AccentHoverBrush"] = Freeze(new SolidColorBrush(Shift(c, IsDark ? 0.14 : -0.10)));
        r["AccentPressedBrush"] = Freeze(new SolidColorBrush(Shift(c, IsDark ? -0.08 : -0.18)));
        r["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x33 : (byte)0x22, c.R, c.G, c.B)));
        r["AccentTextBrush"] = Freeze(new SolidColorBrush(Luminance(c) > 0.6 ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White));
        r["AccentForegroundBrush"] = Freeze(new SolidColorBrush(IsDark ? Shift(c, 0.25) : Shift(c, -0.15)));
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
