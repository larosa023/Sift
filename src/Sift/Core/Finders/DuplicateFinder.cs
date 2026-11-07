using System.Collections.Concurrent;
using System.IO.Hashing;

namespace Sift.Core.Finders;

/// <summary>
/// Byte-identical files, found from the last scan: group by size, then a fast head/tail hash,
/// then a full XxHash128 of every remaining candidate.
/// </summary>
public sealed class DuplicateFinder : ICleanupFinder
{
    public string Id => "duplicates";
    public string Title => "Duplicate files";
    public string Description => "Files with identical contents. Sift keeps the oldest copy in each set and suggests the rest. Comes from your last scan.";
    public string Glyph => "";
    public bool Grouped => true;
    public bool NeedsScan => true;

    private const int SampleSize = 64 * 1024;

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        if (ctx.Scan is null) throw new InvalidOperationException("Scan a drive or folder on the Overview page first. Duplicates are found from the scan results.");

        var excluded = ctx.Settings.DuplicatesSkipSystemFolders ? SystemFolders() : new List<string>();
        long min = Math.Max(1, ctx.Settings.DuplicateMinBytes);

        ctx.Status?.Report("Grouping files by size…");
        var bySize = new Dictionary<long, List<FileNode>>();
        foreach (var f in ctx.Scan.EnumerateFiles())
        {
            if (f.Size < min) continue;
            if (!bySize.TryGetValue(f.Size, out var list)) bySize[f.Size] = list = new List<FileNode>(2);
            list.Add(f);
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        bySize.Clear();

        var paths = new ConcurrentDictionary<FileNode, string>();
        string PathOf(FileNode n) => paths.GetOrAdd(n, static x => x.FullPath);

        if (excluded.Count > 0)
        {
            candidates = candidates
                .Select(g => g.Where(f => !excluded.Any(x => FsUtil.IsUnder(PathOf(f), x))).ToList())
                .Where(g => g.Count > 1)
                .ToList();
        }

        // Stage 1: head + tail sample.
        ctx.Status?.Report($"Checking {candidates.Sum(c => c.Count):N0} candidates…");
        var stage1 = new ConcurrentBag<List<FileNode>>();
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, group =>
        {
            foreach (var sub in group.GroupBy(f => SampleHash(PathOf(f), f.Size)).Where(g => g.Key != 0 && g.Count() > 1))
                stage1.Add(sub.ToList());
        });

        // Stage 2: full hash, only needed when the sample didn't already cover the whole file.
        int done = 0, total = stage1.Count;
        var sets = new ConcurrentBag<(UInt128 Hash, List<FileNode> Files)>();
        Parallel.ForEach(stage1, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 3 }, group =>
        {
            var size = group[0].Size;
            if (size <= SampleSize)
            {
                sets.Add((new UInt128(0, (ulong)size), group));
            }
            else
            {
                foreach (var sub in group.GroupBy(f => FullHash(PathOf(f), ct)).Where(g => g.Key != UInt128.Zero && g.Count() > 1))
                    sets.Add((sub.Key, sub.ToList()));
            }
            var d = Interlocked.Increment(ref done);
            if (d % 25 == 0) ctx.Status?.Report($"Hashing… {d:N0} of {total:N0} sets");
        });

        var items = new List<CleanupItem>();
        int setNo = 0;
        foreach (var (_, files) in sets.OrderByDescending(s => s.Files[0].Size * (s.Files.Count - 1)))
        {
            setNo++;
            // Keep the oldest copy (most likely the original); shorter path breaks ties.
            var ordered = files.OrderBy(f => f.LastWriteTicks).ThenBy(f => PathOf(f).Length).ToList();
            var group = $"Set {setNo} · {ordered.Count} copies of {ordered[0].Name} · {Format.Bytes(ordered[0].Size)} each";
            for (int i = 0; i < ordered.Count; i++)
            {
                var f = ordered[i];
                items.Add(new CleanupItem
                {
                    Path = PathOf(f),
                    Title = f.Name,
                    Detail = i == 0 ? "Original (kept)" : "Copy",
                    Size = f.Size,
                    LastUsedUtc = f.LastWriteUtc,
                    LastUsedLabel = "Modified",
                    Risk = Risk.Review,
                    Suggested = i > 0,
                    Group = group,
                    Source = "Duplicate",
                    Glyph = "",
                });
            }
        }
        return items;
    }, ct);

    private static List<string> SystemFolders()
    {
        var list = new List<string>();
        void Add(Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) list.Add(p);
        }
        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);
        Add(Environment.SpecialFolder.LocalApplicationData);
        Add(Environment.SpecialFolder.ApplicationData);
        foreach (var lib in GameFinder.SteamLibraries()) list.Add(Path.Combine(lib, "steamapps"));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                list.Add(Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"));
                list.Add(Path.Combine(d.RootDirectory.FullName, "System Volume Information"));
            }
            catch { /* ignore */ }
        }
        return list;
    }

    private static ulong SampleHash(string path, long size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var h = new XxHash64();
            var buf = new byte[SampleSize];
            int n = ReadFull(fs, buf);
            h.Append(buf.AsSpan(0, n));
            if (size > SampleSize)
            {
                fs.Seek(-SampleSize, SeekOrigin.End);
                n = ReadFull(fs, buf);
                h.Append(buf.AsSpan(0, n));
            }
            var v = h.GetCurrentHashAsUInt64();
            return v == 0 ? 1 : v;
        }
        catch { return 0; }
    }

    private static UInt128 FullHash(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var h = new XxHash128();
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                h.Append(buf.AsSpan(0, n));
            }
            var v = h.GetCurrentHashAsUInt128();
            return v == UInt128.Zero ? UInt128.One : v;
        }
        catch (OperationCanceledException) { throw; }
        catch { return UInt128.Zero; }
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }
}
