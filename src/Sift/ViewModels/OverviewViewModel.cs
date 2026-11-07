using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

public sealed class RecentScan
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public bool Exists => Directory.Exists(Path);
}

public sealed class NodeRow
{
    public NodeRow(FileNode node, long parentSize, string location = "")
    {
        Node = node;
        Percent = parentSize > 0 ? node.Size * 100.0 / parentSize : 0;
        Location = location;
    }

    public FileNode Node { get; }
    public string Name => Node.Name;
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;
    public string SizeText => Format.Bytes(Node.Size);
    public double Percent { get; }
    public string PercentText => Percent >= 10 ? $"{Percent:0}%" : Percent >= 0.1 ? $"{Percent:0.0}%" : "<0.1%";
    public string ItemsText => Node.IsDirectory ? Format.Count(Node.FileCount) : "";
    public string ModifiedText => Format.Age(Node.LastWriteUtc);
    public string Glyph => Node.IsDirectory ? "\uE8B7" : "\uE8A5";
    public Brush Accent => Node.IsDirectory ? CategoryPalette.SolidBrush(FileCategory.Folder) : CategoryPalette.SolidBrush(Node.Category);
    public bool IsDirectory => Node.IsDirectory;
    public string Tip => HasLocation ? $"{Node.Name}\n{Location}" : Node.Name;
}

public sealed record CategoryStat
{
    public required FileCategory Category { get; init; }
    public required string Name { get; init; }
    public required Brush Brush { get; init; }
    public long Size { get; init; }
    public double Percent { get; init; }
    public bool IsDimmed { get; init; }
    public bool IsActive { get; init; }
    public string SizeText => Format.Bytes(Size);
    public string Tip => $"{Name}: {Format.Bytes(Size)} ({Percent:0.#}%) — click to highlight in the map";
}

public enum ListMode { Contents, LargestFiles }
public enum SortKey { Size, Name, Modified }

public sealed class OverviewViewModel : ObservableObject
{
    private const int LargestCount = 250;

    private readonly AppSettings _settings;
    private readonly ReviewViewModel _review;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _filterTimer;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _clock = new();

    public OverviewViewModel(AppSettings settings, ReviewViewModel review)
    {
        _settings = settings;
        _review = review;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RebuildRows(); };

        ScanCommand = new RelayCommand(p => _ = ScanAsync(p as string ?? (p as DriveEntry)?.Root ?? (p as RecentScan)?.Path ?? Target), _ => !IsScanning);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        RescanCommand = new RelayCommand(() => _ = ScanAsync(Root?.Name ?? Target), () => !IsScanning && Root is not null);
        NewScanCommand = new RelayCommand(() => { Root = null; Current = null; RefreshDrives(); }, () => !IsScanning);
        UpCommand = new RelayCommand(() => { if (Current?.Parent is { } p) NavigateTo(p, select: Current); }, () => Current?.Parent is not null);
        NavigateCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) NavigateTo(n.IsDirectory ? n : n.Parent ?? n); });
        OpenInExplorerCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) FsUtil.OpenInExplorer(n.FullPath); }, p => (p as FileNode ?? Selected) is not null);
        OpenCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) FsUtil.OpenUri(n.FullPath); }, p => (p as FileNode ?? Selected) is not null);
        CopyPathCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) try { Clipboard.SetText(n.FullPath); } catch { } }, p => (p as FileNode ?? Selected) is not null);
        AddToReviewCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { } n) AddToReview(n); },
            p => (p as FileNode ?? Selected) is { IsRoot: false });
        ExcludeCommand = new RelayCommand(p => { if ((p as FileNode ?? Selected) is { IsDirectory: true, IsRoot: false } n) ExcludeRequested?.Invoke(n.FullPath); },
            p => (p as FileNode ?? Selected) is { IsDirectory: true, IsRoot: false });
        SortCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<SortKey>(s, out var k)) SortBy(k); });
        HighlightCommand = new RelayCommand(p => { if (p is CategoryStat c) Highlight = Highlight == c.Category ? null : c.Category; });
        ClearHighlightCommand = new RelayCommand(() => Highlight = null);
        ExportCommand = new RelayCommand(Export, () => Current is not null);
        RemoveRecentCommand = new RelayCommand(p =>
        {
            if (p is RecentScan r) { _settings.RecentScans.Remove(r.Path); _settings.Save(); RefreshRecents(); }
        });
        RefreshDrives();
        RefreshRecents();
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
    public ICommand OpenCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand AddToReviewCommand { get; }
    public ICommand ExcludeCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand HighlightCommand { get; }
    public ICommand ClearHighlightCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RemoveRecentCommand { get; }

    /// <summary>Raised when a scan finishes successfully.</summary>
    public event Action<FileNode>? ScanCompleted;
    /// <summary>Raised when the user asks to skip a folder in future scans.</summary>
    public event Action<string>? ExcludeRequested;

    // ------------------------------------------------------------------ drives & recents
    public ObservableCollection<DriveEntry> Drives { get; } = new();
    public ObservableCollection<RecentScan> Recents { get; } = new();
    public bool HasRecents => Recents.Count > 0;

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
        RefreshRecents();
    }

    private void RefreshRecents()
    {
        Recents.Clear();
        foreach (var p in _settings.RecentScans)
        {
            // Whole drives already have their own cards.
            if (Drives.Any(d => string.Equals(d.Root.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
            Recents.Add(new RecentScan { Path = p });
        }
        Raise(nameof(HasRecents));
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

    /// <summary>True when the scan has a known size to measure progress against (a whole drive).</summary>
    public bool HasProgressEstimate => _expectedBytes > 0;

    private long _expectedBytes;
    private TimeSpan _lastDuration;
    private long _lastErrors, _lastSkipped;

    public string ScanSummary => Root is null ? "" :
        $"{Format.Bytes(Root.Size)} · {Format.Count(Root.FileCount)} files · {Format.Count(Root.FolderCount)} folders · scanned in {Format.Duration(_lastDuration)}"
        + (_lastSkipped > 0 ? $" · {Format.Count(_lastSkipped)} skipped by your settings" : "")
        + (_lastErrors > 0 ? $" · {Format.Count(_lastErrors)} folders unreadable (try Run as administrator)" : "");

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
    public IReadOnlyList<NodeRow> Rows { get => _rows; private set { if (Set(ref _rows, value)) Raise(nameof(RowsEmptyText), nameof(ShowRowsEmpty)); } }

    public bool ShowRowsEmpty => Rows.Count == 0 && Current is not null;
    public string RowsEmptyText => !string.IsNullOrWhiteSpace(FilterText) ? $"Nothing here matches “{FilterText.Trim()}”." : "This folder is empty.";

    public string CurrentSummary => Current is null ? "" :
        $"{Format.Bytes(Current.Size)} · {Format.Count(Current.FileCount)} files" + (Current.HadErrors ? " · some items unreadable" : "");

    // ------------------------------------------------------------------ list: mode, filter, sort
    private ListMode _mode = ListMode.Contents;
    public ListMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { Raise(nameof(IsLargestMode), nameof(ListTitle)); RebuildRows(); } }
    }
    public bool IsLargestMode => Mode == ListMode.LargestFiles;
    public string ListTitle => IsLargestMode ? $"Largest files in {Current?.Name}" : Current?.Name ?? "";

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Set(ref _filterText, value ?? "")) return;
            _filterTimer.Stop();
            _filterTimer.Start();
        }
    }

    private SortKey _sortKey = SortKey.Size;
    private bool _sortDescending = true;

    public string NameHeader => "Name" + Arrow(SortKey.Name);
    public string SizeHeader => "Size" + Arrow(SortKey.Size);
    public string ModifiedHeader => "Modified" + Arrow(SortKey.Modified);
    private string Arrow(SortKey k) => _sortKey == k ? (_sortDescending ? "  ↓" : "  ↑") : "";

    public void SortBy(SortKey key)
    {
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else { _sortKey = key; _sortDescending = key != SortKey.Name; }
        Raise(nameof(NameHeader), nameof(SizeHeader), nameof(ModifiedHeader));
        RebuildRows();
    }

    private void RebuildRows()
    {
        var c = Current;
        if (c is null) { Rows = Array.Empty<NodeRow>(); return; }
        var filter = FilterText.Trim();
        Func<FileNode, bool> match = filter.Length == 0 ? _ => true : n => n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        IEnumerable<FileNode> src = IsLargestMode
            ? TopFiles(c, LargestCount, match)
            : (c.Children ?? new List<FileNode>()).Where(match);

        src = (_sortKey, _sortDescending) switch
        {
            (SortKey.Name, false) => src.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase),
            (SortKey.Name, true) => src.OrderByDescending(n => n.Name, StringComparer.CurrentCultureIgnoreCase),
            (SortKey.Modified, false) => src.OrderBy(n => n.LastWriteTicks),
            (SortKey.Modified, true) => src.OrderByDescending(n => n.LastWriteTicks),
            (SortKey.Size, false) => src.OrderBy(n => n.Size),
            _ => src.OrderByDescending(n => n.Size),
        };

        Rows = src.Select(n => new NodeRow(n, c.Size, IsLargestMode ? RelativeFolder(c, n) : "")).ToList();
        Raise(nameof(ListTitle));
    }

    /// <summary>The N biggest files anywhere under a folder (min-heap, one pass).</summary>
    private static List<FileNode> TopFiles(FileNode folder, int n, Func<FileNode, bool> match)
    {
        var heap = new PriorityQueue<FileNode, long>(n + 1);
        foreach (var f in folder.EnumerateFiles())
        {
            if (!match(f)) continue;
            if (heap.Count < n) heap.Enqueue(f, f.Size);
            else if (f.Size > heap.Peek().Size) heap.EnqueueDequeue(f, f.Size);
        }
        var list = new List<FileNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    private static string RelativeFolder(FileNode baseFolder, FileNode file)
    {
        var parts = new Stack<string>();
        for (var p = file.Parent; p is not null && !ReferenceEquals(p, baseFolder); p = p.Parent) parts.Push(p.Name);
        return parts.Count == 0 ? "(this folder)" : string.Join("\\", parts);
    }

    // ------------------------------------------------------------------ selection
    private FileNode? _selected;
    private bool _syncing;
    public FileNode? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectedText), nameof(HoverText));
            if (_syncing) return;
            _syncing = true;
            SelectedRow = value is null ? null : Rows.FirstOrDefault(r => ReferenceEquals(r.Node, value))
                                                 ?? (IsLargestMode ? null : Rows.FirstOrDefault(r => value.IsDescendantOf(r.Node)));
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

    // ------------------------------------------------------------------ categories / highlight
    private IReadOnlyList<CategoryStat> _categories = Array.Empty<CategoryStat>();
    public IReadOnlyList<CategoryStat> Categories { get => _categories; private set => Set(ref _categories, value); }

    private FileCategory? _highlight;
    public FileCategory? Highlight
    {
        get => _highlight;
        set
        {
            if (!Set(ref _highlight, value)) return;
            Categories = Categories.Select(c => c with { IsActive = c.Category == value, IsDimmed = value is not null && c.Category != value }).ToList();
            Raise(nameof(HasHighlight), nameof(HighlightText));
        }
    }
    public bool HasHighlight => Highlight is not null;
    public string HighlightText => Highlight is { } h
        ? $"Highlighting {FileCategories.Label(h).ToLowerInvariant()} · {Format.Bytes(Categories.FirstOrDefault(c => c.Category == h)?.Size ?? 0)}"
        : "";

    public void NavigateTo(FileNode node, FileNode? select = null)
    {
        if (!node.IsDirectory) node = node.Parent ?? node;
        Current = node;
        Selected = select;
    }

    /// <summary>Esc: clear the filter first, then the highlight, then the selection.</summary>
    public bool ClearOne()
    {
        if (FilterText.Length > 0) { FilterText = ""; return true; }
        if (Highlight is not null) { Highlight = null; return true; }
        if (Selected is not null) { Selected = null; return true; }
        return false;
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
        if (!Directory.Exists(path))
        {
            Error = $"Can't find {path}.";
            return;
        }
        _cts = new CancellationTokenSource();
        _scanner = new DiskScanner(_settings.Exclusions);
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
        Raise(nameof(HasProgressEstimate));

        IsScanning = true;
        _clock.Restart();
        _progressTimer.Start();
        UpdateProgress();
        try
        {
            var result = await _scanner.ScanAsync(path, _cts.Token);
            _lastDuration = _clock.Elapsed;
            _lastErrors = _scanner.Errors;
            _lastSkipped = _scanner.Skipped;
            _highlight = null;
            Raise(nameof(Highlight), nameof(HasHighlight), nameof(HighlightText));
            Categories = await Task.Run(() => BuildCategories(result));
            _filterText = "";
            Raise(nameof(FilterText));
            Root = result;
            Current = result;
            Selected = null;
            _settings.LastTarget = path;
            _settings.AddRecent(path);
            _settings.Save();
            RefreshRecents();
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

    private IReadOnlyList<CategoryStat> BuildCategories(FileNode root)
    {
        var totals = new Dictionary<FileCategory, long>();
        foreach (var f in root.EnumerateFiles())
        {
            var c = f.Category;
            totals[c] = totals.GetValueOrDefault(c) + f.Size;
        }
        long all = Math.Max(1, root.Size);
        var hl = _highlight;
        return totals.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CategoryStat
            {
                Category = kv.Key,
                Name = FileCategories.Label(kv.Key),
                Brush = CategoryPalette.SolidBrush(kv.Key),
                Size = kv.Value,
                Percent = kv.Value * 100.0 / all,
                IsActive = hl == kv.Key,
                IsDimmed = hl is not null && hl != kv.Key,
            })
            .ToList();
    }

    // ------------------------------------------------------------------ export
    private void Export()
    {
        var c = Current;
        if (c is null) return;
        var name = IsLargestMode ? $"Sift - largest files in {SafeName(c.Name)}.csv" : $"Sift - {SafeName(c.Name)}.csv";
        var dlg = new SaveFileDialog { FileName = name, Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        try { ExportTo(dlg.FileName); }
        catch (Exception ex) { Error = $"Couldn't save the file: {ex.Message}"; }
    }

    /// <summary>Writes the current list to CSV (also used by the smoke test).</summary>
    public void ExportTo(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Type,Path,Size (bytes),Size,Files,Modified");
        foreach (var r in Rows)
        {
            var n = r.Node;
            sb.Append(Csv(n.Name)).Append(',')
              .Append(n.IsDirectory ? "Folder" : FileCategories.Label(n.Category)).Append(',')
              .Append(Csv(n.FullPath)).Append(',')
              .Append(n.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(Format.Bytes(n.Size))).Append(',')
              .Append(n.FileCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(n.LastWriteTicks > 0 ? n.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")
              .AppendLine();
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static string SafeName(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } n ? n : "scan";

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
        var root = Root;
        _ = Task.Run(() => BuildCategories(root)).ContinueWith(t => { if (t.IsCompletedSuccessfully) Categories = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }
}
