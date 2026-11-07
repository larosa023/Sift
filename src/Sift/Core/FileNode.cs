namespace Sift.Core;

/// <summary>One file or folder in a scan tree. Kept deliberately small: millions of these can exist.</summary>
public sealed class FileNode
{
    public FileNode(string name, FileNode? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    /// <summary>File/folder name. For the scan root this is the full path.</summary>
    public string Name { get; }
    public FileNode? Parent { get; private set; }
    public bool IsDirectory { get; }
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public long FolderCount { get; internal set; }
    /// <summary>UTC ticks. For folders: newest write time of anything inside.</summary>
    public long LastWriteTicks { get; internal set; }
    public List<FileNode>? Children { get; internal set; }
    public bool HadErrors { get; internal set; }

    public bool IsRoot => Parent is null;
    public DateTime LastWriteUtc => LastWriteTicks > 0 ? new DateTime(LastWriteTicks, DateTimeKind.Utc) : DateTime.MinValue;
    public string Extension => IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public FileCategory Category => IsDirectory ? FileCategory.Folder : FileCategories.Of(Extension);
    public string DisplayName => IsRoot ? Name : Name;

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var parts = new Stack<string>();
            var n = this;
            while (n.Parent is not null) { parts.Push(n.Name); n = n.Parent; }
            var sb = new System.Text.StringBuilder(n.Name.TrimEnd('\\'));
            while (parts.Count > 0) sb.Append('\\').Append(parts.Pop());
            return sb.ToString();
        }
    }

    public FileNode Root
    {
        get { var n = this; while (n.Parent is not null) n = n.Parent; return n; }
    }

    public IEnumerable<FileNode> AncestorsAndSelf()
    {
        for (var n = this; n is not null; n = n.Parent) yield return n;
    }

    public bool IsDescendantOf(FileNode other)
    {
        for (var n = Parent; n is not null; n = n.Parent) if (ReferenceEquals(n, other)) return true;
        return false;
    }

    /// <summary>All files below this node (iterative, safe for deep trees).</summary>
    public IEnumerable<FileNode> EnumerateFiles()
    {
        if (!IsDirectory) { yield return this; yield break; }
        var stack = new Stack<FileNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d.Children is null) continue;
            foreach (var c in d.Children)
            {
                if (c.IsDirectory) stack.Push(c);
                else yield return c;
            }
        }
    }

    /// <summary>Finds a descendant by absolute path, or null.</summary>
    public FileNode? Find(string fullPath)
    {
        var rootPath = Root.Name.TrimEnd('\\');
        var p = fullPath.TrimEnd('\\');
        if (!p.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = p.Substring(rootPath.Length).Trim('\\');
        var node = Root;
        if (rest.Length == 0) return node;
        foreach (var part in rest.Split('\\'))
        {
            if (node.Children is null) return null;
            FileNode? next = null;
            foreach (var c in node.Children)
                if (string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next is null) return null;
            node = next;
        }
        return node;
    }

    /// <summary>Removes this node from the tree and subtracts its totals from every ancestor.</summary>
    public void Detach()
    {
        var parent = Parent;
        if (parent is null) return;
        parent.Children?.Remove(this);
        long folders = FolderCount + (IsDirectory ? 1 : 0);
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.FileCount -= FileCount;
            p.FolderCount -= folders;
        }
        Parent = null;
    }

    /// <summary>Empties a folder node (used when a folder's contents were deleted but the folder kept).</summary>
    public void ClearContents()
    {
        if (!IsDirectory || Children is null) return;
        foreach (var c in Children.ToList()) c.Detach();
    }

    public override string ToString() => FullPath;
}
