namespace Sift.Core.Finders;

/// <summary>Things sitting in Downloads that haven't been opened or changed in a long time.</summary>
public sealed class StaleDownloadsFinder : ICleanupFinder
{
    public string Id => "downloads";
    public string Title => "Stale downloads";
    public string Description => "Files and folders in Downloads you haven't touched in a while. Installers, archives and disk images are the usual suspects once they've been used.";
    public string Glyph => "";
    public bool Grouped => true;
    public bool NeedsScan => false;

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        var downloads = Native.KnownFolder(Native.FolderDownloads)
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var cutoff = DateTime.UtcNow.AddDays(-ctx.Settings.StaleDownloadDays);
        var result = new List<CleanupItem>();
        if (!Directory.Exists(downloads)) return result;

        ctx.Status?.Report("Looking through Downloads…");
        foreach (var f in FsUtil.SafeFiles(downloads))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(f);
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (DiskScanner.IsCloudOnly(fi.Attributes)) continue;
                var used = Latest(fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.LastAccessTimeUtc);
                if (used > cutoff) continue;
                var cat = FileCategories.Of(fi.Extension);
                result.Add(Make(f, fi.Name, fi.Length, used, cat));
            }
            catch { /* ignore */ }
        }
        foreach (var d in FsUtil.SafeDirectories(downloads))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var m = FsUtil.Measure(d, ct);
                var created = Directory.GetCreationTimeUtc(d);
                var used = Latest(m.NewestWriteUtc, created, DateTime.MinValue);
                if (used > cutoff || m.Size == 0) continue;
                result.Add(Make(d, Path.GetFileName(d), m.Size, used, FileCategory.Folder));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore */ }
        }
        return result.OrderBy(r => GroupOrder(r.Group)).ThenByDescending(r => r.Size).ToList();
    }, ct);

    private static DateTime Latest(DateTime a, DateTime b, DateTime c) => new[] { a, b, c }.Max();

    private static CleanupItem Make(string path, string name, long size, DateTime used, FileCategory cat)
    {
        var (group, risk, suggested) = cat switch
        {
            FileCategory.Program => ("Installers", Risk.Safe, true),
            FileCategory.DiskImage => ("Disk images", Risk.Safe, true),
            FileCategory.Archive => ("Archives", Risk.Review, false),
            FileCategory.Video or FileCategory.Audio or FileCategory.Image => ("Media", Risk.Review, false),
            FileCategory.Document => ("Documents", Risk.Caution, false),
            FileCategory.Folder => ("Folders", Risk.Review, false),
            _ => ("Other files", Risk.Review, false),
        };
        return new CleanupItem
        {
            Path = path,
            Title = name,
            Detail = group,
            Size = size,
            LastUsedUtc = used,
            LastUsedLabel = "Last touched",
            Risk = risk,
            Suggested = suggested,
            Group = group,
            Source = "Downloads",
            Glyph = cat == FileCategory.Folder ? "" : "",
        };
    }

    private static int GroupOrder(string? g) => g switch
    {
        "Installers" => 0, "Disk images" => 1, "Archives" => 2, "Media" => 3, "Folders" => 4, "Documents" => 5, _ => 6,
    };
}
