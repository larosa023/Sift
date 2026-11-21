using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Sift.Core;

namespace Sift.Themes;

public static class ThemeManager
{
    public const string WindowsAccent = "windows";
    public const string ThemeAccent = "theme";

    /// <summary>Hangar's accent swatches, so a colour picked in one app exists in all of them.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Theme's own", ThemeAccent),
        ("Windows accent", WindowsAccent),
        ("Blue", "#4F8CFF"),
        ("Violet", "#9B6CF2"),
        ("Pink", "#E86A92"),
        ("Red", "#FF5D5D"),
        ("Orange", "#FF8A4C"),
        ("Gold", "#F2B84B"),
        ("Green", "#3FB98E"),
        ("Teal", "#2EC4C4"),
        ("Gray", "#A0A0AA"),
    };

    /// <summary>Accent actually in use (after resolving "theme", "windows" or Hangar's choice).</summary>
    public static string EffectiveAccent { get; private set; } = ThemeAccent;

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
        CurrentAccent = string.IsNullOrWhiteSpace(accent) ? ThemeAccent : accent;

        ThemeInfo theme;
        string effectiveAccent = CurrentAccent;
        if (CurrentThemeId == ThemeCatalog.HangarId && ThemeCatalog.ReadHangar() is { } h)
        {
            theme = h.Theme;
            effectiveAccent = h.Accent; // Hangar decides the accent too
        }
        else theme = Resolve(CurrentThemeId);
        Current = theme;
        IsDark = theme.IsDark;
        EffectiveAccent = effectiveAccent;

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

        ApplyAccent(effectiveAccent);
        WatchHangar(CurrentThemeId == ThemeCatalog.HangarId);
        foreach (Window w in app.Windows) ApplyFrame(w);

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                bool followsSystem = CurrentThemeId is ThemeCatalog.SystemId or ThemeCatalog.HangarId
                                     || EffectiveAccent is WindowsAccent or ThemeAccent;
                if (followsSystem && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                    app.Dispatcher.InvokeAsync(() => Apply(CurrentThemeId, CurrentAccent));
            };
        }

        ThemeChanged?.Invoke();
    }

    public static ThemeInfo Resolve(string id)
    {
        if (id == ThemeCatalog.SystemId) return ThemeCatalog.Find(SystemPrefersDark() ? "dark" : "light");
        if (id == ThemeCatalog.HangarId) return ThemeCatalog.ReadHangar()?.Theme ?? ThemeCatalog.Find("dark");
        if (id.StartsWith("custom:", StringComparison.Ordinal))
            return ThemeCatalog.LoadCustom().FirstOrDefault(t => t.Id == id) ?? ThemeCatalog.Find("dark");
        return ThemeCatalog.Find(id);
    }

    public static Color AccentColor(string accent)
    {
        if (accent == ThemeAccent) accent = Current.Accent ?? WindowsAccent;
        if (accent == WindowsAccent) return WindowsAccentColor() ?? Color.FromRgb(0x00, 0x78, 0xD4);
        try { return (Color)ColorConverter.ConvertFromString(accent); }
        catch { return Color.FromRgb(0x63, 0x66, 0xF1); }
    }

    private static void ApplyAccent(string accent)
    {
        var c = Readable(AccentColor(accent));
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

    /// <summary>Keeps an accent visible against the theme (as Hangar does).</summary>
    private static Color Readable(Color c)
    {
        double l = Luminance(c);
        if (IsDark && l < 0.30) return Shift(c, 0.35);
        if (!IsDark && l > 0.75) return Shift(c, -0.35);
        return c;
    }

    // ------------------------------------------------------------------ live-follow Hangar
    private static FileSystemWatcher? _hangarWatcher;
    private static System.Windows.Threading.DispatcherTimer? _hangarDebounce;

    private static void WatchHangar(bool on)
    {
        if (!on)
        {
            _hangarWatcher?.Dispose();
            _hangarWatcher = null;
            return;
        }
        if (_hangarWatcher is not null || !Directory.Exists(ThemeCatalog.HangarFolder)) return;
        var app = Application.Current;
        _hangarDebounce ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300),
            System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                _hangarDebounce!.Stop();
                if (CurrentThemeId == ThemeCatalog.HangarId) Apply(CurrentThemeId, CurrentAccent);
            }, app.Dispatcher);
        _hangarDebounce.Stop();
        try
        {
            _hangarWatcher = new FileSystemWatcher(ThemeCatalog.HangarFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, e) =>
            {
                if (e.Name is null || !(e.Name.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("themes", StringComparison.OrdinalIgnoreCase))) return;
                app.Dispatcher.InvokeAsync(() => { _hangarDebounce!.Stop(); _hangarDebounce.Start(); });
            };
            _hangarWatcher.Changed += changed;
            _hangarWatcher.Created += changed;
            _hangarWatcher.Renamed += (s, e) => changed(s, e);
        }
        catch { _hangarWatcher = null; }
    }

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
