using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Sift.Core;

/// <summary>
/// Multi-threaded directory scanner. A fixed set of dedicated worker threads pull folders from a shared
/// queue (so every depth of the tree is parallel, with no thread-pool starvation), using the low-allocation
/// FileSystemEnumerable API. Totals are rolled up in one pass at the end. Junctions/symlinks are never followed.
/// </summary>
public sealed class DiskScanner
{
    private readonly EnumerationOptions _options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly int _workers = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
    private long _files, _folders, _bytes, _errors, _pending;
    private SemaphoreSlim? _signal;
    private volatile string _current = string.Empty;

    public long Files => Interlocked.Read(ref _files);
    public long Folders => Interlocked.Read(ref _folders);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public string CurrentPath => _current;

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, long Ticks, FileAttributes Attributes);
    private readonly record struct Work(FileNode Node, string Path);

    public Task<FileNode> ScanAsync(string rootPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(rootPath);
        if (full.Length > 3) full = full.TrimEnd('\\');
        var root = new FileNode(full, null, isDirectory: true);
        try { root.LastWriteTicks = Directory.GetLastWriteTimeUtc(full).Ticks; } catch { /* ignore */ }

        var tcs = new TaskCompletionSource<FileNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ConcurrentStack<Work>();   // LIFO = depth-first, keeps the queue small
        queue.Push(new Work(root, full));
        _pending = 1;                              // folders queued or being processed
        int alive = _workers;
        Exception? failure = null;
        var signal = new SemaphoreSlim(1);
        _signal = signal;

        void Worker()
        {
            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested || failure is not null) break;
                    if (queue.TryPop(out var w))
                    {
                        ScanOne(w.Node, w.Path, queue);
                        if (Interlocked.Decrement(ref _pending) == 0)
                        {
                            signal.Release(_workers); // wake everyone so they can exit
                            break;
                        }
                    }
                    else
                    {
                        if (Interlocked.Read(ref _pending) == 0) break;
                        signal.Wait(20);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
            finally
            {
                if (Interlocked.Decrement(ref alive) == 0)
                {
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                    else if (failure is not null) tcs.TrySetException(failure);
                    else
                    {
                        try { RollUp(root); tcs.TrySetResult(root); }
                        catch (Exception ex) { tcs.TrySetException(ex); }
                    }
                }
            }
        }

        for (int i = 0; i < _workers; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"Sift scan {i}", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        return tcs.Task;
    }

    /// <summary>Enumerates one folder; returns how many subfolders were queued.</summary>
    private int ScanOne(FileNode node, string path, ConcurrentStack<Work> queue)
    {
        _current = path;
        var children = new List<FileNode>();
        long size = 0, files = 0;
        int subdirs = 0;

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
                    // Count it as pending *before* it becomes visible to other workers.
                    Interlocked.Increment(ref _pending);
                    queue.Push(new Work(child, Path.Join(path, e.Name)));
                    subdirs++;
                }
                else
                {
                    long len = IsCloudOnly(e.Attributes) ? 0 : e.Length;
                    children.Add(new FileNode(e.Name, node, false) { Size = len, FileCount = 1, LastWriteTicks = e.Ticks });
                    size += len;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.HadErrors = true;
            Interlocked.Increment(ref _errors);
        }

        node.Children = children;
        if (subdirs > 0)
        {
            try { _signal?.Release(Math.Min(subdirs, _workers)); } catch (SemaphoreFullException) { }
        }
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, size);
        Interlocked.Add(ref _folders, subdirs);
        return subdirs;
    }

    /// <summary>Post-order pass: folder sizes, counts and newest-write, then sort children by size.</summary>
    private static void RollUp(FileNode root)
    {
        var stack = new Stack<(FileNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (n, visited) = stack.Pop();
            var kids = n.Children;
            if (!visited)
            {
                stack.Push((n, true));
                if (kids is not null)
                    foreach (var c in kids)
                        if (c.IsDirectory) stack.Push((c, false));
                continue;
            }

            long size = 0, files = 0, folders = 0, newest = n.LastWriteTicks;
            if (kids is not null)
            {
                foreach (var c in kids)
                {
                    size += c.Size;
                    if (c.IsDirectory) { files += c.FileCount; folders += c.FolderCount + 1; }
                    else files++;
                    if (c.LastWriteTicks > newest) newest = c.LastWriteTicks;
                }
                kids.Sort(static (a, b) => b.Size.CompareTo(a.Size));
                kids.TrimExcess();
            }
            else n.Children = new List<FileNode>(0);
            n.Size = size;
            n.FileCount = files;
            n.FolderCount = folders;
            n.LastWriteTicks = newest;
        }
    }

    /// <summary>OneDrive / cloud placeholders report a size but take no local space.</summary>
    internal static bool IsCloudOnly(FileAttributes a) =>
        (a & FileAttributes.Offline) != 0 ||
        ((int)a & 0x00400000) != 0 || // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        ((int)a & 0x00040000) != 0;   // FILE_ATTRIBUTE_RECALL_ON_OPEN
}
