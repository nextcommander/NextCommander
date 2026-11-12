using NextCommander.Core.Vfs;

namespace NextCommander.Core.Search;

/// <summary>Which copy of each duplicate group stays when the others are marked (for deleting / moving).</summary>
public enum DuplicateKeep
{
    /// <summary>The most recently modified one.</summary>
    Newest,
    /// <summary>The oldest one.</summary>
    Oldest,
    /// <summary>The one with the shortest path (the "original" in a backup tree).</summary>
    ShortestPath,
    /// <summary>The one with the longest path.</summary>
    LongestPath,
    /// <summary>The one in the earliest of the searched folders (their order).</summary>
    FirstFolder,
}

/// <summary>Marking rules for duplicate groups: every rule leaves at least one file of each group unmarked.</summary>
public static class DuplicateSelection
{
    /// <summary>All files of each group except the one to keep.</summary>
    public static IReadOnlyList<VfsEntry> AllBut(IEnumerable<IReadOnlyList<VfsEntry>> groups, DuplicateKeep keep, IReadOnlyList<VfsPath>? folders = null)
    {
        var marked = new List<VfsEntry>();
        foreach (var group in groups)
        {
            if (group.Count < 2)
            {
                continue;
            }
            var kept = keep switch
            {
                DuplicateKeep.Newest => group.MaxBy(e => e.LastWriteTime)!,
                DuplicateKeep.Oldest => group.MinBy(e => e.LastWriteTime)!,
                DuplicateKeep.ShortestPath => group.MinBy(e => e.Path.ToDisplayString().Length)!,
                DuplicateKeep.LongestPath => group.MaxBy(e => e.Path.ToDisplayString().Length)!,
                DuplicateKeep.FirstFolder => group.MinBy(e => FolderIndex(e, folders ?? []))!,
                _ => group[0],
            };
            marked.AddRange(group.Where(e => !ReferenceEquals(e, kept)));
        }
        return marked;
    }

    /// <summary>
    /// The files inside <paramref name="folder"/> (and its subfolders); a group lying wholly inside it keeps one
    /// (the first outside rule: never every copy).
    /// </summary>
    public static IReadOnlyList<VfsEntry> InFolder(IEnumerable<IReadOnlyList<VfsEntry>> groups, VfsPath folder)
    {
        var marked = new List<VfsEntry>();
        foreach (var group in groups)
        {
            var inside = group.Where(e => IsUnder(e.Path, folder)).ToList();
            if (inside.Count == group.Count)
            {
                inside.RemoveAt(0);
            }
            marked.AddRange(inside);
        }
        return marked;
    }

    /// <summary>Groups in which every file is marked (deleting them would lose the content).</summary>
    public static IReadOnlyList<IReadOnlyList<VfsEntry>> FullyMarked(IEnumerable<IReadOnlyList<VfsEntry>> groups, IReadOnlySet<VfsPath> marked) =>
        groups.Where(g => g.Count > 0 && g.All(e => marked.Contains(e.Path))).ToList();

    /// <summary>The bytes freed by removing the marked files.</summary>
    public static long Size(IEnumerable<VfsEntry> marked) => marked.Sum(e => e.Size);

    /// <summary>Space the duplicates take beyond one copy of each group.</summary>
    public static long Wasted(IEnumerable<IReadOnlyList<VfsEntry>> groups) => groups.Sum(g => g.Count > 1 ? g[0].Size * (g.Count - 1) : 0);

    private static int FolderIndex(VfsEntry entry, IReadOnlyList<VfsPath> folders)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            if (IsUnder(entry.Path, folders[i]))
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    private static bool IsUnder(VfsPath path, VfsPath folder)
    {
        var p = path.ToDisplayString();
        var f = folder.ToDisplayString().TrimEnd('/', '\\');
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return p.Length > f.Length && p.StartsWith(f, comparison) && p[f.Length] is '/' or '\\';
    }
}
