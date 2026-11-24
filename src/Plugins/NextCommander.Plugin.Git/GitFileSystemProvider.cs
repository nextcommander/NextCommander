using System.Collections.Concurrent;
using System.Globalization;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Git;

/// <summary>
/// The history of a Git repository as read-only folders: <c>git://&lt;repository&gt;/</c> holds <c>branches</c>,
/// <c>tags</c> and <c>commits</c> (the newest of the current branch); each branch, tag and commit holds the files as
/// they were there, so an old version is viewed (F3) or copied out like any file. The repository is the authority
/// with its separators as <c>|</c> (<c>git://C:|Projects|NextCommander/branches/main/src</c>). The Plugins menu offers
/// "Git history" while the active panel is in a repository; ".." from its root goes back to the repository's folder.
/// </summary>
public sealed class GitFileSystemProvider : IFileSystemProvider, IPluginFolderEntry, IOriginProvider, IPluginFolderActions
{
    public const string GitScheme = "git";

    internal const string Branches = "branches";
    internal const string Tags = "tags";
    internal const string Commits = "commits";

    /// <summary>How many commits of the current branch the commits folder lists.</summary>
    internal static int CommitCount { get; set; } = 300;

    /// <summary>A slash in a branch or tag name, shown as a fraction slash so the name stays one folder.</summary>
    private const char NameSlash = '⁄';

    private readonly GitTool? _fixedTool;
    private readonly bool _installed;
    private readonly ConcurrentDictionary<string, IReadOnlyList<VfsEntry>> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, string Hash, DateTime Time)> _refs = new(StringComparer.Ordinal);

    /// <summary>The plugin loader's constructor: the installed git.</summary>
    /// <summary>The plugin loader's constructor: the installed git, as the plugin's settings name it now.</summary>
    public GitFileSystemProvider() => _installed = true;

    public GitFileSystemProvider(GitTool? tool) => _fixedTool = tool;

    private GitTool? CurrentTool => _installed ? GitTool.Installed : _fixedTool;

    public string Scheme => GitScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public string EntryName => "Git history";

    public string EntryGroup => "Git";

    // ---------------------------------------------------------------- the paths

    /// <summary>The history root of a repository folder.</summary>
    public static VfsPath RootOf(string repository) => VfsPath.Create(GitScheme, Encode(repository), "/");

    internal static string Encode(string repository) => repository.TrimEnd('\\', '/').Replace('\\', '|').Replace('/', '|');

    internal static string Decode(string authority) => authority.Replace('|', OperatingSystem.IsWindows() ? '\\' : '/');

    /// <summary>Stage, commit, switch branch ... in a repository's working folder (<see cref="GitActions"/>).</summary>
    public IReadOnlyList<PluginAction> ActionsFor(VfsPath folder, IReadOnlyList<VfsEntry> selected, IPluginDialogs dialogs) =>
        (_actions ??= new GitActions(() => CurrentTool)).ActionsFor(folder, selected, dialogs);

    private GitActions? _actions;

    public VfsPath? RootFor(VfsPath folder) =>
        folder.IsLocal && GitTool.RepositoryOf(folder.Path) is { } repository ? RootOf(repository) : null;

    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root) =>
        root.Scheme == GitScheme && root.Authority.Length > 0 ? (VfsPath.Local(Decode(root.Authority)), string.Empty) : null;

    /// <summary>The parts of a path: the repository, the kind (branches / tags / commits), the ref's folder name, the path in its tree.</summary>
    private static (string Repository, string? Kind, string? Ref, string Inner) Split(VfsPath path)
    {
        if (path.Authority.Length == 0)
        {
            throw new DirectoryNotFoundException("Open the Git history from a folder of a repository (Plugins > Git history).");
        }
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (Decode(path.Authority), parts.Length > 0 ? parts[0] : null, parts.Length > 1 ? parts[1] : null, string.Join('/', parts.Skip(2)));
    }

    /// <summary>The revision a ref folder stands for: a branch or tag name, or a commit's hash from its folder name.</summary>
    private static string RevisionOf(string kind, string folder) => kind switch
    {
        Branches => "refs/heads/" + folder.Replace(NameSlash, '/'),
        Tags => "refs/tags/" + folder.Replace(NameSlash, '/'),
        Commits when folder.Split(' ', 4) is [_, _, var hash, ..] => hash,
        _ => throw new DirectoryNotFoundException(folder),
    };

    private GitTool Tool => CurrentTool ?? throw new IOException(GitTool.NotInstalled);

    // ---------------------------------------------------------------- listing

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        var (repository, kind, folder, inner) = Split(directory);
        if (kind is null)
        {
            var time = Directory.GetLastWriteTime(repository);
            return [.. new[] { Branches, Tags, Commits }.Select(n => Folder(directory, n, time))];
        }
        if (folder is null)
        {
            return kind switch
            {
                Branches => await RefsAsync(directory, repository, "refs/heads", cancellationToken).ConfigureAwait(false),
                Tags => await RefsAsync(directory, repository, "refs/tags", cancellationToken).ConfigureAwait(false),
                Commits => await CommitsAsync(directory, repository, cancellationToken).ConfigureAwait(false),
                _ => throw new DirectoryNotFoundException(directory.ToString()),
            };
        }
        var (hash, commitTime) = await ResolveAsync(repository, RevisionOf(kind, folder), cancellationToken).ConfigureAwait(false);
        return await TreeAsync(directory, repository, hash, inner, commitTime, cancellationToken).ConfigureAwait(false);
    }

    private static VfsEntry Folder(VfsPath parent, string name, DateTime time) =>
        new() { Name = name, Path = parent.Combine(name), IsDirectory = true, LastWriteTime = time };

    /// <summary>Branches or tags: a folder each, with its commit's date.</summary>
    private async Task<IReadOnlyList<VfsEntry>> RefsAsync(VfsPath directory, string repository, string prefix, CancellationToken cancellationToken)
    {
        var text = await Tool.RunAsync(repository, ["for-each-ref", "--format=%(refname)%00%(committerdate:unix)%00%(*committerdate:unix)", prefix], cancellationToken).ConfigureAwait(false);
        var entries = new List<VfsEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            var name = fields[0][(prefix.Length + 1)..].Replace('/', NameSlash);
            var seconds = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : fields.Length > 1 ? fields[1] : string.Empty;
            entries.Add(Folder(directory, name, TimeOf(seconds)));
        }
        return entries;
    }

    /// <summary>The newest commits of the current branch: "2026-11-21 23.40 1a2b3c4 subject".</summary>
    private async Task<IReadOnlyList<VfsEntry>> CommitsAsync(VfsPath directory, string repository, CancellationToken cancellationToken)
    {
        var text = await Tool.RunAsync(repository, ["log", "-n", CommitCount.ToString(CultureInfo.InvariantCulture), "--format=%h%x1f%ct%x1f%s"], cancellationToken).ConfigureAwait(false);
        var entries = new List<VfsEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Split('\x1f') is not [var hash, var seconds, var subject])
            {
                continue;
            }
            var time = TimeOf(seconds);
            entries.Add(Folder(directory, CommitFolder(time, hash, subject), time));
        }
        return entries;
    }

    internal static string CommitFolder(DateTime time, string hash, string subject)
    {
        var clean = new string([.. subject.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? ' ' : c)]).Trim();
        if (clean.Length > 60)
        {
            clean = clean[..60].TrimEnd() + "…";
        }
        return $"{time.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture)} {hash} {clean}".TrimEnd();
    }

    private static DateTime TimeOf(string seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime : DateTime.MinValue;

    /// <summary>A revision's commit and its date (a branch moves: looked up again after a few seconds).</summary>
    private async Task<(string Hash, DateTime Time)> ResolveAsync(string repository, string revision, CancellationToken cancellationToken)
    {
        var key = repository + "\n" + revision;
        if (_refs.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromSeconds(5))
        {
            return (known.Hash, known.Time);
        }
        string text;
        try
        {
            text = await Tool.RunAsync(repository, ["log", "-1", "--format=%H%x1f%ct", revision, "--"], cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DirectoryNotFoundException($"{revision.Replace("refs/heads/", string.Empty, StringComparison.Ordinal)}: {ex.Message}", ex);
        }
        if (text.Trim().Split('\x1f') is not [var hash, var seconds])
        {
            throw new DirectoryNotFoundException(revision);
        }
        _refs[key] = (DateTime.UtcNow, hash, TimeOf(seconds));
        return (hash, TimeOf(seconds));
    }

    /// <summary>A folder of a commit's tree (a commit never changes: kept once read). Files carry the commit's date.</summary>
    private async Task<IReadOnlyList<VfsEntry>> TreeAsync(VfsPath directory, string repository, string hash, string inner, DateTime time, CancellationToken cancellationToken)
    {
        var key = repository + "\n" + hash + "\n" + inner;
        if (_trees.TryGetValue(key, out var cached))
        {
            return [.. cached.Select(e => e with { Path = directory.Combine(e.Name) })];
        }
        string text;
        try
        {
            text = await Tool.RunAsync(repository, ["ls-tree", "-l", "-z", hash + ":" + inner], cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DirectoryNotFoundException($"{inner}: {ex.Message}", ex);
        }
        var entries = new List<VfsEntry>();
        foreach (var record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <object> <size>\t<name>"
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }
            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = record[(tab + 1)..];
            if (fields.Length < 4)
            {
                continue;
            }
            var directoryLike = fields[1] is "tree" or "commit"; // a submodule shows as an (empty) folder
            entries.Add(new VfsEntry
            {
                Name = name,
                Path = directory.Combine(name),
                IsDirectory = directoryLike,
                Size = !directoryLike && long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                LastWriteTime = time,
                IsLink = fields[0] == "120000",
            });
        }
        _trees[key] = entries;
        return entries;
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return path.Authority.Length > 0 && Directory.Exists(Decode(path.Authority))
                ? new VfsEntry { Name = Path.GetFileName(Decode(path.Authority)), Path = path, IsDirectory = true, LastWriteTime = Directory.GetLastWriteTime(Decode(path.Authority)) }
                : null;
        }
        if (path.Parent is not { } parent)
        {
            return null;
        }
        try
        {
            var siblings = await ListAsync(parent, cancellationToken).ConfigureAwait(false);
            return siblings.FirstOrDefault(e => string.Equals(e.Name, path.Name, StringComparison.Ordinal));
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (repository, kind, folder, inner) = Split(path);
        if (kind is null || folder is null || inner.Length == 0)
        {
            throw new FileNotFoundException(path.ToString());
        }
        var (hash, _) = await ResolveAsync(repository, RevisionOf(kind, folder), cancellationToken).ConfigureAwait(false);
        return Tool.Stream(repository, ["cat-file", "blob", hash + ":" + inner]);
    }

    // ---------------------------------------------------------------- read-only

    private static NotSupportedException ReadOnly() => new("The Git history is read-only: copy the files out to change them.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
}
