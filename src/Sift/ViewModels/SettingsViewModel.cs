using System.Windows.Input;
using System.Windows.Media;
using Sift.Core;
using Sift.Themes;

namespace Sift.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed class AccentChoice : ObservableObject
{
    public required string Name { get; init; }
    public required string Hex { get; init; }
    public Brush Brush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;

    public SettingsViewModel(AppSettings settings, UpdateViewModel updates)
    {
        _s = settings;
        Updates = updates;
        Accents = ThemeManager.Accents.Select(a => new AccentChoice { Name = a.Name, Hex = a.Hex, IsSelected = string.Equals(a.Hex, settings.Accent, StringComparison.OrdinalIgnoreCase) }).ToList();
        if (!Accents.Any(a => a.IsSelected)) Accents[0].IsSelected = true;
        foreach (var a in Accents)
            a.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AccentChoice.IsSelected) && a.IsSelected)
                {
                    _s.Accent = a.Hex;
                    ThemeManager.Apply(_s.Theme, _s.Accent);
                    Save();
                }
            };
        OpenRepoCommand = new RelayCommand(() => FsUtil.OpenUri(Updater.RepoUrl));
        OpenDataFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(AppSettings.Folder); FsUtil.OpenInExplorer(AppSettings.Folder); });
    }

    public UpdateViewModel Updates { get; }
    public event Action? Changed;
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public IReadOnlyList<Choice<ThemeMode>> ThemeChoices { get; } = new[]
    {
        new Choice<ThemeMode>("Match Windows", ThemeMode.System),
        new Choice<ThemeMode>("Light", ThemeMode.Light),
        new Choice<ThemeMode>("Dark", ThemeMode.Dark),
    };

    public IReadOnlyList<AccentChoice> Accents { get; }

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

    public string Version => Updater.CurrentVersionText;
    public string RepoUrl => Updater.RepoUrl;
    public bool IsAdmin { get; } = Native.IsElevated();

    public ThemeMode Theme
    {
        get => _s.Theme;
        set { if (_s.Theme == value) return; _s.Theme = value; ThemeManager.Apply(value, _s.Accent); Save(); }
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
