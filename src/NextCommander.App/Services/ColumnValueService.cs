using System.Collections.Concurrent;
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>Computes custom column values in the background (a few files at a time) and caches them per file version.</summary>
public sealed class ColumnValueService(ColumnRegistry columns, ProviderRegistry providers)
{
    private const int MaxCached = 100_000;

    private readonly ConcurrentDictionary<string, object?> _cache = new(StringComparer.Ordinal);

    public ColumnRegistry Columns { get; } = columns;

    private static string Key(VfsEntry e, string field) => $"{e.Path}|{e.Size}|{e.LastWriteTime.Ticks}|{field}";

    public bool TryGet(VfsEntry entry, string field, out object? value) => _cache.TryGetValue(Key(entry, field), out value);

    /// <summary>True when every field of the entry is cached.</summary>
    public bool HasAll(VfsEntry entry, IReadOnlyList<string> fields) => fields.All(f => _cache.ContainsKey(Key(entry, f)));

    /// <summary>Loads missing values; <paramref name="loaded"/> runs (on a worker thread) after each entry.</summary>
    public async Task LoadAsync(IReadOnlyList<VfsEntry> entries, IReadOnlyList<string> fields, Action<VfsEntry> loaded, CancellationToken cancellationToken)
    {
        if (_cache.Count > MaxCached)
        {
            _cache.Clear();
        }
        var todo = entries.Where(e => !HasAll(e, fields)).ToList();
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (entry, ct) =>
        {
            var values = await Columns.GetValuesAsync(entry, fields, providers, ct).ConfigureAwait(false);
            foreach (var field in fields)
            {
                _cache[Key(entry, field)] = values.GetValueOrDefault(field);
            }
            loaded(entry);
        }).ConfigureAwait(false);
    }
}
