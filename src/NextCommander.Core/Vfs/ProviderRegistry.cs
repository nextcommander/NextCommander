namespace NextCommander.Core.Vfs;

public sealed class ProviderRegistry
{
    private readonly List<IFileSystemProvider> _providers = [];

    public ProviderRegistry(IEnumerable<IFileSystemProvider> providers)
    {
        _providers.AddRange(providers);
    }

    public IReadOnlyList<IFileSystemProvider> Providers => _providers;

    public void Register(IFileSystemProvider provider) => _providers.Add(provider);

    /// <summary>Root of the container (archive) <paramref name="file"/> can be opened as, or null.</summary>
    public VfsPath? FindContainerRoot(VfsEntry file, bool force = false) =>
        file.IsDirectory ? null : _providers.OfType<IContainerProvider>().FirstOrDefault(c => c.CanOpen(file, force))?.GetRoot(file.Path);

    /// <summary>
    /// Maps a typed local path that runs through an archive file (<c>C:\x\a.zip\dir\f.txt</c>, as shown in the path
    /// bar) to the archive path. Other paths are returned unchanged.
    /// </summary>
    public VfsPath MapIntoContainers(VfsPath path)
    {
        if (!path.IsLocal || File.Exists(path.Path) || Directory.Exists(path.Path))
        {
            return path;
        }
        var inner = new Stack<string>();
        for (var p = path; p is not null; p = p.Parent)
        {
            if (File.Exists(p.Path))
            {
                var container = _providers.OfType<IContainerProvider>().FirstOrDefault(c => c.CanOpen(p.Name));
                if (container is null)
                {
                    return path;
                }
                var result = container.GetRoot(p);
                while (inner.Count > 0)
                {
                    result = result.Combine(inner.Pop());
                }
                return result;
            }
            if (Directory.Exists(p.Path))
            {
                return path;
            }
            inner.Push(p.Name);
        }
        return path;
    }

    /// <summary>Writes buffered changes of all providers (after a job, before exit).</summary>
    public async Task FlushAsync()
    {
        foreach (var provider in _providers.OfType<IFlushableProvider>())
        {
            await provider.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Where a root of a file system came from (<see cref="IOriginProvider"/>), or null.</summary>
    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root) =>
        _providers.FirstOrDefault(p => p.CanHandle(root)) is IOriginProvider origin ? origin.OriginOf(root) : null;

    public IFileSystemProvider Resolve(VfsPath path) =>
        _providers.FirstOrDefault(p => p.CanHandle(path))
        ?? throw new NotSupportedException($"No file system provider for '{path.Scheme}'.");
}
