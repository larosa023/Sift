using System.Windows.Media;

namespace Sift.Themes;

/// <summary>A complete colour palette. Every theme defines the same keys; ThemeManager turns it into brushes.</summary>
public sealed class ThemeInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsDark { get; init; }
    public string Blurb { get; init; } = "";
    public required IReadOnlyDictionary<string, string> Colors { get; init; }

    public Color this[string key] => (Color)ColorConverter.ConvertFromString(Colors[key]);

    // Preview swatches for the theme gallery.
    public Brush PreviewBg => Brush("WindowBg");
    public Brush PreviewSidebar => Brush("SidebarBg");
    public Brush PreviewCard => Brush("CardBg");
    public Brush PreviewLine => Brush("LineBrush");
    public Brush PreviewText => Brush("TextBrush");
    public Brush PreviewDim => Brush("TextFaintBrush");

    private Brush Brush(string key)
    {
        var b = new SolidColorBrush(this[key]);
        b.Freeze();
        return b;
    }
}

public static class ThemeCatalog
{
    public const string SystemId = "system";

    private static readonly string[] Keys =
    {
        "WindowBg", "SidebarBg", "SurfaceBg", "SurfaceAltBg", "CardBg", "PopupBg", "LineBrush", "LineStrongBrush",
        "TextBrush", "TextDimBrush", "TextFaintBrush", "HoverBrush", "PressedBrush", "ScrollThumbBrush", "TrackBrush",
        "TreemapBgBrush", "TreemapFolderBrush", "TreemapFolderLineBrush", "TreemapLabelBrush",
    };

    // Status colours shared by every dark / light theme (soft variants are derived with alpha).
    private static readonly (string Danger, string Warning, string Success) DarkStatus = ("#F87171", "#FBBF24", "#34D399");
    private static readonly (string Danger, string Warning, string Success) LightStatus = ("#DC2626", "#B45309", "#059669");

    private static ThemeInfo Make(string id, string name, bool dark, string blurb, string[] values,
        (string Danger, string Warning, string Success)? status = null)
    {
        if (values.Length != Keys.Length) throw new ArgumentException($"Theme {id} has {values.Length} colours, expected {Keys.Length}");
        var map = new Dictionary<string, string>();
        for (int i = 0; i < Keys.Length; i++) map[Keys[i]] = values[i];
        var s = status ?? (dark ? DarkStatus : LightStatus);
        string alpha = dark ? "33" : "1F";
        map["DangerBrush"] = s.Danger; map["DangerSoftBrush"] = "#" + alpha + s.Danger[1..];
        map["WarningBrush"] = s.Warning; map["WarningSoftBrush"] = "#" + alpha + s.Warning[1..];
        map["SuccessBrush"] = s.Success; map["SuccessSoftBrush"] = "#" + alpha + s.Success[1..];
        return new ThemeInfo { Id = id, Name = name, IsDark = dark, Blurb = blurb, Colors = map };
    }

    //                          Window     Sidebar    Surface    SurfAlt    Card       Popup      Line       LineStrong Text       TextDim    TextFaint  Hover      Pressed    Scroll     Track      TmBg       TmFolder   TmLine     TmLabel
    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        Make("light", "Light", false, "Clean and bright",
            new[] { "#F6F7FA", "#EEF0F5", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#E3E6EE", "#CDD2DE", "#161A24", "#5B6275", "#8B92A5", "#EDEFF5", "#E2E5EE", "#C3C8D4", "#E7E9F0", "#E9ECF2", "#F3F5F9", "#C9CEDA", "#3A4152" }),
        Make("dark", "Dark", true, "The default dark look",
            new[] { "#0F1117", "#0B0D12", "#151822", "#1C202C", "#171A24", "#1E2230", "#262B3A", "#343A4D", "#E7E9F0", "#9AA1B5", "#6B7287", "#222736", "#2A3042", "#3A4055", "#232837", "#0B0D12", "#1A1E2A", "#0B0D12", "#C7CCDA" }),
        Make("midnight", "Midnight", true, "Deep navy, easy on the eyes at night",
            new[] { "#0A0E1A", "#070A13", "#0F1526", "#141B30", "#0F1526", "#18203A", "#1E2742", "#2B3657", "#E3E8F5", "#93A0BF", "#63708F", "#18213A", "#202B4A", "#2E3A5C", "#1A2340", "#070A13", "#121A2E", "#070A13", "#C3CCE4" }),
        Make("graphite", "Graphite", true, "Neutral grey with no colour cast",
            new[] { "#18181B", "#131316", "#1F1F23", "#26262B", "#1F1F23", "#2A2A30", "#2E2E34", "#3C3C44", "#EDEDF0", "#A1A1AA", "#71717A", "#2A2A30", "#33333A", "#45454D", "#2A2A30", "#131316", "#222226", "#131316", "#D4D4D8" }),
        Make("mocha", "Mocha", true, "Warm, coffee-toned dark",
            new[] { "#1C1715", "#161210", "#241D1A", "#2C2420", "#241D1A", "#322925", "#3A2F2A", "#4A3C35", "#F2E9E4", "#B8A69C", "#85746B", "#322925", "#3B302B", "#54453E", "#2F2622", "#161210", "#2A221E", "#161210", "#E2D3CB" }),
        Make("forest", "Forest", true, "Dark with a soft green tint",
            new[] { "#0F1714", "#0B120F", "#14201B", "#192821", "#14201B", "#1D2E26", "#22352C", "#2E463A", "#E4F0EA", "#96B0A3", "#66806F", "#1C2C24", "#23362D", "#34503F", "#1B2A23", "#0B120F", "#17241E", "#0B120F", "#C5DACF" }),
        Make("fjord", "Fjord", true, "Cool slate blues, lower contrast",
            new[] { "#2B303B", "#262A34", "#323845", "#383F4D", "#323845", "#3B4252", "#434C5E", "#4C566A", "#ECEFF4", "#B4BCCB", "#8790A3", "#3B4252", "#434C5E", "#55607A", "#3B4252", "#262A34", "#353B48", "#262A34", "#D8DEE9" },
            ("#E07A80", "#EBCB8B", "#A3BE8C")),
        Make("paper", "Paper", false, "Warm cream, like a notebook",
            new[] { "#F7F3EC", "#EFE9DF", "#FFFDF9", "#FFFDF9", "#FFFDF9", "#FFFDF9", "#E5DDCF", "#D2C7B5", "#2A241C", "#6B6052", "#988B7A", "#EFE8DC", "#E6DDCD", "#CFC3B0", "#EAE2D4", "#EAE3D6", "#F6F1E8", "#D2C7B5", "#4A4034" }),
        Make("frost", "Frost", false, "Crisp light blue-grey",
            new[] { "#F1F5FA", "#E7EEF6", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#DCE4EF", "#C5D1E1", "#142033", "#536079", "#8592A8", "#E8EEF6", "#DCE5F0", "#BCC8D9", "#E1E8F1", "#E3EAF3", "#F2F6FB", "#C5D1E1", "#34425A" }),
        Make("contrast", "High contrast", true, "Maximum legibility",
            new[] { "#000000", "#000000", "#0A0A0A", "#111111", "#0A0A0A", "#111111", "#6A6A6A", "#9A9A9A", "#FFFFFF", "#E6E6E6", "#C8C8C8", "#222222", "#333333", "#9A9A9A", "#333333", "#000000", "#141414", "#000000", "#FFFFFF" },
            ("#FF6B6B", "#FFD43B", "#51CF66")),
    };

    public static ThemeInfo Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[1];
}
