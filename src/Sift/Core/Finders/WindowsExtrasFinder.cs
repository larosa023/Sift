namespace Sift.Core.Finders;

/// <summary>
/// Big Windows files that don't show up as "junk" anywhere else: the hibernation file, a previous Windows
/// installation and upgrade leftovers, and crash memory dumps. Each one is removed the way Windows expects.
/// </summary>
public sealed class WindowsExtrasFinder : ICleanupFinder
{
    public string Id => "windows";
    public string Title => "Windows extras";
    public string Description => "The hibernation file, a previous Windows installation, upgrade leftovers and crash memory dumps. Each is removed the way Windows expects — by turning a feature off, through Windows' own Storage settings, or by deleting dumps.";
    public string Glyph => "\uE770";
    public bool Grouped => false;
    public bool NeedsScan => false;

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        var result = new List<CleanupItem>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysRoot = Path.GetPathRoot(windows) ?? "C:\\";
        bool admin = Native.IsElevated();
        string needsAdmin = admin ? "" : " · asks for administrator";

        // ---- Hibernation file: 40% of RAM by default.
        ctx.Status?.Report("Checking the hibernation file…");
        long hiber = RootFileSize(sysRoot, "hiberfil.sys");
        if (hiber > 256L << 20)
        {
            result.Add(new CleanupItem
            {
                Path = Path.Combine(sysRoot, "hiberfil.sys"),
                Title = "Hibernation file",
                Detail = "Turns off hibernation and Fast Startup; sleep still works" + needsAdmin,
                Size = hiber,
                Risk = Risk.Review,
                Action = ItemAction.RunCommand,
                Command = "powercfg.exe",
                CommandArgs = "/hibernate off",
                CommandElevated = true,
                CommandFrees = Path.Combine(sysRoot, "hiberfil.sys"),
                UninstallLabel = "Turn off hibernation",
                Source = "Windows",
                Glyph = "\uE708",
            });
        }

        // ---- A previous Windows installation and upgrade leftovers: Windows removes these itself.
        ctx.Status?.Report("Checking for old Windows installations…");
        var leftovers = new[] { "Windows.old", "$WINDOWS.~BT", "$Windows.~WS", "$GetCurrent", "ESD" }
            .Select(n => Path.Combine(sysRoot, n))
            .Where(Directory.Exists)
            .ToList();
        foreach (var dir in leftovers)
        {
            ct.ThrowIfCancellationRequested();
            var m = FsUtil.Measure(dir, ct);
            if (m.Size < 64L << 20) continue;
            bool old = Path.GetFileName(dir).Equals("Windows.old", StringComparison.OrdinalIgnoreCase);
            result.Add(new CleanupItem
            {
                Path = dir,
                Title = old ? "Previous Windows installation" : $"Windows upgrade files ({Path.GetFileName(dir)})",
                Detail = old
                    ? "Lets you roll back a Windows upgrade. Remove it in Storage › Temporary files › Previous Windows installation(s)"
                    : "Left over from a Windows upgrade. Remove it in Storage › Temporary files",
                Size = m.Size,
                LastUsedUtc = m.NewestWriteUtc,
                LastUsedLabel = "Last changed",
                Risk = Risk.Review,
                Action = ItemAction.Uninstall,
                UninstallUri = "ms-settings:storagesense",
                UninstallLabel = "Open Storage settings",
                Source = "Windows",
                Glyph = "\uE770",
            });
        }

        // ---- Crash memory dumps (only useful to whoever is debugging the crash).
        ctx.Status?.Report("Checking for crash dumps…");
        var fullDump = Path.Combine(windows, "MEMORY.DMP");
        try
        {
            if (File.Exists(fullDump))
            {
                var fi = new FileInfo(fullDump);
                result.Add(new CleanupItem
                {
                    Path = fullDump,
                    Title = "Full crash dump (MEMORY.DMP)",
                    Detail = "Written when Windows crashed (blue screen)" + needsAdmin,
                    Size = fi.Length,
                    LastUsedUtc = fi.LastWriteTimeUtc,
                    LastUsedLabel = "Written",
                    Risk = Risk.Safe,
                    Suggested = admin,
                    Source = "Windows",
                    Glyph = "\uE7BA",
                });
            }
        }
        catch { /* access denied without admin */ }
        foreach (var (folder, title) in new[] { (Path.Combine(windows, "Minidump"), "Small crash dumps"), (Path.Combine(windows, "LiveKernelReports"), "Driver crash reports") })
        {
            if (!Directory.Exists(folder)) continue;
            var m = FsUtil.Measure(folder, ct);
            if (m.Size < 16L << 20) continue;
            result.Add(new CleanupItem
            {
                Path = folder,
                Title = title,
                Detail = "Diagnostic dumps from past crashes" + needsAdmin,
                Size = m.Size,
                LastUsedUtc = m.NewestWriteUtc,
                LastUsedLabel = "Last written",
                Risk = Risk.Safe,
                Action = ItemAction.DeleteContents,
                Suggested = admin,
                Source = "Windows",
                Glyph = "\uE7BA",
            });
        }

        return result.OrderByDescending(r => r.Size).ToList();
    }, ct);

    /// <summary>Size of a protected file in a drive root, read from the folder listing (opening it would fail).</summary>
    private static long RootFileSize(string root, string name)
    {
        try
        {
            var fi = new DirectoryInfo(root).EnumerateFiles(name, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).FirstOrDefault();
            return fi?.Length ?? 0;
        }
        catch { return 0; }
    }
}
