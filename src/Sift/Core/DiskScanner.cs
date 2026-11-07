using System.IO.Enumeration;

namespace Sift.Core;

/// <summary>
/// Multi-threaded directory scanner. Uses the low-allocation FileSystemEnumerable API,
/// fans out across the first few directory levels, and never follows junctions or symlinks.
/// </summary>
public sealed class DiskScanner
{
    private const int ParallelDepth = 3;

    private readonly EnumerationOptions _options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly int _parallelism = Math.Clamp(Environment.ProcessorCount, 2, 16);
    private long _files, _folders, _bytes, _errors;
    private volatile string _current = string.Empty;

    public long Files => Interlocked.Read(ref _files);
    public long Folders => Interlocked.Read(ref _folders);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public string CurrentPath => _current;

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, long Ticks, FileAttributes Attributes);

    public Task<FileNode> ScanAsync(string rootPath, CancellationToken ct) => Task.Run(() =>
    {
        var full = Path.GetFullPath(rootPath);
        if (full.Length > 3) full = full.TrimEnd('\\');
        var root = new FileNode(full, null, isDirectory: true);
        try
        {
            root.LastWriteTicks = Directory.GetLastWriteTimeUtc(full).Ticks;
        }
        catch { /* ignore */ }
        ScanDirectory(root, full, 0, ct);
        return root;
    }, ct);

    private void ScanDirectory(FileNode node, string path, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current = path;

        var children = new List<FileNode>();
        List<(FileNode Node, string Path)>? subdirs = null;
        long size = 0, files = 0, newest = node.LastWriteTicks;

        try
        {
            var en = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcTicks, e.Attributes),
                _options);

            foreach (var e in en)
            {
                if (e.IsDirectory)
                {
                    // Junctions / symlinked folders would double count or loop.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var child = new FileNode(e.Name, node, true) { LastWriteTicks = e.Ticks };
                    children.Add(child);
                    (subdirs ??= new()).Add((child, Path.Join(path, e.Name)));
                }
                else
                {
                    long len = IsCloudOnly(e.Attributes) ? 0 : e.Length;
                    children.Add(new FileNode(e.Name, node, false) { Size = len, FileCount = 1, LastWriteTicks = e.Ticks });
                    size += len;
                    files++;
                    if (e.Ticks > newest) newest = e.Ticks;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.HadErrors = true;
            Interlocked.Increment(ref _errors);
        }

        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, size);

        if (subdirs is not null)
        {
            Interlocked.Add(ref _folders, subdirs.Count);
            if (depth < ParallelDepth && subdirs.Count > 1)
            {
                Parallel.ForEach(subdirs,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _parallelism },
                    s => ScanDirectory(s.Node, s.Path, depth + 1, ct));
            }
            else
            {
                foreach (var s in subdirs) ScanDirectory(s.Node, s.Path, depth + 1, ct);
            }

            long folders = subdirs.Count;
            foreach (var (c, _) in subdirs)
            {
                size += c.Size;
                files += c.FileCount;
                folders += c.FolderCount;
                if (c.LastWriteTicks > newest) newest = c.LastWriteTicks;
            }
            node.FolderCount = folders;
        }

        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        children.TrimExcess();
        node.Children = children;
        node.Size = size;
        node.FileCount = files;
        node.LastWriteTicks = newest;
    }

    /// <summary>OneDrive / cloud placeholders report a size but take no local space.</summary>
    internal static bool IsCloudOnly(FileAttributes a) =>
        (a & FileAttributes.Offline) != 0 ||
        ((int)a & 0x00400000) != 0 || // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        ((int)a & 0x00040000) != 0;   // FILE_ATTRIBUTE_RECALL_ON_OPEN
}
