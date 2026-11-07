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
