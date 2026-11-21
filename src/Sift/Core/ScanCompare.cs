namespace Sift.Core;

public enum ChangeKind { Grew, Shrank, Added, Removed }

public sealed record ScanChange(
    string Path,
    string Name,
    bool IsDirectory,
    ChangeKind Kind,
    long Delta,
    long NowSize,
    long ThenSize,
    /// <summary>The node in the current scan, or null if it no longer exists.</summary>
    FileNode? Node);

/// <summary>Matches a current scan against a previous one by path.</summary>
public sealed class ScanCompare
{
    private readonly PreviousScan _prev;
    private readonly Dictionary<FileNode, Dictionary<string, FileNode>> _index = new(ReferenceEqualityComparer.Instance);

    public ScanCompare(PreviousScan prev) => _prev = prev;

    public DateTime ThenUtc => _prev.Info.ScannedUtc;

    public bool Covers(FileNode currentRoot) =>
        string.Equals(currentRoot.Name.TrimEnd('\\'), _prev.Root.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, FileNode> IndexOf(FileNode prevDir)
    {
        if (!_index.TryGetValue(prevDir, out var map))
        {
            map = new Dictionary<string, FileNode>(prevDir.Children?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
            if (prevDir.Children is not null)
                foreach (var c in prevDir.Children) map.TryAdd(c.Name, c);
            _index[prevDir] = map;
        }
        return map;
    }

    /// <summary>The previous-scan node at the same path as <paramref name="current"/>, if any.</summary>
    public FileNode? Match(FileNode current)
    {
        if (current.Parent is null) return Covers(current) ? _prev.Root : null;
        var parentMatch = Match(current.Parent);
        if (parentMatch is null || !parentMatch.IsDirectory) return null;
        return IndexOf(parentMatch).TryGetValue(current.Name, out var m) && m.IsDirectory == current.IsDirectory ? m : null;
    }

    /// <summary>
    /// Size change for one item, or null when it can't be known (a small file the compact previous scan
    /// didn't keep individually).
    /// </summary>
    public long? DeltaOf(FileNode current)
    {
        var m = Match(current);
        if (m is not null) return current.Size - m.Size;
        if (current.Parent is null) return null;
        // Not in the previous scan: a folder or a big file is new; a small file may simply not have been kept.
        return current.IsDirectory || current.Size >= Snapshots.CompareMinFile ? current.Size : null;
    }

    /// <summary>
    /// The places that explain most of the change below <paramref name="current"/>: walks down while a few
    /// children account for the folder's change, and reports a folder itself when its change is spread out.
    /// </summary>
    public List<ScanChange> Changes(FileNode current, int max = 200)
    {
        var result = new List<ScanChange>();
        var then = Match(current);
        if (then is null) return result;

        long minDelta = Math.Max(Snapshots.CompareMinFile, Math.Max(current.Size, then.Size) / 2000);
        Walk(current, then, result, minDelta, depth: 0);
        return result.OrderByDescending(c => Math.Abs(c.Delta)).Take(max).ToList();
    }

    private void Walk(FileNode now, FileNode then, List<ScanChange> output, long minDelta, int depth)
    {
        long delta = now.Size - then.Size;
        var thenKids = IndexOf(then);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var big = new List<(FileNode? Now, FileNode? Then, long Delta)>();

        if (now.Children is not null)
        {
            foreach (var c in now.Children)
            {
                thenKids.TryGetValue(c.Name, out var t);
                if (t is not null && t.IsDirectory != c.IsDirectory) t = null;
                if (t is not null) seen.Add(c.Name);
                long d = t is null
                    ? (c.IsDirectory || c.Size >= Snapshots.CompareMinFile ? c.Size : 0)
                    : c.Size - t.Size;
                if (Math.Abs(d) >= minDelta) big.Add((c, t, d));
            }
        }
        foreach (var (name, t) in thenKids)
        {
            if (seen.Contains(name)) continue;
            // Gone (or turned from a file into a folder or back, which was counted above as new).
            if (t.Size >= minDelta) big.Add((null, t, -t.Size));
        }

        long explained = big.Where(b => Math.Sign(b.Delta) == Math.Sign(delta)).Sum(b => b.Delta);
        bool descend = depth < 40 && big.Count > 0 && Math.Abs(explained) >= Math.Abs(delta) * 0.75;

        if (!descend)
        {
            if (Math.Abs(delta) >= minDelta && depth > 0) output.Add(Make(now, then, delta));
            else if (depth == 0) foreach (var b in big) Emit(b, output, minDelta, depth);
            return;
        }

        foreach (var b in big) Emit(b, output, minDelta, depth);
        long rest = delta - explained;
        if (Math.Abs(rest) >= minDelta && depth > 0) output.Add(Make(now, then, rest) with { Name = now.Name + " (other files)" });
    }

    private void Emit((FileNode? Now, FileNode? Then, long Delta) b, List<ScanChange> output, long minDelta, int depth)
    {
        if (b.Now is { IsDirectory: true } nd && b.Then is { IsDirectory: true } td) Walk(nd, td, output, minDelta, depth + 1);
        else if (b.Now is not null) output.Add(Make(b.Now, b.Then, b.Delta));
        else output.Add(MakeRemoved(b.Then!));
    }

    private static ScanChange Make(FileNode now, FileNode? then, long delta) => new(
        now.FullPath, now.Name, now.IsDirectory,
        then is null ? ChangeKind.Added : delta >= 0 ? ChangeKind.Grew : ChangeKind.Shrank,
        delta, now.Size, then?.Size ?? 0, now);

    private static ScanChange MakeRemoved(FileNode then) => new(
        then.FullPath, then.Name, then.IsDirectory, ChangeKind.Removed, -then.Size, 0, then.Size, null);
}
