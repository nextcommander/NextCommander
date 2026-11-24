using System.Text.Json;
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tags;

/// <summary>A tag files can have: its name and its color label (#RRGGBB).</summary>
public sealed class TagDefinition
{
    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#808080";

    /// <summary>The tags of a new installation: colors with their names, as the user's own labels to rename.</summary>
    public static List<TagDefinition> Defaults() =>
    [
        new() { Name = "Red", Color = "#E53935" },
        new() { Name = "Orange", Color = "#FB8C00" },
        new() { Name = "Yellow", Color = "#FDD835" },
        new() { Name = "Green", Color = "#43A047" },
        new() { Name = "Blue", Color = "#1E88E5" },
        new() { Name = "Purple", Color = "#8E24AA" },
        new() { Name = "Gray", Color = "#757575" },
    ];
}

/// <summary>
/// The tags of files and folders (user's request, 23 November 2026), kept in one file of the settings folder
/// (<c>tags.json</c>) by the full path, so they work the same on every system and every place (local, servers,
/// archives). The program's own copy, move and rename take them along; a file moved by another program loses them.
/// </summary>
public sealed class TagStore
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private static readonly StringComparer s_paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, List<string>> _tags = new(s_paths);
    private readonly Lock _gate = new();

    public TagStore(string? file)
    {
        File = file;
        if (file is not null && System.IO.File.Exists(file))
        {
            try
            {
                using var stream = System.IO.File.OpenRead(file);
                foreach (var (path, tags) in JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream) ?? [])
                {
                    if (tags.Count > 0)
                    {
                        _tags[path] = tags;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // a damaged file: no tags rather than no program
            }
        }
    }

    /// <summary>The store of the program (null until the settings are loaded; tests make their own).</summary>
    public static TagStore? Current { get; set; }

    /// <summary>The file the tags are kept in (null: in memory only).</summary>
    public string? File { get; }

    /// <summary>Tags were added, removed or moved.</summary>
    public event EventHandler? Changed;

    private static string Key(VfsPath path) => path.ToString().TrimEnd('/', '\\') is { Length: > 0 } key ? key : path.ToString();

    /// <summary>The tags of a file or folder, in the order given.</summary>
    public IReadOnlyList<string> TagsOf(VfsPath path)
    {
        lock (_gate)
        {
            return _tags.TryGetValue(Key(path), out var tags) ? [.. tags] : [];
        }
    }

    /// <summary>Whether anything has tags (a panel needs no lookups otherwise).</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _tags.Count == 0;
            }
        }
    }

    /// <summary>
    /// The tag on every one of <paramref name="paths"/>, or off every one when they all have it already (a toggle for a
    /// selection). Returns whether it was added.
    /// </summary>
    public bool Toggle(IReadOnlyCollection<VfsPath> paths, string tag)
    {
        bool add;
        lock (_gate)
        {
            add = !paths.All(p => _tags.TryGetValue(Key(p), out var tags) && tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            foreach (var path in paths)
            {
                var key = Key(path);
                if (add)
                {
                    if (!_tags.TryGetValue(key, out var tags))
                    {
                        _tags[key] = tags = [];
                    }
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }
                else if (_tags.TryGetValue(key, out var tags))
                {
                    tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                    if (tags.Count == 0)
                    {
                        _tags.Remove(key);
                    }
                }
            }
        }
        Save();
        return add;
    }

    /// <summary>Removes every tag of <paramref name="paths"/>.</summary>
    public void Clear(IEnumerable<VfsPath> paths)
    {
        lock (_gate)
        {
            foreach (var path in paths)
            {
                _tags.Remove(Key(path));
            }
        }
        Save();
    }

    /// <summary>A file or folder was moved or renamed: its tags (and those of everything inside a folder) go with it.</summary>
    public void Moved(VfsPath from, VfsPath to) => Carry(from, to, keep: false);

    /// <summary>A file or folder was copied: the copy (and everything inside) gets the same tags.</summary>
    public void Copied(VfsPath from, VfsPath to) => Carry(from, to, keep: true);

    private void Carry(VfsPath from, VfsPath to, bool keep)
    {
        var source = Key(from);
        var target = Key(to);
        var changed = false;
        lock (_gate)
        {
            foreach (var (path, tags) in _tags.ToList())
            {
                string? moved = null;
                if (s_paths.Equals(path, source))
                {
                    moved = target;
                }
                else if (path.Length > source.Length && path.StartsWith(source, s_paths.Equals(StringComparer.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    && path[source.Length] is '/' or '\\')
                {
                    moved = target + path[source.Length..];
                }
                if (moved is null)
                {
                    continue;
                }
                if (!keep)
                {
                    _tags.Remove(path);
                }
                _tags[moved] = [.. tags];
                changed = true;
            }
        }
        if (changed)
        {
            Save();
        }
    }

    /// <summary>A tag was renamed in the settings: the files keep it under its new name.</summary>
    public void RenameTag(string oldName, string newName)
    {
        lock (_gate)
        {
            foreach (var tags in _tags.Values)
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        tags[i] = newName;
                    }
                }
            }
        }
        Save();
    }

    /// <summary>The paths that have <paramref name="tag"/> (for a search by tag).</summary>
    public IReadOnlyList<string> PathsWith(string tag)
    {
        lock (_gate)
        {
            return [.. _tags.Where(t => t.Value.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(t => t.Key)];
        }
    }

    private void Save()
    {
        if (File is { } file)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var temp = file + ".tmp";
                    System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(_tags, s_json));
                    System.IO.File.Move(temp, file, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // kept in memory; written with the next change
                }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The "Tags" column of custom column sets: the tags of a file, separated by commas.</summary>
public sealed class TagColumns : IColumnProvider
{
    public string Id => "tags";

    public string DisplayName => "Tags";

    public IReadOnlyList<ColumnField> Fields { get; } = [new("tags.names", "Tags")];

    public bool Supports(VfsEntry entry) => TagStore.Current is { IsEmpty: false };

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var tags = TagStore.Current?.TagsOf(entry.Path) ?? [];
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["tags.names"] = tags.Count == 0 ? null : string.Join(", ", tags) });
    }
}
