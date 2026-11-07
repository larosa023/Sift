using System.Globalization;

namespace Sift.Core;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        string fmt = v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00";
        return v.ToString(fmt, CultureInfo.CurrentCulture) + " " + Units[u];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Age(DateTime utc)
    {
        if (utc == DateTime.MinValue || utc.Year < 1990) return "—";
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 0) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Plural((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1) return Plural((int)span.TotalHours, "hour");
        if (span.TotalDays < 31) return Plural((int)span.TotalDays, "day");
        if (span.TotalDays < 365) return Plural((int)(span.TotalDays / 30.4), "month");
        return Plural((int)(span.TotalDays / 365.25), "year");
    }

    public static string Date(DateTime utc) =>
        utc == DateTime.MinValue || utc.Year < 1990 ? "—" : utc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalMinutes}m {t.Seconds}s";

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
}
