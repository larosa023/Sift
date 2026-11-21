using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sift.Core;

/// <summary>Putting things back from the Recycle Bin, and running the Windows commands some cleanups use.</summary>
public static class RecycleBin
{
    /// <param name="Path">The file or folder that was recycled, or (with <paramref name="Contents"/>) the folder whose contents were.</param>
    public sealed record Target(string Path, bool Contents);

    /// <summary>
    /// Restores items recycled between <paramref name="fromUtc"/> and <paramref name="toUtc"/> that came from the
    /// given places. Runs the Shell's own "Restore" on each, so they go back exactly where they were.
    /// Must be called on an STA thread. Returns how many items were restored.
    /// </summary>
    public static int Restore(IReadOnlyList<Target> targets, DateTime fromUtc, DateTime toUtc)
    {
        var from = fromUtc.ToLocalTime().AddSeconds(-5);
        var to = toUtc.ToLocalTime().AddSeconds(5);
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("The Windows shell isn't available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic bin = shell.NameSpace(10); // ssfBITBUCKET
            var matches = new List<(dynamic Item, string Original)>();
            foreach (dynamic item in bin.Items())
            {
                string deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string ?? "";
                object? dd = item.ExtendedProperty("System.Recycle.DateDeleted");
                if (dd is not DateTime when || when < from || when > to) continue;
                string name = item.Name as string ?? "";
                foreach (var t in targets)
                {
                    bool match;
                    if (t.Contents) match = FsUtil.IsUnder(deletedFrom, t.Path);
                    else
                    {
                        var parent = System.IO.Path.GetDirectoryName(t.Path) ?? "";
                        var file = System.IO.Path.GetFileName(t.Path);
                        // Explorer may hide known extensions in the display name.
                        match = string.Equals(deletedFrom.TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                                (string.Equals(name, file, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(name, System.IO.Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase));
                    }
                    if (match) { matches.Add((item, System.IO.Path.Combine(deletedFrom, name))); break; }
                }
            }

            int restored = 0;
            foreach (var (item, _) in matches)
            {
                if (InvokeRestore(item)) restored++;
            }
            return restored;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool InvokeRestore(dynamic item)
    {
        try
        {
            // "undelete" is the language-independent name of the Recycle Bin's Restore command.
            item.InvokeVerb("undelete");
            return true;
        }
        catch
        {
            try
            {
                foreach (dynamic verb in item.Verbs())
                {
                    string label = (verb.Name as string ?? "").Replace("&", "");
                    if (label.Contains("Restore", StringComparison.OrdinalIgnoreCase)) { verb.DoIt(); return true; }
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }

    /// <summary>Runs a Windows command, elevating if asked and not already elevated. Returns (ok, message).</summary>
    public static async Task<(bool Ok, string Message)> RunCommandAsync(string exe, string? args, bool elevated)
    {
        try
        {
            bool needRunAs = elevated && !Native.IsElevated();
            var psi = new ProcessStartInfo(exe, args ?? "")
            {
                UseShellExecute = needRunAs,
                Verb = needRunAs ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Couldn't start " + exe);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 ? (true, "Done") : (false, $"{exe} exited with code {p.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Cancelled — administrator permission wasn't given");
        }
        catch (OperationCanceledException) { return (false, $"{exe} didn't finish in time"); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
