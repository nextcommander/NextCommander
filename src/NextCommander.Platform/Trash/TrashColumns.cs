using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace NextCommander.Platform.Trash;

/// <summary>
/// Column of the trash: the folder an item was deleted from. The panel shows it by itself in the trash
/// (<see cref="ViewMode"/>, chosen automatically unless a view mode of the user matches the trash).
/// </summary>
public sealed class TrashColumns : IColumnProvider
{
    public const string OriginalField = "trash.original";

    public string Id => "trash";

    public string DisplayName => "Trash";

    public IReadOnlyList<ColumnField> Fields { get; } = [new(OriginalField, "Original location")];

    /// <summary>The view mode of the trash: the standard columns and the original location.</summary>
    public static ColumnSet ViewMode { get; } = new()
    {
        Name = "Trash",
        KeepStandardColumns = true,
        Columns = [new ColumnSpec { Field = OriginalField, Width = 320 }],
    };

    public bool Supports(VfsEntry entry) => entry.Path.Scheme == TrashFileSystemProvider.TrashScheme;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var original = registry.Providers.OfType<TrashFileSystemProvider>().FirstOrDefault()?.OriginalPathOf(entry.Path);
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [OriginalField] = original is null ? null : Path.GetDirectoryName(original.TrimEnd('/', '\\')) ?? original,
        };
        return Task.FromResult(values);
    }
}
