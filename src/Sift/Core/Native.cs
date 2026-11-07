using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Sift.Core;

internal static class Native
{
    // ---- Shell file operations (Recycle Bin) ----
    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // ---- Known folders ----
    public static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ---- DWM dark title / border ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void SetDarkFrame(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    public static void SetRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch { /* Windows 10 */ }
    }

    // ---- Recycle Bin ----
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>Total size and item count across all drives' Recycle Bins.</summary>
    public static (long Size, long Items) RecycleBinInfo()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0) return (info.i64Size, info.i64NumItems);
        }
        catch { /* ignore */ }
        return (0, 0);
    }

    /// <summary>Empties every Recycle Bin. Windows shows its own confirmation. Returns true if it was emptied.</summary>
    public static bool EmptyRecycleBin(IntPtr owner) => SHEmptyRecycleBin(owner, null, 0) == 0;

    // ---- Taskbar flash ----
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Flashes the taskbar button until the window comes to the foreground.</summary>
    public static void FlashUntilFocused(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
                uCount = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch { /* ignore */ }
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
