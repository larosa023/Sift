namespace Sift.Core.Finders;

/// <summary>
/// Big files you haven't changed in a long time — old recordings, VM disks, ISOs, backups — outside the
/// places apps and Windows manage themselves. Comes from the last scan, so it's instant.
/// </summary>
public sealed class LargeOldFilesFinder : ICleanupFinder
{
    public string Id => "largeold";
    public string Title => "Large old files";
    public string Description => "Big files untouched for a long time, outside Windows, program folders and game libraries. Often old videos, disk images, VM disks or backups you forgot about. Comes from your last scan.";
    public string Glyph => "";
    public bool Grouped => true;
    public bool NeedsScan => true;

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        if (ctx.Scan is null) throw new InvalidOperationException("Scan a drive or folder on the Overview page first. Large old files are found from the scan results.");
        long min = Math.Max(1L << 20, ctx.Settings.LargeOldMinBytes);
        long cutoff = DateTime.UtcNow.AddDays(-ctx.Settings.LargeOldDays).Ticks;

        var skip = ManagedFolders();
        var result = new List<CleanupItem>();
        // A scan that starts inside a managed folder (e.g. Program Files\dotnet) has nothing safe to suggest.
        var rootPath = ctx.Scan.FullPath;
        if (skip.Any(m => FsUtil.IsUnder(rootPath, m))) return result;
        var stack = new Stack<FileNode>();
        stack.Push(ctx.Scan);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (dir.Children is null || dir.Size < min) continue; // nothing big enough below here
            if (!dir.IsRoot && IsManaged(dir, skip)) continue;
            foreach (var c in dir.Children)
            {
                if (c.IsDirectory) { stack.Push(c); continue; }
                if (c.Size < min || c.LastWriteTicks <= 0 || c.LastWriteTicks > cutoff) continue;
                if (IsSystemFile(c.Name)) continue;
                var cat = c.Category;
                result.Add(new CleanupItem
                {
                    Path = c.FullPath,
                    Title = c.Name,
                    Detail = FileCategories.Label(cat),
                    Size = c.Size,
                    LastUsedUtc = c.LastWriteUtc,
                    LastUsedLabel = "Last modified",
                    Risk = cat is FileCategory.Document ? Risk.Caution : Risk.Review,
                    Group = GroupOf(cat),
                    Source = "Large old file",
                    Glyph = "",
                });
            }
        }
        return result.OrderBy(r => GroupOrder(r.Group)).ThenByDescending(r => r.Size).Take(500).ToList();
    }, ct);

    private static string GroupOf(FileCategory c) => c switch
    {
        FileCategory.Video => "Videos",
        FileCategory.DiskImage => "Disk images and virtual disks",
        FileCategory.Archive => "Archives and backups",
        FileCategory.Audio or FileCategory.Image => "Music and photos",
        FileCategory.Program => "Installers",
        FileCategory.Document => "Documents",
        _ => "Other files",
    };

    private static int GroupOrder(string? g) => g switch
    {
        "Videos" => 0, "Disk images and virtual disks" => 1, "Archives and backups" => 2, "Installers" => 3,
        "Music and photos" => 4, "Other files" => 5, _ => 6,
    };

    private static bool IsSystemFile(string name) =>
        name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("DumpStack.log", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders whose contents Windows, apps or game launchers manage; deleting files there breaks things.</summary>
    private static List<string> ManagedFolders()
    {
        var list = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) list.Add(p.TrimEnd('\\')); }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        foreach (var lib in GameFinder.SteamLibraries()) Add(Path.Combine(lib, "steamapps"));
        // Hidden "dot" folders in the user profile belong to tools (.ollama models, .cache, .nuget, .vscode…).
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var d in FsUtil.SafeDirectories(profile, ".*")) Add(d);
        return list;
    }

    private static bool IsManaged(FileNode dir, List<string> managed)
    {
        var name = dir.Name;
        if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase) || name.Equals("Epic Games", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GOG Games", StringComparison.OrdinalIgnoreCase) || name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase)) return true;
        var path = dir.FullPath;
        foreach (var m in managed) if (string.Equals(path, m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
