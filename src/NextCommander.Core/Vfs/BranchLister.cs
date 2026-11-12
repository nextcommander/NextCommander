namespace NextCommander.Core.Vfs;

/// <summary>Branch view (Ctrl+B): all files below a directory as one flat list. Directory links are not followed.</summary>
public static class BranchLister
{
    public const int DefaultLimit = 500_000;

    /// <param name="roots">Directories to expand; files given here are included as they are.</param>
    public static async Task<IReadOnlyList<VfsEntry>> ListAsync(ProviderRegistry registry, IEnumerable<VfsEntry> roots,
        int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        var result = new List<VfsEntry>();
        var pending = new Stack<VfsEntry>(roots.Reverse());
        while (pending.Count > 0 && result.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = pending.Pop();
            if (!entry.IsDirectory)
            {
                result.Add(entry);
                continue;
            }
            if (entry.IsLink)
            {
                continue;
            }
            IReadOnlyList<VfsEntry> children;
            try
            {
                children = await registry.Resolve(entry.Path).ListAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable subdirectory: skip
            }
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
        return result;
    }
}
