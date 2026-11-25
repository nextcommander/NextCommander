using System.Collections.Concurrent;
using System.Diagnostics;
using NextCommander.Core.Net;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Repositories;

/// <summary>
/// A Git hosting service's repositories as read-only folders, through its REST API (nothing is cloned):
/// <list type="bullet">
/// <item><c>github:///</c> lists the signed-in user's repositories (with a token), the ones of the settings and the ones
/// opened with "Open a repository..." as <c>owner⁄name</c>.</item>
/// <item><c>github://&lt;owner&gt;/</c> lists an owner's repositories (a GitLab group: its projects and subgroups; a
/// group path's slashes are written <c>|</c> in the authority, <c>gitlab://group|sub/project/</c>).</item>
/// <item><c>github://&lt;owner&gt;/&lt;repo&gt;/</c> holds the folders <c>branches</c>, <c>tags</c> and <c>releases</c>
/// and the default branch's files; <c>branches/&lt;branch&gt;/&lt;path&gt;</c> and <c>tags/&lt;tag&gt;/&lt;path&gt;</c> the
/// files there (a slash in a branch name is shown as <c>⁄</c>); <c>releases/&lt;tag&gt;/</c> a release's assets.</item>
/// </list>
/// Trees are read once per commit (recursively, folder by folder when the host cuts a big one short) and every file
/// carries its commit's time. The file list menu opens an entry's page in the browser; the Plugins menu opens any
/// repository by its name and clones one with the installed git.
/// </summary>
public abstract class RepositoryFileSystemProvider : IFileSystemProvider, IPluginEntryPoint, IOriginProvider, IPluginEntryActions, IPluginFolderActions,
    IDisposable
{
    internal const string Branches = "branches";
    internal const string Tags = "tags";
    internal const string Releases = "releases";

    /// <summary>A slash in a name shown as a fraction slash, so the name stays one folder.</summary>
    internal const char NameSlash = '⁄';

    /// <summary>How long a listing of repositories, refs and releases is used before the host is asked again.</summary>
    internal static TimeSpan Fresh { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Func<RepositoryOptions> _options;
    private readonly HttpMessageHandler? _handler;
    private readonly object _hostLock = new();
    private RepositoryHost? _host;
    private RepositoryOptions? _hostOptions;
    private int _hostProxyVersion;
    private HttpClient? _client;

    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TreeIndex> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<TreeItem>> _folders = new(StringComparer.Ordinal);

    /// <summary>The repositories opened with "Open a repository..." in this run (listed at the root).</summary>
    private readonly ConcurrentDictionary<string, string> _opened = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="options">The host's settings now (the loader's instance: the plugin's settings).</param>
    /// <param name="handler">The HTTP handler (a fake one in tests); null: the program's proxy.</param>
    private protected RepositoryFileSystemProvider(Func<RepositoryOptions> options, HttpMessageHandler? handler)
    {
        _options = options;
        _handler = handler;
    }

    public abstract string Scheme { get; }

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public abstract string EntryName { get; }

    public string EntryGroup => "GitHub / GitLab repositories";

    public VfsPath EntryRoot => VfsPath.Create(Scheme, string.Empty, "/");

    public string? Unavailable => null;

    private protected abstract RepositoryHost CreateHost(HttpClient client, RepositoryOptions options);

    /// <summary>The host for the settings now (made again when they or the program's proxy change; the caches go with it).</summary>
    internal RepositoryHost Host
    {
        get
        {
            var options = _options();
            lock (_hostLock)
            {
                var proxy = _handler is null ? WebProxyConfig.Version : 0;
                if (_host is null || !ReferenceEquals(options, _hostOptions) || proxy != _hostProxyVersion)
                {
                    if (_client is null || proxy != _hostProxyVersion)
                    {
                        _client?.Dispose();
                        _client = _handler is null ? RepositoryHost.CreateClient() : new HttpClient(_handler, disposeHandler: false);
                    }
                    if (!ReferenceEquals(options, _hostOptions))
                    {
                        _cache.Clear();
                        _trees.Clear();
                        _folders.Clear();
                    }
                    _host = CreateHost(_client, options);
                    _hostOptions = options;
                    _hostProxyVersion = proxy;
                }
                return _host;
            }
        }
    }

    public void Dispose()
    {
        lock (_hostLock)
        {
            _client?.Dispose();
            _client = null;
            _host = null;
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- the paths

    /// <summary>Where a path is: the owner (null: the root), the repository, the kind of folder, the ref and the path in its tree.</summary>
    internal sealed record Location(string? Owner, string? Repository, string? Kind, string? Reference, string Inner)
    {
        /// <summary>The default branch's files (directly in the repository's folder).</summary>
        public bool IsDefaultBranch => Repository is not null && Kind is null;
    }

    internal static string EncodeOwner(string owner) => owner.Replace('/', '|');

    internal static string DecodeOwner(string authority) => authority.Replace('|', '/');

    /// <summary>A branch or tag name as one folder ("feature/x" → "feature⁄x") and back.</summary>
    internal static string FolderName(string reference) => reference.Replace('/', NameSlash);

    internal static string RefName(string folder) => folder.Replace(NameSlash, '/');

    /// <summary>The folder of a repository: <c>github://owner/name/</c>.</summary>
    public VfsPath RepositoryPath(string owner, string name) => VfsPath.Create(Scheme, EncodeOwner(owner), "/" + name);

    internal static Location Parse(VfsPath path)
    {
        if (path.Authority.Length == 0)
        {
            return path.Path == "/" ? new Location(null, null, null, null, string.Empty) : throw new DirectoryNotFoundException(path.ToString());
        }
        var owner = DecodeOwner(path.Authority);
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new Location(owner, null, null, null, string.Empty);
        }
        if (parts.Length > 1 && parts[1] is Branches or Tags or Releases)
        {
            return new Location(owner, parts[0], parts[1], parts.Length > 2 ? RefName(parts[2]) : null, string.Join('/', parts.Skip(3)));
        }
        return new Location(owner, parts[0], null, null, string.Join('/', parts.Skip(1)));
    }

    // ---------------------------------------------------------------- cached answers

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> read)
        where T : class
    {
        if (_cache.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < Fresh && known.Value is T value)
        {
            return value;
        }
        var fresh = await read().ConfigureAwait(false);
        _cache[key] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    private Task<RepositoryInfo> RepositoryAsync(RepositoryHost host, string owner, string name, CancellationToken ct) =>
        CachedAsync($"repo\n{owner}/{name}", () => host.RepositoryAsync(owner, name, ct));

    /// <summary>The commit a location's files come from: its branch or tag, or the default branch (null: an empty repository).</summary>
    private async Task<CommitInfo?> CommitAsync(RepositoryHost host, Location location, CancellationToken ct)
    {
        var (owner, name) = (location.Owner!, location.Repository!);
        string reference;
        var tag = location.Kind == Tags;
        if (location.IsDefaultBranch)
        {
            if ((await RepositoryAsync(host, owner, name, ct).ConfigureAwait(false)).DefaultBranch is not { Length: > 0 } branch)
            {
                return null;
            }
            reference = branch;
        }
        else
        {
            reference = location.Reference ?? throw new DirectoryNotFoundException(owner + "/" + name);
        }
        return await CachedAsync($"commit\n{owner}/{name}\n{(tag ? "t" : "b")}\n{reference}", () => host.CommitAsync(owner, name, reference, tag, ct)).ConfigureAwait(false);
    }

    /// <summary>A commit's tree by folder (folder path → its entries), read once.</summary>
    private sealed class TreeIndex(TreeListing listing)
    {
        public bool Complete { get; } = listing.Complete;

        public Dictionary<string, List<TreeItem>> Folders { get; } = Build(listing.Items);

        private static Dictionary<string, List<TreeItem>> Build(IReadOnlyList<TreeItem> items)
        {
            var folders = new Dictionary<string, List<TreeItem>>(StringComparer.Ordinal) { [string.Empty] = [] };
            foreach (var item in items.Where(i => i.IsDirectory))
            {
                folders.TryAdd(item.Path, []);
            }
            foreach (var item in items)
            {
                var slash = item.Path.LastIndexOf('/');
                var parent = slash < 0 ? string.Empty : item.Path[..slash];
                if (!folders.TryGetValue(parent, out var list))
                {
                    folders[parent] = list = [];
                }
                list.Add(item);
            }
            return folders;
        }
    }

    /// <summary>The entries of a folder of a commit's tree.</summary>
    private async Task<IReadOnlyList<TreeItem>> FolderItemsAsync(RepositoryHost host, string owner, string name, CommitInfo commit, string folder, CancellationToken ct)
    {
        var treeKey = $"{owner}/{name}\n{commit.Sha}";
        if (!_trees.TryGetValue(treeKey, out var index))
        {
            if (_trees.Count > 32)
            {
                _trees.Clear();
                _folders.Clear();
            }
            index = new TreeIndex(await host.TreeAsync(owner, name, commit, ct).ConfigureAwait(false));
            _trees[treeKey] = index;
        }
        if (index.Complete)
        {
            return index.Folders.TryGetValue(folder, out var items) ? items : throw new DirectoryNotFoundException($"{owner}/{name}/{folder}");
        }
        // a tree the host cut short: folder by folder (each folder's own tree, found in its parent)
        var key = treeKey + "\n" + folder;
        if (_folders.TryGetValue(key, out var known))
        {
            return known;
        }
        string? sha = null;
        if (folder.Length > 0)
        {
            var slash = folder.LastIndexOf('/');
            var parent = slash < 0 ? string.Empty : folder[..slash];
            var siblings = await FolderItemsAsync(host, owner, name, commit, parent, ct).ConfigureAwait(false);
            var item = siblings.FirstOrDefault(i => i.IsDirectory && i.Path == folder) ?? throw new DirectoryNotFoundException($"{owner}/{name}/{folder}");
            sha = item.Sha;
        }
        var read = await host.FolderAsync(owner, name, commit, folder, sha, ct).ConfigureAwait(false);
        _folders[key] = read;
        return read;
    }

    /// <summary>The tree entry at a location (null: not there).</summary>
    private async Task<(CommitInfo Commit, TreeItem Item)?> ItemAsync(RepositoryHost host, Location location, CancellationToken ct)
    {
        if (await CommitAsync(host, location, ct).ConfigureAwait(false) is not { } commit)
        {
            return null;
        }
        var slash = location.Inner.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : location.Inner[..slash];
        try
        {
            var items = await FolderItemsAsync(host, location.Owner!, location.Repository!, commit, parent, ct).ConfigureAwait(false);
            return items.FirstOrDefault(i => i.Path == location.Inner) is { } item ? (commit, item) : null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- listing

    private static VfsEntry Folder(VfsPath path, string name, DateTime time) => new() { Name = name, Path = path, IsDirectory = true, LastWriteTime = time };

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        var host = Host;
        var location = Parse(directory);
        if (location.Owner is null)
        {
            return await RootAsync(host, cancellationToken).ConfigureAwait(false);
        }
        var owner = location.Owner;
        if (location.Repository is not { } name)
        {
            var repositories = await CachedAsync("owner\n" + owner, () => host.OwnerRepositoriesAsync(owner, cancellationToken)).ConfigureAwait(false);
            return [.. repositories.Select(r => r.IsGroup
                ? Folder(VfsPath.Create(Scheme, EncodeOwner(owner + "/" + r.Name), "/"), r.Name, r.Updated)
                : Folder(directory.Combine(r.Name), r.Name, r.Updated))];
        }
        switch (location.Kind)
        {
            case null when location.Inner.Length == 0:
                {
                    var repository = await RepositoryAsync(host, owner, name, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<VfsEntry> files;
                    try
                    {
                        files = await FilesAsync(host, directory, location, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        files = []; // an empty repository names a default branch that does not exist yet
                    }
                    return
                    [
                        .. new[] { Branches, Tags, Releases }.Select(n => Folder(directory.Combine(n), n, repository.Updated)),
                        .. files.Where(f => f.Name is not (Branches or Tags or Releases)),
                    ];
                }
            case Branches or Tags when location.Reference is null:
                {
                    var tag = location.Kind == Tags;
                    var refs = await CachedAsync($"{location.Kind}\n{owner}/{name}",
                        () => tag ? host.TagsAsync(owner, name, cancellationToken) : host.BranchesAsync(owner, name, cancellationToken)).ConfigureAwait(false);
                    return [.. refs.Select(r => Folder(directory.Combine(FolderName(r.Name)), FolderName(r.Name), r.Time))];
                }
            case Releases when location.Reference is null:
                {
                    var releases = await ReleasesAsync(host, owner, name, cancellationToken).ConfigureAwait(false);
                    return [.. releases.Select(r => Folder(directory.Combine(FolderName(r.Tag)), FolderName(r.Tag), r.Time))];
                }
            case Releases:
                {
                    var release = await ReleaseAsync(host, location, cancellationToken).ConfigureAwait(false)
                        ?? throw new DirectoryNotFoundException($"{owner}/{name}: {location.Reference}");
                    if (location.Inner.Length > 0)
                    {
                        throw new DirectoryNotFoundException(directory.ToString());
                    }
                    return [.. release.Assets.Select(a => new VfsEntry { Name = a.Name, Path = directory.Combine(a.Name), Size = a.Size ?? 0, LastWriteTime = a.Time })];
                }
            default:
                return await FilesAsync(host, directory, location, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The files of a folder of a branch or tag (the default branch's: of the repository's folder).</summary>
    private async Task<IReadOnlyList<VfsEntry>> FilesAsync(RepositoryHost host, VfsPath directory, Location location, CancellationToken ct)
    {
        if (await CommitAsync(host, location, ct).ConfigureAwait(false) is not { } commit)
        {
            return []; // an empty repository
        }
        var items = await FolderItemsAsync(host, location.Owner!, location.Repository!, commit, location.Inner, ct).ConfigureAwait(false);
        return [.. items.Select(i => new VfsEntry
        {
            Name = i.Name,
            Path = directory.Combine(i.Name),
            IsDirectory = i.IsDirectory,
            IsLink = i.IsLink,
            Size = i.Size ?? 0,
            LastWriteTime = commit.Time,
        })];
    }

    /// <summary>The root: the signed-in user's repositories, the ones of the settings and the ones opened in this run.</summary>
    private async Task<IReadOnlyList<VfsEntry>> RootAsync(RepositoryHost host, CancellationToken ct)
    {
        var mine = await CachedAsync("mine", () => host.MyRepositoriesAsync(ct)).ConfigureAwait(false);
        var entries = new List<VfsEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string owner, string name, DateTime time)
        {
            if (seen.Add(owner + "/" + name))
            {
                entries.Add(Folder(RepositoryPath(owner, name), FolderName(owner + "/" + name), time));
            }
        }
        foreach (var full in _options().Repositories.Concat(_opened.Keys))
        {
            var slash = full.LastIndexOf('/');
            if (slash > 0)
            {
                Add(full[..slash], full[(slash + 1)..], DateTime.MinValue);
            }
        }
        foreach (var repository in mine)
        {
            Add(repository.Owner, repository.Name, repository.Updated);
        }
        return entries;
    }

    private Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(RepositoryHost host, string owner, string name, CancellationToken ct) =>
        CachedAsync($"releases\n{owner}/{name}", () => host.ReleasesAsync(owner, name, ct));

    private async Task<ReleaseInfo?> ReleaseAsync(RepositoryHost host, Location location, CancellationToken ct) =>
        (await ReleasesAsync(host, location.Owner!, location.Repository!, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Tag == location.Reference);

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var host = Host;
        Location location;
        try
        {
            location = Parse(path);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        try
        {
            if (location.Owner is null)
            {
                return Folder(path, EntryName, DateTime.MinValue);
            }
            if (location.Repository is not { } name)
            {
                return Folder(path, location.Owner[(location.Owner.LastIndexOf('/') + 1)..], DateTime.MinValue);
            }
            var owner = location.Owner;
            if (location.Kind is null && location.Inner.Length == 0)
            {
                var repository = await RepositoryAsync(host, owner, name, cancellationToken).ConfigureAwait(false);
                return Folder(path, name, repository.Updated);
            }
            if (location.Kind is not null && location.Reference is null)
            {
                await RepositoryAsync(host, owner, name, cancellationToken).ConfigureAwait(false);
                return Folder(path, location.Kind, DateTime.MinValue);
            }
            if (location.Kind == Releases)
            {
                if (await ReleaseAsync(host, location, cancellationToken).ConfigureAwait(false) is not { } release)
                {
                    return null;
                }
                if (location.Inner.Length == 0)
                {
                    return Folder(path, path.Name, release.Time);
                }
                return release.Assets.FirstOrDefault(a => a.Name == location.Inner) is { } asset
                    ? new VfsEntry { Name = asset.Name, Path = path, Size = asset.Size ?? 0, LastWriteTime = asset.Time }
                    : null;
            }
            if (location.Inner.Length == 0)
            {
                // a branch or tag folder
                return await CommitAsync(host, location, cancellationToken).ConfigureAwait(false) is { } commit ? Folder(path, path.Name, commit.Time) : null;
            }
            if (await ItemAsync(host, location, cancellationToken).ConfigureAwait(false) is not { } found)
            {
                return null;
            }
            var size = found.Item.IsDirectory ? 0 : await host.FileSizeAsync(owner, name, found.Commit, found.Item, cancellationToken).ConfigureAwait(false) ?? 0;
            return new VfsEntry
            {
                Name = found.Item.Name,
                Path = path,
                IsDirectory = found.Item.IsDirectory,
                IsLink = found.Item.IsLink,
                Size = size,
                LastWriteTime = found.Commit.Time,
            };
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var host = Host;
        var location = Parse(path);
        if (location.Repository is null || location.Inner.Length == 0)
        {
            throw new IOException($"Not a file: {path}");
        }
        if (location.Kind == Releases)
        {
            var release = await ReleaseAsync(host, location, cancellationToken).ConfigureAwait(false);
            var asset = release?.Assets.FirstOrDefault(a => a.Name == location.Inner) ?? throw new FileNotFoundException($"Not found: {path}", path.ToString());
            return await host.OpenAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        var found = await ItemAsync(host, location, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path}", path.ToString());
        if (found.Item.IsDirectory)
        {
            throw new IOException($"Not a file: {path}");
        }
        return await host.OpenFileAsync(location.Owner!, location.Repository, found.Commit, found.Item, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- read-only

    private static NotSupportedException ReadOnly() => new("GitHub and GitLab repositories are read-only here: clone the repository to change its files.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>".." from an owner's folder goes back to the root (onto a GitLab subgroup's parent group: that group).</summary>
    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != Scheme || root.Authority.Length == 0 || root.Path != "/")
        {
            return null;
        }
        var owner = DecodeOwner(root.Authority);
        var slash = owner.LastIndexOf('/');
        return slash > 0 ? (VfsPath.Create(Scheme, EncodeOwner(owner[..slash]), "/"), owner[(slash + 1)..]) : (EntryRoot, string.Empty);
    }

    // ---------------------------------------------------------------- the menus

    /// <summary>Opens an address in the browser (tests replace it).</summary>
    internal static Action<string> OpenBrowser { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>The web page of a path: an owner, a repository, a branch, a folder or file, a release; null for the root.</summary>
    internal string? PageOf(VfsPath path, bool isDirectory)
    {
        if (path.Scheme != Scheme)
        {
            return null;
        }
        var location = Parse(path);
        if (location.Owner is not { } owner)
        {
            return null;
        }
        var host = Host;
        if (location.Repository is not { } name)
        {
            return host.WebUrl + "/" + owner;
        }
        if (location.Kind == Releases)
        {
            return host.PageUrl(owner, name, Releases, location.Reference);
        }
        if (location.Kind is null && location.Inner.Length == 0)
        {
            return host.PageUrl(owner, name);
        }
        // the default branch's files: HEAD (both hosts take it)
        return host.PageUrl(owner, name, location.Kind ?? Branches, location.Reference ?? "HEAD", location.Inner, isDirectory);
    }

    /// <summary>"Open in browser" for every entry below the root.</summary>
    public IReadOnlyList<PluginAction> ActionsFor(VfsEntry entry)
    {
        string? page;
        try
        {
            page = PageOf(entry.Path, entry.IsDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            page = null;
        }
        return page is null
            ? []
            :
            [
                new PluginAction("Open in browser", PluginActionIcon.Info, _ =>
                {
                    OpenBrowser(page);
                    return Task.FromResult<string?>(null);
                }),
            ];
    }

    /// <summary>
    /// In the root: "Open a repository..." (any repository by its name, listed at the root afterwards). In a repository
    /// (or with one selected): "Clone the repository..." into a folder asked for, with the installed git.
    /// </summary>
    public IReadOnlyList<PluginAction> ActionsFor(VfsPath folder, IReadOnlyList<VfsEntry> selected, IPluginDialogs dialogs)
    {
        if (folder.Scheme != Scheme)
        {
            return [];
        }
        var actions = new List<PluginAction>();
        Location here;
        try
        {
            here = Parse(folder);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        if (here.Owner is null)
        {
            actions.Add(new PluginAction("Open a repository...", PluginActionIcon.Other, ct => OpenRepositoryAsync(dialogs, ct)));
        }
        // the repository the folder is in, else the one selected (one only)
        var target = here.Repository is not null ? here : selected.Count == 1 ? TryParse(selected[0].Path) : null;
        if (target is { Owner: { } owner, Repository: { } name })
        {
            var branch = target.Kind is Branches or Tags ? target.Reference : null;
            actions.Add(new PluginAction("Clone the repository...", PluginActionIcon.Other, ct => CloneAsync(owner, name, branch, dialogs, ct)));
        }
        return actions;
    }

    private Location? TryParse(VfsPath path)
    {
        try
        {
            return path.Scheme == Scheme ? Parse(path) : null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A repository's name from what was typed: "owner/name", or its page's address (".git" at the end too).</summary>
    internal static string? RepositoryName(string text, string webUrl)
    {
        var name = text.Trim().Trim('/');
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            name = uri.AbsolutePath.Trim('/');
            if (Uri.TryCreate(webUrl, UriKind.Absolute, out var web) && web.AbsolutePath.Trim('/') is { Length: > 0 } prefix
                && name.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                name = name[(prefix.Length + 1)..];
            }
            // a page inside the repository: GitLab's "/-/tree/...", GitHub's "/tree/...", "/blob/..." ...
            var dash = name.IndexOf("/-/", StringComparison.Ordinal);
            if (dash > 0)
            {
                name = name[..dash];
            }
        }
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = Uri.UnescapeDataString(name);
        return name.Contains('/', StringComparison.Ordinal) && !name.StartsWith('/') ? name : null;
    }

    private async Task<string?> OpenRepositoryAsync(IPluginDialogs dialogs, CancellationToken ct)
    {
        var host = Host;
        var text = await dialogs.PromptAsync("Open a repository", "The repository's name (owner/name; GitLab: group/project) or its address:", string.Empty).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var full = RepositoryName(text, host.WebUrl);
        if (full is null)
        {
            return $"Not a repository's name: {text.Trim()}";
        }
        var parts = full.Split('/');
        if (host is GitHubHost && parts.Length > 2)
        {
            // a page of the repository ("owner/name/tree/main")
            full = parts[0] + "/" + parts[1];
        }
        var slash = full.LastIndexOf('/');
        var repository = await RepositoryAsync(host, full[..slash], full[(slash + 1)..], ct).ConfigureAwait(false);
        _opened[repository.FullName] = repository.FullName;
        return null;
    }

    /// <summary>The folder the last clone went into (offered again).</summary>
    private static string? s_cloneFolder;

    private async Task<string?> CloneAsync(string owner, string name, string? branch, IPluginDialogs dialogs, CancellationToken ct)
    {
        var host = Host;
        if (GitCloner.Find() is not { } git)
        {
            return GitCloner.NotInstalled;
        }
        var start = s_cloneFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = await dialogs.PromptAsync("Clone the repository",
            string.Format(System.Globalization.CultureInfo.CurrentCulture, "The folder to clone {0} into (the folder {1} is made in it):", owner + "/" + name, name), start)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }
        folder = folder.Trim();
        if (!Directory.Exists(folder))
        {
            return $"The folder does not exist: {folder}";
        }
        s_cloneFolder = folder;
        var target = Path.Combine(folder, name);
        var token = await host.TokenAsync().ConfigureAwait(false);
        await GitCloner.CloneAsync(git, host.CloneUrl(owner, name), target, branch, token is null ? null : (host.CloneUser, token), ct).ConfigureAwait(false);
        return $"Cloned into {target}";
    }
}

/// <summary>GitHub's repositories (github.com, or GitHub Enterprise Server by the API address of the settings).</summary>
public sealed class GitHubFileSystemProvider : RepositoryFileSystemProvider
{
    public const string GitHubScheme = "github";

    /// <summary>The plugin loader's constructor: the plugin's settings and the program's proxy.</summary>
    public GitHubFileSystemProvider()
        : base(() => RepositoriesSettings.GitHub, null)
    {
    }

    internal GitHubFileSystemProvider(RepositoryOptions options, HttpMessageHandler handler)
        : base(() => options, handler)
    {
    }

    public override string Scheme => GitHubScheme;

    public override string EntryName => "GitHub repositories";

    private protected override RepositoryHost CreateHost(HttpClient client, RepositoryOptions options) => new GitHubHost(client, options);
}

/// <summary>GitLab's projects (gitlab.com, or a GitLab server by the address of the settings).</summary>
public sealed class GitLabFileSystemProvider : RepositoryFileSystemProvider
{
    public const string GitLabScheme = "gitlab";

    /// <summary>The plugin loader's constructor: the plugin's settings and the program's proxy.</summary>
    public GitLabFileSystemProvider()
        : base(() => RepositoriesSettings.GitLab, null)
    {
    }

    internal GitLabFileSystemProvider(RepositoryOptions options, HttpMessageHandler handler)
        : base(() => options, handler)
    {
    }

    public override string Scheme => GitLabScheme;

    public override string EntryName => "GitLab repositories";

    private protected override RepositoryHost CreateHost(HttpClient client, RepositoryOptions options) => new GitLabHost(client, options);
}
