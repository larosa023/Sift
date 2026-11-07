using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sift.Core.Finders;

public enum Risk { Safe, Review, Caution }

public enum ItemAction
{
    /// <summary>Delete the file or folder.</summary>
    Delete,
    /// <summary>Delete a folder's contents but keep the folder (caches, temp).</summary>
    DeleteContents,
    /// <summary>Hand off to an uninstaller (e.g. steam://uninstall/...) instead of deleting files.</summary>
    Uninstall,
}

public sealed class CleanupItem : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastUsedUtc { get; init; } = DateTime.MinValue;
    public string LastUsedLabel { get; init; } = "Last used";
    public Risk Risk { get; init; } = Risk.Review;
    public ItemAction Action { get; init; } = ItemAction.Delete;
    public string? UninstallUri { get; init; }
    public string? UninstallLabel { get; init; }
    public bool Suggested { get; init; }
    public string Source { get; init; } = "";
    public string Glyph { get; init; } = "";

    /// <summary>Items with the same group (e.g. duplicate copies) are listed together.</summary>
    public string? Group { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
    }

    private bool _isQueued;
    public bool IsQueued
    {
        get => _isQueued;
        set { if (_isQueued != value) { _isQueued = value; OnChanged(); } }
    }

    public string SizeText => Format.Bytes(Size);
    public string LastUsedText => LastUsedUtc == DateTime.MinValue ? "—" : Format.Age(LastUsedUtc);
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string RiskText => Risk switch { Risk.Safe => "Safe", Risk.Caution => "Caution", _ => "Review" };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class FinderContext
{
    public required AppSettings Settings { get; init; }
    /// <summary>Most recent scan, if any. Some finders (duplicates) need it.</summary>
    public FileNode? Scan { get; init; }
    public IProgress<string>? Status { get; init; }
}

public interface ICleanupFinder
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    string Glyph { get; }
    bool Grouped { get; }
    bool NeedsScan { get; }
    Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct);
}
