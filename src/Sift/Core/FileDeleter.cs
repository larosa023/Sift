namespace Sift.Core;

public readonly record struct DeleteOutcome(bool Success, long Freed, string? Error);

/// <summary>Deletes files and folders, to the Recycle Bin by default.</summary>
public static class FileDeleter
{
    /// <summary>
    /// Shell file operations must run on an STA thread, so every batch goes through one here.
    /// </summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Sift delete" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <param name="contentsOnly">Delete what is inside a folder but keep the folder (caches, temp).</param>
    public static DeleteOutcome Delete(string path, long expectedSize, bool contentsOnly, bool permanent)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            bool isFile = !isDir && File.Exists(path);
            if (!isDir && !isFile) return new DeleteOutcome(true, 0, "Already gone");

            if (contentsOnly && isDir)
            {
                long before = FsUtil.Measure(path).Size;
                string? err = permanent ? DeleteContentsPermanently(path) : Recycle(Path.Join(path, "*"));
                long after = FsUtil.Measure(path).Size;
                long freed = Math.Max(0, before - after);
                // Caches and temp folders always have a few files in use; partial success is normal.
                if (err is not null && freed == 0 && after > 0) return new DeleteOutcome(false, 0, err);
                return new DeleteOutcome(true, freed, after > 0 ? "Some files were in use and were skipped" : null);
            }

            if (permanent)
            {
                if (isDir) Directory.Delete(path, recursive: true);
                else
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                return new DeleteOutcome(true, expectedSize, null);
            }

            var error = Recycle(path);
            if (error is not null) return new DeleteOutcome(false, 0, error);
            bool stillThere = isDir ? Directory.Exists(path) : File.Exists(path);
            return stillThere
                ? new DeleteOutcome(false, 0, "Some items could not be moved (in use or protected)")
                : new DeleteOutcome(true, expectedSize, null);
        }
        catch (UnauthorizedAccessException) { return new DeleteOutcome(false, 0, "Access denied — try running Sift as administrator"); }
        catch (IOException ex) { return new DeleteOutcome(false, 0, ex.Message); }
        catch (Exception ex) { return new DeleteOutcome(false, 0, ex.Message); }
    }

    private static string? Recycle(string pathOrPattern)
    {
        if (pathOrPattern.Length >= 260)
            return "Path is too long for the Recycle Bin";

        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = Native.FO_DELETE,
            pFrom = pathOrPattern + "\0\0",
            // WANTNUKEWARNING: if something is too big for the Recycle Bin, Windows asks before
            // deleting it permanently instead of silently destroying it.
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT |
                              Native.FOF_NOERRORUI | Native.FOF_WANTNUKEWARNING),
        };
        int rc = Native.SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return "Cancelled";
        return rc == 0 ? null : $"Windows could not move it to the Recycle Bin (code 0x{rc:X})";
    }

    private static string? DeleteContentsPermanently(string dir)
    {
        int failures = 0;
        foreach (var f in FsUtil.SafeFiles(dir))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            catch { failures++; }
        }
        foreach (var d in FsUtil.SafeDirectories(dir))
        {
            try { Directory.Delete(d, recursive: true); }
            catch { failures++; }
        }
        return failures > 0 ? $"{failures} item(s) were in use" : null;
    }
}
