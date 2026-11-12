using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>Deletes entries permanently or moves local entries to the trash.</summary>
public sealed class DeleteJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _sources;
    private readonly ITrashService? _trash;

    public DeleteJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> sources, ITrashService? trash)
        : base(trash is null ? "Delete" : "Move to trash")
    {
        _registry = registry;
        _sources = sources;
        _trash = trash;
    }

    /// <summary>The entries deleted.</summary>
    public IReadOnlyList<VfsEntry> Sources => _sources;

    /// <summary>Whether they go to the recycle bin.</summary>
    public bool UsesTrash => _trash is not null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_trash is { IsAvailable: true } && _sources.All(s => s.Path.IsLocal))
        {
            Progress.SetTotals(0, _sources.Count);
            foreach (var source in _sources)
            {
                await CheckpointAsync(ct).ConfigureAwait(false);
                Progress.BeginItem(source.Name, 0);
                await WithErrorHandlingAsync(source.Path.ToDisplayString(),
                    () => _trash.MoveToTrashAsync(source.Path.Path, ct), ct).ConfigureAwait(false);
                Progress.CompleteFile();
            }
            return;
        }

        // Permanent delete: children first (post-order), never following directory links.
        var plan = new List<VfsEntry>();
        foreach (var source in _sources)
        {
            await CollectAsync(source, plan, ct).ConfigureAwait(false);
        }
        Progress.SetTotals(0, plan.Count);

        foreach (var entry in plan)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (StopAfterCurrentFile)
            {
                throw new OperationCanceledException("Stopped after current file.");
            }
            Progress.BeginItem(entry.Name, 0);
            var provider = _registry.Resolve(entry.Path);
            await WithErrorHandlingAsync(entry.Path.ToDisplayString(),
                () => provider.DeleteAsync(entry.Path, recursive: false, ct), ct).ConfigureAwait(false);
            Progress.CompleteFile();
        }
    }

    private async Task CollectAsync(VfsEntry entry, List<VfsEntry> plan, CancellationToken ct)
    {
        await CheckpointAsync(ct).ConfigureAwait(false);
        if (entry.IsDirectory && !entry.IsLink)
        {
            var provider = _registry.Resolve(entry.Path);
            IReadOnlyList<VfsEntry> children = [];
            await WithErrorHandlingAsync(entry.Path.ToDisplayString(), async () =>
                children = await provider.ListAsync(entry.Path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                await CollectAsync(child, plan, ct).ConfigureAwait(false);
            }
        }
        plan.Add(entry);
    }
}
