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
    private string? _glyph;
    /// <summary>Folder or file icon; worked out from the path when the caller doesn't say.</summary>
    public string Glyph { get => _glyph ??= Directory.Exists(Path) ? "\uE8B7" : "\uE8A5"; init => _glyph = value; }
    public ItemAction Action { get; init; } = ItemAction.Delete;
    public string? UninstallUri { get; init; }
    public string? UninstallLabel { get; init; }
    public string? Command { get; init; }
    public string? CommandArgs { get; init; }
    public bool CommandElevated { get; init; }
    public string? CommandFrees { get; init; }
    public Risk Risk { get; init; } = Risk.Review;
    public CleanupItem? Origin { get; init; }

    public string SizeText => Format.Bytes(Size);
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string ActionText => Action switch
    {
        ItemAction.DeleteContents => "Empty folder",
        ItemAction.Uninstall => UninstallLabel ?? "Uninstall",
        ItemAction.RunCommand => UninstallLabel ?? "Run Windows command",
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
        OpenRecycleBinCommand = new RelayCommand(() => FsUtil.OpenUri("shell:RecycleBinFolder"));
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => CanUndo);
        EmptyRecycleBinCommand = new RelayCommand(EmptyRecycleBin, () => !IsWorking && RecycleBinItems > 0);
        RefreshRecycleBin();
    }

    public ICommand OpenRecycleBinCommand { get; }
    public ICommand UndoCommand { get; }

    private (DateTime From, DateTime To, List<ReviewItem> Items)? _lastRun;
    public bool CanUndo => _lastRun is not null && !IsWorking;

    /// <summary>Raised after an undo, with how many items came back.</summary>
    public event Action<int>? ItemsRestored;

    /// <summary>Puts everything the last cleanup sent to the Recycle Bin back where it was.</summary>
    public async Task UndoAsync()
    {
        if (_lastRun is not { } run) return;
        IsWorking = true;
        ProgressText = "Restoring from the Recycle Bin…";
        try
        {
            var targets = run.Items.Select(i => new RecycleBin.Target(i.Path, i.Action == ItemAction.DeleteContents)).ToList();
            int restored = await FileDeleter.RunSta(() => RecycleBin.Restore(targets, run.From, run.To));
            foreach (var i in run.Items)
            {
                bool back = i.Action == ItemAction.DeleteContents || File.Exists(i.Path) || Directory.Exists(i.Path);
                if (!back) continue;
                i.Status = ReviewStatus.Pending;
                i.Message = null;
                i.IsChecked = false;
            }
            ResultText = restored > 0 ? $"Restored {restored} item{(restored == 1 ? "" : "s")} to where they were" : "Nothing to restore — the Recycle Bin may have been emptied";
            _lastRun = null;
            Raise(nameof(CanUndo));
            RefreshRecycleBin();
            if (restored > 0) ItemsRestored?.Invoke(restored);
        }
        catch (Exception ex) { ResultText = "Couldn't restore: " + ex.Message; }
        finally
        {
            IsWorking = false;
            ProgressText = null;
            RaiseTotals();
        }
    }
    public ICommand EmptyRecycleBinCommand { get; }

    private long _recycleBinSize, _recycleBinItems;
    public long RecycleBinSize { get => _recycleBinSize; private set => Set(ref _recycleBinSize, value); }
    public long RecycleBinItems { get => _recycleBinItems; private set { if (Set(ref _recycleBinItems, value)) RelayCommand.Refresh(); } }
    public string RecycleBinText => RecycleBinItems == 0
        ? "The Recycle Bin is empty."
        : $"{Format.Bytes(RecycleBinSize)} in {Format.Count(RecycleBinItems)} item{(RecycleBinItems == 1 ? "" : "s")} — this space is only freed once the Recycle Bin is emptied.";

    public void RefreshRecycleBin()
    {
        var (size, items) = Native.RecycleBinInfo();
        RecycleBinSize = size;
        RecycleBinItems = items;
        Raise(nameof(RecycleBinText));
    }

    private void EmptyRecycleBin()
    {
        var owner = System.Windows.Application.Current?.MainWindow is { } w
            ? new System.Windows.Interop.WindowInteropHelper(w).Handle
            : IntPtr.Zero;
        long before = RecycleBinSize;
        // Windows shows its own "Are you sure?" prompt.
        if (Native.EmptyRecycleBin(owner))
        {
            _lastRun = null;
            Raise(nameof(CanUndo));
            RefreshRecycleBin();
            ResultText = $"Emptied the Recycle Bin · {Format.Bytes(Math.Max(0, before - RecycleBinSize))} freed";
        }
        else RefreshRecycleBin();
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
    public bool IsWorking { get => _isWorking; set { if (Set(ref _isWorking, value)) { Raise(nameof(CanUndo)); RelayCommand.Refresh(); } } }

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
            var handoffs = checkedItems.Where(i => i.Action == ItemAction.Uninstall).ToList();
            var commands = checkedItems.Where(i => i.Action == ItemAction.RunCommand).ToList();
            var files = checkedItems.Where(i => i.Action is ItemAction.Delete or ItemAction.DeleteContents).ToList();
            var parts = new List<string>();
            if (files.Count > 0)
            {
                string what = $"{files.Count} item{(files.Count == 1 ? "" : "s")} ({Format.Bytes(files.Sum(i => i.Size))})";
                parts.Add(Permanent ? $"Permanently delete {what}? This can't be undone." : $"Move {what} to the Recycle Bin? You can undo this afterwards.");
            }
            foreach (var c in commands)
                parts.Add($"{c.ActionText} will free {Format.Bytes(c.Size)}{(c.CommandElevated && !Native.IsElevated() ? " (Windows asks for administrator permission)" : "")}.");
            int games = handoffs.Count(h => h.UninstallUri?.Contains("uninstall", StringComparison.OrdinalIgnoreCase) == true || h.Source is "Steam" or "Epic" or "GOG");
            if (games > 0)
                parts.Add($"{games} game{(games == 1 ? "" : "s")} will open in {(games == 1 ? "its launcher" : "their launchers")} to uninstall.");
            int pages = handoffs.Count - games;
            if (pages > 0)
                parts.Add($"{pages} item{(pages == 1 ? "" : "s")} will open Windows Settings, where Windows removes {(pages == 1 ? "it" : "them")} itself.");
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
        var recycled = new List<ReviewItem>();
        long commandFreed = 0;
        var started = DateTime.UtcNow;

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

                if (item.Action == ItemAction.RunCommand && item.Command is not null)
                {
                    var (cmdOk, msg) = await RecycleBin.RunCommandAsync(item.Command, item.CommandArgs, item.CommandElevated);
                    bool gone = item.CommandFrees is null || !File.Exists(item.CommandFrees);
                    if (cmdOk && gone)
                    {
                        freed += item.Size;
                        commandFreed += item.Size;
                        ok++;
                        item.Message = $"Done · {Format.Bytes(item.Size)} freed";
                        item.Status = ReviewStatus.Done;
                        deleted.Add(item);
                    }
                    else
                    {
                        failed++;
                        item.Message = cmdOk ? "Windows didn't remove the file" : msg;
                        item.Status = ReviewStatus.Failed;
                    }
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
                    if (!permanent) recycled.Add(item);
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
        long recycledBytes = freed - commandFreed;
        if (ok > 0 && recycled.Count > 0) parts.Add($"Moved {Format.Bytes(recycled.Sum(r => r.Size))} to the Recycle Bin");
        if (ok > 0 && recycledBytes - recycled.Sum(r => r.Size) > 0) parts.Add($"Freed {Format.Bytes(recycledBytes - recycled.Sum(r => r.Size))}");
        if (commandFreed > 0) parts.Add($"Freed {Format.Bytes(commandFreed)} through Windows");
        if (handed > 0) parts.Add($"{handed} uninstaller{(handed == 1 ? "" : "s")} opened");
        if (failed > 0) parts.Add($"{failed} couldn't be removed");
        var drives = work.Select(w => Path.GetPathRoot(w.Path)).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in drives)
        {
            try
            {
                var d = new DriveInfo(root!);
                if (d.IsReady) parts.Add($"{d.Name.TrimEnd('\\')} now has {Format.Bytes(d.AvailableFreeSpace)} free");
            }
            catch { /* ignore */ }
        }
        ResultText = string.Join(" · ", parts);
        _lastRun = recycled.Count > 0 ? (started, DateTime.UtcNow, recycled) : null;
        Raise(nameof(CanUndo));
        RefreshRecycleBin();
        if (deleted.Count > 0) ItemsDeleted?.Invoke(deleted);
        RaiseTotals();
    }
}
