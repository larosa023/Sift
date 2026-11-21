using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Sift.Core;
using Sift.Themes;

namespace Sift.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed class AccentChoice : ObservableObject
{
    public required string Name { get; init; }
    public required string Value { get; set; }
    public bool IsWindows => Value == ThemeManager.WindowsAccent;
    public bool IsThemeOwn => Value == ThemeManager.ThemeAccent;
    public bool IsCustom { get; init; }
    public bool HasGlyph => IsWindows || IsThemeOwn;
    public string Glyph => IsWindows ? "\uE770" : IsThemeOwn ? "\uE790" : "";
    public Brush Brush
    {
        get
        {
            var b = new SolidColorBrush(ThemeManager.AccentColor(Value));
            b.Freeze();
            return b;
        }
    }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public void Refresh() => Raise(nameof(Brush), nameof(Value));
}

public sealed class ThemeChoice : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Blurb { get; init; } = "";
    public string? Tip { get; init; }
    public bool IsCustom => Id.StartsWith("custom:", StringComparison.Ordinal);
    /// <summary>Theme used for the preview tile ("Match Windows"/"Match Hangar" show the current pick).</summary>
    public required ThemeInfo Preview { get; set; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public void RefreshPreview(ThemeInfo t) { Preview = t; Raise(nameof(Preview)); }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;
    private bool _applying;

    public SettingsViewModel(AppSettings settings, UpdateViewModel updates)
    {
        _s = settings;
        Updates = updates;

        BuildThemes();

        Accents = ThemeManager.Accents.Select(a => new AccentChoice { Name = a.Name, Value = a.Hex }).ToList();
        var custom = new AccentChoice { Name = "Custom", Value = "#7C7CF8", IsCustom = true, IsVisible = false };
        Accents.Add(custom);
        var currentAccent = Accents.FirstOrDefault(a => !a.IsCustom && string.Equals(a.Value, settings.Accent, StringComparison.OrdinalIgnoreCase));
        if (currentAccent is null && ThemeCatalog.ParseHex(settings.Accent, out _))
        {
            custom.Value = settings.Accent;
            custom.IsVisible = true;
            currentAccent = custom;
        }
        (currentAccent ?? Accents[0]).IsSelected = true;
        CustomAccentText = ThemeCatalog.ParseHex(settings.Accent, out _) ? settings.Accent.ToUpperInvariant() : "";
        foreach (var a in Accents)
            a.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AccentChoice.IsSelected) || !a.IsSelected || _applying) return;
                _s.Accent = a.Value;
                Apply();
            };

        Exclusions = new ObservableCollection<string>(settings.Exclusions);
        AddExclusionCommand = new RelayCommand(AddExclusion);
        RemoveExclusionCommand = new RelayCommand(p => { if (p is string path) RemoveExclusion(path); });

        OpenRepoCommand = new RelayCommand(() => FsUtil.OpenUri(Updater.RepoUrl));
        OpenDataFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(AppSettings.Folder); FsUtil.OpenInExplorer(AppSettings.Folder); });
        OpenThemesFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(ThemeCatalog.SiftThemesFolder);
            var sample = Path.Combine(ThemeCatalog.SiftThemesFolder, "my-theme.json.example");
            if (!File.Exists(sample)) File.WriteAllText(sample, SampleTheme);
            FsUtil.OpenInExplorer(ThemeCatalog.SiftThemesFolder);
        });
        ReloadThemesCommand = new RelayCommand(() => { BuildThemes(); ThemeManager.Apply(_s.Theme, _s.Accent); });
        ApplyCustomAccentCommand = new RelayCommand(ApplyCustomAccent);
        ClearSavedScansCommand = new RelayCommand(() => { Snapshots.DeleteAll(); Raise(nameof(SavedScansText)); });

        ThemeManager.ThemeChanged += () =>
        {
            foreach (var t in Themes.Where(t => t.Id is ThemeCatalog.SystemId or ThemeCatalog.HangarId))
                t.RefreshPreview(ThemeManager.Resolve(t.Id));
            foreach (var a in Accents.Where(a => a.IsWindows || a.IsThemeOwn)) a.Refresh();
            Raise(nameof(AccentsEnabled), nameof(AccentNote));
        };
    }

    private void BuildThemes()
    {
        var list = new List<ThemeChoice>
        {
            new() { Id = ThemeCatalog.SystemId, Name = "Match Windows", Blurb = "Light or Dark, like Windows", Preview = ThemeManager.Resolve(ThemeCatalog.SystemId) },
        };
        if (ThemeCatalog.HangarInstalled || _s.Theme == ThemeCatalog.HangarId)
            list.Add(new ThemeChoice { Id = ThemeCatalog.HangarId, Name = "Match Hangar", Blurb = "Follows Hangar's theme, live", Preview = ThemeManager.Resolve(ThemeCatalog.HangarId) });
        list.AddRange(ThemeCatalog.All.Select(t => new ThemeChoice { Id = t.Id, Name = t.Name, Blurb = t.Blurb, Preview = t }));
        list.AddRange(ThemeCatalog.LoadCustom().Select(t => new ThemeChoice { Id = t.Id, Name = t.Name, Blurb = t.Blurb, Preview = t, Tip = t.Source }));
        var current = list.FirstOrDefault(t => t.Id == _s.Theme) ?? list[0];
        current.IsSelected = true;
        foreach (var t in list)
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ThemeChoice.IsSelected) || !t.IsSelected || _applying) return;
                _s.Theme = t.Id;
                Apply();
            };
        Themes = list;
        Raise(nameof(Themes), nameof(CustomThemeCount));
    }

    private const string SampleTheme = """
        {
          "schema": 1,
          "name": "My Theme",
          "description": "Rename this file to my-theme.json, edit the colours, then press Reload in Settings",
          "mode": "dark",
          "colors": {
            "background": "#2E3440",
            "sidebar": "#272C36",
            "surface": "#3B4252",
            "surfaceHover": "#434C5E",
            "surfaceStrong": "#4C566A",
            "text": "#ECEFF4",
            "subtext": "#C0C8D6",
            "faint": "#7B8698",
            "accent": "#88C0D0"
          }
        }
        """;

    public ICommand OpenThemesFolderCommand { get; }
    public ICommand ReloadThemesCommand { get; }
    public ICommand ApplyCustomAccentCommand { get; }
    public ICommand ClearSavedScansCommand { get; }

    public int CustomThemeCount => Themes.Count(t => t.IsCustom);
    public bool AccentsEnabled => _s.Theme != ThemeCatalog.HangarId;
    public string AccentNote => AccentsEnabled
        ? "“Theme's own” uses the colour each theme was designed with; the Windows swatch follows Settings › Personalization › Colors."
        : "Following Hangar, so the accent comes from Hangar too.";

    private string _customAccentText = "";
    public string CustomAccentText { get => _customAccentText; set => Set(ref _customAccentText, value ?? ""); }

    private void ApplyCustomAccent()
    {
        if (!ThemeCatalog.ParseHex(CustomAccentText, out var v)) { CustomAccentError = "Use a colour like #4F8CFF"; return; }
        CustomAccentError = null;
        var hex = "#" + v.ToString("X6");
        CustomAccentText = hex;
        var custom = Accents.First(a => a.IsCustom);
        custom.Value = hex;
        custom.IsVisible = true;
        custom.Refresh();
        _s.Accent = hex;
        Apply();
    }

    private string? _customAccentError;
    public string? CustomAccentError { get => _customAccentError; set => Set(ref _customAccentError, value); }

    public string SavedScansText
    {
        get
        {
            long bytes = Snapshots.DiskUsage();
            return bytes == 0 ? "No saved scans." : $"Saved scans use {Format.Bytes(bytes)}.";
        }
    }

    public UpdateViewModel Updates { get; }
    public event Action? Changed;
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand AddExclusionCommand { get; }
    public ICommand RemoveExclusionCommand { get; }

    public List<ThemeChoice> Themes { get; private set; } = new();
    public List<AccentChoice> Accents { get; }
    public ObservableCollection<string> Exclusions { get; }
    public bool HasExclusions => Exclusions.Count > 0;

    public IReadOnlyList<Choice<int>> DayChoices { get; } = new[]
    {
        new Choice<int>("30 days", 30), new Choice<int>("60 days", 60), new Choice<int>("90 days", 90),
        new Choice<int>("6 months", 180), new Choice<int>("1 year", 365), new Choice<int>("2 years", 730),
    };

    public IReadOnlyList<Choice<long>> DuplicateSizeChoices { get; } = new[]
    {
        new Choice<long>("100 KB", 100L * 1024), new Choice<long>("1 MB", 1L << 20), new Choice<long>("10 MB", 10L << 20),
        new Choice<long>("100 MB", 100L << 20), new Choice<long>("1 GB", 1L << 30),
    };

    public IReadOnlyList<Choice<long>> LargeSizeChoices { get; } = new[]
    {
        new Choice<long>("100 MB", 100L << 20), new Choice<long>("250 MB", 250L << 20), new Choice<long>("500 MB", 500L << 20),
        new Choice<long>("1 GB", 1L << 30), new Choice<long>("5 GB", 5L << 30),
    };

    public IReadOnlyList<Choice<long>> CacheSizeChoices { get; } = new[]
    {
        new Choice<long>("10 MB", 10L << 20), new Choice<long>("50 MB", 50L << 20), new Choice<long>("100 MB", 100L << 20),
        new Choice<long>("500 MB", 500L << 20), new Choice<long>("1 GB", 1L << 30),
    };

    public sealed record Shortcut(string Keys, string Action);
    public IReadOnlyList<Shortcut> Shortcuts { get; } = new Shortcut[]
    {
        new("Ctrl+1 … Ctrl+4", "Overview, Cleanup, Review, Settings"),
        new("F5", "Rescan the current location"),
        new("Ctrl+F", "Filter or search (wildcards like *.mp4 work)"),
        new("Enter / double-click", "Zoom into the selected folder"),
        new("Backspace / mouse back", "Up one folder"),
        new("Delete", "Add the selection to Review"),
        new("Ctrl+E", "Show the selection in File Explorer"),
        new("Esc", "Clear filter, highlight and selection"),
        new("Ctrl+Z", "Undo the last cleanup (restores from the Recycle Bin)"),
        new("Drag and drop", "Drop a folder or drive on the window to scan it"),
    };

    public string Version => Updater.CurrentVersionText;
    public string RepoUrl => Updater.RepoUrl;

    /// <summary>Selects a theme by id (used by the smoke test and keyboard-free callers).</summary>
    public void SelectTheme(string id)
    {
        var t = Themes.FirstOrDefault(x => x.Id == id);
        if (t is not null) t.IsSelected = true;
    }

    private void Apply()
    {
        _applying = true;
        try
        {
            foreach (var t in Themes) t.IsSelected = t.Id == _s.Theme;
            bool found = false;
            foreach (var a in Accents)
            {
                a.IsSelected = !found && string.Equals(a.Value, _s.Accent, StringComparison.OrdinalIgnoreCase);
                found |= a.IsSelected;
            }
        }
        finally { _applying = false; }
        ThemeManager.Apply(_s.Theme, _s.Accent);
        Save(nameof(Themes));
        Raise(nameof(AccentsEnabled), nameof(AccentNote));
    }

    public void AddExclusion(string path)
    {
        path = path.TrimEnd('\\');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (Exclusions.Any(e => string.Equals(e, path, StringComparison.OrdinalIgnoreCase))) return;
        Exclusions.Add(path);
        _s.Exclusions = Exclusions.ToList();
        Save(nameof(HasExclusions));
    }

    private void AddExclusion()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder Sift should skip when scanning", Multiselect = true };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FolderNames) AddExclusion(f);
    }

    private void RemoveExclusion(string path)
    {
        Exclusions.Remove(path);
        _s.Exclusions = Exclusions.ToList();
        Save(nameof(HasExclusions));
    }

    public int GameUnplayedDays { get => _s.GameUnplayedDays; set { _s.GameUnplayedDays = value; Save(); } }
    public int StaleDownloadDays { get => _s.StaleDownloadDays; set { _s.StaleDownloadDays = value; Save(); } }
    public int LeftoverMinAgeDays { get => _s.LeftoverMinAgeDays; set { _s.LeftoverMinAgeDays = value; Save(); } }
    public long DuplicateMinBytes { get => _s.DuplicateMinBytes; set { _s.DuplicateMinBytes = value; Save(); } }
    public long CacheMinBytes { get => _s.CacheMinBytes; set { _s.CacheMinBytes = value; Save(); } }
    public long LargeOldMinBytes { get => _s.LargeOldMinBytes; set { _s.LargeOldMinBytes = value; Save(); } }
    public int LargeOldDays { get => _s.LargeOldDays; set { _s.LargeOldDays = value; Save(); } }
    public bool DuplicatesSkipSystemFolders { get => _s.DuplicatesSkipSystemFolders; set { _s.DuplicatesSkipSystemFolders = value; Save(); } }
    public bool PermanentDelete { get => _s.PermanentDelete; set { _s.PermanentDelete = value; Save(); } }
    public bool PermanentDeleteCaches { get => _s.PermanentDeleteCaches; set { _s.PermanentDeleteCaches = value; Save(); } }
    public bool AutoCheckUpdates { get => _s.AutoCheckUpdates; set { _s.AutoCheckUpdates = value; Save(); } }
    public bool FastScan { get => _s.FastScan; set { _s.FastScan = value; Save(); } }
    public bool RememberScans { get => _s.RememberScans; set { _s.RememberScans = value; Save(); if (!value) { Snapshots.DeleteAll(); Raise(nameof(SavedScansText)); } } }
    public bool IsElevated { get; } = Native.IsElevated();

    public bool ExplorerMenuEnabled
    {
        get => ExplorerMenu.IsInstalled();
        set
        {
            try
            {
                if (value) ExplorerMenu.Install(); else ExplorerMenu.Remove();
                _s.ExplorerMenu = value;
            }
            catch { /* registry blocked by policy */ }
            Save();
        }
    }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _s.Save();
        Raise(name);
        Changed?.Invoke();
    }
}
