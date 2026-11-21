using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace Sift.Core;

/// <summary>
/// Remembers file hashes between duplicate checks (keyed by path, size and modified time), so a second run
/// only reads files that changed. Stored in %LOCALAPPDATA%\Sift\hashes.bin.
/// </summary>
public sealed class HashCache
{
    private const int Version = 1;
    private const int MaxEntries = 400_000;

    private record struct Entry(long Size, long Ticks, ulong Sample, UInt128 Full, bool Used);

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string FilePath => Path.Combine(AppSettings.Folder, "hashes.bin");

    public int Hits;
    public int Misses;

    public static HashCache Load()
    {
        var c = new HashCache();
        try
        {
            if (!File.Exists(FilePath)) return c;
            using var fs = File.OpenRead(FilePath);
            using var bs = new BrotliStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(bs, 1 << 16), Encoding.UTF8);
            if (r.ReadInt32() != Version) return c;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var path = r.ReadString();
                long size = r.ReadInt64(), ticks = r.ReadInt64();
                ulong sample = r.ReadUInt64();
                var full = new UInt128(r.ReadUInt64(), r.ReadUInt64());
                c._map[path] = new Entry(size, ticks, sample, full, false);
            }
        }
        catch { c._map.Clear(); } // damaged cache: start over
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            // Keep what this run used first, then older entries up to the cap.
            var keep = _map.OrderByDescending(kv => kv.Value.Used).Take(MaxEntries).ToList();
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bs = new BrotliStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(new BufferedStream(bs, 1 << 16), Encoding.UTF8))
            {
                w.Write(Version);
                w.Write(keep.Count);
                foreach (var (path, e) in keep)
                {
                    w.Write(path);
                    w.Write(e.Size); w.Write(e.Ticks);
                    w.Write(e.Sample);
                    w.Write((ulong)(e.Full >> 64)); w.Write((ulong)e.Full);
                }
            }
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public ulong Sample(string path, long size, long ticks, Func<ulong> compute)
    {
        if (_map.TryGetValue(path, out var e) && e.Size == size && e.Ticks == ticks && e.Sample != 0)
        {
            Interlocked.Increment(ref Hits);
            _map[path] = e with { Used = true };
            return e.Sample;
        }
        Interlocked.Increment(ref Misses);
        ulong v = compute();
        if (v != 0) _map.AddOrUpdate(path, new Entry(size, ticks, v, UInt128.Zero, true),
            (_, old) => old.Size == size && old.Ticks == ticks ? old with { Sample = v, Used = true } : new Entry(size, ticks, v, UInt128.Zero, true));
        return v;
    }

    public UInt128 Full(string path, long size, long ticks, Func<UInt128> compute)
    {
        if (_map.TryGetValue(path, out var e) && e.Size == size && e.Ticks == ticks && e.Full != UInt128.Zero)
        {
            Interlocked.Increment(ref Hits);
            _map[path] = e with { Used = true };
            return e.Full;
        }
        Interlocked.Increment(ref Misses);
        var v = compute();
        if (v != UInt128.Zero) _map.AddOrUpdate(path, new Entry(size, ticks, 0, v, true),
            (_, old) => old.Size == size && old.Ticks == ticks ? old with { Full = v, Used = true } : new Entry(size, ticks, 0, v, true));
        return v;
    }
}
