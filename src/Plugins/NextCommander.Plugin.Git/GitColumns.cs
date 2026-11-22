using System.Collections.Concurrent;
using System.Globalization;
using NextCommander.Core.Columns;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Git;

/// <summary>
/// Git columns for files of a repository on this computer: the state (modified, added, untracked, ignored ...), and
/// the last commit that changed the file or folder (its subject, author, date and hash). One "git status" per
/// repository every few seconds, and one "git log" per folder and commit of HEAD feed every row of a folder.
/// </summary>
public sealed class GitColumns : IColumnProvider
{
    public const string StatusField = "git.status";
    public const string SubjectField = "git.subject";
    public const string AuthorField = "git.author";
    public const string DateField = "git.date";
    public const string HashField = "git.hash";

    /// <summary>How many commits one folder's "git log" reads for its rows; older files are looked up one by one.</summary>
    internal const int LogDepth = 2000;

    private static readonly TimeSpan s_fresh = TimeSpan.FromSeconds(3);

    private readonly GitTool? _tool;
    private readonly ConcurrentDictionary<string, string?> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<(DateTime At, Status Status)>>> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<(DateTime At, string Hash)>>> _heads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Dictionary<string, Commit>>>> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Commit?>>> _single = new(StringComparer.OrdinalIgnoreCase);

    public GitColumns()
        : this(GitTool.Installed)
    {
    }

    public GitColumns(GitTool? tool) => _tool = tool;

    public string Id => "git";

    public string DisplayName => "Git";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new(StatusField, "Git status"),
        new(SubjectField, "Last commit"),
        new(AuthorField, "Commit author"),
        new(DateField, "Commit date", ColumnKind.Date),
        new(HashField, "Commit"),
    ];

    public bool Supports(VfsEntry entry) => _tool is not null && entry.Path.IsLocal;

    /// <summary>The last commit that touched a path.</summary>
    internal sealed record Commit(string Hash, string Author, DateTime Time, string Subject);

    /// <summary>The changed paths of a repository (relative, with /) and the ignored ones.</summary>
    internal sealed record Status(Dictionary<string, string> Files, List<string> IgnoredFolders);

    public async Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>();
        if (_tool is null || entry.Path.Path is not { Length: > 0 } full || Path.GetDirectoryName(full) is not { } folder || RepositoryOf(folder) is not { } repository)
        {
            return values;
        }
        var relative = Path.GetRelativePath(repository, full).Replace('\\', '/');
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            return values;
        }
        try
        {
            if (fieldIds.Contains(StatusField))
            {
                var status = await StatusAsync(repository, cancellationToken).ConfigureAwait(false);
                values[StatusField] = StateOf(status, relative, entry.IsDirectory) is { } state ? Localizer.T(state) : null;
            }
            if (fieldIds.Any(f => f is SubjectField or AuthorField or DateField or HashField))
            {
                var commit = await LastCommitAsync(repository, relative, cancellationToken).ConfigureAwait(false);
                if (commit is not null)
                {
                    values[SubjectField] = commit.Subject;
                    values[AuthorField] = commit.Author;
                    values[DateField] = commit.Time;
                    values[HashField] = commit.Hash;
                }
            }
        }
        catch (IOException)
        {
            // not a working repository (a bare one, a broken one): no values
        }
        return values;
    }

    private string? RepositoryOf(string folder) => _repositories.GetOrAdd(folder, GitTool.RepositoryOf);

    // ---------------------------------------------------------------- status

    private async Task<Status> StatusAsync(string repository, CancellationToken cancellationToken)
    {
        while (true)
        {
            var lazy = _status.GetOrAdd(repository, r => new Lazy<Task<(DateTime, Status)>>(() => ReadStatusAsync(r)));
            (DateTime At, Status Status) read;
            try
            {
                read = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _status.TryRemove(new KeyValuePair<string, Lazy<Task<(DateTime, Status)>>>(repository, lazy)); // asked again next time
                throw;
            }
            var (at, status) = read;
            if (DateTime.UtcNow - at < s_fresh)
            {
                return status;
            }
            _status.TryRemove(new KeyValuePair<string, Lazy<Task<(DateTime, Status)>>>(repository, lazy)); // stale: read again
        }
    }

    private async Task<(DateTime, Status)> ReadStatusAsync(string repository)
    {
        var text = await _tool!.RunAsync(repository, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching"], CancellationToken.None).ConfigureAwait(false);
        return (DateTime.UtcNow, ParseStatus(text));
    }

    /// <summary>"XY path\0" records (a rename or copy is followed by its old path).</summary>
    internal static Status ParseStatus(string text)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        var records = text.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4)
            {
                continue;
            }
            var code = record[..2];
            var path = record[3..];
            if (code[0] is 'R' or 'C')
            {
                i++; // the old path
            }
            if (code == "!!" && path.EndsWith('/'))
            {
                ignored.Add(path.TrimEnd('/'));
                continue;
            }
            files[path] = code;
        }
        return new Status(files, ignored);
    }

    /// <summary>A file's state, or a folder's: ignored, or changes inside it. Null: unchanged.</summary>
    internal static string? StateOf(Status status, string relative, bool directory)
    {
        if (status.IgnoredFolders.Any(f => relative == f || relative.StartsWith(f + "/", StringComparison.Ordinal)))
        {
            return "Ignored";
        }
        if (!directory)
        {
            return status.Files.TryGetValue(relative, out var code) ? Describe(code) : null;
        }
        var prefix = relative + "/";
        return status.Files.Any(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && f.Value != "!!") ? "Changes inside" : null;
    }

    internal static string Describe(string code) => code switch
    {
        "??" => "Untracked",
        "!!" => "Ignored",
        "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU" => "Conflict",
        _ => (code[0] != ' ' ? code[0] : code[1]) switch
        {
            // "Changed", not "Modified": that is the date column of the application too (one text, one translation)
            'M' => code[0] != ' ' && code[1] == ' ' ? "Changed (staged)" : "Changed",
            'A' => "Added",
            'D' => "Deleted",
            'R' => "Renamed",
            'C' => "Copied",
            'T' => "Type changed",
            _ => "Changed",
        },
    };

    // ---------------------------------------------------------------- last commit

    private async Task<string> HeadAsync(string repository, CancellationToken cancellationToken)
    {
        while (true)
        {
            var lazy = _heads.GetOrAdd(repository, r => new Lazy<Task<(DateTime, string)>>(async () =>
                (DateTime.UtcNow, (await _tool!.RunAsync(r, ["rev-parse", "HEAD"], CancellationToken.None).ConfigureAwait(false)).Trim())));
            (DateTime At, string Hash) read;
            try
            {
                read = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _heads.TryRemove(new KeyValuePair<string, Lazy<Task<(DateTime, string)>>>(repository, lazy));
                throw;
            }
            var (at, hash) = read;
            if (DateTime.UtcNow - at < s_fresh)
            {
                return hash;
            }
            _heads.TryRemove(new KeyValuePair<string, Lazy<Task<(DateTime, string)>>>(repository, lazy));
        }
    }

    /// <summary>The last commit that changed a path: from its folder's log (one per folder and HEAD), else looked up alone.</summary>
    private async Task<Commit?> LastCommitAsync(string repository, string relative, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(repository, cancellationToken).ConfigureAwait(false);
        var slash = relative.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : relative[..slash];
        var name = slash < 0 ? relative : relative[(slash + 1)..];
        var log = _logs.GetOrAdd(repository + "\n" + head + "\n" + folder, _ => new Lazy<Task<Dictionary<string, Commit>>>(() => FolderLogAsync(repository, folder)));
        var children = await log.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (children.TryGetValue(name, out var commit))
        {
            return commit;
        }
        // older than the folder's log reaches (or never committed: null)
        var single = _single.GetOrAdd(repository + "\n" + head + "\n" + relative, _ => new Lazy<Task<Commit?>>(async () =>
        {
            var text = await _tool!.RunAsync(repository, ["log", "-1", "--format=%h%x1f%an%x1f%ct%x1f%s", "--", relative], CancellationToken.None).ConfigureAwait(false);
            return ParseHeader(text.Trim());
        }));
        return await single.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One "git log --name-only" of a folder: the newest commit of each of its children (files and subfolders).</summary>
    private async Task<Dictionary<string, Commit>> FolderLogAsync(string repository, string folder)
    {
        var text = await _tool!.RunAsync(repository,
            ["log", "-n", LogDepth.ToString(CultureInfo.InvariantCulture), "--name-only", "--format=%x1e%h%x1f%an%x1f%ct%x1f%s", "--", folder.Length == 0 ? "." : folder],
            CancellationToken.None).ConfigureAwait(false);
        return ParseFolderLog(text, folder);
    }

    internal static Dictionary<string, Commit> ParseFolderLog(string text, string folder)
    {
        var children = new Dictionary<string, Commit>(StringComparer.Ordinal);
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        foreach (var record in text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || ParseHeader(lines[0]) is not { } commit)
            {
                continue;
            }
            foreach (var path in lines.Skip(1))
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var rest = path[prefix.Length..];
                var child = rest.Split('/', 2)[0];
                children.TryAdd(child, commit); // the log is newest first
            }
        }
        return children;
    }

    private static Commit? ParseHeader(string line) =>
        line.Split('\x1f') is [var hash, var author, var seconds, var subject]
        && long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? new Commit(hash, author, DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime, subject)
            : null;
}
