using System.Globalization;
using System.Text;
using NextCommander.Core.Matching;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Columns;

public enum ColumnKind
{
    Text,
    Number,
    Size,
    Date,
}

/// <summary>A value a column can show: "provider.field".</summary>
public sealed record ColumnField(string Id, string DisplayName, ColumnKind Kind = ColumnKind.Text)
{
    public string Provider => Id[..Id.IndexOf('.', StringComparison.Ordinal)];
}

/// <summary>
/// Source of column values (built in or a plugin). Values are computed in the background;
/// return null for "not available".
/// </summary>
public interface IColumnProvider
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<ColumnField> Fields { get; }

    /// <summary>Cheap check before any work (extension, directory, local file...).</summary>
    bool Supports(VfsEntry entry);

    /// <summary>Values of the requested fields (ids as in <see cref="Fields"/>) for one entry.</summary>
    Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken);
}

/// <summary>One column of a custom column set.</summary>
public sealed class ColumnSpec
{
    public string Field { get; set; } = string.Empty;

    /// <summary>Header text; empty = the field's name.</summary>
    public string? Header { get; set; }

    public double Width { get; set; } = 110;
}

/// <summary>A custom column set (view mode): extra columns after the name, optionally chosen automatically by path.</summary>
public sealed class ColumnSet
{
    public string Name { get; set; } = "New view";

    public List<ColumnSpec> Columns { get; set; } = [];

    /// <summary>Show size / date / attributes too (otherwise only name + the custom columns).</summary>
    public bool KeepStandardColumns { get; set; } = true;

    /// <summary>Folders whose full path matches this mask switch to the set automatically ("*\Pictures*", "/home/*/Music*").</summary>
    public string? AutoPathMask { get; set; }

    /// <summary>List background while the view mode is shown ("#RRGGBB" or a name; empty = the normal one).</summary>
    public string? Background { get; set; }

    /// <summary>Color of the tab showing this view mode.</summary>
    public string? TabColor { get; set; }

    /// <summary>Command run when a panel switches to this view mode ("nc_..." with parameters, or a user command).</summary>
    public string? EnterCommand { get; set; }

    /// <summary>Command run when a panel leaves this view mode.</summary>
    public string? ExitCommand { get; set; }

    /// <summary>The icon of the set in the column sets window: "image", "audio", "video", "code", "document"; empty = a file.</summary>
    public string? Icon { get; set; }

    /// <summary>A copy with its own columns.</summary>
    public ColumnSet Clone() => new()
    {
        Name = Name,
        Columns = Columns.Select(c => new ColumnSpec { Field = c.Field, Header = c.Header, Width = c.Width }).ToList(),
        KeepStandardColumns = KeepStandardColumns,
        AutoPathMask = AutoPathMask,
        Background = Background,
        TabColor = TabColor,
        EnterCommand = EnterCommand,
        ExitCommand = ExitCommand,
        Icon = Icon,
    };

    public bool MatchesPath(VfsPath path) =>
        !string.IsNullOrWhiteSpace(AutoPathMask)
        && AutoPathMask.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(m => WildcardMatcher.ToRegex(m).IsMatch(path.ToDisplayString()));
}

/// <summary>All column providers (built in and plugins) and value formatting.</summary>
public sealed class ColumnRegistry
{
    private readonly List<IColumnProvider> _providers = [];

    public ColumnRegistry(IEnumerable<IColumnProvider>? providers = null)
    {
        _providers.AddRange(providers ?? []);
    }

    public IReadOnlyList<IColumnProvider> Providers => _providers;

    public void Register(IColumnProvider provider)
    {
        _providers.RemoveAll(p => p.Id == provider.Id);
        _providers.Add(provider);
    }

    public IEnumerable<ColumnField> AllFields => _providers.SelectMany(p => p.Fields);

    public ColumnField? Find(string fieldId) => AllFields.FirstOrDefault(f => string.Equals(f.Id, fieldId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Values for one entry, each provider asked once for its fields.</summary>
    public async Task<Dictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in fieldIds.GroupBy(f => f[..Math.Max(0, f.IndexOf('.', StringComparison.Ordinal))], StringComparer.OrdinalIgnoreCase))
        {
            var provider = _providers.FirstOrDefault(p => string.Equals(p.Id, group.Key, StringComparison.OrdinalIgnoreCase));
            if (provider is null || !provider.Supports(entry))
            {
                continue;
            }
            try
            {
                foreach (var (key, value) in await provider.GetValuesAsync(entry, group.ToList(), registry, cancellationToken).ConfigureAwait(false))
                {
                    result[key] = value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException)
            {
                // unreadable file: empty cells
            }
        }
        return result;
    }

    public static string Format(object? value, ColumnKind kind) => value switch
    {
        null => string.Empty,
        DateTime d => d.ToString("g", CultureInfo.CurrentCulture),
        TimeSpan t => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture),
        long l when kind == ColumnKind.Size => Formatting.SizeFormatter.Bytes(l),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>Values every entry has: no file reading.</summary>
public sealed class FileInfoColumns : IColumnProvider
{
    public string Id => "file";

    public string DisplayName => "File";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new("file.created", "Created", ColumnKind.Date),
        new("file.accessed", "Last accessed", ColumnKind.Date),
        new("file.pathlength", "Path length", ColumnKind.Number),
        new("file.namelength", "Name length", ColumnKind.Number),
        new("file.folder", "Parent folder"),
        new("file.type", "Type"),
        new("file.age", "Age (days)", ColumnKind.Number),
    ];

    public bool Supports(VfsEntry entry) => true;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in fieldIds)
        {
            values[id] = id.ToLowerInvariant() switch
            {
                "file.created" => entry.CreationTime,
                "file.accessed" => entry.LastAccessTime,
                "file.pathlength" => (long)entry.Path.ToDisplayString().Length,
                "file.namelength" => (long)entry.Name.Length,
                "file.folder" => entry.Path.Parent?.Name,
                "file.type" => entry.IsDirectory ? "Folder" : entry.Extension.Length == 0 ? "File" : entry.Extension.ToUpperInvariant() + " file",
                "file.age" => (long)Math.Max(0, (DateTime.Now - entry.LastWriteTime).TotalDays),
                _ => null,
            };
        }
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }
}

/// <summary>Text files: line count, encoding, first line.</summary>
public sealed class TextColumns : IColumnProvider
{
    private const long MaxBytes = 64L * 1024 * 1024;

    public string Id => "text";

    public string DisplayName => "Text";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new("text.lines", "Line count", ColumnKind.Number),
        new("text.encoding", "Encoding"),
        new("text.firstline", "First line"),
    ];

    public bool Supports(VfsEntry entry) => !entry.IsDirectory && entry.Size <= MaxBytes && entry.Path.IsLocal;

    public async Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await using var stream = await registry.Resolve(entry.Path).OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        var head = new byte[8192];
        var n = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (EncodingDetector.LooksBinary(head.AsSpan(0, n)))
        {
            return values;
        }
        var encoding = EncodingDetector.Detect(head.AsSpan(0, n), out var bom, complete: n < head.Length);
        values["text.encoding"] = encoding.WebName.ToUpperInvariant() + (bom > 0 ? " BOM" : string.Empty);
        if (fieldIds.Contains("text.firstline", StringComparer.OrdinalIgnoreCase))
        {
            var text = encoding.GetString(head, bom, n - bom);
            var end = text.IndexOfAny(['\r', '\n']);
            values["text.firstline"] = (end < 0 ? text : text[..end]).Trim();
        }
        if (fieldIds.Contains("text.lines", StringComparer.OrdinalIgnoreCase))
        {
            // Count '\n' (UTF-16: the 0x0A of every code unit pair).
            var charSize = encoding.CodePage is 1200 or 1201 ? 2 : 1;
            long lines = 0;
            long total = 0;
            var lastWasNewline = true;
            stream.Position = 0;
            var buffer = new byte[256 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n' && (charSize == 1 || ((total + i) % 2 == (encoding.CodePage == 1201 ? 1 : 0))))
                    {
                        lines++;
                        lastWasNewline = true;
                    }
                    else if (buffer[i] != '\r' && buffer[i] != 0)
                    {
                        lastWasNewline = false;
                    }
                }
                total += count;
            }
            values["text.lines"] = lines + (lastWasNewline || total == 0 ? 0 : 1);
        }
        return values;
    }
}

/// <summary>File comments from descript.ion (Ctrl+Z edits them).</summary>
public sealed class CommentColumns : IColumnProvider
{
    private readonly Dictionary<string, (DateTime Stamp, Dictionary<string, string> Comments)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string Id => "comment";

    public string DisplayName => "Comment";

    public IReadOnlyList<ColumnField> Fields { get; } = [new("comment.text", "Comment")];

    public bool Supports(VfsEntry entry) => entry.Path.IsLocal && entry.Path.Parent is not null;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var dir = entry.Path.Parent!.Path;
        var file = Tools.Descriptions.PathIn(dir);
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
        Dictionary<string, string> comments;
        lock (_lock)
        {
            if (!_cache.TryGetValue(dir, out var cached) || cached.Stamp != stamp)
            {
                cached = (stamp, stamp == default ? new Dictionary<string, string>() : Tools.Descriptions.Read(dir));
                _cache[dir] = cached;
            }
            comments = cached.Comments;
        }
        var text = comments.TryGetValue(entry.Name, out var c) ? c.Replace('\n', ' ') : null;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["comment.text"] = text });
    }
}
