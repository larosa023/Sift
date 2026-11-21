using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sift.Core;

/// <summary>
/// Fast scanner for NTFS volumes: reads the Master File Table straight off the volume (needs administrator)
/// instead of asking Windows for every folder. One sequential read of the MFT replaces hundreds of thousands
/// of directory listings, so whole drives scan in seconds. Works for any folder on the volume: the whole
/// table is read and the requested folder's subtree is kept.
///
/// Differences from <see cref="DiskScanner"/>, both deliberate:
/// - Hard-linked files are counted once (the enumerator counts every link, e.g. inside WinSxS).
/// - Folders Windows normally refuses to list (System Volume Information, other users' profiles) are included.
/// </summary>
public sealed unsafe class MftScanner : IScanner
{
    private const int RootRecord = 5;
    private const int FirstUserRecord = 24;   // 0–23 are NTFS metafiles ($MFT, $LogFile, $Extend, …) or reserved
    private const int ChunkBytes = 4 << 20;

    private readonly HashSet<string> _excluded;
    private long _files, _folders, _bytes, _errors, _skipped, _readBytes, _totalBytes;
    private volatile string _current = "Reading the drive's file table…";

    public MftScanner(IEnumerable<string>? exclusions = null)
    {
        _excluded = new HashSet<string>(
            (exclusions ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.TrimEnd('\\')),
            StringComparer.OrdinalIgnoreCase);
    }

    public long Files => Interlocked.Read(ref _files);
    public long Folders => Interlocked.Read(ref _folders);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public long Skipped => Interlocked.Read(ref _skipped);
    public string CurrentPath => _current;
    public bool IsFast => true;
    public double? Fraction
    {
        get
        {
            long t = Interlocked.Read(ref _totalBytes);
            return t > 0 ? Math.Min(1.0, (double)Interlocked.Read(ref _readBytes) / t) : null;
        }
    }

    /// <summary>Whether the fast scanner can handle this path; <paramref name="reason"/> says why not.</summary>
    public static bool CanScan(string path, out string reason)
    {
        reason = "";
        if (!Native.IsElevated()) { reason = "Fast scan needs Sift to run as administrator."; return false; }
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is null || root.Length < 2 || root[1] != ':' || root.StartsWith(@"\\", StringComparison.Ordinal))
            { reason = "Fast scan only works on local drives."; return false; }
            var d = new DriveInfo(root);
            if (!d.IsReady) { reason = "The drive isn't ready."; return false; }
            if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            { reason = $"Fast scan needs an NTFS drive ({d.Name.TrimEnd('\\')} is {d.DriveFormat})."; return false; }
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
            { reason = "Fast scan only works on local drives."; return false; }
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    public Task<FileNode> ScanAsync(string rootPath, CancellationToken ct) =>
        Task.Factory.StartNew(() => Scan(rootPath, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ================================================================== per-record data

    private sealed class Table
    {
        public readonly int Count;
        public readonly int[] Parent;
        public readonly ushort[] ParentSeq;
        public readonly ushort[] Seq;
        public readonly string?[] Name;
        public readonly byte[] NameRank;
        public readonly byte[] Flags;
        public readonly long[] Size;
        public readonly long[] Ticks;

        public const byte InUse = 1, Dir = 2, Cloud = 4, Reparse = 8;

        public Table(int count)
        {
            Count = count;
            Parent = new int[count];
            ParentSeq = new ushort[count];
            Seq = new ushort[count];
            Name = new string?[count];
            NameRank = new byte[count];
            Flags = new byte[count];
            Size = new long[count];
            Ticks = new long[count];
        }
    }

    // ================================================================== scan

    private FileNode Scan(string rootPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(rootPath);
        if (full.Length > 3) full = full.TrimEnd('\\');
        var volRoot = Path.GetPathRoot(full)!;                 // "C:\"
        var device = @"\\.\" + volRoot.TrimEnd('\\');          // "\\.\C:"

        FlushVolume(device); // so files written moments ago are in the table we read
        using var h = OpenVolume(device);
        var vd = VolumeData(h);
        int recordSize = vd.BytesPerRecord;
        int cluster = vd.BytesPerCluster;
        if (recordSize <= 0 || cluster <= 0 || recordSize > 65536) throw new NotSupportedException("Unexpected NTFS layout.");

        // ---- Record 0 is $MFT itself; its $DATA run list says where the rest of the table lives.
        int firstRead = Math.Max(cluster, recordSize);
        firstRead = (firstRead + vd.BytesPerSector - 1) / vd.BytesPerSector * vd.BytesPerSector;
        var rec0Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)firstRead, 4096);
        List<(long Lcn, long Clusters)> runs;
        long mftDataSize;
        try
        {
            ReadAt(h, vd.MftStartLcn * cluster, rec0Ptr, firstRead);
            var rec0 = new Span<byte>(rec0Ptr, recordSize);
            if (!ApplyFixups(rec0)) throw new NotSupportedException("The file table header is damaged.");
            (runs, mftDataSize) = MftRuns(rec0, cluster);
        }
        finally { NativeMemory.AlignedFree(rec0Ptr); }
        long tableBytes = Math.Min(mftDataSize, vd.MftValidDataLength > 0 ? vd.MftValidDataLength : mftDataSize);
        long count64 = tableBytes / recordSize;
        if (count64 <= RootRecord || count64 > int.MaxValue) throw new NotSupportedException("Unexpected file table size.");
        int count = (int)count64;
        Interlocked.Exchange(ref _totalBytes, (long)count * recordSize);

        var t = new Table(count);

        // ---- Read and parse, overlapping disk reads with parsing.
        // Unbuffered reads need sector-aligned memory.
        var buffers = new IntPtr[4];
        for (int i = 0; i < buffers.Length; i++) buffers[i] = (IntPtr)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        try
        {
        using (var queue = new BlockingCollection<(IntPtr Buf, int Len, long FirstRecord)>(boundedCapacity: 3))
        using (var free = new BlockingCollection<IntPtr>(4))
        {
            foreach (var b in buffers) free.Add(b);
            Exception? readError = null;
            // The reader writes into native buffers: it must be stopped before they're freed, whatever happens.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var rt = stop.Token;

            var reader = new Thread(() =>
            {
                try
                {
                    long record = 0;
                    foreach (var (lcn, clusters) in runs)
                    {
                        long runBytes = clusters * cluster;
                        long pos = 0;
                        while (pos < runBytes && record < count)
                        {
                            rt.ThrowIfCancellationRequested();
                            long want = Math.Min(Math.Min(ChunkBytes, runBytes - pos), (count - record) * (long)recordSize);
                            // Reads on a volume handle must be whole sectors.
                            int len = (int)((want + vd.BytesPerSector - 1) / vd.BytesPerSector * vd.BytesPerSector);
                            var buf = free.Take(rt);
                            ReadAt(h, lcn * cluster + pos, (byte*)buf, len);
                            queue.Add((buf, (int)Math.Min(want, len), record), rt);
                            record += want / recordSize;
                            pos += want;
                        }
                        if (record >= count) break;
                    }
                }
                catch (Exception ex) { readError = ex; }
                finally { queue.CompleteAdding(); }
            }) { IsBackground = true, Name = "Sift MFT read" };
            reader.Start();

            try
            {
            foreach (var (buf, len, first) in queue.GetConsumingEnumerable())
            {
                int n = len / recordSize;
                var chunk = new Span<byte>((void*)buf, len);
                for (int i = 0; i < n; i++)
                {
                    long recNo = first + i;
                    if (recNo >= count) break;
                    try { ParseRecord(chunk.Slice(i * recordSize, recordSize), (int)recNo, t); }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
                    { Interlocked.Increment(ref _errors); } // malformed record
                }
                Interlocked.Add(ref _readBytes, (long)n * recordSize);
                free.Add(buf);
                if (ct.IsCancellationRequested) break;
            }
            }
            finally
            {
                stop.Cancel();
                reader.Join();
            }
            ct.ThrowIfCancellationRequested();
            if (readError is not null)
            {
                if (readError is OperationCanceledException) throw readError;
                throw new IOException("Couldn't read the drive's file table: " + readError.Message, readError);
            }
        }
        }
        finally
        {
            foreach (var b in buffers) NativeMemory.AlignedFree((void*)b);
        }

        _current = "Building the folder tree…";
        var root = BuildTree(t, volRoot, full, ct);

        foreach (var ex in _excluded)
        {
            if (!FsUtil.IsUnder(ex, full) || string.Equals(ex, full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            var node = root.Find(ex);
            if (node is not null && !node.IsRoot) { node.Detach(); Interlocked.Increment(ref _skipped); }
        }

        DiskScanner.RollUp(root);
        Interlocked.Exchange(ref _files, root.FileCount);
        Interlocked.Exchange(ref _folders, root.FolderCount);
        Interlocked.Exchange(ref _bytes, root.Size);
        return root;
    }

    // ================================================================== tree

    private FileNode BuildTree(Table t, string volRoot, string target, CancellationToken ct)
    {
        int n = t.Count;
        var nodes = new FileNode?[n];
        var rootNode = new FileNode(volRoot, null, isDirectory: true) { LastWriteTicks = t.Ticks[RootRecord] };
        nodes[RootRecord] = rootNode;

        long files = 0, bytes = 0;
        for (int i = FirstUserRecord; i < n; i++)
        {
            byte f = t.Flags[i];
            if ((f & Table.InUse) == 0 || t.Name[i] is null) continue;
            bool dir = (f & Table.Dir) != 0;
            // Junctions and symlinked folders: the folder-by-folder scanner skips them, so do the same.
            if (dir && (f & Table.Reparse) != 0) continue;
            var node = new FileNode(t.Name[i]!, null, dir) { LastWriteTicks = t.Ticks[i] };
            if (!dir)
            {
                long size = (f & Table.Cloud) != 0 ? 0 : t.Size[i];
                node.Size = size;
                node.FileCount = 1;
                files++;
                bytes += size;
            }
            nodes[i] = node;
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _files, files);
                Interlocked.Exchange(ref _bytes, bytes);
            }
        }

        // Attach every node to its parent. Children lists are sized first to avoid regrowth.
        var childCount = new int[n];
        for (int i = FirstUserRecord; i < n; i++)
        {
            if (nodes[i] is null) continue;
            int p = t.Parent[i];
            if (p == i || (uint)p >= (uint)n || nodes[p] is null || !nodes[p]!.IsDirectory) { nodes[i] = null; continue; }
            // A stale parent reference (the parent record was deleted and reused) must not attach.
            if (p != RootRecord && t.Seq[p] != t.ParentSeq[i]) { nodes[i] = null; continue; }
            childCount[p]++;
        }
        for (int i = 0; i < n; i++)
            if (nodes[i] is { IsDirectory: true } d) d.Children = new List<FileNode>(childCount[i]);
        for (int i = FirstUserRecord; i < n; i++)
        {
            var node = nodes[i];
            if (node is null) continue;
            var parent = nodes[t.Parent[i]];
            if (parent?.Children is null) continue; // parent turned out to be an orphan itself
            node.Parent = parent;
            parent.Children!.Add(node);
        }

        if (string.Equals(target.TrimEnd('\\'), volRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return rootNode;

        var sub = rootNode.Find(target) ?? throw new DirectoryNotFoundException($"{target} isn't in the drive's file table.");
        if (!sub.IsDirectory) throw new DirectoryNotFoundException($"{target} isn't a folder.");
        sub.BecomeRoot(target);
        return sub;
    }

    // ================================================================== record parsing

    private const uint AttrStandardInfo = 0x10, AttrFileName = 0x30, AttrData = 0x80, AttrEnd = 0xFFFFFFFF;
    private const long FileTimeToTicks = 504911232000000000L; // 1601-01-01 in DateTime ticks

    private void ParseRecord(Span<byte> r, int recNo, Table t)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(r) != 0x454C4946) return; // "FILE"
        if (!ApplyFixups(r)) { Interlocked.Increment(ref _errors); return; }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x16));
        if ((flags & 0x01) == 0) return; // not in use
        long baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(0x20)) & 0xFFFFFFFFFFFF);
        int target = baseRef == 0 ? recNo : (int)baseRef;
        if ((uint)target >= (uint)t.Count) return;

        if (baseRef == 0)
        {
            t.Flags[target] |= Table.InUse;
            if ((flags & 0x02) != 0) t.Flags[target] |= Table.Dir;
            t.Seq[target] = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x10));
        }

        int a = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x14));
        int limit = (int)Math.Min((uint)r.Length, BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(0x18)));
        while (a + 16 <= limit)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a));
            if (type == AttrEnd) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a + 4));
            if (len < 16 || a + len > limit) break;
            bool nonResident = r[a + 8] != 0;
            int attrNameLen = r[a + 9];

            if (!nonResident && type is AttrStandardInfo or AttrFileName)
            {
                int vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a + 0x10));
                int voff = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(a + 0x14));
                if (voff + vlen <= len)
                {
                    var v = r.Slice(a + voff, vlen);
                    if (type == AttrStandardInfo && vlen >= 0x24)
                    {
                        long ft = BinaryPrimitives.ReadInt64LittleEndian(v.Slice(0x08));
                        if (ft > 0 && ft < DateTime.MaxValue.Ticks - FileTimeToTicks) t.Ticks[target] = ft + FileTimeToTicks;
                        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(v.Slice(0x20));
                        if ((attrs & (0x1000 | 0x40000 | 0x400000)) != 0) t.Flags[target] |= Table.Cloud;
                        if ((attrs & 0x400) != 0) t.Flags[target] |= Table.Reparse;
                    }
                    else if (type == AttrFileName && vlen >= 0x42)
                    {
                        int nameLen = v[0x40];
                        byte ns = v[0x41];
                        byte rank = ns switch { 1 or 3 => 3, 0 => 2, _ => 1 }; // Win32 > POSIX > DOS 8.3
                        if (rank > t.NameRank[target] && 0x42 + nameLen * 2 <= vlen)
                        {
                            ulong pref = BinaryPrimitives.ReadUInt64LittleEndian(v);
                            t.Parent[target] = (int)(pref & 0xFFFFFFFFFFFF);
                            t.ParentSeq[target] = (ushort)(pref >> 48);
                            t.Name[target] = Encoding.Unicode.GetString(v.Slice(0x42, nameLen * 2));
                            t.NameRank[target] = rank;
                        }
                    }
                }
            }
            else if (type == AttrData && attrNameLen == 0)
            {
                // Only the unnamed stream is the file's content; named streams (ADS) are ignored.
                if (!nonResident) t.Size[target] = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a + 0x10));
                else if (len >= 0x38 && BinaryPrimitives.ReadInt64LittleEndian(r.Slice(a + 0x10)) == 0)
                    t.Size[target] = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(a + 0x30)); // first extent carries the real size
            }
            a += len;
        }
    }

    /// <summary>Undoes NTFS "update sequence" protection: the last two bytes of every stride were swapped out on write.</summary>
    private static bool ApplyFixups(Span<byte> r)
    {
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x04));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x06));
        if (usaCount < 2 || usaOff + usaCount * 2 > r.Length) return false;
        int stride = r.Length / (usaCount - 1);
        if (stride < 256) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(usaOff));
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * stride - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(pos)) != usn) return false;
            r[pos] = r[usaOff + i * 2];
            r[pos + 1] = r[usaOff + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Extents (LCN, cluster count) of $MFT's own data, from record 0.</summary>
    private static (List<(long Lcn, long Clusters)> Runs, long DataSize) MftRuns(ReadOnlySpan<byte> r, int cluster)
    {
        int a = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x14));
        int limit = (int)Math.Min((uint)r.Length, BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(0x18)));
        while (a + 16 <= limit)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a));
            if (type == AttrEnd) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(a + 4));
            if (len < 16 || a + len > limit) break;
            if (type == AttrData && r[a + 9] == 0 && r[a + 8] != 0)
            {
                long startVcn = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(a + 0x10));
                long lastVcn = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(a + 0x18));
                int runOff = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(a + 0x20));
                long dataSize = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(a + 0x30));
                if (startVcn != 0) break;
                var runs = DecodeRuns(r.Slice(a + runOff, len - runOff));
                long clusters = runs.Sum(x => x.Clusters);
                // A hugely fragmented $MFT continues its run list in another record; let the normal scanner handle it.
                if (clusters < lastVcn + 1 || clusters * cluster < dataSize)
                    throw new NotSupportedException("The file table is too fragmented for fast scan.");
                return (runs, dataSize);
            }
            a += len;
        }
        throw new NotSupportedException("Couldn't find the file table's data.");
    }

    private static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> s)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        int i = 0;
        while (i < s.Length && s[i] != 0)
        {
            int lenBytes = s[i] & 0x0F, offBytes = s[i] >> 4;
            i++;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || i + lenBytes + offBytes > s.Length) break;
            long length = 0;
            for (int b = 0; b < lenBytes; b++) length |= (long)s[i + b] << (8 * b);
            i += lenBytes;
            // A sparse run has no clusters on disk; $MFT is never sparse, so treat it as unsupported.
            if (offBytes == 0) throw new NotSupportedException("Unexpected sparse file table.");
            long delta = 0;
            for (int b = 0; b < offBytes; b++) delta |= (long)s[i + b] << (8 * b);
            if ((s[i + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes); // sign-extend
            i += offBytes;
            lcn += delta;
            runs.Add((lcn, length));
        }
        return runs;
    }

    // ================================================================== Win32

    private readonly record struct VolData(int BytesPerSector, int BytesPerCluster, int BytesPerRecord, long MftValidDataLength, long MftStartLcn);

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte* buf, int toRead, out int read, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);

    /// <summary>Best effort: asks Windows to write out pending changes on the volume (needs a write handle).</summary>
    private static void FlushVolume(string device)
    {
        try
        {
            using var h = CreateFileW(device, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) FlushFileBuffers(h);
        }
        catch { /* not fatal: the table may just miss the last few seconds of changes */ }
    }

    private static SafeFileHandle OpenVolume(string device)
    {
        // No buffering: never serve a stale copy of the table from an earlier scan out of the volume cache.
        var h = CreateFileW(device, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new UnauthorizedAccessException($"Couldn't open {device} ({new Win32Exception(err).Message}).");
        }
        return h;
    }

    private static VolData VolumeData(SafeFileHandle h)
    {
        var buf = new byte[128];
        if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, buf, buf.Length, out _, IntPtr.Zero))
            throw new NotSupportedException("This drive didn't return NTFS information: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var s = buf.AsSpan();
        return new VolData(
            BytesPerSector: BinaryPrimitives.ReadInt32LittleEndian(s.Slice(40)),
            BytesPerCluster: BinaryPrimitives.ReadInt32LittleEndian(s.Slice(44)),
            BytesPerRecord: BinaryPrimitives.ReadInt32LittleEndian(s.Slice(48)),
            MftValidDataLength: BinaryPrimitives.ReadInt64LittleEndian(s.Slice(56)),
            MftStartLcn: BinaryPrimitives.ReadInt64LittleEndian(s.Slice(64)));
    }

    private static void ReadAt(SafeFileHandle h, long offset, byte* buf, int len)
    {
        if (!SetFilePointerEx(h, offset, out _, 0))
            throw new IOException("Seek failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        int done = 0;
        while (done < len)
        {
            if (!ReadFile(h, buf + done, len - done, out int read, IntPtr.Zero))
                throw new IOException("Read failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (read == 0) throw new EndOfStreamException("Unexpected end of the drive.");
            done += read;
        }
    }
}
