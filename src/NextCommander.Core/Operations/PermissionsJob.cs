using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>Changes the permissions of the selected items (and, if asked, of everything inside the folders).</summary>
public sealed class PermissionsJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> entries, PermissionChange change) : FileJob("Permissions")
{
    public IReadOnlyList<VfsEntry> Entries => entries;

    public PermissionChange Change => change;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var plan = new List<VfsEntry>();
        foreach (var entry in entries)
        {
            await CollectAsync(entry, plan, ct).ConfigureAwait(false);
        }
        Progress.SetTotals(0, plan.Count);
        foreach (var entry in plan)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(entry.Name, 0);
            await WithErrorHandlingAsync(entry.Path.ToDisplayString(), () =>
                registry.Resolve(entry.Path) is IPermissionSupport support
                    ? support.SetPermissionsAsync(entry.Path, entry.IsDirectory, change, ct)
                    : throw new NotSupportedException("This place has no permissions to change."), ct).ConfigureAwait(false);
            Progress.CompleteFile();
        }
    }

    /// <summary>The item, then (recursive) what is inside; links to folders are not followed.</summary>
    private async Task CollectAsync(VfsEntry entry, List<VfsEntry> plan, CancellationToken ct)
    {
        plan.Add(entry);
        if (!change.Recursive || !entry.IsDirectory || entry.IsLink)
        {
            return;
        }
        IReadOnlyList<VfsEntry> children = [];
        await WithErrorHandlingAsync(entry.Path.ToDisplayString(), async () =>
            children = await registry.Resolve(entry.Path).ListAsync(entry.Path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            await CollectAsync(child, plan, ct).ConfigureAwait(false);
        }
    }
}
