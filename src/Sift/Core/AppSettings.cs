using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sift.Core;

public sealed class AppSettings
{
    /// <summary>Theme id from ThemeCatalog, or "system". Older settings files stored System/Light/Dark, which still map.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"theme" (the theme's own accent), "windows", or "#RRGGBB".</summary>
    public string Accent { get; set; } = "theme";
    /// <summary>2 = theme ids are Hangar's presets (1.2+).</summary>
    public int ThemeSchema { get; set; }

    public int GameUnplayedDays { get; set; } = 90;
    public int StaleDownloadDays { get; set; } = 60;
    public int LeftoverMinAgeDays { get; set; } = 60;
    public long DuplicateMinBytes { get; set; } = 1L * 1024 * 1024;
    public long CacheMinBytes { get; set; } = 50L * 1024 * 1024;
    public bool DuplicatesSkipSystemFolders { get; set; } = true;
    public long LargeOldMinBytes { get; set; } = 500L * 1024 * 1024;
    public int LargeOldDays { get; set; } = 365;

    /// <summary>When false (default) everything goes to the Recycle Bin.</summary>
    public bool PermanentDelete { get; set; }
    /// <summary>Caches are rebuilt by apps, so recycling them just moves the space. Off by default.</summary>
    public bool PermanentDeleteCaches { get; set; }

    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Read the NTFS file table directly when running as administrator.</summary>
    public bool FastScan { get; set; } = true;
    /// <summary>Save scans so Sift reopens with the last results and can show what changed.</summary>
    public bool RememberScans { get; set; } = true;
    public bool ColorByAge { get; set; }
    /// <summary>"Analyze with Sift" on folders and drives in File Explorer.</summary>
    public bool ExplorerMenu { get; set; }
    public bool SearchSubfolders { get; set; } = true;
    public double ListPaneWidth { get; set; } = 440;
    public string? SkippedVersion { get; set; }
    public string? LastTarget { get; set; }
    /// <summary>Most recent first.</summary>
    public List<string> RecentScans { get; set; } = new();
    /// <summary>Folders skipped entirely while scanning.</summary>
    public List<string> Exclusions { get; set; } = new();

    public void AddRecent(string path)
    {
        RecentScans.RemoveAll(p => string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        RecentScans.Insert(0, path);
        if (RecentScans.Count > 6) RecentScans.RemoveRange(6, RecentScans.Count - 6);
    }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sift");
    [JsonIgnore]
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings { ThemeSchema = 2 };
                if (loaded.ThemeSchema < 2)
                {
                    loaded.Theme = Sift.Themes.ThemeCatalog.MigrateFrom11(loaded.Theme);
                    loaded.ThemeSchema = 2;
                }
                return loaded;
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings { ThemeSchema = 2 };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
