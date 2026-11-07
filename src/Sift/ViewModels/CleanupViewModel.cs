using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Sift.Core;
using Sift.Core.Finders;

namespace Sift.ViewModels;

public sealed class FinderViewModel : ObservableObject
{
    private readonly ICleanupFinder _finder;
    private readonly Func<FinderContext> _context;
    private CancellationTokenSource? _cts;

    public FinderViewModel(ICleanupFinder finder, Func<FinderContext> context)
    {
        _finder = finder;
        _context = context;
        View = CollectionViewSource.GetDefaultView(Items);
        if (finder.Grouped) View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupItem.Group)));
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
    }

    public string Id => _finder.Id;
    public string Title => _finder.Title;
    public string Description => _finder.Description;
    public string Glyph => _finder.Glyph;
    public bool NeedsScan => _finder.NeedsScan;

    public ObservableCollection<CleanupItem> Items { get; } = new();
    public ICollectionView View { get; }
    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) { Raise(nameof(StatusText), nameof(ShowEmpty)); RelayCommand.Refresh(); } }
    }

    private bool _hasRun;
    public bool HasRun { get => _hasRun; private set { if (Set(ref _hasRun, value)) Raise(nameof(StatusText), nameof(ShowEmpty), nameof(ShowIntro)); } }

    private string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(StatusText), nameof(ShowEmpty)); } }

    private string _progress = "";
    public string Progress { get => _progress; private set => Set(ref _progress, value); }

    public long TotalSize => Items.Sum(i => i.Size);
    public int Count => Items.Count;
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedSize => Items.Where(i => i.IsSelected).Sum(i => i.Size);
    public string SelectionText => SelectedCount == 0 ? "Nothing selected" : $"{SelectedCount} selected · {Format.Bytes(SelectedSize)}";
    public bool ShowEmpty => HasRun && !IsRunning && Error is null && Items.Count == 0;
    public bool ShowIntro => !HasRun && !IsRunning;

    public string StatusText =>
        IsRunning ? "Checking…" :
        Error is not null ? "Couldn't run" :
        !HasRun ? "Not checked yet" :
        Items.Count == 0 ? "Nothing found" :
        $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · {Format.Bytes(TotalSize)}";

    public string BigNumber => HasRun && Items.Count > 0 ? Format.Bytes(TotalSize) : "";

    public async Task RunAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Error = null;
        Progress = "Starting…";
        try
        {
            var baseCtx = _context();
            var ctx = new FinderContext
            {
                Settings = baseCtx.Settings,
                Scan = baseCtx.Scan,
                Status = new Progress<string>(s => Progress = s),
            };
            var found = await _finder.FindAsync(ctx, _cts.Token);
            foreach (var i in Items) i.PropertyChanged -= ItemChanged;
            Items.Clear();
            foreach (var i in found)
            {
                i.PropertyChanged += ItemChanged;
                Items.Add(i);
            }
            HasRun = true;
        }
        catch (Exception ex) when (ex is OperationCanceledException || (ex is AggregateException ae && ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException)))
        {
            Progress = "";
        }
        catch (Exception ex)
        {
            Error = ex is AggregateException ae ? ae.Flatten().InnerExceptions[0].Message : ex.Message;
            HasRun = true;
        }
        finally
        {
            IsRunning = false;
            Progress = "";
            _cts.Dispose();
            _cts = null;
            RaiseTotals();
        }
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupItem.IsSelected)) Raise(nameof(SelectedCount), nameof(SelectedSize), nameof(SelectionText));
    }

    public void RaiseTotals() =>
        Raise(nameof(TotalSize), nameof(Count), nameof(SelectedCount), nameof(SelectedSize), nameof(SelectionText),
              nameof(StatusText), nameof(BigNumber), nameof(ShowEmpty), nameof(ShowIntro));

    /// <summary>Drops items that were deleted elsewhere (paths at or under a removed path).</summary>
    public void RemovePaths(IReadOnlyList<string> paths)
    {
        var gone = Items.Where(i => paths.Any(p => FsUtil.IsUnder(i.Path, p))).ToList();
        foreach (var i in gone) { i.PropertyChanged -= ItemChanged; Items.Remove(i); }
        if (gone.Count > 0) RaiseTotals();
    }
}

public sealed class CleanupViewModel : ObservableObject
{
    private readonly ReviewViewModel _review;

    public CleanupViewModel(AppSettings settings, ReviewViewModel review, Func<FileNode?> scan)
    {
        _review = review;
        FinderContext Ctx() => new() { Settings = settings, Scan = scan() };
        Finders = new ObservableCollection<FinderViewModel>(new ICleanupFinder[]
        {
            new GameFinder(),
            new DuplicateFinder(),
            new LeftoverFinder(),
            new CacheFinder(),
            new StaleDownloadsFinder(),
        }.Select(f => new FinderViewModel(f, Ctx)));
        foreach (var f in Finders) f.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FinderViewModel.TotalSize) or nameof(FinderViewModel.IsRunning)) RaiseSummary(); };
        _selected = Finders[0];

        RunAllCommand = new RelayCommand(() => _ = RunAllAsync(), () => !Finders.Any(f => f.IsRunning));
        SelectSuggestedCommand = new RelayCommand(() => { foreach (var i in Selected.Items) i.IsSelected = i.Suggested && !i.IsQueued; }, () => Selected.Items.Count > 0);
        SelectAllCommand = new RelayCommand(() => { foreach (var i in Selected.Items) i.IsSelected = !i.IsQueued; }, () => Selected.Items.Count > 0);
        SelectNoneCommand = new RelayCommand(() => { foreach (var i in Selected.Items) i.IsSelected = false; }, () => Selected.SelectedCount > 0);
        AddToReviewCommand = new RelayCommand(AddSelectedToReview, () => Selected.SelectedCount > 0);
        OpenCommand = new RelayCommand(p => { if (p is CleanupItem i) FsUtil.OpenInExplorer(i.Path); });
        AddOneCommand = new RelayCommand(p => { if (p is CleanupItem i) Queue(new[] { i }); });
        QueueAllSuggestedCommand = new RelayCommand(
            () => Queue(Finders.SelectMany(f => f.Items).Where(i => i.Suggested && !i.IsQueued).ToList()),
            () => Finders.Any(f => f.Items.Any(i => i.Suggested && !i.IsQueued)));
    }

    public ObservableCollection<FinderViewModel> Finders { get; }

    private FinderViewModel _selected;
    public FinderViewModel Selected
    {
        get => _selected;
        set { if (value is not null && Set(ref _selected, value)) RelayCommand.Refresh(); }
    }

    public ICommand RunAllCommand { get; }
    public ICommand SelectSuggestedCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand AddToReviewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand AddOneCommand { get; }
    public ICommand QueueAllSuggestedCommand { get; }

    public string SuggestedText
    {
        get
        {
            var items = Finders.SelectMany(f => f.Items).Where(i => i.Suggested && !i.IsQueued).ToList();
            return items.Count == 0 ? "Queue suggested" : $"Queue {items.Count} suggested · {Format.Bytes(items.Sum(i => i.Size))}";
        }
    }

    public string Summary
    {
        get
        {
            var ran = Finders.Where(f => f.HasRun).ToList();
            if (Finders.Any(f => f.IsRunning)) return "Checking…";
            if (ran.Count == 0) return "Run the checks to see what can be cleaned up.";
            return $"Up to {Format.Bytes(ran.Sum(f => f.TotalSize))} found across {ran.Sum(f => f.Count)} items";
        }
    }

    private void RaiseSummary() => Raise(nameof(Summary), nameof(SuggestedText));

    public async Task RunAllAsync()
    {
        // Run independent checks side by side; duplicates last since it reads file contents.
        var first = Finders.Where(f => f.Id != "duplicates").Select(f => f.RunAsync()).ToList();
        await Task.WhenAll(first);
        var dup = Finders.FirstOrDefault(f => f.Id == "duplicates");
        if (dup is not null) await dup.RunAsync();
    }

    private void AddSelectedToReview() => Queue(Selected.Items.Where(i => i.IsSelected).ToList());

    private void Queue(IEnumerable<CleanupItem> items)
    {
        foreach (var i in items)
        {
            _review.Add(new ReviewItem
            {
                Path = i.Path,
                Title = i.Title,
                Size = i.Size,
                Source = i.Source,
                Glyph = i.Glyph,
                Action = i.Action,
                UninstallUri = i.UninstallUri,
                UninstallLabel = i.UninstallLabel,
                Risk = i.Risk,
                Origin = i,
            });
            i.IsSelected = false;
        }
        RaiseSummary();
        RelayCommand.Refresh();
    }

    public void ApplyDeletions(IReadOnlyList<ReviewItem> deleted)
    {
        var paths = deleted.Where(d => d.Action == ItemAction.Delete).Select(d => d.Path).ToList();
        var contents = deleted.Where(d => d.Action == ItemAction.DeleteContents).Select(d => d.Path).ToList();
        foreach (var f in Finders)
        {
            if (paths.Count > 0) f.RemovePaths(paths);
            if (contents.Count > 0) f.RemovePaths(contents);
        }
        RaiseSummary();
    }
}
