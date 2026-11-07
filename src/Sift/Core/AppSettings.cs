using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sift.Core;

public enum ThemeMode { System, Light, Dark }

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "#6366F1";

    public int GameUnplayedDays { get; set; } = 90;
    public int StaleDownloadDays { get; set; } = 60;
    public int LeftoverMinAgeDays { get; set; } = 60;
    public long DuplicateMinBytes { get; set; } = 1L * 1024 * 1024;
    public long CacheMinBytes { get; set; } = 50L * 1024 * 1024;
    public bool DuplicatesSkipSystemFolders { get; set; } = true;

    /// <summary>When false (default) everything goes to the Recycle Bin.</summary>
    public bool PermanentDelete { get; set; }
    /// <summary>Caches are rebuilt by apps, so recycling them just moves the space. Off by default.</summary>
    public bool PermanentDeleteCaches { get; set; }

    public bool AutoCheckUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public string? LastTarget { get; set; }

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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
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
