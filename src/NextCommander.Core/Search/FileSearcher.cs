using System.Text;
using System.Text.RegularExpressions;
using NextCommander.Core.Matching;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Search;

/// <summary>Live search state for the find files window.</summary>
public sealed class SearchProgress
{
    private long _scanned;
    private long _found;

    public long Scanned => Interlocked.Read(ref _scanned);

    public long Found => Interlocked.Read(ref _found);

    public string? CurrentDirectory { get; internal set; }

    /// <summary>Name of the file index used (null: folders were walked).</summary>
    public string? IndexUsed { get; internal set; }

    /// <summary>Why the index could not be used (then the folders were walked).</summary>
    public string? IndexError { get; internal set; }

    internal void AddScanned() => Interlocked.Increment(ref _scanned);

    internal void AddFound() => Interlocked.Increment(ref _found);
}

/// <summary>Find files engine: walks directories (or a list file / previous results) and applies the criteria.</summary>
public sealed class FileSearcher
{
    private readonly ProviderRegistry _registry;
    private readonly SearchOptions _options;
    private readonly WildcardMatcher _mask;
    private readonly Regex? _nameRegex;
    private readonly TextMatcher? _content;
    private readonly IReadOnlyList<Encoding> _encodings;

    /// <exception cref="ArgumentException">Invalid regex.</exception>
    public FileSearcher(ProviderRegistry registry, SearchOptions options)
    {
        _registry = registry;
        _options = options;
        _mask = WildcardMatcher.Parse(options.NameIsRegex ? null : options.NameMask);
        if (options.NameIsRegex && !string.IsNullOrEmpty(options.NameMask))
        {
            _nameRegex = new Regex(options.NameMask, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        if (!string.IsNullOrEmpty(options.ContainingText))
        {
            _content = new TextMatcher(options.ContainingText, options.CaseSensitive, options.WholeWords, options.ContentIsRegex);
            var encodings = new List<Encoding> { EncodingDetector.Utf8NoBom };
            if (!_content.IsAscii || options.ContentIsRegex)
            {
                encodings.Add(EncodingDetector.Ansi);
            }
            if (options.Utf16)
            {
                encodings.Add(Encoding.Unicode);
            }
            _encodings = encodings;
        }
        else
        {
            _encodings = [];
        }
    }

    public SearchProgress Progress { get; } = new();

    /// <summary>Runs the search, calling <paramref name="found"/> for every hit (on the search thread).</summary>
    /// <param name="within">Search only these entries (search in previous results).</param>
    public async Task RunAsync(Action<VfsEntry> found, CancellationToken cancellationToken, IReadOnlyList<VfsEntry>? within = null)
    {
        EncodingDetector.EnsureCodePages();
        if (within is not null)
        {
            foreach (var entry in within)
            {
                await CheckAsync(entry, found, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var searchIn = _options.SearchIn.Trim();
        if (searchIn.StartsWith('@'))
        {
            await RunListFileAsync(searchIn[1..].Trim().Trim('"'), found, cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (var part in searchIn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (VfsPath.TryParse(EnvironmentExpanderShim(part.Trim('"')), out var root))
            {
                if (_options.UseIndex && root!.IsLocal && FileIndexes.Default is { } index
                    && await TryIndexAsync(index, root, found, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                await WalkAsync(root!, found, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string EnvironmentExpanderShim(string text) => Commands.EnvironmentExpander.Expand(text);

    private async Task RunListFileAsync(string listFile, Action<VfsEntry> found, CancellationToken cancellationToken)
    {
        foreach (var line in await File.ReadAllLinesAsync(listFile, cancellationToken).ConfigureAwait(false))
        {
            var text = line.Trim().Trim('"');
            if (text.Length == 0 || !VfsPath.TryParse(text, out var path))
            {
                continue;
            }
            var entry = await _registry.Resolve(path!).GetEntryAsync(path!, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }
            if (entry.IsDirectory)
            {
                await WalkAsync(entry.Path, found, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CheckAsync(entry, found, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Checks the index candidates below <paramref name="root"/> with every criterion. False (walk instead) when the index
    /// fails before producing anything.
    /// </summary>
    private async Task<bool> TryIndexAsync(IFileIndex index, VfsPath root, Action<VfsEntry> found, CancellationToken cancellationToken)
    {
        var patterns = FileIndexes.SimplePatterns(_options.NameMask, _options.NameIsRegex) ?? [];
        var rootPath = Path.TrimEndingDirectorySeparator(root.Path);
        var provider = _registry.Resolve(root);
        var any = false;
        try
        {
            Progress.CurrentDirectory = $"{rootPath} ({index.Name})";
            await foreach (var path in index.QueryAsync(rootPath, patterns, cancellationToken).ConfigureAwait(false))
            {
                any = true;
                if (!IsBelow(path, rootPath, out var relative) || !PassesWalkRules(relative))
                {
                    continue;
                }
                VfsEntry? entry;
                try
                {
                    entry = await provider.GetEntryAsync(VfsPath.Local(path), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue; // index is stale or the path is unreadable
                }
                if (entry is not null && !(entry.IsDirectory && _nameRegex is null && _mask.IsDirectoryExcluded(entry.Name)))
                {
                    await CheckAsync(entry, found, cancellationToken).ConfigureAwait(false);
                }
            }
            Progress.IndexUsed = index.Name;
            return true;
        }
        catch (IOException ex) when (!any)
        {
            Progress.IndexError = ex.Message;
            return false;
        }
    }

    private static bool IsBelow(string path, string root, out string[] relative)
    {
        relative = [];
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison) || path.Length == prefix.Length)
        {
            return false;
        }
        relative = path[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    /// <summary>The rules the walk applies to folders on the way: depth limit and excluded folders ("| obj\").</summary>
    private bool PassesWalkRules(string[] relative)
    {
        // relative = [dir1, dir2, ..., name]: the item is at depth relative.Length - 1 below the root
        if (_options.MaxDepth >= 0 && relative.Length - 1 > _options.MaxDepth)
        {
            return false;
        }
        if (_nameRegex is null)
        {
            for (var i = 0; i < relative.Length - 1; i++)
            {
                if (_mask.IsDirectoryExcluded(relative[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private async Task WalkAsync(VfsPath root, Action<VfsEntry> found, CancellationToken cancellationToken)
    {
        var pending = new Stack<(VfsPath Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            Progress.CurrentDirectory = dir.ToDisplayString();
            IReadOnlyList<VfsEntry> children;
            try
            {
                children = await _registry.Resolve(dir).ListAsync(dir, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var subdirectories = new List<VfsPath>();
            foreach (var child in children)
            {
                if (child.IsDirectory)
                {
                    if (_nameRegex is null && _mask.IsDirectoryExcluded(child.Name))
                    {
                        continue; // excluded subtree ("| obj\")
                    }
                    var enter = (_options.MaxDepth < 0 || depth < _options.MaxDepth)
                        && (!child.IsLink || _options.FollowLinks)
                        && !(child.IsHidden && _options.SkipHiddenDirectories);
                    if (enter)
                    {
                        subdirectories.Add(child.Path);
                    }
                }
                await CheckAsync(child, found, cancellationToken).ConfigureAwait(false);
            }
            for (var i = subdirectories.Count - 1; i >= 0; i--)
            {
                pending.Push((subdirectories[i], depth + 1));
            }
        }
    }

    private async Task CheckAsync(VfsEntry entry, Action<VfsEntry> found, CancellationToken cancellationToken)
    {
        Progress.AddScanned();
        if (!await MatchesAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        Progress.AddFound();
        found(entry);
    }

    /// <summary>All criteria except the location.</summary>
    public async Task<bool> MatchesAsync(VfsEntry entry, CancellationToken cancellationToken)
    {
        var o = _options;
        if (entry.IsDirectory ? o.Kind == SearchItemKind.Files || _content is not null : o.Kind == SearchItemKind.Directories)
        {
            return false;
        }
        if (!MatchesName(entry))
        {
            return false;
        }
        if (!entry.IsDirectory && ((o.MinSize is { } min && entry.Size < min) || (o.MaxSize is { } max && entry.Size > max)))
        {
            return false;
        }
        if ((o.ModifiedAfter is { } after && entry.LastWriteTime < after) || (o.ModifiedBefore is { } before && entry.LastWriteTime > before))
        {
            return false;
        }
        if (!Check(o.Hidden, EntryAttributes.Hidden, entry) || !Check(o.ReadOnly, EntryAttributes.ReadOnly, entry)
            || !Check(o.Archive, EntryAttributes.Archive, entry) || !Check(o.System, EntryAttributes.System, entry))
        {
            return false;
        }
        if (_content is null)
        {
            return true;
        }
        var contains = await ContainsTextAsync(entry, cancellationToken).ConfigureAwait(false);
        return contains is { } c && c != o.NotContaining;
    }

    private bool MatchesName(VfsEntry entry)
    {
        if (_nameRegex is not null)
        {
            return _nameRegex.IsMatch(entry.Name);
        }
        if (!entry.IsDirectory)
        {
            // A mask with only directory patterns ("obj\") finds directories only.
            return (_mask.HasFileIncludes || !_mask.HasDirectoryIncludes) && _mask.MatchesFile(entry.Name);
        }
        // Directories: explicit "dir\" patterns, otherwise the file patterns ("*proj*" finds folders too).
        return _mask.HasDirectoryIncludes ? _mask.MatchesDirectory(entry.Name) : _mask.HasFileIncludes && _mask.MatchesFile(entry.Name);
    }

    private static bool Check(TriState state, EntryAttributes flag, VfsEntry entry) => state switch
    {
        TriState.Yes => (entry.Attributes & flag) != 0,
        TriState.No => (entry.Attributes & flag) == 0,
        _ => true,
    };

    /// <summary>Null when the file cannot be read (never listed, not even for "not containing").</summary>
    private async Task<bool?> ContainsTextAsync(VfsEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            if (_options.OfficeDocuments && OfficeText.IsSupported(entry.Extension) && stream.CanSeek)
            {
                var text = await OfficeText.ExtractAsync(stream, cancellationToken).ConfigureAwait(false);
                if (text is not null)
                {
                    return _content!.Find(text).Index >= 0;
                }
                stream.Position = 0;
            }
            return await StreamTextScanner.ContainsAsync(stream, _content!, _encodings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
