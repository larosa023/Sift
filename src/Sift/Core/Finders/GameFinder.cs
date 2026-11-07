using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sift.Core.Finders;

/// <summary>Installed Steam, Epic and GOG games that haven't been played in a while.</summary>
public sealed partial class GameFinder : ICleanupFinder
{
    public string Id => "games";
    public string Title => "Unplayed games";
    public string Description => "Installed Steam, Epic Games and GOG titles you haven't launched in a while. Uninstalling through the launcher keeps your library clean, and you can reinstall any time.";
    public string Glyph => "\uE7FC";
    public bool Grouped => false;
    public bool NeedsScan => false;

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        var cutoff = DateTime.UtcNow.AddDays(-ctx.Settings.GameUnplayedDays);
        var games = new List<CleanupItem>();
        ctx.Status?.Report("Reading Steam libraries…");
        games.AddRange(Steam(cutoff, ct));
        ctx.Status?.Report("Reading Epic Games manifests…");
        games.AddRange(Epic(cutoff, ct));
        ctx.Status?.Report("Reading GOG installs…");
        games.AddRange(Gog(cutoff, ct));
        return games.OrderByDescending(g => g.Size).ToList();
    }, ct);

    // ------------------------------------------------------------------ Steam

    [GeneratedRegex("\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"")]
    private static partial Regex VdfPair();

    public static string? SteamRoot()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { /* ignore */ }
        try
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var k = hk.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
        }
        catch { /* ignore */ }
        return null;
    }

    public static List<string> SteamLibraries()
    {
        var libs = new List<string>();
        var root = SteamRoot();
        if (root is null) return libs;
        libs.Add(root);
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in VdfPair().Matches(File.ReadAllText(vdf)))
                {
                    if (!m.Groups["k"].Value.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = m.Groups["v"].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p) && !libs.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('\\'), Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                        libs.Add(p);
                }
            }
        }
        catch { /* ignore */ }
        return libs;
    }

    private static readonly HashSet<string> SteamNonGames = new() { "228980" /* Steamworks redist */, "1070560", "1391110", "1628350" /* Linux runtimes */ };

    private static IEnumerable<CleanupItem> Steam(DateTime cutoff, CancellationToken ct)
    {
        var result = new List<CleanupItem>();
        foreach (var lib in SteamLibraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            foreach (var acf in FsUtil.SafeFiles(apps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in VdfPair().Matches(File.ReadAllText(acf)))
                        kv.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);

                    if (!kv.TryGetValue("appid", out var appId) || SteamNonGames.Contains(appId)) continue;
                    var name = kv.GetValueOrDefault("name") ?? $"App {appId}";
                    var dir = Path.Combine(apps, "common", kv.GetValueOrDefault("installdir") ?? "");
                    if (!Directory.Exists(dir)) continue;

                    long size = long.TryParse(kv.GetValueOrDefault("SizeOnDisk"), out var s) && s > 0 ? s : FsUtil.SizeOf(dir, ct);
                    long lastPlayed = long.TryParse(kv.GetValueOrDefault("LastPlayed"), out var lp) ? lp : 0;
                    var installed = File.GetLastWriteTimeUtc(acf);
                    DateTime used = lastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed).UtcDateTime : DateTime.MinValue;

                    bool stale = used == DateTime.MinValue ? installed < cutoff : used < cutoff;
                    if (!stale || size < 100L * 1024 * 1024) continue;

                    result.Add(new CleanupItem
                    {
                        Path = dir,
                        Title = name,
                        Detail = used == DateTime.MinValue ? "Steam · never played" : "Steam",
                        Size = size,
                        LastUsedUtc = used,
                        LastUsedLabel = "Last played",
                        Risk = Risk.Review,
                        Action = ItemAction.Uninstall,
                        UninstallUri = $"steam://uninstall/{appId}",
                        UninstallLabel = "Uninstall in Steam",
                        Source = "Steam",
                        Glyph = "\uE7FC",
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* unreadable manifest */ }
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ Epic

    private static IEnumerable<CleanupItem> Epic(DateTime cutoff, CancellationToken ct)
    {
        var result = new List<CleanupItem>();
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (var item in FsUtil.SafeFiles(manifests, "*.item"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                var dir = r.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                var name = r.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);

                var m = FsUtil.Measure(dir, ct);
                // Epic doesn't record play time; the newest write (saves, configs, shader caches) is the best signal.
                var used = m.NewestWriteUtc;
                if (used >= cutoff || m.Size < 100L * 1024 * 1024) continue;

                result.Add(new CleanupItem
                {
                    Path = dir,
                    Title = name,
                    Detail = "Epic Games",
                    Size = m.Size,
                    LastUsedUtc = used,
                    LastUsedLabel = "Last activity",
                    Risk = Risk.Review,
                    Action = ItemAction.Uninstall,
                    UninstallUri = "com.epicgames.launcher://store/library",
                    UninstallLabel = "Open Epic library",
                    Source = "Epic",
                    Glyph = "\uE7FC",
                });
            }
            catch (OperationCanceledException) { throw; }
            catch { /* bad manifest */ }
        }
        return result;
    }

    // ------------------------------------------------------------------ GOG

    private static IEnumerable<CleanupItem> Gog(DateTime cutoff, CancellationToken ct)
    {
        var result = new List<CleanupItem>();
        try
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var games = hk.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games is null) return result;
            foreach (var id in games.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var g = games.OpenSubKey(id);
                if (g?.GetValue("path") is not string dir || !Directory.Exists(dir)) continue;
                var name = g.GetValue("gameName") as string ?? Path.GetFileName(dir);
                var m = FsUtil.Measure(dir, ct);
                if (m.NewestWriteUtc >= cutoff || m.Size < 100L * 1024 * 1024) continue;
                result.Add(new CleanupItem
                {
                    Path = dir,
                    Title = name,
                    Detail = "GOG",
                    Size = m.Size,
                    LastUsedUtc = m.NewestWriteUtc,
                    LastUsedLabel = "Last activity",
                    Risk = Risk.Review,
                    Action = ItemAction.Uninstall,
                    UninstallUri = "ms-settings:appsfeatures",
                    UninstallLabel = "Open Installed apps",
                    Source = "GOG",
                    Glyph = "\uE7FC",
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ignore */ }
        return result;
    }
}
