using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>Attribute and time changes: flags to set / clear, optional new modification time, optionally recursive.</summary>
public sealed record AttributeChange(EntryAttributes Set, EntryAttributes Clear, DateTime? LastWriteTime, bool Recursive);

/// <summary>Files &gt; Change attributes: read-only, hidden, system, archive and the modification time.</summary>
public sealed class ChangeAttributesJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _entries;
    private readonly AttributeChange _change;

    public ChangeAttributesJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> entries, AttributeChange change)
        : base("Change attributes")
    {
        _registry = registry;
        _entries = entries;
        _change = change;
    }

    public int Changed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var all = new List<VfsEntry>();
        foreach (var entry in _entries)
        {
            all.Add(entry);
            if (_change.Recursive && entry.IsDirectory && !entry.IsLink)
            {
                await CollectAsync(entry.Path, all, ct).ConfigureAwait(false);
            }
        }
        Progress.SetTotals(0, all.Count);
        foreach (var entry in all)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(entry.Path.ToDisplayString(), 0);
            var provider = _registry.Resolve(entry.Path);
            var ok = await WithErrorHandlingAsync(entry.Path.ToDisplayString(), async () =>
            {
                var attributes = (entry.Attributes | _change.Set) & ~_change.Clear;
                if (attributes != entry.Attributes)
                {
                    if (provider is not IAttributeSupport support)
                    {
                        throw new NotSupportedException("Attributes cannot be changed at this location.");
                    }
                    await support.SetAttributesAsync(entry.Path, attributes, entry.UnixMode, ct).ConfigureAwait(false);
                }
                if (_change.LastWriteTime is { } time)
                {
                    // A read-only file must be writable for the time change on Windows.
                    var readOnly = attributes.HasFlag(EntryAttributes.ReadOnly) && provider is IAttributeSupport s && OperatingSystem.IsWindows();
                    if (readOnly)
                    {
                        await ((IAttributeSupport)provider).SetAttributesAsync(entry.Path, attributes & ~EntryAttributes.ReadOnly, entry.UnixMode, ct).ConfigureAwait(false);
                    }
                    await provider.SetTimesAsync(entry.Path, null, time, ct).ConfigureAwait(false);
                    if (readOnly)
                    {
                        await ((IAttributeSupport)provider).SetAttributesAsync(entry.Path, attributes, entry.UnixMode, ct).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);
            if (ok)
            {
                Changed++;
                Progress.CompleteFile();
            }
        }
    }

    private async Task CollectAsync(VfsPath dir, List<VfsEntry> all, CancellationToken ct)
    {
        IReadOnlyList<VfsEntry> children = [];
        await WithErrorHandlingAsync(dir.ToDisplayString(), async () =>
            children = await _registry.Resolve(dir).ListAsync(dir, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            all.Add(child);
            if (child.IsDirectory && !child.IsLink)
            {
                await CollectAsync(child.Path, all, ct).ConfigureAwait(false);
            }
        }
    }
}
