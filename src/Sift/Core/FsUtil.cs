using System.IO.Enumeration;

namespace Sift.Core;

public readonly record struct FolderMeasure(long Size, long Files, long NewestWriteTicks)
{
    public DateTime NewestWriteUtc => NewestWriteTicks > 0 ? new DateTime(NewestWriteTicks, DateTimeKind.Utc) : DateTime.MinValue;
}

public static class FsUtil
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly record struct Item(long Length, long Ticks, FileAttributes Attributes);

    /// <summary>Total size, file count and newest write time below a folder. Does not follow junctions.</summary>
    public static FolderMeasure Measure(string dir, CancellationToken ct = default)
    {
        long size = 0, files = 0, newest = 0;
        try
        {
            if (!Directory.Exists(dir)) return default;
            var en = new FileSystemEnumerable<Item>(dir,
                (ref FileSystemEntry e) => new Item(e.Length, e.LastWriteTimeUtc.UtcTicks, e.Attributes),
                Recursive)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            foreach (var i in en)
            {
                if ((files & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (!DiskScanner.IsCloudOnly(i.Attributes)) size += i.Length;
                files++;
                if (i.Ticks > newest) newest = i.Ticks;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* inaccessible: return what we have */ }
        return new FolderMeasure(size, files, newest);
    }

    public static long SizeOf(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Measure(path, ct).Size;
        }
        catch { return 0; }
    }

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

    public static IEnumerable<string> SafeDirectories(string path, string pattern = "*")
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(path, pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public static IEnumerable<string> SafeFiles(string path, string pattern = "*")
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();
            return Directory.EnumerateFiles(path, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\') + "\\";
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is not null && Directory.Exists(parent))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{parent}\"");
            }
        }
        catch { /* ignore */ }
    }

    public static void OpenUri(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
