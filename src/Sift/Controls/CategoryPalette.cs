using System.Windows;
using System.Windows.Media;
using Sift.Core;

namespace Sift.Controls;

/// <summary>Fixed, theme-independent colours for file categories (mid-tones that read on light and dark).</summary>
public static class CategoryPalette
{
    private static readonly Dictionary<FileCategory, Color> Colors = new()
    {
        [FileCategory.Video] = Color.FromRgb(0x81, 0x6C, 0xF5),
        [FileCategory.Audio] = Color.FromRgb(0xEC, 0x6F, 0xB3),
        [FileCategory.Image] = Color.FromRgb(0x3F, 0xBF, 0xAE),
        [FileCategory.Archive] = Color.FromRgb(0xE9, 0xA2, 0x3B),
        [FileCategory.DiskImage] = Color.FromRgb(0xD9, 0x7A, 0x4A),
        [FileCategory.Program] = Color.FromRgb(0x4C, 0x8D, 0xF0),
        [FileCategory.Document] = Color.FromRgb(0x5E, 0xB7, 0x5A),
        [FileCategory.Code] = Color.FromRgb(0x2C, 0xA8, 0xD6),
        [FileCategory.GameData] = Color.FromRgb(0xC2, 0x5B, 0xD9),
        [FileCategory.System] = Color.FromRgb(0x7D, 0x87, 0x9C),
        [FileCategory.Other] = Color.FromRgb(0x9C, 0xA3, 0xAF),
        [FileCategory.Folder] = Color.FromRgb(0x6B, 0x72, 0x80),
    };

    private static readonly Dictionary<FileCategory, Brush> Solid = new();
    private static readonly Dictionary<FileCategory, Brush> Cushion = new();

    public static Color ColorOf(FileCategory c) => Colors.TryGetValue(c, out var col) ? col : Colors[FileCategory.Other];

    public static Brush SolidBrush(FileCategory c)
    {
        if (!Solid.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(ColorOf(c));
            b.Freeze();
            Solid[c] = b;
        }
        return b;
    }

    /// <summary>Soft diagonal gradient that gives treemap cells a subtle "cushion" depth.</summary>
    public static Brush CushionBrush(FileCategory c)
    {
        if (!Cushion.TryGetValue(c, out var b))
        {
            var col = ColorOf(c);
            var g = new LinearGradientBrush(
                Themes.ThemeManager.Shift(col, 0.18),
                Themes.ThemeManager.Shift(col, -0.18),
                new Point(0, 0), new Point(1, 1));
            g.Freeze();
            b = g;
            Cushion[c] = b;
        }
        return b;
    }
}

public enum TreemapColorMode { Type, Age }

/// <summary>Age buckets for "colour by age": warm = recently changed, cool = untouched for years.</summary>
public static class AgePalette
{
    public static readonly (string Name, double MaxDays, Color Color)[] Buckets =
    {
        ("Last week", 7, Color.FromRgb(0xF2, 0x6D, 0x4F)),
        ("Last month", 31, Color.FromRgb(0xF0, 0x9A, 0x3E)),
        ("Last 6 months", 183, Color.FromRgb(0xD9, 0xC1, 0x45)),
        ("Last year", 366, Color.FromRgb(0x6D, 0xC0, 0x7A)),
        ("1–3 years", 1096, Color.FromRgb(0x3F, 0x9F, 0xC7)),
        ("Older", double.MaxValue, Color.FromRgb(0x5B, 0x6C, 0xD6)),
    };

    private static readonly Brush[] Solid = Buckets.Select(b => Frozen(new SolidColorBrush(b.Color))).ToArray();
    private static readonly Brush[] Cushion = Buckets.Select(b => Frozen(new LinearGradientBrush(
        Themes.ThemeManager.Shift(b.Color, 0.18), Themes.ThemeManager.Shift(b.Color, -0.18), new Point(0, 0), new Point(1, 1)))).ToArray();

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public static int BucketOf(long lastWriteTicks, long nowTicks)
    {
        if (lastWriteTicks <= 0) return Buckets.Length - 1;
        double days = (nowTicks - lastWriteTicks) / (double)TimeSpan.TicksPerDay;
        for (int i = 0; i < Buckets.Length; i++) if (days <= Buckets[i].MaxDays) return i;
        return Buckets.Length - 1;
    }

    public static Brush SolidBrush(int bucket) => Solid[Math.Clamp(bucket, 0, Solid.Length - 1)];
    public static Brush CushionBrush(int bucket) => Cushion[Math.Clamp(bucket, 0, Cushion.Length - 1)];
}
