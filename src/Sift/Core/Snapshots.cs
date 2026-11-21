using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Sift.Core;

public sealed record SnapshotInfo(string RootPath, DateTime ScannedUtc, bool Fast, long Size, long Files, long Folders);

/// <summary>A previous scan held in a compact form, only for comparing against.</summary>
public sealed class PreviousScan
{
    public required SnapshotInfo Info { get; init; }
    /// <summary>Folders, plus files at least <see cref="Snapshots.CompareMinFile"/> bytes. Folder sizes include every file.</summary>
    public required FileNode Root { get; init; }
}

/// <summary>
/// Saves every scan to %LOCALAPPDATA%\Sift\scans so Sift can reopen with the last results instantly and
/// show what changed since the scan before. Each scanned location keeps a "current" and a "previous" file.
/// Format: a small plain header (so listing is cheap) followed by a Brotli-compressed pre-order node stream.
/// </summary>
public static class Snapshots
{
    private const string Magic = "SIFTSNAP";
    private const int Version = 1;
    private const int KeepLocations = 8;
    /// <summary>Smallest file kept individually in a previous scan; smaller files only count toward folder sizes.</summary>
    public const long CompareMinFile = 1L << 20;

    public static string Folder => Path.Combine(AppSettings.Folder, "scans");

    private static string Key(string root)
    {
        var norm = root.TrimEnd('\\').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16].ToLowerInvariant();
    }

    private static string CurrentFile(string root) => Path.Combine(Folder, Key(root) + ".current.snap");
    private static string PreviousFile(string root) => Path.Combine(Folder, Key(root) + ".previous.snap");

    public static bool HasCurrent(string root) => File.Exists(CurrentFile(root));

    // ------------------------------------------------------------------ write

    /// <summary>Saves a finished scan as the location's current snapshot. The old current becomes previous.</summary>
    public static void SaveNew(FileNode root, DateTime scannedUtc, bool fast)
    {
        Directory.CreateDirectory(Folder);
        var cur = CurrentFile(root.Name);
        var prev = PreviousFile(root.Name);
        Write(root, scannedUtc, fast, cur + ".tmp");
        if (File.Exists(cur)) File.Move(cur, prev, overwrite: true);
        File.Move(cur + ".tmp", cur, overwrite: true);
        Prune();
    }

    /// <summary>Rewrites the current snapshot in place (after items were deleted), keeping its scan time.</summary>
    public static void Update(FileNode root, DateTime scannedUtc, bool fast)
    {
        Directory.CreateDirectory(Folder);
        var cur = CurrentFile(root.Name);
        Write(root, scannedUtc, fast, cur + ".tmp");
        File.Move(cur + ".tmp", cur, overwrite: true);
    }

    private static void Write(FileNode root, DateTime scannedUtc, bool fast, string file)
    {
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using (var hw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            hw.Write(Magic);
            hw.Write(Version);
            hw.Write(root.Name);
            hw.Write(scannedUtc.Ticks);
            hw.Write(fast);
            hw.Write(root.Size);
            hw.Write(root.FileCount);
            hw.Write(root.FolderCount);
        }
        using var bs = new BrotliStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(new BufferedStream(bs, 1 << 16), Encoding.UTF8);

        // Pre-order: kind, name, then (file) size + ticks or (folder) ticks + child count.
        var stack = new Stack<FileNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDirectory)
            {
                w.Write((byte)0);
                w.Write(n.Name);
                w.Write7BitEncodedInt64(n.Size);
                w.Write7BitEncodedInt64(n.LastWriteTicks);
                continue;
            }
            var kids = n.Children;
            w.Write(n.HadErrors ? (byte)2 : (byte)1);
            w.Write(ReferenceEquals(n, root) ? "" : n.Name);
            w.Write7BitEncodedInt64(n.LastWriteTicks);
            w.Write7BitEncodedInt(kids?.Count ?? 0);
            if (kids is null) continue;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]); // keep stored order = display order
        }
    }

    private static void Prune()
    {
        try
        {
            var groups = Directory.GetFiles(Folder, "*.snap")
                .GroupBy(f => Path.GetFileName(f).Split('.')[0])
                .Select(g => (Files: g.ToList(), Newest: g.Max(File.GetLastWriteTimeUtc)))
                .OrderByDescending(g => g.Newest)
                .Skip(KeepLocations);
            foreach (var g in groups)
                foreach (var f in g.Files) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public static void DeleteAll()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); } catch { /* ignore */ }
    }

    public static long DiskUsage()
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder).Sum(f => new FileInfo(f).Length) : 0; }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ read

    public static SnapshotInfo? ReadInfo(string root) => ReadInfoFile(CurrentFile(root));

    private static SnapshotInfo? ReadInfoFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var fs = File.OpenRead(file);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            return ReadHeader(r);
        }
        catch { return null; }
    }

    private static SnapshotInfo ReadHeader(BinaryReader r)
    {
        if (r.ReadString() != Magic) throw new InvalidDataException("Not a Sift scan file.");
        if (r.ReadInt32() != Version) throw new InvalidDataException("Saved scan is from another version.");
        var path = r.ReadString();
        var when = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        bool fast = r.ReadBoolean();
        return new SnapshotInfo(path, when, fast, r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
    }

    /// <summary>Loads the location's current snapshot as a full tree, or null if there isn't a usable one.</summary>
    public static (FileNode Root, SnapshotInfo Info)? LoadCurrent(string root, CancellationToken ct = default)
    {
        try
        {
            var file = CurrentFile(root);
            if (!File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var hr = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            var info = ReadHeader(hr);
            using var bs = new BrotliStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(bs, 1 << 16), Encoding.UTF8);
            var tree = ReadTree(r, info.RootPath, minFile: 0, ct);
            DiskScanner.RollUp(tree);
            return (tree, info);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Loads the snapshot before the current one in compact form, for comparisons.</summary>
    public static PreviousScan? LoadPrevious(string root, CancellationToken ct = default)
    {
        try
        {
            var file = PreviousFile(root);
            if (!File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var hr = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            var info = ReadHeader(hr);
            using var bs = new BrotliStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(bs, 1 << 16), Encoding.UTF8);
            var tree = ReadTree(r, info.RootPath, CompareMinFile, ct);
            return new PreviousScan { Info = info, Root = tree };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Rebuilds the tree. With <paramref name="minFile"/> &gt; 0 small files aren't kept as nodes, but every
    /// folder's Size and FileCount are still exact (they're summed here rather than by RollUp).
    /// </summary>
    private static FileNode ReadTree(BinaryReader r, string rootPath, long minFile, CancellationToken ct)
    {
        FileNode? root = null;
        // Each open folder and how many of its children are still to come.
        var open = new Stack<(FileNode Dir, int Remaining)>();
        long read = 0;
        do
        {
            if ((++read & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            byte kind = r.ReadByte();
            string name = r.ReadString();
            FileNode? parent = open.Count > 0 ? open.Peek().Dir : null;

            if (kind == 0)
            {
                long size = r.Read7BitEncodedInt64();
                long ticks = r.Read7BitEncodedInt64();
                if (parent is null) throw new InvalidDataException("A file can't be the root.");
                if (minFile == 0 || size >= minFile)
                    parent.Children!.Add(new FileNode(name, parent, false) { Size = size, FileCount = 1, LastWriteTicks = ticks });
                if (minFile > 0)
                {
                    parent.Size += size;
                    parent.FileCount++;
                    if (ticks > parent.LastWriteTicks) parent.LastWriteTicks = ticks;
                }
                Consumed(open, minFile);
            }
            else
            {
                long ticks = r.Read7BitEncodedInt64();
                int count = r.Read7BitEncodedInt();
                var dir = new FileNode(parent is null ? rootPath : name, parent, true)
                {
                    LastWriteTicks = ticks,
                    HadErrors = kind == 2,
                    Children = new List<FileNode>(minFile > 0 ? 0 : count),
                };
                if (parent is null) root = dir;
                else parent.Children!.Add(dir);
                if (count > 0) open.Push((dir, count));
                else
                {
                    if (minFile > 0 && parent is not null) Fold(dir, parent);
                    Consumed(open, minFile);
                }
            }
        } while (open.Count > 0);

        return root ?? throw new InvalidDataException("Empty scan file.");
    }

    /// <summary>One child of the innermost open folder is complete; close folders that have no more children.</summary>
    private static void Consumed(Stack<(FileNode Dir, int Remaining)> open, long minFile)
    {
        while (open.Count > 0)
        {
            var (dir, remaining) = open.Pop();
            if (remaining > 1) { open.Push((dir, remaining - 1)); return; }
            // dir is finished
            if (minFile > 0 && open.Count > 0) Fold(dir, open.Peek().Dir);
        }
    }

    /// <summary>Adds a finished folder's totals to its parent (compact mode only).</summary>
    private static void Fold(FileNode dir, FileNode parent)
    {
        parent.Size += dir.Size;
        parent.FileCount += dir.FileCount;
        parent.FolderCount += dir.FolderCount + 1;
        if (dir.LastWriteTicks > parent.LastWriteTicks) parent.LastWriteTicks = dir.LastWriteTicks;
    }
}
