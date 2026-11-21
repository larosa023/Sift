using System.Text.Json;
using System.Windows.Media;

namespace Sift.Themes;

/// <summary>A complete colour palette. Every theme defines the same keys; ThemeManager turns it into brushes.</summary>
public sealed class ThemeInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsDark { get; init; }
    public string Blurb { get; init; } = "";
    /// <summary>The theme's own accent ("#RRGGBB"), or null to use the Windows accent.</summary>
    public string? Accent { get; init; }
    /// <summary>Where a custom theme came from (a file path), for the gallery tooltip.</summary>
    public string? Source { get; init; }
    public required IReadOnlyDictionary<string, string> Colors { get; init; }

    public Color this[string key] => (Color)ColorConverter.ConvertFromString(Colors[key]);

    // Preview swatches for the theme gallery.
    public Brush PreviewBg => Brush("WindowBg");
    public Brush PreviewSidebar => Brush("SidebarBg");
    public Brush PreviewCard => Brush("CardBg");
    public Brush PreviewLine => Brush("LineBrush");
    public Brush PreviewText => Brush("TextBrush");
    public Brush PreviewDim => Brush("TextFaintBrush");
    public Brush PreviewAccent
    {
        get
        {
            var b = new SolidColorBrush(Accent is null ? ThemeManager.AccentColor(ThemeManager.WindowsAccent) : (Color)ColorConverter.ConvertFromString(Accent));
            b.Freeze();
            return b;
        }
    }

    private Brush Brush(string key)
    {
        var b = new SolidColorBrush(this[key]);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Sift's themes. The presets are the same as Hangar's (larosa023/Hangar, docs/THEMES.md) so every app in the
/// suite looks alike; Mocha and High contrast are Sift extras. Custom themes use Hangar's JSON theme format.
/// </summary>
public static class ThemeCatalog
{
    public const string SystemId = "system";
    public const string HangarId = "hangar";

    private static readonly string[] Keys =
    {
        "WindowBg", "SidebarBg", "SurfaceBg", "SurfaceAltBg", "CardBg", "PopupBg", "LineBrush", "LineStrongBrush",
        "TextBrush", "TextDimBrush", "TextFaintBrush", "HoverBrush", "PressedBrush", "ScrollThumbBrush", "TrackBrush",
        "TreemapBgBrush", "TreemapFolderBrush", "TreemapFolderLineBrush", "TreemapLabelBrush",
    };

    // Status colours shared by every dark / light theme (soft variants are derived with alpha).
    private static readonly (string Danger, string Warning, string Success) DarkStatus = ("#F87171", "#FBBF24", "#34D399");
    private static readonly (string Danger, string Warning, string Success) LightStatus = ("#DC2626", "#B45309", "#059669");

    private static Dictionary<string, string> WithStatus(Dictionary<string, string> map, bool dark, (string Danger, string Warning, string Success)? status)
    {
        var s = status ?? (dark ? DarkStatus : LightStatus);
        string alpha = dark ? "33" : "1F";
        map["DangerBrush"] = s.Danger; map["DangerSoftBrush"] = "#" + alpha + s.Danger[1..];
        map["WarningBrush"] = s.Warning; map["WarningSoftBrush"] = "#" + alpha + s.Warning[1..];
        map["SuccessBrush"] = s.Success; map["SuccessSoftBrush"] = "#" + alpha + s.Success[1..];
        return map;
    }

    /// <summary>A theme with every key spelled out.</summary>
    private static ThemeInfo Make(string id, string name, bool dark, string blurb, string[] values, string? accent = null,
        (string Danger, string Warning, string Success)? status = null)
    {
        if (values.Length != Keys.Length) throw new ArgumentException($"Theme {id} has {values.Length} colours, expected {Keys.Length}");
        var map = new Dictionary<string, string>();
        for (int i = 0; i < Keys.Length; i++) map[Keys[i]] = values[i];
        return new ThemeInfo { Id = id, Name = name, IsDark = dark, Blurb = blurb, Accent = accent, Colors = WithStatus(map, dark, status) };
    }

    /// <summary>
    /// A theme from Hangar's nine colours; borders, inputs and the treemap colours are derived from them.
    /// <paramref name="surfaceOpacity"/> &lt; 1 blends see-through surfaces (Hangar's Glass) onto the background.
    /// </summary>
    public static ThemeInfo FromPalette(string id, string name, string blurb, bool dark,
        uint bg, uint sidebar, uint surface, uint hover, uint strong, uint text, uint subtext, uint faint, uint accent,
        double surfaceOpacity = 1.0, string? source = null)
    {
        if (surfaceOpacity < 1)
        {
            surface = Mix(bg, surface, surfaceOpacity);
            hover = Mix(bg, hover, Math.Min(1, surfaceOpacity * 1.8));
            strong = Mix(bg, strong, Math.Min(1, surfaceOpacity * 2.6));
        }
        var map = new Dictionary<string, string>
        {
            ["WindowBg"] = Hex(bg),
            ["SidebarBg"] = Hex(sidebar),
            ["SurfaceBg"] = Hex(surface),
            ["SurfaceAltBg"] = Hex(dark ? hover : surface),
            ["CardBg"] = Hex(surface),
            ["PopupBg"] = Hex(dark ? hover : surface),
            ["LineBrush"] = Hex(Mix(bg, text, dark ? 0.10 : 0.11)),
            ["LineStrongBrush"] = Hex(Mix(bg, text, dark ? 0.19 : 0.20)),
            ["TextBrush"] = Hex(text),
            ["TextDimBrush"] = Hex(subtext),
            ["TextFaintBrush"] = Hex(faint),
            ["HoverBrush"] = Hex(dark ? hover : Mix(surface, text, 0.06)),
            ["PressedBrush"] = Hex(dark ? strong : Mix(surface, text, 0.11)),
            ["ScrollThumbBrush"] = Hex(Mix(bg, text, dark ? 0.24 : 0.22)),
            ["TrackBrush"] = Hex(dark ? strong : Mix(bg, text, 0.08)),
            ["TreemapBgBrush"] = Hex(dark ? sidebar : Mix(bg, text, 0.06)),
            ["TreemapFolderBrush"] = Hex(dark ? surface : Mix(bg, surface, 0.6)),
            ["TreemapFolderLineBrush"] = Hex(dark ? sidebar : Mix(bg, text, 0.18)),
            ["TreemapLabelBrush"] = Hex(subtext),
        };
        return new ThemeInfo
        {
            Id = id, Name = name, Blurb = blurb, IsDark = dark, Source = source,
            Accent = accent == 0 ? null : Hex(accent),
            Colors = WithStatus(map, dark, null),
        };
    }

    // Hangar's presets (src/themes.cpp), same order and colours.
    //                                                               bg        sidebar   surface   hover     strong    text      subtext   faint     accent
    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        FromPalette("dark", "Dark", "The default", true,             0x16161A, 0x111114, 0x222228, 0x2A2A31, 0x34343C, 0xF2F2F5, 0xA8A8B3, 0x6E6E7A, 0),
        FromPalette("light", "Light", "Bright and clean", false,     0xF5F5F7, 0xEBEBEF, 0xFFFFFF, 0xF7F7FA, 0xE6E6EC, 0x16161A, 0x5C5C68, 0x8E8E9A, 0),
        FromPalette("midnight", "Midnight", "Pure black for OLED screens", true,
                                                                     0x000000, 0x000000, 0x0E0E10, 0x16161A, 0x202025, 0xF5F5F7, 0xA0A0AA, 0x64646E, 0x7AA2FF),
        FromPalette("frost", "Frost", "Cool arctic blues", true,     0x2E3440, 0x272C36, 0x3B4252, 0x434C5E, 0x4C566A, 0xECEFF4, 0xC0C8D6, 0x7B8698, 0x88C0D0),
        FromPalette("twilight", "Twilight", "Deep violet", true,     0x1E1B2E, 0x19162A, 0x2A2640, 0x332E4D, 0x3E3859, 0xEEEAF7, 0xB4ADC9, 0x7D7596, 0xB794F6),
        FromPalette("forest", "Forest", "Evergreen and calm", true,  0x141B17, 0x101612, 0x1D2721, 0x24302A, 0x2E3B33, 0xE8F0EA, 0xA7B8AD, 0x6C7F72, 0x5FD08B),
        FromPalette("sunset", "Sunset", "Warm and cozy", true,       0x1C1614, 0x17110F, 0x2A201C, 0x332722, 0x3E302A, 0xF6EEE9, 0xC4B3A9, 0x8B776C, 0xFF8A4C),
        FromPalette("ocean", "Ocean", "Deep teal", true,             0x0F1A20, 0x0B151A, 0x16252D, 0x1C2E37, 0x243843, 0xE6F1F5, 0xA3BAC4, 0x66808C, 0x3CC8C8),
        FromPalette("rose", "Rose", "Soft pink on plum", true,       0x1D1519, 0x181115, 0x2A1F25, 0x33262D, 0x3E2E37, 0xF7ECF1, 0xC7AFBA, 0x8D7380, 0xFF7AA8),
        FromPalette("graphite", "Graphite", "Monochrome, no color", true,
                                                                     0x1A1A1A, 0x141414, 0x262626, 0x2E2E2E, 0x3A3A3A, 0xF0F0F0, 0xABABAB, 0x707070, 0xE6E6E6),
        FromPalette("paper", "Paper", "Warm light, easy on the eyes", false,
                                                                     0xF4EFE6, 0xEAE3D6, 0xFBF8F2, 0xF6F1E7, 0xE5DCCB, 0x2B241B, 0x6B5F50, 0x9A8E7E, 0xB5651D),
        FromPalette("mist", "Mist", "Cool light gray-blue", false,   0xEEF2F6, 0xE3E9EF, 0xFAFCFE, 0xF3F7FA, 0xDCE4EC, 0x17202A, 0x56636F, 0x8A97A3, 0x2F7DD1),
        //                       Window     Sidebar    Surface    SurfAlt    Card       Popup      Line       LineStrong Text       TextDim    TextFaint  Hover      Pressed    Scroll     Track      TmBg       TmFolder   TmLine     TmLabel
        Make("mocha", "Mocha", true, "Warm, coffee-toned dark",
            new[] { "#1C1715", "#161210", "#241D1A", "#2C2420", "#241D1A", "#322925", "#3A2F2A", "#4A3C35", "#F2E9E4", "#B8A69C", "#85746B", "#322925", "#3B302B", "#54453E", "#2F2622", "#161210", "#2A221E", "#161210", "#E2D3CB" },
            accent: "#D9A066"),
        Make("contrast", "High contrast", true, "Maximum legibility",
            new[] { "#000000", "#000000", "#0A0A0A", "#111111", "#0A0A0A", "#111111", "#6A6A6A", "#9A9A9A", "#FFFFFF", "#E6E6E6", "#C8C8C8", "#222222", "#333333", "#9A9A9A", "#333333", "#000000", "#141414", "#000000", "#FFFFFF" },
            accent: "#FFD43B", status: ("#FF6B6B", "#FFD43B", "#51CF66")),
    };

    /// <summary>
    /// Sift 1.1 had its own presets; 1.2 adopts Hangar's. Map old ids to the closest new look (runs once).
    /// </summary>
    public static string MigrateFrom11(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "" => SystemId,
        "fjord" => "frost",   // 1.1's Fjord is Hangar's Frost (Nord blues)
        "frost" => "mist",    // 1.1's Frost was a light blue-grey, like Hangar's Mist
        "midnight" => "twilight", // 1.1's Midnight was deep navy, not OLED black
        var other => other,
    };

    public static ThemeInfo Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    // ------------------------------------------------------------------ custom theme files

    public static string SiftThemesFolder => Path.Combine(Core.AppSettings.Folder, "themes");
    public static string HangarFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangar");
    public static string HangarThemesFolder => Path.Combine(HangarFolder, "themes");

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Theme files from Sift's and Hangar's themes folders (Hangar's JSON theme format).</summary>
    public static List<ThemeInfo> LoadCustom()
    {
        var list = new List<ThemeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { SiftThemesFolder, HangarThemesFolder })
        {
            foreach (var file in Core.FsUtil.SafeFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var t = LoadThemeFile(file);
                if (t is not null && seen.Add(t.Id)) list.Add(t);
            }
        }
        return list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static ThemeInfo? LoadThemeFile(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            var r = doc.RootElement;
            var name = r.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(name)) return null;
            var idPart = r.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 } explicitId ? explicitId : name;
            bool dark = !(r.TryGetProperty("mode", out var m) && string.Equals(m.GetString(), "light", StringComparison.OrdinalIgnoreCase));
            double opacity = r.TryGetProperty("surfaceOpacity", out var so) && so.TryGetDouble(out var sov) ? Math.Clamp(sov, 0.02, 1) : 1;
            var desc = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            // Missing colours come from Dark or Light, as in Hangar.
            var baseT = dark
                ? (bg: 0x16161Au, sidebar: 0x111114u, surface: 0x222228u, hover: 0x2A2A31u, strong: 0x34343Cu, text: 0xF2F2F5u, subtext: 0xA8A8B3u, faint: 0x6E6E7Au)
                : (bg: 0xF5F5F7u, sidebar: 0xEBEBEFu, surface: 0xFFFFFFu, hover: 0xF7F7FAu, strong: 0xE6E6ECu, text: 0x16161Au, subtext: 0x5C5C68u, faint: 0x8E8E9Au);
            uint accent = 0;
            JsonElement colors = r.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            uint Get(string key, uint fallback) =>
                colors.ValueKind == JsonValueKind.Object && colors.TryGetProperty(key, out var v) && ParseHex(v.GetString(), out var x) ? x : fallback;
            if (colors.ValueKind == JsonValueKind.Object && colors.TryGetProperty("accent", out var a) && ParseHex(a.GetString(), out var ax)) accent = ax;

            return FromPalette("custom:" + idPart.Trim().ToLowerInvariant(), name, desc.Length > 0 ? desc : Path.GetFileName(file), dark,
                Get("background", baseT.bg), Get("sidebar", baseT.sidebar), Get("surface", baseT.surface), Get("surfaceHover", baseT.hover),
                Get("surfaceStrong", baseT.strong), Get("text", baseT.text), Get("subtext", baseT.subtext), Get("faint", baseT.faint),
                accent, opacity, file);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ following Hangar

    public static bool HangarInstalled => File.Exists(Path.Combine(HangarFolder, "settings.json"));

    /// <summary>Hangar's current theme and accent setting, from its settings.json.</summary>
    public static (ThemeInfo Theme, string Accent)? ReadHangar()
    {
        try
        {
            var file = Path.Combine(HangarFolder, "settings.json");
            if (!File.Exists(file)) return null;
            string json;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json, JsonOptions);
            var r = doc.RootElement;
            var themeId = r.TryGetProperty("theme", out var t) ? t.GetString() ?? "dark" : "dark";
            var accent = r.TryGetProperty("accent", out var a) ? a.GetString() ?? "theme" : "theme";

            ThemeInfo theme;
            if (themeId == "system") theme = Find(ThemeManager.SystemPrefersDark() ? "dark" : "light");
            else if (themeId.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                theme = LoadCustom().FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? Find("dark");
            else if (themeId == "glass")
                theme = FromPalette("glass", "Glass", "", true, 0x141418, 0x0E0E12, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xF7F7FA, 0xC2C2CC, 0x8A8A96, 0, 0.07);
            else theme = Find(themeId);
            return (theme, accent);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ colour helpers

    private static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");

    private static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    public static bool ParseHex(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
        return s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
