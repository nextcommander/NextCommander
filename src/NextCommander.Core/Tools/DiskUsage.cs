using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tools;

/// <summary>A file or folder of a disk usage scan; a folder's size and counts are those of everything under it.</summary>
public sealed class UsageNode
{
    private List<UsageNode>? _children;

    public required string Name { get; init; }

    public required VfsPath Path { get; init; }

    public bool IsDirectory { get; init; }

    public UsageNode? Parent { get; internal set; }

    /// <summary>Bytes (a folder: of every file under it).</summary>
    public long Size { get; internal set; }

    /// <summary>Files under a folder (1 for a file).</summary>
    public long Files { get; internal set; }

    /// <summary>Folders under a folder (its own not counted).</summary>
    public long Folders { get; internal set; }

    public DateTime LastWriteTime { get; internal set; }

    /// <summary>A folder that could not be read (its size counts what was readable).</summary>
    public bool Unreadable { get; internal set; }

    /// <summary>Lower-case extension with the dot (".jpg"); empty for folders and files without one.</summary>
    public string Extension => IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Largest first (after the scan).</summary>
    public IReadOnlyList<UsageNode> Children => (IReadOnlyList<UsageNode>?)_children ?? [];

    internal void Add(UsageNode child)
    {
        child.Parent = this;
        (_children ??= []).Add(child);
    }

    internal void Remove(UsageNode child) => _children?.Remove(child);

    internal void Sort()
    {
        _children?.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>Share of the parent's size (1 for the root).</summary>
    public double ShareOfParent => Parent is null || Parent.Size == 0 ? 1 : (double)Size / Parent.Size;

    /// <summary>Takes this node out of the tree (after it was deleted) and lowers the totals of its folders.</summary>
    public void Detach()
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.Files -= Files;
            p.Folders -= Folders + (IsDirectory ? 1 : 0);
        }
        Parent?.Remove(this);
        Parent = null;
    }
}

/// <summary>Running totals of a scan (read from another thread for progress).</summary>
public sealed class UsageProgress
{
    private long _files;
    private long _bytes;

    public long Files => Interlocked.Read(ref _files);

    public long Bytes => Interlocked.Read(ref _bytes);

    public string? CurrentFolder { get; internal set; }

    internal void AddFile(long size)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, size);
    }
}

/// <summary>
/// Folder sizes like WinDirStat: the tree under a folder with the size of every file and folder, the totals per
/// extension, and a treemap layout. Local folders are read directly (fast); others through their provider. Folder links
/// (junctions, symbolic links) are not followed.
/// </summary>
public static class DiskUsage
{
    public static async Task<UsageNode> ScanAsync(ProviderRegistry registry, VfsPath root, UsageProgress progress, CancellationToken cancellationToken)
    {
        var node = new UsageNode { Name = root.Name.Length > 0 ? root.Name : root.ToDisplayString(), Path = root, IsDirectory = true };
        if (root.IsLocal)
        {
            await Task.Run(() => ScanLocal(node, root.Path, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ScanProviderAsync(registry, node, progress, cancellationToken).ConfigureAwait(false);
        }
        return node;
    }

    private static readonly EnumerationOptions s_options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private static void ScanLocal(UsageNode folder, string path, UsageProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress.CurrentFolder = path;
        IEnumerable<FileSystemInfo> items;
        try
        {
            items = new DirectoryInfo(path).EnumerateFileSystemInfos("*", s_options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            folder.Unreadable = true;
            return;
        }
        foreach (var item in items)
        {
            if (item is DirectoryInfo directory)
            {
                var child = new UsageNode { Name = directory.Name, Path = VfsPath.Local(directory.FullName), IsDirectory = true, LastWriteTime = directory.LastWriteTime };
                folder.Add(child);
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    ScanLocal(child, directory.FullName, progress, ct);
                }
                folder.Folders += 1 + child.Folders;
                Accumulate(folder, child);
            }
            else if (item is FileInfo file)
            {
                long size;
                try
                {
                    size = file.Length;
                }
                catch (IOException)
                {
                    size = 0;
                }
                AddFile(folder, file.Name, VfsPath.Local(file.FullName), size, file.LastWriteTime, progress);
            }
        }
        folder.Sort();
    }

    private static async Task ScanProviderAsync(ProviderRegistry registry, UsageNode folder, UsageProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress.CurrentFolder = folder.Path.ToDisplayString();
        IReadOnlyList<VfsEntry> entries;
        try
        {
            entries = await registry.Resolve(folder.Path).ListAsync(folder.Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folder.Unreadable = true;
            return;
        }
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                var child = new UsageNode { Name = entry.Name, Path = entry.Path, IsDirectory = true, LastWriteTime = entry.LastWriteTime };
                folder.Add(child);
                if (!entry.IsLink)
                {
                    await ScanProviderAsync(registry, child, progress, ct).ConfigureAwait(false);
                }
                folder.Folders += 1 + child.Folders;
                Accumulate(folder, child);
            }
            else
            {
                AddFile(folder, entry.Name, entry.Path, entry.Size, entry.LastWriteTime, progress);
            }
        }
        folder.Sort();
    }

    private static void AddFile(UsageNode folder, string name, VfsPath path, long size, DateTime written, UsageProgress progress)
    {
        var file = new UsageNode { Name = name, Path = path, Size = size, Files = 1, LastWriteTime = written };
        folder.Add(file);
        Accumulate(folder, file);
        progress.AddFile(size);
    }

    private static void Accumulate(UsageNode folder, UsageNode child)
    {
        folder.Size += child.Size;
        folder.Files += child.Files;
        if (child.LastWriteTime > folder.LastWriteTime)
        {
            folder.LastWriteTime = child.LastWriteTime;
        }
    }

    /// <summary>Size and count of the files per extension under a node, largest first.</summary>
    public static IReadOnlyList<(string Extension, long Size, long Files)> Extensions(UsageNode root)
    {
        var totals = new Dictionary<string, (long Size, long Files)>(StringComparer.Ordinal);
        var stack = new Stack<UsageNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsDirectory)
            {
                var t = totals.GetValueOrDefault(node.Extension);
                totals[node.Extension] = (t.Size + node.Size, t.Files + 1);
                continue;
            }
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
        return totals.Select(kv => (kv.Key, kv.Value.Size, kv.Value.Files)).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
}

/// <summary>A rectangle of the treemap.</summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// Squarified treemap (Bruls, Huizing, van Wijk): every file gets a rectangle whose area is its share of the whole,
/// placed inside its folder's rectangle, in rows that keep the rectangles close to squares.
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// The rectangles of the files (and of folders too small to divide further), drawn in this order. Nodes smaller than
    /// <paramref name="minimumArea"/> are left out (their space stays with the folder).
    /// </summary>
    public static List<(UsageNode Node, TreemapRect Rect)> Layout(UsageNode root, TreemapRect bounds, double minimumArea = 1)
    {
        var result = new List<(UsageNode, TreemapRect)>();
        if (root.Size <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }
        Place(root, bounds, minimumArea, result);
        return result;
    }

    private static void Place(UsageNode node, TreemapRect rect, double minimumArea, List<(UsageNode, TreemapRect)> result)
    {
        if (!node.IsDirectory || node.Children.Count == 0 || rect.Width < 2 || rect.Height < 2)
        {
            result.Add((node, rect));
            return;
        }
        var children = node.Children.Where(c => c.Size > 0).ToList(); // already largest first
        var scale = rect.Area / node.Size;
        var areas = children.Select(c => c.Size * scale).ToList();
        var placed = Squarify(areas, rect);
        for (var i = 0; i < children.Count; i++)
        {
            if (placed[i].Area >= minimumArea)
            {
                Place(children[i], placed[i], minimumArea, result);
            }
        }
    }

    /// <summary>Rectangles for <paramref name="areas"/> (largest first) filling <paramref name="rect"/>.</summary>
    public static List<TreemapRect> Squarify(IReadOnlyList<double> areas, TreemapRect rect)
    {
        var result = new List<TreemapRect>(areas.Count);
        var free = rect;
        var index = 0;
        while (index < areas.Count)
        {
            var side = Math.Min(free.Width, free.Height);
            if (side <= 0)
            {
                // no room left (rounding): the rest gets empty rectangles
                for (; index < areas.Count; index++)
                {
                    result.Add(new TreemapRect(free.X, free.Y, 0, 0));
                }
                break;
            }
            // grow the row while the worst aspect ratio improves
            var rowEnd = index + 1;
            var rowSum = areas[index];
            var worst = Worst(areas, index, rowEnd, rowSum, side);
            while (rowEnd < areas.Count)
            {
                var next = Worst(areas, index, rowEnd + 1, rowSum + areas[rowEnd], side);
                if (next > worst)
                {
                    break;
                }
                worst = next;
                rowSum += areas[rowEnd];
                rowEnd++;
            }
            // lay the row along the shorter side
            var thickness = rowSum / side;
            var offset = 0.0;
            var horizontal = free.Width >= free.Height; // the row is a column on the left
            for (var i = index; i < rowEnd; i++)
            {
                var length = rowSum <= 0 ? 0 : areas[i] / rowSum * side;
                result.Add(horizontal
                    ? new TreemapRect(free.X, free.Y + offset, thickness, length)
                    : new TreemapRect(free.X + offset, free.Y, length, thickness));
                offset += length;
            }
            free = horizontal
                ? new TreemapRect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height)
                : new TreemapRect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
            index = rowEnd;
        }
        return result;
    }

    /// <summary>The worst aspect ratio of a row of <paramref name="areas"/>[from..to) laid along a side.</summary>
    private static double Worst(IReadOnlyList<double> areas, int from, int to, double sum, double side)
    {
        if (sum <= 0)
        {
            return double.MaxValue;
        }
        var max = double.MinValue;
        var min = double.MaxValue;
        for (var i = from; i < to; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * Math.Max(min, double.Epsilon)));
    }
}
