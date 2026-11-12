using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Rename;

/// <summary>
/// Applies a multi-rename plan. Names that another file of the plan still uses (swaps, cycles) go through a temporary
/// name first. A '/' in a new name moves the file into that subfolder (created when missing). <see cref="Done"/> is the
/// undo information.
/// </summary>
public sealed class MultiRenameJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<(VfsPath From, VfsPath To)> _moves;

    public MultiRenameJob(ProviderRegistry registry, IEnumerable<RenamePlanItem> plan)
        : this(registry, plan.Where(p => p.Changes && p.Issue == RenameIssue.None)
            .Select(p => (p.Source.Path, Target(p.Source.Path, p.NewName))).ToList(), "Multi-Rename Tool")
    {
    }

    private MultiRenameJob(ProviderRegistry registry, IReadOnlyList<(VfsPath From, VfsPath To)> moves, string title)
        : base(title)
    {
        _registry = registry;
        _moves = moves;
    }

    /// <summary>Renames that succeeded (from → to), in order.</summary>
    public List<(VfsPath From, VfsPath To)> Done { get; } = [];

    /// <summary>A job that reverses <see cref="Done"/>.</summary>
    public MultiRenameJob CreateUndo() =>
        new(_registry, Done.AsEnumerable().Reverse().Select(d => (d.To, d.From)).ToList(), "Undo rename");

    private static VfsPath Target(VfsPath source, string newName)
    {
        var dir = source.Parent ?? throw new ArgumentException("The root cannot be renamed.", nameof(source));
        foreach (var part in newName.Split('/', '\\'))
        {
            dir = dir.Combine(part);
        }
        return dir;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Progress.SetTotals(0, _moves.Count);
        var sources = new HashSet<VfsPath>(_moves.Select(m => m.From));
        var staged = new List<(VfsPath Temp, VfsPath From, VfsPath To)>();

        // Pass 1: direct renames; targets still held by another source are staged under a temporary name.
        foreach (var (from, to) in _moves)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(from.ToDisplayString(), 0);
            var caseOnly = from != to && string.Equals(from.ToString(), to.ToString(), StringComparison.OrdinalIgnoreCase);
            if (sources.Contains(to) && !caseOnly)
            {
                var temp = from.Parent!.Combine(from.Name + ".~mr" + Guid.NewGuid().ToString("N")[..6]);
                if (await RenameAsync(from, temp, ct).ConfigureAwait(false))
                {
                    staged.Add((temp, from, to));
                }
                continue;
            }
            if (await RenameAsync(from, to, ct).ConfigureAwait(false))
            {
                Done.Add((from, to));
                Progress.CompleteFile();
            }
        }
        // Pass 2: staged files to their final names.
        foreach (var (temp, from, to) in staged)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (await RenameAsync(temp, to, ct).ConfigureAwait(false))
            {
                Done.Add((from, to));
                Progress.CompleteFile();
            }
            else
            {
                await RenameAsync(temp, from, ct).ConfigureAwait(false); // put it back
            }
        }
    }

    private Task<bool> RenameAsync(VfsPath from, VfsPath to, CancellationToken ct) =>
        WithErrorHandlingAsync(from.ToDisplayString(), async () =>
        {
            var provider = _registry.Resolve(from);
            if (to.Parent is { } parent && parent != from.Parent && await provider.GetEntryAsync(parent, ct).ConfigureAwait(false) is null)
            {
                await provider.CreateDirectoryAsync(parent, ct).ConfigureAwait(false);
            }
            await provider.MoveAsync(from, to, overwrite: false, ct).ConfigureAwait(false);
        }, ct);
}
