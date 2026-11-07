using System.Diagnostics;
using Microsoft.Win32;

namespace Sift.Core.Finders;

/// <summary>
/// Folders in Program Files / ProgramData / AppData that don't belong to any installed app
/// and haven't been touched in a long time — typical remains of uninstalled software.
/// Matching is deliberately conservative and every result is marked for review.
/// </summary>
public sealed class LeftoverFinder : ICleanupFinder
{
    public string Id => "leftovers";
    public string Title => "Leftover app folders";
    public string Description => "Folders in Program Files, ProgramData and AppData that don't match any installed app and haven't changed in months. Check each one — portable apps can look like leftovers.";
    public string Glyph => "";
    public bool Grouped => false;
    public bool NeedsScan => false;

    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Package Cache", "Packages", "Temp", "WindowsApps", "ModifiableWindowsApps",
        "Internet Explorer", "Reference Assemblies", "MSBuild", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security",
        "Windows Sidebar", "WindowsPowerShell", "Uninstall Information", "PackageManagement", "Comms", "ConnectedDevicesPlatform",
        "D3DSCache", "CrashDumps", "Programs", "Publishers", "Application Data", "History", "Temporary Internet Files",
        "dotnet", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Realtek", "Dell", "HP", "Lenovo", "ASUS", "Synaptics",
        "Adobe", "Google", "Mozilla", "Steam", "Epic", "Origin", "EA", "Ubisoft", "Battle.net", "Blizzard Entertainment",
        "regid.1991-06.com.microsoft", "ssh", "USOPrivate", "USOShared", "SoftwareDistribution", "Desktop", "Documents", "Start Menu",
        "Templates", "Favorites", "IsolatedStorage", "PeerDistRepub", "Sift", "npm", "npm-cache", "pip", "NuGet", "Yarn", "Code",
        "JetBrains", "Docker", "Git", "GitHub", "GitHubDesktop", "Python", "Programs", "Discord", "Spotify", "Zoom", "Slack",
        "VirtualStore", "Apps", "assembly", "SquirrelTemp", "fontconfig", "CEF", "cache", "Diagnostics", "PlaceholderTileLogoFolder",
    };

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        ctx.Status?.Report("Reading installed apps…");
        var installed = InstalledApps();
        var names = installed.Names;
        var locations = installed.Locations;

        // Folders that hold a running process are obviously in use.
        var running = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainModule?.FileName is string f) running.Add(f); }
            catch { /* access denied / exited */ }
            finally { p.Dispose(); }
        }

        var roots = new List<(string Path, string Label)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppData\\Local"),
        };
        var lowLocal = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "", "LocalLow");
        roots.Add((lowLocal, "AppData\\LocalLow"));

        var cutoff = DateTime.UtcNow.AddDays(-ctx.Settings.LeftoverMinAgeDays);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CleanupItem>();

        foreach (var (root, label) in roots)
        {
            if (string.IsNullOrEmpty(root) || !seen.Add(root)) continue;
            foreach (var dir in FsUtil.SafeDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (Ignore.Contains(name) || name.StartsWith('.') || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith('{')) continue;

                if (locations.Any(l => FsUtil.IsUnder(l, dir) || FsUtil.IsUnder(dir, l))) continue;
                if (MatchesInstalled(name, names)) continue;
                if (running.Any(r => FsUtil.IsUnder(r, dir))) continue;

                ctx.Status?.Report($"Checking {label}\\{name}…");
                var m = FsUtil.Measure(dir, ct);
                if (m.Size < 1024 * 1024) continue;
                if (m.NewestWriteUtc > cutoff) continue;

                result.Add(new CleanupItem
                {
                    Path = dir,
                    Title = name,
                    Detail = label,
                    Size = m.Size,
                    LastUsedUtc = m.NewestWriteUtc,
                    LastUsedLabel = "Last changed",
                    Risk = label.StartsWith("Program", StringComparison.Ordinal) ? Risk.Caution : Risk.Review,
                    Source = "Leftover",
                    Glyph = "",
                });
            }
        }
        return result.OrderByDescending(r => r.Size).ToList();
    }, ct);

    private sealed record Installed(List<string> Names, List<string> Locations);

    private static Installed InstalledApps()
    {
        var names = new List<string>();
        var locations = new List<string>();

        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var un = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un is null) return;
                foreach (var sub in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(sub);
                    if (k is null) continue;
                    if (k.GetValue("DisplayName") is string dn && dn.Length > 0) names.Add(Normalize(dn));
                    if (k.GetValue("Publisher") is string pub && pub.Length > 0) names.Add(Normalize(pub));
                    names.Add(Normalize(sub));
                    foreach (var v in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                    {
                        if (k.GetValue(v) is not string raw || raw.Length < 4) continue;
                        var path = ExtractPath(raw);
                        if (path is null) continue;
                        if (v != "InstallLocation") path = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(path)) locations.Add(path.TrimEnd('\\'));
                    }
                }
            }
            catch { /* ignore */ }
        }

        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64);

        // Registered software vendors (HKLM\SOFTWARE\<Vendor>) catch many apps without uninstall entries.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var sw = bk.OpenSubKey("SOFTWARE");
                if (sw is not null) names.AddRange(sw.GetSubKeyNames().Select(Normalize));
            }
            catch { /* ignore */ }
        }
        try
        {
            using var sw = Registry.CurrentUser.OpenSubKey("SOFTWARE");
            if (sw is not null) names.AddRange(sw.GetSubKeyNames().Select(Normalize));
        }
        catch { /* ignore */ }

        return new Installed(names.Where(n => n.Length >= 3).Distinct().ToList(), locations.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? ExtractPath(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            s = end > 1 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) s = s[..(exe + 4)];
            var comma = s.IndexOf(',');
            if (comma > 0) s = s[..comma];
        }
        return s.Length >= 3 && s[1] == ':' ? s : null;
    }

    private static string Normalize(string s)
    {
        Span<char> buf = stackalloc char[Math.Min(s.Length, 256)];
        int n = 0;
        foreach (var c in s)
        {
            if (n >= buf.Length) break;
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..n]);
    }

    private static bool MatchesInstalled(string folder, List<string> names)
    {
        var f = Normalize(folder);
        if (f.Length < 3) return true; // too short to judge: leave it alone
        foreach (var n in names)
        {
            if (n.Contains(f, StringComparison.Ordinal)) return true;
            if (n.Length >= 4 && f.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
