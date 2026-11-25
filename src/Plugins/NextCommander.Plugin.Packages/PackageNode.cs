namespace NextCommander.Plugin.Packages;

/// <summary>
/// A file or folder of an opened package. A folder may fill itself when it is first listed (the files of a .deb's
/// data.tar.xz, of an image layer): <see cref="Fill"/> runs once, under the node's lock.
/// </summary>
internal sealed class PackageNode
{
    private readonly Dictionary<string, PackageNode> _children = new(StringComparer.Ordinal);
    private readonly List<PackageNode> _order = [];
    private Action<PackageNode>? _fill;
    private bool _filling;

    public PackageNode(string name, bool isDirectory)
    {
        Name = name;
        IsDirectory = isDirectory;
    }

    public string Name { get; private set; }

    public bool IsDirectory { get; }

    public long Size { get; set; }

    /// <summary>Last change, as local time (<see cref="DateTime.MinValue"/>: unknown).</summary>
    public DateTime Time { get; set; }

    public UnixFileMode? Mode { get; set; }

    public string? LinkTarget { get; set; }

    /// <summary>The DOS attributes a cabinet keeps (read-only, hidden, system, archive).</summary>
    public NextCommander.Core.Vfs.EntryAttributes Attributes { get; set; }

    /// <summary>The bytes of a file (null: none, or <see cref="Unreadable"/> says why).</summary>
    public Blob? Content { get; set; }

    /// <summary>Why the file's bytes cannot be read (a compression the plugin cannot unpack).</summary>
    public string? Unreadable { get; set; }

    /// <summary>Fills the folder when it is first listed.</summary>
    public Action<PackageNode>? Fill
    {
        set => _fill = value;
    }

    public IReadOnlyList<PackageNode> Children
    {
        get
        {
            Load();
            return _order;
        }
    }

    public PackageNode? Child(string name)
    {
        Load();
        return _children.GetValueOrDefault(name);
    }

    private void Load()
    {
        lock (_children)
        {
            if (_fill is { } fill && !_filling)
            {
                _filling = true;
                try
                {
                    fill(this);
                    _fill = null;
                }
                catch
                {
                    Clear(); // tried again on the next listing
                    throw;
                }
                finally
                {
                    _filling = false;
                }
            }
        }
    }

    /// <summary>Adds or replaces a child (a folder that is already there keeps its children and takes the new one's details).</summary>
    public PackageNode Put(PackageNode node)
    {
        if (_children.TryGetValue(node.Name, out var existing))
        {
            if (existing.IsDirectory && node.IsDirectory)
            {
                existing.Time = node.Time;
                existing.Mode = node.Mode ?? existing.Mode;
                return existing;
            }
            _order[_order.IndexOf(existing)] = node;
        }
        else
        {
            _order.Add(node);
        }
        _children[node.Name] = node;
        return node;
    }

    public bool Remove(string name)
    {
        if (!_children.Remove(name, out var existing))
        {
            return false;
        }
        _order.Remove(existing);
        return true;
    }

    public void Clear()
    {
        _children.Clear();
        _order.Clear();
    }

    /// <summary>The folder at <paramref name="parts"/>, made (with unknown details) where it is missing; a file in the way is replaced.</summary>
    public PackageNode Folder(IEnumerable<string> parts)
    {
        var node = this;
        foreach (var part in parts)
        {
            var next = node.Child(part);
            if (next is not { IsDirectory: true })
            {
                next = node.Put(new PackageNode(part, isDirectory: true));
            }
            node = next;
        }
        return node;
    }

    /// <summary>Puts <paramref name="node"/> at <paramref name="path"/> ("a/b/c": c is its name), making the folders on the way.</summary>
    public PackageNode Add(string path, PackageNode node)
    {
        var parts = Split(path);
        if (parts.Length == 0)
        {
            return this;
        }
        node.Name = parts[^1];
        return Folder(parts[..^1]).Put(node);
    }

    /// <summary>The node at <paramref name="path"/> ("/a/b"), or null.</summary>
    public PackageNode? Find(string path)
    {
        var node = this;
        foreach (var part in Split(path))
        {
            node = node.Child(part);
            if (node is null)
            {
                return null;
            }
        }
        return node;
    }

    /// <summary>
    /// The parts of an entry name: '/' or '\' separated, without empty, "." and ".." parts (an entry can never point
    /// outside the package), control characters replaced.
    /// </summary>
    public static string[] Split(string path) =>
        [.. path.Split('/', '\\').Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(p => string.Concat(p.Select(c => char.IsControl(c) ? '_' : c)))];
}
