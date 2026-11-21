using Microsoft.Win32;

namespace Sift.Core;

/// <summary>
/// "Analyze with Sift" on the right-click menu of folders, drives and folder backgrounds in File Explorer.
/// Per-user (HKCU), so no administrator rights are needed. On Windows 11 it appears under "Show more options".
/// </summary>
public static class ExplorerMenu
{
    private const string Verb = "Sift.Analyze";
    private static readonly string[] Roots =
    {
        @"Software\Classes\Directory\shell\" + Verb,
        @"Software\Classes\Drive\shell\" + Verb,
        @"Software\Classes\Directory\Background\shell\" + Verb,
    };

    public static bool IsInstalled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Roots[0] + @"\command");
            return k?.GetValue(null) is string cmd && cmd.Contains("Sift", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Adds (or refreshes, if Sift moved) the menu entries.</summary>
    public static void Install()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        foreach (var root in Roots)
        {
            using var k = Registry.CurrentUser.CreateSubKey(root);
            k.SetValue(null, "Analyze with Sift");
            k.SetValue("Icon", $"\"{exe}\",0");
            using var c = k.CreateSubKey("command");
            c.SetValue(null, $"\"{exe}\" \"%V\"");
        }
    }

    public static void Remove()
    {
        foreach (var root in Roots)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); } catch { /* ignore */ }
        }
    }
}
