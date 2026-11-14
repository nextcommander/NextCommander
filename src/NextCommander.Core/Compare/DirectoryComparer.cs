using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Compare;

public enum SyncStatus
{
    Equal,
    LeftNewer,
    RightNewer,
    /// <summary>Same time, different size or content.</summary>
    Different,
    LeftOnly,
    RightOnly,
    /// <summary>A file on one side, a folder with the same name on the other.</summary>
    TypeMismatch,
}

public enum SyncAction
{
    None,
    CopyToRight,
    CopyToLeft,
    DeleteLeft,
    DeleteRight,
}

/// <summary>Synchronize dirs options. Serializable (presets).</summary>
public sealed class SyncOptions
{
    public bool Subdirectories { get; set; } = true;

    /// <summary>Compare file contents of files with equal size (slow, exact).</summary>
    public bool ByContent { get; set; }

    /// <summary>Ignore the date: only size (and content) decide.</summary>
    public bool IgnoreDate { get; set; }

    /// <summary>Allowed time difference (FAT has 2 s resolution).</summary>
    public int ToleranceSeconds { get; set; } = 2;

    /// <summary>Treat exactly 1 hour difference as equal (daylight saving on FAT / network drives).</summary>
    public bool IgnoreDstHour { get; set; } = true;

    /// <summary>Only copy left → right (right-only files are listed but get no action).</summary>
    public bool Asymmetric { get; set; }

    /// <summary>File mask applied to files ("*.cs | obj\" excludes folders too).</summary>
    public string Mask { get; set; } = "*";

    public bool SkipHidden { get; set; }
}

/// <summary>One relative path compared on both sides.</summary>
public sealed class SyncItem
{
    public required string RelativePath { get; init; }

    public VfsEntry? Left { get; init; }

    public VfsEntry? Right { get; init; }

    public SyncStatus Status { get; set; }

    public SyncAction Action { get; set; }

    public bool IsDirectory => (Left ?? Right)!.IsDirectory;
}

/// <summary>Compares two folder trees (Synchronize dirs).</summary>
public sealed class DirectoryComparer(ProviderRegistry registry, SyncOptions options)
{
    private readonly WildcardMatcher _mask = WildcardMatcher.Parse(options.Mask);

    public async Task<IReadOnlyList<SyncItem>> CompareAsync(VfsPath left, VfsPath right, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new List<SyncItem>();
        await CompareDirectoryAsync(left, right, string.Empty, result, progress, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task CompareDirectoryAsync(VfsPath? left, VfsPath? right, string relative, List<SyncItem> result,
        IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(relative.Length == 0 ? "/" : relative);
        var leftEntries = left is null ? [] : await ListAsync(left, ct).ConfigureAwait(false);
        var rightEntries = right is null ? [] : await ListAsync(right, ct).ConfigureAwait(false);
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var rightByName = rightEntries.ToDictionary(e => e.Name, comparer);
        var names = leftEntries.Select(e => e.Name).Concat(rightEntries.Select(e => e.Name)).Distinct(comparer)
            .Order(StringComparer.OrdinalIgnoreCase);
        var leftByName = leftEntries.ToDictionary(e => e.Name, comparer);

        foreach (var name in names)
        {
            leftByName.TryGetValue(name, out var l);
            rightByName.TryGetValue(name, out var r);
            var path = relative.Length == 0 ? name : relative + "/" + name;
            var isDirectory = (l ?? r)!.IsDirectory;
            if (l is not null && r is not null && l.IsDirectory != r.IsDirectory)
            {
                result.Add(new SyncItem { RelativePath = path, Left = l, Right = r, Status = SyncStatus.TypeMismatch });
                continue;
            }
            if (isDirectory)
            {
                if (!options.Subdirectories || _mask.IsDirectoryExcluded(name))
                {
                    continue;
                }
                var count = result.Count;
                await CompareDirectoryAsync(l?.Path, r?.Path, path, result, progress, ct).ConfigureAwait(false);
                if (l is null || r is null)
                {
                    // One-sided folder: list it too (copying it copies the empty ones as well).
                    var item = new SyncItem { RelativePath = path, Left = l, Right = r, Status = l is null ? SyncStatus.RightOnly : SyncStatus.LeftOnly };
                    item.Action = DefaultAction(item.Status);
                    result.Insert(count, item);
                }
                continue;
            }
            if (!_mask.MatchesFile(name))
            {
                continue;
            }
            var status = l is null ? SyncStatus.RightOnly : r is null ? SyncStatus.LeftOnly : await CompareFilesAsync(l, r, ct).ConfigureAwait(false);
            result.Add(new SyncItem { RelativePath = path, Left = l, Right = r, Status = status, Action = DefaultAction(status) });
        }
    }

    private async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath dir, CancellationToken ct)
    {
        var list = await registry.Resolve(dir).ListAsync(dir, ct).ConfigureAwait(false);
        return options.SkipHidden ? list.Where(e => !e.IsHidden).ToList() : list;
    }

    private SyncAction DefaultAction(SyncStatus status) => status switch
    {
        SyncStatus.LeftNewer or SyncStatus.LeftOnly => SyncAction.CopyToRight,
        SyncStatus.RightNewer or SyncStatus.RightOnly when !options.Asymmetric => SyncAction.CopyToLeft,
        _ => SyncAction.None,
    };

    private async Task<SyncStatus> CompareFilesAsync(VfsEntry left, VfsEntry right, CancellationToken ct)
    {
        var delta = (left.LastWriteTime - right.LastWriteTime).TotalSeconds;
        var sameTime = Math.Abs(delta) <= options.ToleranceSeconds
            || (options.IgnoreDstHour && Math.Abs(Math.Abs(delta) - 3600) <= options.ToleranceSeconds);
        if (left.Size == right.Size && (options.IgnoreDate || sameTime))
        {
            if (!options.ByContent || await SameContentAsync(left, right, ct).ConfigureAwait(false))
            {
                return SyncStatus.Equal;
            }
            return SyncStatus.Different;
        }
        if (options.IgnoreDate || sameTime)
        {
            return SyncStatus.Different;
        }
        return delta > 0 ? SyncStatus.LeftNewer : SyncStatus.RightNewer;
    }

    /// <summary>Byte comparison of two files (any providers).</summary>
    public async Task<bool> SameContentAsync(VfsEntry left, VfsEntry right, CancellationToken ct)
    {
        if (left.Size != right.Size)
        {
            return false;
        }
        await using var a = await registry.Resolve(left.Path).OpenReadAsync(left.Path, ct).ConfigureAwait(false);
        await using var b = await registry.Resolve(right.Path).OpenReadAsync(right.Path, ct).ConfigureAwait(false);
        var bufferA = new byte[128 * 1024];
        var bufferB = new byte[128 * 1024];
        while (true)
        {
            var n = await a.ReadAtLeastAsync(bufferA, bufferA.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            var m = await b.ReadAtLeastAsync(bufferB, bufferB.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (n != m || !bufferA.AsSpan(0, n).SequenceEqual(bufferB.AsSpan(0, m)))
            {
                return false;
            }
            if (n == 0)
            {
                return true;
            }
        }
    }
}

/// <summary>Carries out the actions of a comparison (copy left ↔ right, delete).</summary>
public sealed class SyncJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<SyncItem> _items;
    private readonly VfsPath _leftRoot;
    private readonly VfsPath _rightRoot;

    public SyncJob(ProviderRegistry registry, IReadOnlyList<SyncItem> items, VfsPath leftRoot, VfsPath rightRoot)
        : base("Synchronize directories")
    {
        _registry = registry;
        _items = items;
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
    }

    /// <summary>Deleted local files and folders go here (the Recycle Bin / trash); null: they are deleted for good.</summary>
    public ITrashService? Trash { get; init; }

    private VfsPath Combine(VfsPath root, string relative)
    {
        foreach (var part in relative.Split('/'))
        {
            root = root.Combine(part);
        }
        return root;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var copies = _items.Where(i => i.Action is SyncAction.CopyToLeft or SyncAction.CopyToRight).ToList();
        Progress.SetTotals(copies.Where(i => !i.IsDirectory).Sum(i => (i.Action == SyncAction.CopyToRight ? i.Left : i.Right)!.Size),
            copies.Count(i => !i.IsDirectory));
        foreach (var item in _items.Where(i => i.Action != SyncAction.None))
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            switch (item.Action)
            {
                case SyncAction.CopyToRight or SyncAction.CopyToLeft:
                    await CopyAsync(item, item.Action == SyncAction.CopyToRight, ct).ConfigureAwait(false);
                    break;
                case SyncAction.DeleteLeft or SyncAction.DeleteRight:
                    var victim = item.Action == SyncAction.DeleteLeft ? item.Left : item.Right;
                    if (victim is not null)
                    {
                        await WithErrorHandlingAsync(victim.Path.ToDisplayString(),
                            () => Trash is { IsAvailable: true } trash && victim.Path.IsLocal
                                ? trash.MoveToTrashAsync(victim.Path.Path, ct)
                                : _registry.Resolve(victim.Path).DeleteAsync(victim.Path, recursive: true, ct), ct).ConfigureAwait(false);
                    }
                    break;
            }
        }
    }

    private async Task CopyAsync(SyncItem item, bool toRight, CancellationToken ct)
    {
        var source = toRight ? item.Left : item.Right;
        if (source is null)
        {
            return;
        }
        var target = Combine(toRight ? _rightRoot : _leftRoot, item.RelativePath);
        var provider = _registry.Resolve(target);
        if (source.IsDirectory)
        {
            await WithErrorHandlingAsync(target.ToDisplayString(), () => provider.CreateDirectoryAsync(target, ct), ct).ConfigureAwait(false);
            return;
        }
        Progress.BeginItem(source.Path.ToDisplayString(), source.Size);
        var ok = await WithErrorHandlingAsync(source.Path.ToDisplayString(), async () =>
        {
            if (target.Parent is { } parent && await provider.GetEntryAsync(parent, ct).ConfigureAwait(false) is null)
            {
                await provider.CreateDirectoryAsync(parent, ct).ConfigureAwait(false);
            }
            await using (var input = await _registry.Resolve(source.Path).OpenReadAsync(source.Path, ct).ConfigureAwait(false))
            await using (var output = await provider.OpenWriteAsync(target, overwrite: true, ct).ConfigureAwait(false))
            {
                var buffer = new byte[1024 * 1024];
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    Progress.AddBytes(n);
                    await CheckpointAsync(ct).ConfigureAwait(false);
                }
            }
            if (provider.Capabilities.HasFlag(ProviderCapabilities.SetTimes) && source.LastWriteTime.Year >= 1980)
            {
                await provider.SetTimesAsync(target, null, source.LastWriteTime, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        if (ok)
        {
            Progress.CompleteFile();
        }
        else
        {
            Progress.SkipFile(source.Size);
        }
    }
}
