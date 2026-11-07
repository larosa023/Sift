using System.Windows;
using System.Windows.Input;
using Sift.Core;

namespace Sift.ViewModels;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Error }

public sealed class UpdateViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Updater _updater = new();
    private CancellationTokenSource? _cts;

    public UpdateViewModel(AppSettings settings)
    {
        _settings = settings;
        CheckCommand = new RelayCommand(() => _ = CheckAsync(manual: true), () => State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing));
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => Latest is not null && State is UpdateState.Available or UpdateState.Error);
        LaterCommand = new RelayCommand(() => BannerVisible = false);
        SkipCommand = new RelayCommand(() =>
        {
            if (Latest is not null) { _settings.SkippedVersion = Latest.Version.ToString(3); _settings.Save(); }
            BannerVisible = false;
        });
        NotesCommand = new RelayCommand(() => FsUtil.OpenUri(Latest?.PageUrl ?? $"{Updater.RepoUrl}/releases"));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => State == UpdateState.Downloading);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand NotesCommand { get; }
    public ICommand CancelCommand { get; }

    public string CurrentVersion => Updater.CurrentVersionText;

    private UpdateState _state;
    public UpdateState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) { Raise(nameof(StatusText), nameof(IsDownloading)); RelayCommand.Refresh(); } }
    }

    private UpdateInfo? _latest;
    public UpdateInfo? Latest
    {
        get => _latest;
        private set { if (Set(ref _latest, value)) Raise(nameof(LatestVersion), nameof(BannerText), nameof(StatusText)); }
    }

    public string LatestVersion => Latest?.Version.ToString(3) ?? "";

    private bool _bannerVisible;
    public bool BannerVisible { get => _bannerVisible; set => Set(ref _bannerVisible, value); }

    private double _progress;
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(StatusText), nameof(BannerText)); } }

    private string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(StatusText), nameof(BannerText)); } }

    public bool IsDownloading => State is UpdateState.Downloading or UpdateState.Installing;

    public string BannerText => State switch
    {
        UpdateState.Downloading => $"Downloading Sift {LatestVersion}… {Progress:P0}",
        UpdateState.Installing => "Installing — Sift will restart in a moment",
        UpdateState.Error => $"Update failed: {Error}",
        _ => $"Sift {LatestVersion} is available — you have {CurrentVersion}.",
    };

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"You're on the latest version ({CurrentVersion}).",
        UpdateState.Available => $"Version {LatestVersion} is available.",
        UpdateState.Downloading => $"Downloading {LatestVersion}… {Progress:P0}",
        UpdateState.Installing => "Installing…",
        UpdateState.Error => $"Couldn't update: {Error}",
        _ => $"Version {CurrentVersion}",
    };

    public async Task CheckAsync(bool manual)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        State = UpdateState.Checking;
        Error = null;
        try
        {
            var info = await _updater.CheckAsync();
            Latest = info;
            if (info is null)
            {
                State = UpdateState.UpToDate;
                return;
            }
            State = UpdateState.Available;
            bool skipped = !manual && _settings.SkippedVersion == info.Version.ToString(3);
            BannerVisible = !skipped;
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
            State = manual ? UpdateState.Error : UpdateState.Idle;
        }
    }

    public async Task InstallAsync()
    {
        if (Latest is null) return;
        _cts = new CancellationTokenSource();
        BannerVisible = true;
        Error = null;
        State = UpdateState.Downloading;
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var file = await _updater.DownloadAsync(Latest, progress, _cts.Token);
            State = UpdateState.Installing;
            await Task.Delay(300);
            Updater.ApplyAndRestart(file);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
            State = UpdateState.Error;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => "couldn't reach GitHub. Check your connection and try again.",
        TaskCanceledException => "the request timed out.",
        _ => ex.Message,
    };
}
