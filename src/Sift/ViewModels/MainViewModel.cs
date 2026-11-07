using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Sift.Core;

namespace Sift.ViewModels;

public enum Page { Overview, Cleanup, Review, Settings }

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Review = new ReviewViewModel(settings);
        Overview = new OverviewViewModel(settings, Review);
        Cleanup = new CleanupViewModel(settings, Review, () => Overview.Root);
        Update = new UpdateViewModel(settings);
        SettingsPage = new SettingsViewModel(settings, Update);
        SettingsPage.Changed += () => Review.SettingsChanged();
        Overview.ExcludeRequested += path =>
        {
            SettingsPage.AddExclusion(path);
            Toast = $"{path} will be skipped in future scans · change this in Settings";
        };

        Review.ItemsDeleted += deleted =>
        {
            Overview.ApplyDeletions(deleted);
            Cleanup.ApplyDeletions(deleted);
        };
        Review.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && CurrentPage != Page.Review)
                Toast = $"Added to Review · {Review.PendingCount} waiting";
        };
        Review.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReviewViewModel.PendingCount)) Raise(nameof(ReviewBadge), nameof(HasReviewBadge)); };

        NavigateCommand = new RelayCommand(p => { if (p is Page pg) CurrentPage = pg; else if (p is string s && Enum.TryParse<Page>(s, out var parsed)) CurrentPage = parsed; });
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdmin);
    }

    public AppSettings Settings { get; }
    public OverviewViewModel Overview { get; }
    public CleanupViewModel Cleanup { get; }
    public ReviewViewModel Review { get; }
    public UpdateViewModel Update { get; }
    public SettingsViewModel SettingsPage { get; }

    public ICommand NavigateCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    private Page _currentPage = Page.Overview;
    public Page CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, value)) return;
            if (value == Page.Review) Review.RefreshRecycleBin();
        }
    }

    private string? _toast;
    /// <summary>A short, self-dismissing status message shown at the bottom of the window.</summary>
    public string? Toast
    {
        get => _toast;
        set
        {
            if (!Set(ref _toast, value) || value is null) return;
            var mine = value;
            _ = Task.Delay(4500).ContinueWith(_ => { if (Toast == mine) Toast = null; }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    public bool IsAdmin { get; } = Native.IsElevated();
    public string Version => Updater.CurrentVersionText;
    public string ReviewBadge => Review.PendingCount > 99 ? "99+" : Review.PendingCount.ToString();
    public bool HasReviewBadge => Review.PendingCount > 0;

    public async void OnStartup()
    {
        if (Settings.AutoCheckUpdates)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await Update.CheckAsync(manual: false);
        }
    }

    private void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt.
        }
    }
}
