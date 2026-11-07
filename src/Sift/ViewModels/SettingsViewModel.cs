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
    public required string Value { get; init; }
    public bool IsWindows => Value == ThemeManager.WindowsAccent;
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
    public void Refresh() => Raise(nameof(Brush));
}

public sealed class ThemeChoice : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Blurb { get; init; } = "";
    /// <summary>Theme used for the preview tile ("Match Windows" shows the current system pick).</summary>
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

        Themes = new List<ThemeChoice>
        {
            new() { Id = ThemeCatalog.SystemId, Name = "Match Windows", Blurb = "Light or Dark, following Windows", Preview = ThemeManager.Resolve(ThemeCatalog.SystemId) },
        };
        Themes.AddRange(ThemeCatalog.All.Select(t => new ThemeChoice { Id = t.Id, Name = t.Name, Blurb = t.Blurb, Preview = t }));
        var currentTheme = Themes.FirstOrDefault(t => t.Id == ThemeManager.CurrentThemeId) ?? Themes[0];
        currentTheme.IsSelected = true;
        foreach (var t in Themes)
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ThemeChoice.IsSelected) || !t.IsSelected || _applying) return;
                _s.Theme = t.Id;
                Apply();
            };

        Accents = ThemeManager.Accents.Select(a => new AccentChoice { Name = a.Name, Value = a.Hex }).ToList();
        var currentAccent = Accents.FirstOrDefault(a => string.Equals(a.Value, settings.Accent, StringComparison.OrdinalIgnoreCase)) ?? Accents[1];
        currentAccent.IsSelected = true;
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

        ThemeManager.ThemeChanged += () =>
        {
            Themes[0].RefreshPreview(ThemeManager.Resolve(ThemeCatalog.SystemId));
            Accents.First(a => a.IsWindows).Refresh();
        };
    }

    public UpdateViewModel Updates { get; }
    public event Action? Changed;
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand AddExclusionCommand { get; }
    public ICommand RemoveExclusionCommand { get; }

    public List<ThemeChoice> Themes { get; }
    public IReadOnlyList<AccentChoice> Accents { get; }
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
        new("Ctrl+F", "Filter the folder list"),
        new("Enter / double-click", "Zoom into the selected folder"),
        new("Backspace / mouse back", "Up one folder"),
        new("Delete", "Add the selection to Review"),
        new("Ctrl+E", "Show the selection in File Explorer"),
        new("Esc", "Clear filter, highlight and selection"),
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
            foreach (var a in Accents) a.IsSelected = string.Equals(a.Value, _s.Accent, StringComparison.OrdinalIgnoreCase);
        }
        finally { _applying = false; }
        ThemeManager.Apply(_s.Theme, _s.Accent);
        Save(nameof(Themes));
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
    public bool DuplicatesSkipSystemFolders { get => _s.DuplicatesSkipSystemFolders; set { _s.DuplicatesSkipSystemFolders = value; Save(); } }
    public bool PermanentDelete { get => _s.PermanentDelete; set { _s.PermanentDelete = value; Save(); } }
    public bool PermanentDeleteCaches { get => _s.PermanentDeleteCaches; set { _s.PermanentDeleteCaches = value; Save(); } }
    public bool AutoCheckUpdates { get => _s.AutoCheckUpdates; set { _s.AutoCheckUpdates = value; Save(); } }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _s.Save();
        Raise(name);
        Changed?.Invoke();
    }
}
