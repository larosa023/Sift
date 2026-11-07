using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Sift.Controls;
using Sift.Core;

namespace Sift.ViewModels;

public sealed class DriveEntry
{
    public required string Root { get; init; }
    public required string Letter { get; init; }
    public string Label { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public string Kind { get; init; } = "";
    public long Used => Total - Free;
    public double Percent => Total > 0 ? Used * 100.0 / Total : 0;
    public string UsageText => $"{Format.Bytes(Free)} free of {Format.Bytes(Total)}";
    public string Title => string.IsNullOrWhiteSpace(Label) ? $"{Kind} ({Letter})" : $"{Label} ({Letter})";
    public bool IsLow => Total > 0 && Free < Total * 0.1;
}

public sealed class NodeRow
{
    public NodeRow(FileNode node, long parentSize)
    {
        Node = node;
        Percent = parentSize > 0 ? node.Size * 100.0 / parentSize : 0;
    }

    public FileNode Node { get; }
    public string Name => Node.Name;
    public string SizeText => Format.Bytes(Node.Size);
    public double Percent { get; }
    public string PercentText => Percent >= 10 ? $"{Percent:0}%" : Percent >= 0.1 ? $"{Percent:0.0}%" : "<0.1%";
    public string ItemsText => Node.IsDirectory ? Format.Count(Node.FileCount) : "";
    public string ModifiedText => Format.Age(Node.LastWriteUtc);
    public string Glyph => Node.IsDirectory ? "\uE8B7" : "\uE8A5";
    public Brush Accent => Node.IsDirectory ? CategoryPalette.SolidBrush(FileCategory.Folder) : CategoryPalette.SolidBrush(Node.Category);
    public bool IsDirectory => Node.IsDirectory;
}

public sealed class CategoryStat
{
    public required string Name { get; init; }
    public required Brush Brush { get; init; }
    public long Size { get; init; }
    public double Percent { get; init; }
    public string SizeText => Format.Bytes(Size);
}

public sealed class OverviewViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ReviewViewModel _review;
    private readonly DispatcherTimer _progressTimer;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _clock = new();

    public OverviewViewModel(AppSettings settings, ReviewViewModel review)
    {
        _settings = settings;
        _review = review;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        ScanCommand = new RelayCommand(p => _ = ScanAsync(p as string ?? (p as DriveEntry)?.Root ?? Target), _ => !IsScanning);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        RescanCommand = new RelayCommand(() => _ = ScanAsync(Root?.Name ?? Target), () => !IsScanning && Root is not null);
        NewScanCommand = new RelayCommand(() => { Root = null; Current = null; RefreshDrives(); }, () => !IsScanning);
        UpCommand = new RelayCommand(() => { if (Current?.Parent is { } p) NavigateTo(p); }, () => Current?.Parent is not null);
        NavigateCommand = new RelayCommand(p => { if (p is FileNode n) NavigateTo(n.IsDirectory ? n : n.Parent ?? n); });
        OpenInExplorerCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) FsUtil.OpenInExplorer(n.FullPath); });
        CopyPathCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) try { Clipboard.SetText(n.FullPath); } catch { } });
        AddToReviewCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) AddToReview(n); },
            p => (p as FileNode ?? Selected) is { IsRoot: false });
        RefreshDrives();
    }

    // ------------------------------------------------------------------ commands
    public ICommand ScanCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand NewScanCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand AddToReviewCommand { get; }

    /// <summary>Raised when a scan finishes successfully.</summary>
    public event Action<FileNode>? ScanCompleted;

    // ------------------------------------------------------------------ drives
    public ObservableCollection<DriveEntry> Drives { get; } = new();

    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                Drives.Add(new DriveEntry
                {
                    Root = d.RootDirectory.FullName,
                    Letter = d.Name.TrimEnd('\\'),
                    Label = d.VolumeLabel,
                    Total = d.TotalSize,
                    Free = d.AvailableFreeSpace,
                    Kind = d.DriveType switch { DriveType.Removable => "Removable", DriveType.Network => "Network", _ => "Local Disk" },
                });
            }
            catch { /* drive vanished */ }
        }
    }

    private string Target => _settings.LastTarget is { } t && Directory.Exists(t) ? t : Drives.FirstOrDefault()?.Root ?? "C:\\";

    // ------------------------------------------------------------------ state
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set { if (Set(ref _isScanning, value)) { Raise(nameof(ShowStart), nameof(ShowResults)); RelayCommand.Refresh(); } }
    }

    private FileNode? _root;
    public FileNode? Root
    {
        get => _root;
        private set { if (Set(ref _root, value)) Raise(nameof(ShowStart), nameof(ShowResults), nameof(ScanSummary)); }
    }

    public bool ShowStart => !IsScanning && Root is null;
    public bool ShowResults => !IsScanning && Root is not null;

    private string _scanTarget = "";
    public string ScanTarget { get => _scanTarget; private set => Set(ref _scanTarget, value); }

    private string _progressFiles = "";
    public string ProgressFiles { get => _progressFiles; private set => Set(ref _progressFiles, value); }

    private string _progressBytes = "";
    public string ProgressBytes { get => _progressBytes; private set => Set(ref _progressBytes, value); }

    private string _progressPath = "";
    public string ProgressPath { get => _progressPath; private set => Set(ref _progressPath, value); }

    private string _progressTime = "";
    public string ProgressTime { get => _progressTime; private set => Set(ref _progressTime, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }

    private long _expectedBytes;
    private TimeSpan _lastDuration;
    private long _lastErrors;

    public string ScanSummary => Root is null ? "" :
        $"{Format.Bytes(Root.Size)} · {Format.Count(Root.FileCount)} files · {Format.Count(Root.FolderCount)} folders · scanned in {Format.Duration(_lastDuration)}"
        + (_lastErrors > 0 ? $" · {Format.Count(_lastErrors)} folders skipped (no access)" : "");

    private string? _error;
    public string? Error { get => _error; set => Set(ref _error, value); }

    // ------------------------------------------------------------------ navigation
    private FileNode? _current;
    public FileNode? Current
    {
        get => _current;
        private set
        {
            if (!Set(ref _current, value)) return;
            Breadcrumb.Clear();
            if (value is not null)
                foreach (var n in value.AncestorsAndSelf().Reverse()) Breadcrumb.Add(n);
            RebuildRows();
            Raise(nameof(CurrentSummary));
            RelayCommand.Refresh();
        }
    }

    public ObservableCollection<FileNode> Breadcrumb { get; } = new();

    private IReadOnlyList<NodeRow> _rows = Array.Empty<NodeRow>();
    public IReadOnlyList<NodeRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    public string CurrentSummary => Current is null ? "" :
        $"{Format.Bytes(Current.Size)} · {Format.Count(Current.FileCount)} files" + (Current.HadErrors ? " · some items unreadable" : "");

    private FileNode? _selected;
    private bool _syncing;
    public FileNode? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectedText));
            if (_syncing) return;
            _syncing = true;
            SelectedRow = value is null ? null : Rows.FirstOrDefault(r => ReferenceEquals(r.Node, value))
                                                 ?? Rows.FirstOrDefault(r => value.IsDescendantOf(r.Node));
            _syncing = false;
            RelayCommand.Refresh();
        }
    }

    private NodeRow? _selectedRow;
    public NodeRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (_syncing) return;
            _syncing = true;
            Selected = value?.Node;
            _syncing = false;
        }
    }

    public string SelectedText => Selected is null ? "" : Describe(Selected);

    private FileNode? _hovered;
    public FileNode? Hovered
    {
        get => _hovered;
        set { if (Set(ref _hovered, value)) Raise(nameof(HoverText)); }
    }

    public string HoverText => Hovered is not null ? Describe(Hovered) : SelectedText.Length > 0 ? SelectedText : "Hover to inspect · double-click a folder to zoom in · right-click for actions";

    private static string Describe(FileNode n)
    {
        var pct = n.Parent is { Size: > 0 } p ? $" · {n.Size * 100.0 / p.Size:0.#}% of parent" : "";
        return n.IsDirectory
            ? $"{n.FullPath}  —  {Format.Bytes(n.Size)} · {Format.Count(n.FileCount)} files{pct}"
            : $"{n.FullPath}  —  {Format.Bytes(n.Size)} · modified {Format.Age(n.LastWriteUtc)}{pct}";
    }

    private int _layoutVersion;
    public int LayoutVersion { get => _layoutVersion; private set => Set(ref _layoutVersion, value); }

    private IReadOnlyList<CategoryStat> _categories = Array.Empty<CategoryStat>();
    public IReadOnlyList<CategoryStat> Categories { get => _categories; private set => Set(ref _categories, value); }

    public void NavigateTo(FileNode node)
    {
        if (!node.IsDirectory) node = node.Parent ?? node;
        Current = node;
        Selected = null;
    }

    private void RebuildRows()
    {
        var c = Current;
        Rows = c?.Children is null ? Array.Empty<NodeRow>() : c.Children.Select(n => new NodeRow(n, c.Size)).ToList();
    }

    // ------------------------------------------------------------------ scanning
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dlg.ShowDialog() == true) _ = ScanAsync(dlg.FolderName);
    }

    public async Task ScanAsync(string path)
    {
        if (IsScanning) return;
        Error = null;
        _cts = new CancellationTokenSource();
        _scanner = new DiskScanner();
        ScanTarget = path;
        _expectedBytes = 0;
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is not null && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                var d = new DriveInfo(root);
                if (d.IsReady) _expectedBytes = d.TotalSize - d.TotalFreeSpace;
            }
        }
        catch { /* ignore */ }

        IsScanning = true;
        _clock.Restart();
        _progressTimer.Start();
        UpdateProgress();
        try
        {
            var result = await _scanner.ScanAsync(path, _cts.Token);
            _lastDuration = _clock.Elapsed;
            _lastErrors = _scanner.Errors;
            Categories = await Task.Run(() => BuildCategories(result));
            Root = result;
            Current = result;
            Selected = null;
            _settings.LastTarget = path;
            _settings.Save();
            ScanCompleted?.Invoke(result);
        }
        catch (Exception ex) when (IsCancel(ex))
        {
            // Cancelled: keep the previous result (if any).
        }
        catch (Exception ex)
        {
            Error = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _progressTimer.Stop();
            _clock.Stop();
            IsScanning = false;
            Raise(nameof(ScanSummary));
            _cts.Dispose();
            _cts = null;
        }
    }

    private static bool IsCancel(Exception ex) =>
        ex is OperationCanceledException ||
        (ex is AggregateException ae && ae.Flatten().InnerExceptions.All(i => i is OperationCanceledException));

    private void UpdateProgress()
    {
        var s = _scanner;
        if (s is null) return;
        ProgressFiles = $"{Format.Count(s.Files)} files · {Format.Count(s.Folders)} folders";
        ProgressBytes = Format.Bytes(s.Bytes);
        ProgressPath = s.CurrentPath;
        ProgressTime = Format.Duration(_clock.Elapsed);
        ProgressPercent = _expectedBytes > 0 ? Math.Min(99, s.Bytes * 100.0 / _expectedBytes) : 0;
    }

    private static IReadOnlyList<CategoryStat> BuildCategories(FileNode root)
    {
        var totals = new Dictionary<FileCategory, long>();
        foreach (var f in root.EnumerateFiles())
        {
            var c = f.Category;
            totals[c] = totals.GetValueOrDefault(c) + f.Size;
        }
        long all = Math.Max(1, root.Size);
        return totals.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CategoryStat
            {
                Name = FileCategories.Label(kv.Key),
                Brush = CategoryPalette.SolidBrush(kv.Key),
                Size = kv.Value,
                Percent = kv.Value * 100.0 / all,
            })
            .ToList();
    }

    // ------------------------------------------------------------------ review / deletion integration
    public void AddToReview(FileNode n)
    {
        if (n.IsRoot) return;
        _review.Add(new ReviewItem
        {
            Path = n.FullPath,
            Title = n.Name,
            Size = n.Size,
            Source = "Treemap",
            Glyph = n.IsDirectory ? "\uE8B7" : "\uE8A5",
            Risk = Core.Finders.Risk.Review,
        });
    }

    /// <summary>Removes deleted paths from the scan tree so the treemap reflects reality without a rescan.</summary>
    public void ApplyDeletions(IEnumerable<ReviewItem> items)
    {
        if (Root is null) return;
        bool changed = false;
        foreach (var i in items)
        {
            var node = Root.Find(i.Path);
            if (node is null || node.IsRoot) continue;
            if (i.Action == Core.Finders.ItemAction.DeleteContents) node.ClearContents();
            else node.Detach();
            changed = true;
        }
        if (!changed) return;
        if (Current is not null && !ReferenceEquals(Current.Root, Root)) Current = Root;
        if (Selected is not null && !ReferenceEquals(Selected.Root, Root)) Selected = null;
        RebuildRows();
        Raise(nameof(CurrentSummary), nameof(ScanSummary));
        LayoutVersion++;
        _ = Task.Run(() => BuildCategories(Root)).ContinueWith(t => Categories = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
