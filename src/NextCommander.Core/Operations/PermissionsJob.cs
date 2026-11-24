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
        // Windows' "replace on everything inside": the selected items get the change, what is inside takes their rules
        var children = new List<VfsEntry>();
        if (change.ReplaceChildren && !change.Recursive)
        {
            foreach (var entry in entries.Where(e => e.IsDirectory && !e.IsLink))
            {
                await CollectInsideAsync(entry, children, ct).ConfigureAwait(false);
            }
        }
        var reset = new PermissionChange { ResetToInherited = true };
        Progress.SetTotals(0, plan.Count + children.Count);
        foreach (var (entry, itsChange) in plan.Select(e => (e, change)).Concat(children.Select(c => (c, reset))))
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(entry.Name, 0);
            await WithErrorHandlingAsync(entry.Path.ToDisplayString(), () =>
                registry.Resolve(entry.Path) is IPermissionSupport support
                    ? support.SetPermissionsAsync(entry.Path, entry.IsDirectory, itsChange, ct)
                    : throw new NotSupportedException("This place has no permissions to change."), ct).ConfigureAwait(false);
            Progress.CompleteFile();
        }
    }

    /// <summary>Everything inside a folder (not the folder itself); links to folders are not followed.</summary>
    private async Task CollectInsideAsync(VfsEntry folder, List<VfsEntry> found, CancellationToken ct)
    {
        IReadOnlyList<VfsEntry> children = [];
        await WithErrorHandlingAsync(folder.Path.ToDisplayString(), async () =>
            children = await registry.Resolve(folder.Path).ListAsync(folder.Path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            found.Add(child);
            if (child.IsDirectory && !child.IsLink)
            {
                await CollectInsideAsync(child, found, ct).ConfigureAwait(false);
            }
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
