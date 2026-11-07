using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Sift.Core;
using Sift.Core.Finders;

namespace Sift.ViewModels;

public enum ReviewStatus { Pending, Working, Done, Failed, Handed }

public sealed class ReviewItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public long Size { get; init; }
    public string Source { get; init; } = "";
    public string Glyph { get; init; } = "";
    public ItemAction Action { get; init; } = ItemAction.Delete;
    public string? UninstallUri { get; init; }
    public string? UninstallLabel { get; init; }
    public Risk Risk { get; init; } = Risk.Review;
    public CleanupItem? Origin { get; init; }

    public string SizeText => Format.Bytes(Size);
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string ActionText => Action switch
    {
        ItemAction.DeleteContents => "Empty folder",
        ItemAction.Uninstall => UninstallLabel ?? "Uninstall",
        _ => "Delete",
    };

    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private ReviewStatus _status;
    public ReviewStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Raise(nameof(StatusText), nameof(IsFinished)); }
    }

    private string? _message;
    public string? Message { get => _message; set { if (Set(ref _message, value)) Raise(nameof(StatusText)); } }

    public bool IsFinished => Status is ReviewStatus.Done or ReviewStatus.Handed;

    public string StatusText => Status switch
    {
        ReviewStatus.Working => "Working…",
        ReviewStatus.Done => Message ?? "Done",
        ReviewStatus.Failed => Message ?? "Failed",
        ReviewStatus.Handed => Message ?? "Opened uninstaller",
        _ => "",
    };
}

public sealed class ReviewViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ReviewViewModel(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += OnItemsChanged;

        DeleteCommand = new RelayCommand(() => IsConfirming = true, () => !IsWorking && CheckedCount > 0);
        ConfirmCommand = new RelayCommand(async () => await RunAsync(), () => !IsWorking);
        CancelConfirmCommand = new RelayCommand(() => IsConfirming = false);
        RemoveCommand = new RelayCommand(p => { if (p is ReviewItem i) Remove(i); });
        RemoveCheckedCommand = new RelayCommand(() => { foreach (var i in Items.Where(x => x.IsChecked).ToList()) Remove(i); }, () => !IsWorking);
        ClearCommand = new RelayCommand(() => { foreach (var i in Items.ToList()) Remove(i); }, () => !IsWorking && Items.Count > 0);
        ClearFinishedCommand = new RelayCommand(() => { foreach (var i in Items.Where(x => x.IsFinished).ToList()) Remove(i); });
        OpenCommand = new RelayCommand(p => { if (p is ReviewItem i) FsUtil.OpenInExplorer(i.Path); });
        CheckAllCommand = new RelayCommand(() => { foreach (var i in Items) i.IsChecked = true; });
    }

    public ObservableCollection<ReviewItem> Items { get; } = new();

    /// <summary>Raised after deletions so the scan tree and treemap can drop removed items.</summary>
    public event Action<IReadOnlyList<ReviewItem>>? ItemsDeleted;

    public ICommand DeleteCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RemoveCheckedCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CheckAllCommand { get; }

    private bool _isConfirming;
    public bool IsConfirming { get => _isConfirming; set => Set(ref _isConfirming, value); }

    private bool _isWorking;
    public bool IsWorking { get => _isWorking; set { if (Set(ref _isWorking, value)) RelayCommand.Refresh(); } }

    private string? _progressText;
    public string? ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    private string? _resultText;
    public string? ResultText { get => _resultText; set => Set(ref _resultText, value); }

    public int PendingCount => Items.Count(i => !i.IsFinished);
    public int CheckedCount => Items.Count(i => i.IsChecked && !i.IsFinished);
    public long CheckedSize => Items.Where(i => i.IsChecked && !i.IsFinished).Sum(i => i.Size);
    public string CheckedSizeText => Format.Bytes(CheckedSize);
    public bool IsEmpty => Items.Count == 0;
    public bool Permanent => _settings.PermanentDelete;
    public string DeleteButtonText => Permanent ? "Delete permanently" : "Move to Recycle Bin";
    public string Subtitle => Permanent
        ? "Nothing is removed until you confirm. Permanent delete is on in Settings — items skip the Recycle Bin."
        : "Nothing is removed until you confirm. Files and folders go to the Recycle Bin, so you can restore them.";

    public string ConfirmText
    {
        get
        {
            var checkedItems = Items.Where(i => i.IsChecked && !i.IsFinished).ToList();
            int uninstall = checkedItems.Count(i => i.Action == ItemAction.Uninstall);
            int files = checkedItems.Count - uninstall;
            var parts = new List<string>();
            if (files > 0)
                parts.Add(Permanent
                    ? $"Permanently delete {files} item{(files == 1 ? "" : "s")} ({Format.Bytes(checkedItems.Where(i => i.Action != ItemAction.Uninstall).Sum(i => i.Size))})? This can't be undone."
                    : $"Move {files} item{(files == 1 ? "" : "s")} ({Format.Bytes(checkedItems.Where(i => i.Action != ItemAction.Uninstall).Sum(i => i.Size))}) to the Recycle Bin?");
            if (uninstall > 0)
                parts.Add($"{uninstall} game{(uninstall == 1 ? "" : "s")} will open in {(uninstall == 1 ? "its launcher" : "their launchers")} to uninstall.");
            return string.Join(" ", parts);
        }
    }

    public void SettingsChanged() => Raise(nameof(Permanent), nameof(DeleteButtonText), nameof(Subtitle));

    public bool Contains(string path) =>
        Items.Any(i => !i.IsFinished && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Add(ReviewItem item)
    {
        if (Contains(item.Path)) return;
        // Adding a folder makes queued items inside it redundant.
        foreach (var inner in Items.Where(i => !i.IsFinished && item.Action == ItemAction.Delete && FsUtil.IsUnder(i.Path, item.Path) && !string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)).ToList())
            Remove(inner);
        if (Items.Any(i => !i.IsFinished && i.Action == ItemAction.Delete && FsUtil.IsUnder(item.Path, i.Path))) return;
        Items.Add(item);
        if (item.Origin is not null) item.Origin.IsQueued = true;
        ResultText = null;
    }

    public void Remove(ReviewItem item)
    {
        Items.Remove(item);
        if (item.Origin is not null) item.Origin.IsQueued = false;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ReviewItem i in e.NewItems) i.PropertyChanged += ItemChanged;
        if (e.OldItems is not null) foreach (ReviewItem i in e.OldItems) i.PropertyChanged -= ItemChanged;
        RaiseTotals();
    }

    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReviewItem.IsChecked) or nameof(ReviewItem.Status)) RaiseTotals();
    }

    private void RaiseTotals()
    {
        Raise(nameof(PendingCount), nameof(CheckedCount), nameof(CheckedSize), nameof(CheckedSizeText), nameof(IsEmpty), nameof(ConfirmText));
        RelayCommand.Refresh();
    }

    private async Task RunAsync()
    {
        IsConfirming = false;
        IsWorking = true;
        ResultText = null;
        var work = Items.Where(i => i.IsChecked && !i.IsFinished).ToList();
        long freed = 0;
        int ok = 0, failed = 0, handed = 0;
        var deleted = new List<ReviewItem>();

        try
        {
            int n = 0;
            foreach (var item in work)
            {
                n++;
                ProgressText = $"{n} of {work.Count} · {item.Title}";
                item.Status = ReviewStatus.Working;

                if (item.Action == ItemAction.Uninstall && item.UninstallUri is not null)
                {
                    FsUtil.OpenUri(item.UninstallUri);
                    item.Status = ReviewStatus.Handed;
                    item.Message = item.UninstallLabel is null ? "Opened uninstaller" : $"Opened — {item.UninstallLabel.ToLowerInvariant()}";
                    handed++;
                    await Task.Delay(400);
                    continue;
                }

                bool contents = item.Action == ItemAction.DeleteContents;
                bool permanent = _settings.PermanentDelete || (contents && _settings.PermanentDeleteCaches);
                var outcome = await FileDeleter.RunSta(() => FileDeleter.Delete(item.Path, item.Size, contents, permanent));

                if (outcome.Success)
                {
                    freed += outcome.Freed;
                    ok++;
                    item.Message = outcome.Error is null
                        ? (permanent ? $"Deleted · {Format.Bytes(outcome.Freed)}" : $"In Recycle Bin · {Format.Bytes(outcome.Freed)}")
                        : $"{Format.Bytes(outcome.Freed)} cleared · {outcome.Error}";
                    item.Status = ReviewStatus.Done;
                    deleted.Add(item);
                }
                else
                {
                    failed++;
                    item.Message = outcome.Error;
                    item.Status = ReviewStatus.Failed;
                }
            }
        }
        finally
        {
            IsWorking = false;
            ProgressText = null;
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add(_settings.PermanentDelete ? $"Freed {Format.Bytes(freed)}" : $"Moved {Format.Bytes(freed)} to the Recycle Bin");
        if (handed > 0) parts.Add($"{handed} uninstaller{(handed == 1 ? "" : "s")} opened");
        if (failed > 0) parts.Add($"{failed} couldn't be removed");
        ResultText = string.Join(" · ", parts);
        if (deleted.Count > 0) ItemsDeleted?.Invoke(deleted);
        RaiseTotals();
    }
}
