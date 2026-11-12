namespace NextCommander.Vfs.Archive;

/// <summary>One file or folder inside an archive (committed entry or pending change).</summary>
internal sealed class ArchiveItem
{
    /// <summary>Normalized path "/a/b".</summary>
    public required string Path { get; set; }

    public bool IsDirectory { get; init; }

    /// <summary>Original entry name in the archive; null for implied folders and items not written yet.</summary>
    public string? Key { get; set; }

    /// <summary>Position in the zip central directory (stable even when names decode differently).</summary>
    public int EntryIndex { get; set; } = -1;

    public long Size { get; set; }

    public long CompressedSize { get; set; }

    public DateTime Modified { get; set; }

    public bool Encrypted { get; init; }

    /// <summary>Temp file with the new content (copied into the archive, not committed yet).</summary>
    public string? PendingFile { get; set; }

    /// <summary>Folder created explicitly (written as "dir/" entry).</summary>
    public bool ExplicitDirectory { get; set; }

    /// <summary>Modification time changed and must be written.</summary>
    public bool TimeDirty { get; set; }

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string? ParentPath => Path == "/" ? null : Path.LastIndexOf('/') <= 0 ? "/" : Path[..Path.LastIndexOf('/')];
}

/// <summary>Folder tree of an archive. Folders missing from the archive are implied by the file paths.</summary>
internal sealed class ArchiveIndex
{
    private readonly Dictionary<string, ArchiveItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ArchiveItem>> _children = new(StringComparer.Ordinal);

    public ArchiveIndex()
    {
        _children["/"] = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
    }

    public IEnumerable<ArchiveItem> Items => _items.Values;

    /// <summary>Time for folders that only exist implicitly (the archive file's time).</summary>
    public DateTime ImpliedTime { get; set; } = DateTime.Now;

    public ArchiveItem? Get(string path) => _items.GetValueOrDefault(path);

    public bool DirectoryExists(string path) => path == "/" || (_items.TryGetValue(path, out var item) && item.IsDirectory);

    public IReadOnlyCollection<ArchiveItem> Children(string directory) =>
        _children.TryGetValue(directory, out var children) ? children.Values : [];

    /// <summary>Adds or replaces an item; missing parent folders are implied.</summary>
    public void Add(ArchiveItem item)
    {
        var parent = item.ParentPath ?? "/";
        EnsureDirectory(parent);
        if (_items.TryGetValue(item.Path, out var existing))
        {
            if (existing.IsDirectory && item.IsDirectory)
            {
                // Same folder listed twice ("dir/" entry after files below it): keep the tree, take the details.
                existing.Key ??= item.Key;
                existing.EntryIndex = existing.EntryIndex < 0 ? item.EntryIndex : existing.EntryIndex;
                existing.Modified = item.Modified.Year < 1980 ? existing.Modified : item.Modified;
                existing.ExplicitDirectory |= item.ExplicitDirectory;
                return;
            }
            Remove(item.Path);
        }
        _items[item.Path] = item;
        _children[parent][item.Name] = item;
        if (item.IsDirectory)
        {
            _children.TryAdd(item.Path, new Dictionary<string, ArchiveItem>(StringComparer.Ordinal));
        }
    }

    public void EnsureDirectory(string path)
    {
        if (path == "/" || _items.ContainsKey(path))
        {
            return;
        }
        Add(new ArchiveItem { Path = path, IsDirectory = true, Modified = ImpliedTime });
    }

    /// <summary>Removes the item and everything below it; returns the removed items.</summary>
    public List<ArchiveItem> Remove(string path)
    {
        var removed = new List<ArchiveItem>();
        if (!_items.TryGetValue(path, out var item))
        {
            return removed;
        }
        if (item.IsDirectory && _children.TryGetValue(path, out var children))
        {
            foreach (var child in children.Values.ToList())
            {
                removed.AddRange(Remove(child.Path));
            }
            _children.Remove(path);
        }
        _items.Remove(path);
        if (item.ParentPath is { } parent && _children.TryGetValue(parent, out var siblings))
        {
            siblings.Remove(item.Name);
        }
        removed.Add(item);
        return removed;
    }

    /// <summary>Items at and below <paramref name="path"/>.</summary>
    public IEnumerable<ArchiveItem> Below(string path)
    {
        if (!_items.TryGetValue(path, out var item))
        {
            yield break;
        }
        yield return item;
        if (!item.IsDirectory)
        {
            yield break;
        }
        foreach (var child in Children(path).ToList())
        {
            foreach (var x in Below(child.Path))
            {
                yield return x;
            }
        }
    }
}
