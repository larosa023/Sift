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
    public string LastScanText { get; init; } = "";
}

public sealed record Crumb(FileNode Node, string Label, bool IsFirst);

public sealed class RecentScan
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public bool Exists => Directory.Exists(Path);
}

public sealed class NodeRow
{
    public NodeRow(FileNode node, long parentSize, string location = "", long? delta = null, ChangeKind? kind = null, long? sizeOverride = null, string? typeKey = null)
    {
        Node = node;
        TypeKey = typeKey;
        _size = sizeOverride ?? node.Size;
        Percent = parentSize > 0 ? _size * 100.0 / parentSize : 0;
        Location = location;
        Delta = delta;
        Kind = kind;
    }

    private readonly long _size;
    /// <summary>Set on rows of the File types view: the extension (".mp4") this row stands for.</summary>
    public string? TypeKey { get; }
    public bool IsTypeRow => TypeKey is not null;
    public long? Delta { get; }
    public ChangeKind? Kind { get; }
    /// <summary>1 grew, -1 shrank, 0 no change shown.</summary>
    public int DeltaSign => Delta is long d && Math.Abs(d) >= 1024 ? Math.Sign(d) : 0;
    public bool HasDelta => DeltaSign != 0;
    /// <summary>Under the size: the change ("+1.2 GB"), or in the Changes view what kind of change it was.</summary>
    public string DeltaText => Kind switch
    {
        ChangeKind.Added => "new",
        ChangeKind.Removed => "removed",
        ChangeKind.Grew => "grew",
        ChangeKind.Shrank => "shrank",
        _ => Delta is long d ? (d >= 0 ? "+" : "−") + Format.Bytes(Math.Abs(d)) : "",
    };
    public bool IsRemoved => Kind == ChangeKind.Removed;

    public FileNode Node { get; }
    public string Name => Node.Name;
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;
    public string SizeText => Kind is not null && Delta is long d ? (d >= 0 ? "+" : "−") + Format.Bytes(Math.Abs(d)) : Format.Bytes(_size);
    public double Percent { get; }
    public string PercentText => Percent >= 10 ? $"{Percent:0}%" : Percent >= 0.1 ? $"{Percent:0.0}%" : "<0.1%";
    public string ItemsText => Node.IsDirectory ? Format.Count(Node.FileCount) : "";
    public string ModifiedText => Format.Age(Node.LastWriteUtc);
    public string Glyph => IsTypeRow ? "\uE8FD" : Node.IsDirectory ? "\uE8B7" : "\uE8A5";
    public Brush Accent => Node.IsDirectory ? CategoryPalette.SolidBrush(FileCategory.Folder) : CategoryPalette.SolidBrush(Node.Category);
    public bool IsDirectory => Node.IsDirectory;
    public string Tip => (HasLocation ? $"{Node.Name}\n{Location}" : Node.Name)
        + (HasDelta ? $"\n{(Kind is null ? DeltaText : SizeText)} since the previous scan" : "");
}

/// <summary>One legend entry: a file category, or an age bucket when colouring by age.</summary>
public sealed record CategoryStat
{
    public required int Key { get; init; }
    public required string Name { get; init; }
    public required Brush Brush { get; init; }
    public long Size { get; init; }
    public double Percent { get; init; }
    public bool IsDimmed { get; init; }
    public bool IsActive { get; init; }
    public string SizeText => Format.Bytes(Size);
    public string Tip => $"{Name}: {Format.Bytes(Size)} ({Percent:0.#}%) — click to highlight in the map";
    public Brush SwatchBrush => Brush;
}

public enum ListMode { Contents, LargestFiles, Types, Changes }
public enum SortKey { Size, Name, Modified }

public sealed class OverviewViewModel : ObservableObject
{
    private const int LargestCount = 250;

    private readonly AppSettings _settings;
    private readonly ReviewViewModel _review;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _filterTimer;
    private IScanner? _scanner;
    private ScanCompare? _compare;
    private DateTime _scannedUtc;
    private Task _saveTask = Task.CompletedTask;
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
        NewScanCommand = new RelayCommand(() => { Root = null; Current = null; _fromCache = false; SetCompare(null); RefreshDrives(); Raise(nameof(IsFromCache), nameof(CacheText)); }, () => !IsScanning);
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
        HighlightCommand = new RelayCommand(p => { if (p is CategoryStat c) Highlight = Highlight == c.Key ? null : c.Key; });
        _colorMode = settings.ColorByAge ? TreemapColorMode.Age : TreemapColorMode.Type;
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
                    LastScanText = Snapshots.ReadInfo(d.RootDirectory.FullName) is { } info ? "Scanned " + Format.Age(info.ScannedUtc) : "",
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

    public bool ShowStart => !IsScanning && !IsRestoring && Root is null;
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
    public bool HasProgressEstimate => _expectedBytes > 0 || _scanner?.IsFast == true;

    /// <summary>True when fast scans would be possible if Sift ran as administrator.</summary>
    public bool ShowFastScanTip => _settings.FastScan && !Native.IsElevated();
    public bool IsFastScanning => IsScanning && _scanner?.IsFast == true;

    private long _expectedBytes;
    private bool _lastFast;
    private string? _fastNote;
    private TimeSpan _lastDuration;
    private long _lastErrors, _lastSkipped;

    public string ScanSummary => Root is null ? "" : _fromCache
        ? $"{Format.Bytes(Root.Size)} · {Format.Count(Root.FileCount)} files · {Format.Count(Root.FolderCount)} folders · saved scan from {Format.Age(_scannedUtc)}"
        : $"{Format.Bytes(Root.Size)} · {Format.Count(Root.FileCount)} files · {Format.Count(Root.FolderCount)} folders · scanned in {Format.Duration(_lastDuration)}{(_lastFast ? " (fast scan)" : "")}"
        + (_lastSkipped > 0 ? $" · {Format.Count(_lastSkipped)} skipped by your settings" : "")
        + (_lastErrors > 0 && !_lastFast ? $" · {Format.Count(_lastErrors)} folders unreadable (try Run as administrator)" : "")
        + (_fastNote is null ? "" : " · " + _fastNote);

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
            RebuildBreadcrumb(value);
            RebuildRows();
            Raise(nameof(CurrentSummary));
            RelayCommand.Refresh();
        }
    }

    public ObservableCollection<Crumb> Breadcrumb { get; } = new();

    private void RebuildBreadcrumb(FileNode? value)
    {
        Breadcrumb.Clear();
        if (value is null) return;
        var chain = value.AncestorsAndSelf().Reverse().ToList();
        // Deep paths: root › … › last three, so the bar never overflows.
        if (chain.Count > 6)
        {
            Breadcrumb.Add(new Crumb(chain[0], chain[0].Name, true));
            Breadcrumb.Add(new Crumb(chain[^4], "…", false));
            foreach (var n in chain.Skip(chain.Count - 3)) Breadcrumb.Add(new Crumb(n, n.Name, false));
        }
        else
            for (int i = 0; i < chain.Count; i++) Breadcrumb.Add(new Crumb(chain[i], chain[i].Name, i == 0));
    }

    private IReadOnlyList<NodeRow> _rows = Array.Empty<NodeRow>();
    public IReadOnlyList<NodeRow> Rows { get => _rows; private set { if (Set(ref _rows, value)) Raise(nameof(RowsEmptyText), nameof(ShowRowsEmpty)); } }

    public bool ShowRowsEmpty => Rows.Count == 0 && Current is not null;
    public string RowsEmptyText => !string.IsNullOrWhiteSpace(FilterText) ? $"Nothing here matches “{FilterText.Trim()}”."
        : Mode == ListMode.Changes ? "No changes bigger than 1 MB since the previous scan."
        : "This folder is empty.";

    public string CurrentSummary => Current is null ? "" :
        $"{Format.Bytes(Current.Size)} · {Format.Count(Current.FileCount)} files" + (Current.HadErrors ? " · some items unreadable" : "");

    // ------------------------------------------------------------------ list: mode, filter, sort
    private ListMode _mode = ListMode.Contents;
    public ListMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { Raise(nameof(IsLargestMode), nameof(IsChangesMode), nameof(ListTitle)); RebuildRows(); } }
    }
    public bool IsLargestMode => Mode == ListMode.LargestFiles;
    public bool IsChangesMode => Mode == ListMode.Changes;
    public string ListTitle => Mode switch
    {
        ListMode.LargestFiles => $"Largest files in {Current?.Name}",
        ListMode.Changes => $"Changes in {Current?.Name}",
        ListMode.Types => $"File types in {Current?.Name}",
        _ => Current?.Name ?? "",
    };

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
        Func<FileNode, bool> match = MakeMatcher(filter);

        if (Mode == ListMode.Changes)
        {
            Rows = BuildChangeRows(c, filter.Length == 0 ? null : match);
            Raise(nameof(ListTitle));
            return;
        }

        if (Mode == ListMode.Types)
        {
            Rows = BuildTypeRows(c, filter);
            Raise(nameof(ListTitle));
            return;
        }

        bool deep = Mode == ListMode.Contents && filter.Length > 0 && SearchSubfolders;
        IEnumerable<FileNode> src = IsLargestMode ? TopFiles(c, LargestCount, match)
            : deep ? TopMatches(c, SearchLimit, match)
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

        var cmp = _compare;
        Rows = src.Select(n => new NodeRow(n, c.Size, IsLargestMode || deep ? RelativeFolder(c, n) : "", cmp?.DeltaOf(n))).ToList();
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

    private const int SearchLimit = 500;

    /// <summary>Plain text matches anywhere in the name; * and ? work as wildcards against the whole name.</summary>
    public static Func<FileNode, bool> MakeMatcher(string filter)
    {
        if (filter.Length == 0) return _ => true;
        if (filter.IndexOfAny(new[] { '*', '?' }) < 0) return n => n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var rx = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(filter).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return n => rx.IsMatch(n.Name);
    }

    /// <summary>The biggest files and folders anywhere under a folder whose names match.</summary>
    private static List<FileNode> TopMatches(FileNode folder, int n, Func<FileNode, bool> match)
    {
        var heap = new PriorityQueue<FileNode, long>(n + 1);
        var stack = new Stack<FileNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d.Children is null) continue;
            foreach (var c in d.Children)
            {
                if (c.IsDirectory) stack.Push(c);
                if (!match(c)) continue;
                if (heap.Count < n) heap.Enqueue(c, c.Size);
                else if (c.Size > heap.Peek().Size) heap.EnqueueDequeue(c, c.Size);
            }
        }
        var list = new List<FileNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    /// <summary>File types view: every extension under the folder with its total size and count.</summary>
    private IReadOnlyList<NodeRow> BuildTypeRows(FileNode folder, string filter)
    {
        var totals = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folder.EnumerateFiles())
        {
            var ext = Path.GetExtension(f.Name);
            if (ext.Length == 0 || ext.Length > 16) ext = "";
            var t = totals.GetValueOrDefault(ext);
            totals[ext] = (t.Size + f.Size, t.Count + 1);
        }
        IEnumerable<KeyValuePair<string, (long Size, long Count)>> items = totals;
        if (filter.Length > 0) items = items.Where(kv => kv.Key.Contains(filter.TrimStart('*'), StringComparison.OrdinalIgnoreCase));
        items = (_sortKey, _sortDescending) switch
        {
            (SortKey.Name, false) => items.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
            (SortKey.Name, true) => items.OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
            (SortKey.Size, false) => items.OrderBy(kv => kv.Value.Size),
            _ => items.OrderByDescending(kv => kv.Value.Size),
        };
        return items.Take(1000).Select(kv =>
        {
            var label = kv.Key.Length == 0 ? "(no extension)" : kv.Key.ToLowerInvariant();
            var node = new FileNode(label, null, false) { Size = kv.Value.Size, FileCount = kv.Value.Count };
            var cat = kv.Key.Length == 0 ? FileCategory.Other : FileCategories.Of(kv.Key);
            return new NodeRow(node, folder.Size, $"{Format.Count(kv.Value.Count)} file{(kv.Value.Count == 1 ? "" : "s")} · {FileCategories.Label(cat)}",
                typeKey: kv.Key);
        }).ToList();
    }

    /// <summary>From the File types view: list the biggest files of one type.</summary>
    public void ShowType(string ext)
    {
        if (ext.Length == 0) return;
        _filterText = "*" + ext;
        Raise(nameof(FilterText));
        Mode = ListMode.LargestFiles;
        RebuildRows();
    }

    public double ListPaneWidth
    {
        get => _settings.ListPaneWidth;
        set { _settings.ListPaneWidth = Math.Round(value); _settings.Save(); }
    }

    public bool SearchSubfolders
    {
        get => _settings.SearchSubfolders;
        set
        {
            if (_settings.SearchSubfolders == value) return;
            _settings.SearchSubfolders = value;
            _settings.Save();
            Raise();
            if (FilterText.Length > 0) RebuildRows();
        }
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

    private string Describe(FileNode n)
    {
        var pct = n.Parent is { Size: > 0 } p ? $" · {n.Size * 100.0 / p.Size:0.#}% of parent" : "";
        if (_compare?.DeltaOf(n) is long d && Math.Abs(d) >= 1024)
            pct += $" · {(d >= 0 ? "+" : "−")}{Format.Bytes(Math.Abs(d))} since the previous scan";
        return n.IsDirectory
            ? $"{n.FullPath}  —  {Format.Bytes(n.Size)} · {Format.Count(n.FileCount)} files{pct}"
            : $"{n.FullPath}  —  {Format.Bytes(n.Size)} · modified {Format.Age(n.LastWriteUtc)}{pct}";
    }

    private int _layoutVersion;
    public int LayoutVersion { get => _layoutVersion; private set => Set(ref _layoutVersion, value); }

    // ------------------------------------------------------------------ categories / highlight
    private IReadOnlyList<CategoryStat> _categories = Array.Empty<CategoryStat>();
    public IReadOnlyList<CategoryStat> Categories { get => _categories; private set => Set(ref _categories, value); }

    private int? _highlight;
    public int? Highlight
    {
        get => _highlight;
        set
        {
            if (!Set(ref _highlight, value)) return;
            Categories = Categories.Select(c => c with { IsActive = c.Key == value, IsDimmed = value is not null && c.Key != value }).ToList();
            Raise(nameof(HasHighlight), nameof(HighlightText));
        }
    }
    public bool HasHighlight => Highlight is not null;
    public string HighlightText => Highlight is { } h && Categories.FirstOrDefault(c => c.Key == h) is { } item
        ? $"Highlighting {item.Name.ToLowerInvariant()} · {Format.Bytes(item.Size)}"
        : "";

    private TreemapColorMode _colorMode;
    public TreemapColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            if (!Set(ref _colorMode, value)) return;
            _settings.ColorByAge = value == TreemapColorMode.Age;
            _settings.Save();
            _highlight = null;
            Raise(nameof(Highlight), nameof(HasHighlight), nameof(HighlightText), nameof(LegendTitle));
            if (Root is { } r) _ = Task.Run(() => BuildCategories(r)).ContinueWith(t => { if (t.IsCompletedSuccessfully && ReferenceEquals(Root, r)) Categories = t.Result; },
                TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
    public string LegendTitle => ColorMode == TreemapColorMode.Age ? "Last modified" : "File types";


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
        _fastNote = null;
        _scanner = _settings.FastScan && MftScanner.CanScan(path, out _)
            ? new MftScanner(_settings.Exclusions)
            : new DiskScanner(_settings.Exclusions);
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
        Raise(nameof(HasProgressEstimate), nameof(IsFastScanning));
        _clock.Restart();
        _progressTimer.Start();
        UpdateProgress();
        try
        {
            FileNode result;
            try
            {
                result = await _scanner.ScanAsync(path, _cts.Token);
            }
            catch (Exception ex) when (_scanner.IsFast && !IsCancel(ex))
            {
                // Fast scan isn't possible here (odd layout, locked volume…): fall back to the normal scanner.
                _fastNote = $"Fast scan unavailable ({ex.Message}); used a normal scan";
                _scanner = new DiskScanner(_settings.Exclusions);
                result = await _scanner.ScanAsync(path, _cts.Token);
            }
            _lastFast = _scanner.IsFast;
            _lastDuration = _clock.Elapsed;
            _lastErrors = _scanner.Errors;
            _lastSkipped = _scanner.Skipped;
            _highlight = null;
            Raise(nameof(Highlight), nameof(HasHighlight), nameof(HighlightText));
            Categories = await Task.Run(() => BuildCategories(result));
            _filterText = "";
            Raise(nameof(FilterText));
            _scannedUtc = DateTime.UtcNow;
            _fromCache = false;
            SetCompare(null);
            Root = result;
            Current = result;
            Selected = null;
            Raise(nameof(IsFromCache), nameof(CacheText));
            if (_settings.RememberScans) SaveSnapshotAndLoadPrevious(result, _scannedUtc, _lastFast);
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
        ProgressPercent = s.Fraction is double f ? Math.Min(99, f * 100)
            : _expectedBytes > 0 ? Math.Min(99, s.Bytes * 100.0 / _expectedBytes) : 0;
        Raise(nameof(IsFastScanning));
    }

    private IReadOnlyList<CategoryStat> BuildCategories(FileNode root)
    {
        var mode = _colorMode;
        long now = DateTime.UtcNow.Ticks;
        var totals = new Dictionary<int, long>();
        foreach (var f in root.EnumerateFiles())
        {
            int k = TreemapControl.KeyOf(f, mode, now);
            totals[k] = totals.GetValueOrDefault(k) + f.Size;
        }
        long all = Math.Max(1, root.Size);
        var hl = _highlight;
        var items = totals.Where(kv => kv.Value > 0);
        // Ages read as a scale (newest first); types by size.
        items = mode == TreemapColorMode.Age ? items.OrderBy(kv => kv.Key) : items.OrderByDescending(kv => kv.Value);
        return items.Select(kv => new CategoryStat
            {
                Key = kv.Key,
                Name = mode == TreemapColorMode.Age ? AgePalette.Buckets[kv.Key].Name : FileCategories.Label((FileCategory)kv.Key),
                Brush = mode == TreemapColorMode.Age ? AgePalette.SolidBrush(kv.Key) : CategoryPalette.SolidBrush((FileCategory)kv.Key),
                Size = kv.Value,
                Percent = kv.Value * 100.0 / all,
                IsActive = hl == kv.Key,
                IsDimmed = hl is not null && hl != kv.Key,
            })
            .ToList();
    }

    // ------------------------------------------------------------------ saved scans & comparison
    private bool _fromCache;
    public bool IsFromCache => _fromCache && Root is not null;
    public string CacheText => IsFromCache ? $"Showing your saved scan from {Format.Age(_scannedUtc)} ({Format.Date(_scannedUtc)}). Rescan (F5) for fresh results." : "";

    public bool HasCompare => _compare is not null;
    public string CompareText => _compare is null ? "" : $"Changes since your scan on {Format.Date(_compare.ThenUtc)}";
    public string CompareDate => _compare is null ? "" : $"{Format.Date(_compare.ThenUtc)} ({Format.Age(_compare.ThenUtc)})";

    private void SetCompare(ScanCompare? c)
    {
        _compare = c;
        Raise(nameof(HasCompare), nameof(CompareText), nameof(CompareDate));
        if (c is null && Mode == ListMode.Changes) Mode = ListMode.Contents;
    }

    private bool _isRestoring;
    public bool IsRestoring { get => _isRestoring; private set { if (Set(ref _isRestoring, value)) Raise(nameof(ShowStart)); } }

    private void SaveSnapshotAndLoadPrevious(FileNode root, DateTime when, bool fast)
    {
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        _saveTask = _saveTask.ContinueWith(_ =>
        {
            try { Snapshots.SaveNew(root, when, fast); } catch { /* disk full etc.: not worth bothering the user */ }
            return Snapshots.LoadPrevious(root.Name);
        }, TaskScheduler.Default).ContinueWith(t =>
        {
            if (!ReferenceEquals(Root, root)) return; // a newer scan replaced it meanwhile
            var prev = t.IsCompletedSuccessfully ? t.Result : null;
            SetCompare(prev is null ? null : new ScanCompare(prev));
            RebuildRows();
            Raise(nameof(HoverText));
        }, ui);
    }

    /// <summary>On launch: show the last scan of the last location straight away (saved results), if there is one.</summary>
    public async Task RestoreLastAsync()
    {
        if (!_settings.RememberScans || Root is not null || IsScanning) return;
        var target = _settings.LastTarget;
        if (string.IsNullOrEmpty(target) || !Snapshots.HasCurrent(target)) return;
        IsRestoring = true;
        try
        {
            var loaded = await Task.Run(() => (Current: Snapshots.LoadCurrent(target), Previous: Snapshots.LoadPrevious(target)));
            if (loaded.Current is not { } cur || Root is not null || IsScanning) return;
            _scannedUtc = cur.Info.ScannedUtc;
            _lastFast = cur.Info.Fast;
            _lastErrors = 0; _lastSkipped = 0; _fastNote = null;
            _fromCache = true;
            Categories = await Task.Run(() => BuildCategories(cur.Root));
            Root = cur.Root;
            Current = cur.Root;
            SetCompare(loaded.Previous is null ? null : new ScanCompare(loaded.Previous));
            RebuildRows();
            Raise(nameof(IsFromCache), nameof(CacheText), nameof(ScanSummary));
        }
        finally { IsRestoring = false; }
    }

    private IReadOnlyList<NodeRow> BuildChangeRows(FileNode folder, Func<FileNode, bool>? match)
    {
        if (_compare is null) return Array.Empty<NodeRow>();
        var changes = _compare.Changes(folder);
        long scale = Math.Max(1, changes.Count == 0 ? 1 : changes.Max(c => Math.Abs(c.Delta)));
        var rows = new List<NodeRow>(changes.Count);
        foreach (var ch in changes)
        {
            var node = ch.Node ?? new FileNode(ch.Name, null, ch.IsDirectory);
            if (match is not null && !match(node)) continue;
            var loc = Path.GetDirectoryName(ch.Path) ?? "";
            var folderPath = folder.FullPath.TrimEnd('\\');
            var rel = loc.Length > folderPath.Length ? loc[(folderPath.Length + 1)..] : "(this folder)";
            // The share bar shows each change relative to the biggest change in the list.
            rows.Add(new NodeRow(node, scale, rel, ch.Delta, ch.Kind, sizeOverride: Math.Abs(ch.Delta)));
        }
        return rows;
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
        // A background save may be reading the tree; let it finish before changing it.
        try { _saveTask.Wait(TimeSpan.FromSeconds(10)); } catch { /* ignore */ }
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
        if (_settings.RememberScans)
        {
            var (r, when, fast) = (Root, _scannedUtc, _lastFast);
            _saveTask = _saveTask.ContinueWith(_ => { try { Snapshots.Update(r, when, fast); } catch { } }, TaskScheduler.Default);
        }
        var root = Root;
        _ = Task.Run(() => BuildCategories(root)).ContinueWith(t => { if (t.IsCompletedSuccessfully) Categories = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }
}
