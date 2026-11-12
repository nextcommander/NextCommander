using NextCommander.Core.Matching;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

public sealed record CopyOptions
{
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    /// <summary>Re-read source and target after copying and compare hashes.</summary>
    public bool Verify { get; init; }

    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>Copy read-only/hidden/system attributes (Windows) and permission bits (Unix).</summary>
    public bool PreserveAttributes { get; init; } = true;

    /// <summary>Recreate symbolic links to files as links instead of copying the target's content.
    /// Directory links are always recreated as links (never followed).</summary>
    public bool CopyFileLinksAsLinks { get; init; }

    /// <summary>Only files/subdirectories matching this mask are copied.</summary>
    public WildcardMatcher? Filter { get; init; }

    /// <summary>Name mask applied to every file, e.g. "*.bak".</summary>
    public string? TargetNameMask { get; init; }

    /// <summary>Explicit new name when exactly one item is copied (F5 with a file name as target).</summary>
    public string? SingleTargetName { get; init; }

    public int BufferSize { get; init; } = 1024 * 1024;

    /// <summary>Files from this size are written straight to a local disk (write-through, no system cache); 0 = never.</summary>
    public long BigFileThreshold { get; init; }

    /// <summary>Buffer of the big file mode.</summary>
    public int BigFileBufferSize { get; init; } = 10 * 1024 * 1024;

    /// <summary>Paths (drive roots, folders) read and written in small blocks (compatibility mode for troublesome drives).</summary>
    public IReadOnlyList<string> CompatibilityPaths { get; init; } = [];

    /// <summary>A file unpacked with a wrong check sum (damaged archive) is kept instead of deleted.</summary>
    public bool KeepDamagedFiles { get; init; }

    /// <summary>Ask before overwriting read-only targets even when the policy overwrites.</summary>
    public bool ConfirmReadOnlyOverwrite { get; init; }

    /// <summary>Ask before overwriting hidden / system targets even when the policy overwrites.</summary>
    public bool ConfirmHiddenOverwrite { get; init; }

    /// <summary>Files extracted from an archive that came from the internet inherit its mark (Zone.Identifier / quarantine).</summary>
    public bool PropagateMarkOfTheWeb { get; init; } = true;

    /// <summary>Windows: the access rules (NTFS permissions) of local files and folders are copied too (the copy dialog's check box).</summary>
    public bool CopyPermissions { get; init; }

    /// <summary>A source file that cannot be opened for reading is skipped (listed as an error) without asking.</summary>
    public bool SkipUnreadable { get; init; }

    /// <summary>Initial speed limit in bytes per second (0: unlimited); can be changed later via <see cref="FileJob.SpeedLimit"/>.</summary>
    public long SpeedLimit { get; init; }

    /// <summary>Keep paths relative to this directory (copying from branch view / search results):
    /// <c>base/a/b.txt</c> goes to <c>target/a/b.txt</c> instead of <c>target/b.txt</c>.</summary>
    public VfsPath? RelativeBase { get; init; }
}

/// <summary>Copies or moves entries between any two providers.</summary>
public sealed class CopyMoveJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _sources;
    private readonly VfsPath _targetDirectory;
    private readonly bool _move;
    private readonly CopyOptions _options;
    private ConflictPolicy _policy;

    public CopyMoveJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> sources, VfsPath targetDirectory, bool move, CopyOptions? options = null)
        : base(move ? "Move" : "Copy")
    {
        _registry = registry;
        _sources = sources;
        _targetDirectory = targetDirectory;
        _move = move;
        _options = options ?? new CopyOptions();
        _policy = _options.ConflictPolicy;
        SpeedLimit.BytesPerSecond = _options.SpeedLimit;
    }

    public bool IsMove => _move;

    public VfsPath TargetDirectory => _targetDirectory;

    /// <summary>The entries copied / moved.</summary>
    public IReadOnlyList<VfsEntry> Sources => _sources;

    private sealed record PlanItem(VfsEntry Source, VfsPath Destination);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var target = _registry.Resolve(_targetDirectory);
        var plan = new List<PlanItem>();
        var movedDirectories = new List<VfsEntry>();

        foreach (var source in _sources)
        {
            if (source.IsDirectory && !source.IsLink && _targetDirectory.IsSameOrUnder(source.Path))
            {
                throw new InvalidOperationException($"Cannot copy '{source.Name}' into itself.");
            }
        }

        // Phase 1: fast whole-directory renames (move inside one provider), then build the plan.
        foreach (var source in _sources)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            var destination = _targetDirectory.Combine(MapTopLevelName(source));
            var sourceProvider = _registry.Resolve(source.Path);
            if (_options.RelativeBase is not null && destination.Parent is { } destinationParent && destinationParent != _targetDirectory)
            {
                await WithErrorHandlingAsync(destinationParent.ToDisplayString(),
                    () => target.CreateDirectoryAsync(destinationParent, ct), ct).ConfigureAwait(false);
            }

            // Copy into the same folder with "auto rename": duplicate as "name (2).ext" (Explorer-style paste).
            if (destination == source.Path && !_move && _policy == ConflictPolicy.AutoRenameSource)
            {
                var parentDir = destination.Parent ?? _targetDirectory;
                destination = parentDir.Combine(await ConflictNaming.NextFreeNameAsync(source.Name, source.IsDirectory,
                    async n => await target.GetEntryAsync(parentDir.Combine(n), ct).ConfigureAwait(false) is not null).ConfigureAwait(false));
            }

            // Case-only rename ("a.txt" -> "A.txt") on a case-insensitive file system.
            if (destination == source.Path)
            {
                if (_move && !string.Equals(destination.Name, source.Name, StringComparison.Ordinal))
                {
                    await WithErrorHandlingAsync(source.Name,
                        () => sourceProvider.MoveAsync(source.Path, destination, overwrite: false, ct), ct).ConfigureAwait(false);
                }
                else if (!_move)
                {
                    AddError(source.Name, "Cannot copy a file onto itself.");
                }
                Progress.AddCounted(0, 1);
                Progress.CompleteFile();
                continue;
            }

            if (_move && source.IsDirectory && CanRename(sourceProvider, target)
                && await target.GetEntryAsync(destination, ct).ConfigureAwait(false) is null)
            {
                try
                {
                    await sourceProvider.MoveAsync(source.Path, destination, overwrite: false, ct).ConfigureAwait(false);
                    Progress.AddCounted(0, 1);
                    Progress.CompleteFile();
                    continue;
                }
                catch (CrossDeviceMoveException)
                {
                    // fall back to copy + delete
                }
            }

            await CollectAsync(source, destination, plan, depth: 0, ct).ConfigureAwait(false);
        }
        Progress.SetTotals(plan.Where(p => !p.Source.IsDirectory).Sum(p => p.Source.Size), plan.Count(p => !p.Source.IsDirectory));

        // Phase 2: execute.
        foreach (var item in plan)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (StopAfterCurrentFile)
            {
                throw new OperationCanceledException("Stopped after current file.");
            }

            if (item.Source.IsDirectory)
            {
                await CreateDirectoryAsync(target, item, ct).ConfigureAwait(false);
                if (_move)
                {
                    movedDirectories.Add(item.Source);
                }
            }
            else
            {
                await CopyFileAsync(target, item, ct).ConfigureAwait(false);
            }
        }

        // Phase 3: remove now-empty source directories (deepest first).
        if (_move)
        {
            foreach (var dir in movedDirectories.AsEnumerable().Reverse())
            {
                var provider = _registry.Resolve(dir.Path);
                try
                {
                    await provider.DeleteAsync(dir.Path, recursive: false, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Not empty: something was skipped; keep it.
                }
            }
        }
    }

    private string MapTopLevelName(VfsEntry source)
    {
        if (_sources.Count == 1 && !string.IsNullOrEmpty(_options.SingleTargetName))
        {
            return _options.SingleTargetName;
        }
        if (_options.RelativeBase is { } baseDir && source.Path.Parent is { } parent
            && parent.GetRelativeTo(baseDir) is { Length: > 0 } relativeDir)
        {
            return relativeDir + "/" + MapName(source);
        }
        return MapName(source);
    }

    private string MapName(VfsEntry source) =>
        !source.IsDirectory && RenameMask.IsMask(_options.TargetNameMask)
            ? RenameMask.Apply(source.Name, _options.TargetNameMask!)
            : source.Name;

    private static bool CanRename(IFileSystemProvider source, IFileSystemProvider target) =>
        ReferenceEquals(source, target) && source.Capabilities.HasFlag(ProviderCapabilities.Rename);

    private async Task CollectAsync(VfsEntry source, VfsPath destination, List<PlanItem> plan, int depth, CancellationToken ct)
    {
        await CheckpointAsync(ct).ConfigureAwait(false);
        var filter = _options.Filter;
        if (!source.IsDirectory)
        {
            if (filter is null || filter.MatchesFile(source.Name))
            {
                plan.Add(new PlanItem(source, destination));
                Progress.AddCounted(source.Size, 1);
            }
            return;
        }

        if (depth > 0 && filter is not null && !filter.MatchesDirectory(source.Name))
        {
            return;
        }
        plan.Add(new PlanItem(source, destination));
        if (source.IsLink)
        {
            return; // copied as a link, never followed (avoids loops)
        }

        IReadOnlyList<VfsEntry> children = [];
        var provider = _registry.Resolve(source.Path);
        await WithErrorHandlingAsync(source.Path.ToDisplayString(), async () =>
            children = await provider.ListAsync(source.Path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        foreach (var child in children)
        {
            var childName = child.IsDirectory ? child.Name : MapName(child);
            await CollectAsync(child, destination.Combine(childName), plan, depth + 1, ct).ConfigureAwait(false);
        }
    }

    private async Task CreateDirectoryAsync(IFileSystemProvider target, PlanItem item, CancellationToken ct)
    {
        var display = item.Destination.ToDisplayString();
        if (item.Source.IsLink && item.Source.LinkTarget is not null && target is ILinkSupport links)
        {
            await WithErrorHandlingAsync(display, async () =>
            {
                if (await target.GetEntryAsync(item.Destination, ct).ConfigureAwait(false) is null)
                {
                    await links.CreateSymbolicLinkAsync(item.Destination, item.Source.LinkTarget, isDirectory: true, ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
            return;
        }

        await WithErrorHandlingAsync(display, async () =>
        {
            await target.CreateDirectoryAsync(item.Destination, ct).ConfigureAwait(false);
            if (_options.PreserveTimestamps)
            {
                await TrySetTimesAsync(target, item.Destination, item.Source, ct).ConfigureAwait(false);
            }
            if (_options.PreserveAttributes && item.Source.UnixMode is not null)
            {
                await TrySetAttributesAsync(target, item.Destination, item.Source, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        CopyPermissionsOf(item.Source.Path, item.Destination, directory: true);
    }

    /// <summary>The NTFS access rules of a local source onto its local copy (Copy NTFS permissions); a failure is listed, the copy stays.</summary>
    private void CopyPermissionsOf(VfsPath source, VfsPath destination, bool directory)
    {
        if (!_options.CopyPermissions || !OperatingSystem.IsWindows() || !source.IsLocal || !destination.IsLocal)
        {
            return;
        }
        try
        {
            // only the sections marked as changed are written: the rules go over as text into a new object
            const System.Security.AccessControl.AccessControlSections access = System.Security.AccessControl.AccessControlSections.Access;
            if (directory)
            {
                var rules = new System.Security.AccessControl.DirectorySecurity();
                rules.SetSecurityDescriptorSddlForm(new DirectoryInfo(source.Path).GetAccessControl(access).GetSecurityDescriptorSddlForm(access), access);
                new DirectoryInfo(destination.Path).SetAccessControl(rules);
            }
            else
            {
                var rules = new System.Security.AccessControl.FileSecurity();
                rules.SetSecurityDescriptorSddlForm(new FileInfo(source.Path).GetAccessControl(access).GetSecurityDescriptorSddlForm(access), access);
                new FileInfo(destination.Path).SetAccessControl(rules);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Security.AccessControl.PrivilegeNotHeldException or PlatformNotSupportedException)
        {
            AddError(destination.ToDisplayString(), "Could not copy the permissions: " + ex.Message);
        }
    }

    private enum Resolution
    {
        Overwrite,
        Skip,
        AutoRename,
        RenameTo,
        RenameTarget,
    }

    private bool _protectedOverwriteAll;

    private async Task<(Resolution Resolution, string? NewName)> ResolveConflictAsync(VfsEntry source, VfsEntry existing, CancellationToken ct)
    {
        var targetOlder = existing.LastWriteTime < source.LastWriteTime;
        // read-only / hidden / system targets: asked even when the policy would overwrite (until "all" was answered once)
        var wouldOverwrite = _policy switch
        {
            ConflictPolicy.Overwrite => true,
            ConflictPolicy.OverwriteOlder => targetOlder,
            ConflictPolicy.OverwriteSmaller => source.Size > existing.Size,
            ConflictPolicy.OverwriteLarger => source.Size < existing.Size,
            _ => false,
        };
        var isProtected = !_protectedOverwriteAll && wouldOverwrite
            && ((_options.ConfirmReadOnlyOverwrite && (existing.Attributes & EntryAttributes.ReadOnly) != 0)
                || (_options.ConfirmHiddenOverwrite && (existing.IsHidden || existing.IsSystem)));
        switch (isProtected ? ConflictPolicy.Ask : _policy)
        {
            case ConflictPolicy.Overwrite:
                return (Resolution.Overwrite, null);
            case ConflictPolicy.Skip:
                return (Resolution.Skip, null);
            case ConflictPolicy.OverwriteOlder:
                return (targetOlder ? Resolution.Overwrite : Resolution.Skip, null);
            case ConflictPolicy.AutoRenameSource:
                return (Resolution.AutoRename, null);
            case ConflictPolicy.RenameOlderTarget:
                return (targetOlder ? Resolution.RenameTarget : Resolution.Skip, null);
            case ConflictPolicy.AutoRenameTarget:
                return (Resolution.RenameTarget, null);
            case ConflictPolicy.OverwriteSmaller:
            case ConflictPolicy.OverwriteLarger:
                return (wouldOverwrite ? Resolution.Overwrite : Resolution.Skip, null);
        }

        Progress.StopClock();
        ConflictResponse response;
        try
        {
            response = await Interaction.ResolveConflictAsync(new ConflictRequest(source, existing), ct).ConfigureAwait(false);
        }
        finally
        {
            Progress.ResumeClock();
        }

        if (isProtected && response.ApplyToAll)
        {
            _protectedOverwriteAll = true;
        }
        if (response.ApplyToAll)
        {
            _policy = response.Action switch
            {
                ConflictAction.Overwrite => ConflictPolicy.Overwrite,
                ConflictAction.Skip => ConflictPolicy.Skip,
                ConflictAction.OverwriteOlder => ConflictPolicy.OverwriteOlder,
                ConflictAction.AutoRename => ConflictPolicy.AutoRenameSource,
                _ => _policy,
            };
        }

        return response.Action switch
        {
            ConflictAction.Overwrite => (Resolution.Overwrite, null),
            ConflictAction.Skip => (Resolution.Skip, null),
            ConflictAction.OverwriteOlder => (targetOlder ? Resolution.Overwrite : Resolution.Skip, null),
            ConflictAction.AutoRename => (Resolution.AutoRename, null),
            ConflictAction.RenameTo when !string.IsNullOrWhiteSpace(response.NewName) => (Resolution.RenameTo, response.NewName),
            _ => throw new OperationCanceledException("Cancelled by user."),
        };
    }

    private async Task CopyFileAsync(IFileSystemProvider target, PlanItem item, CancellationToken ct)
    {
        var source = item.Source;
        var destination = item.Destination;
        var sourceProvider = _registry.Resolve(source.Path);
        var overwrite = false;

        // Resolve name conflicts (loop because "rename to" may hit another existing file).
        for (var guard = 0; guard < 100; guard++)
        {
            var existing = await target.GetEntryAsync(destination, ct).ConfigureAwait(false);
            if (existing is null)
            {
                break;
            }
            if (existing.Path == source.Path)
            {
                if (_move)
                {
                    Progress.SkipFile(source.Size);
                    return; // moving onto itself is a no-op
                }
                AddError(source.Name, "Cannot copy a file onto itself.");
                Progress.SkipFile(source.Size);
                return;
            }
            if (existing.IsDirectory)
            {
                AddError(source.Name, "A directory with the same name exists in the target.");
                Progress.SkipFile(source.Size);
                return;
            }

            var (resolution, newName) = await ResolveConflictAsync(source, existing, ct).ConfigureAwait(false);
            var parent = destination.Parent ?? _targetDirectory;
            switch (resolution)
            {
                case Resolution.Skip:
                    Progress.SkipFile(source.Size);
                    AddSkipped(source.Path.ToDisplayString());
                    return;
                case Resolution.Overwrite:
                    overwrite = true;
                    break;
                case Resolution.AutoRename:
                    destination = parent.Combine(await ConflictNaming.NextFreeNameAsync(destination.Name, false,
                        async n => await target.GetEntryAsync(parent.Combine(n), ct).ConfigureAwait(false) is not null).ConfigureAwait(false));
                    continue;
                case Resolution.RenameTo:
                    destination = parent.Combine(newName!);
                    continue;
                case Resolution.RenameTarget:
                    var renamed = parent.Combine(await ConflictNaming.NextFreeNameAsync(destination.Name, false,
                        async n => await target.GetEntryAsync(parent.Combine(n), ct).ConfigureAwait(false) is not null).ConfigureAwait(false));
                    await target.MoveAsync(destination, renamed, overwrite: false, ct).ConfigureAwait(false);
                    continue;
            }
            break;
        }

        // File symlink recreated as a link.
        if (_options.CopyFileLinksAsLinks && source.IsLink && source.LinkTarget is not null && target is ILinkSupport fileLinks
            && !(_move && CanRename(sourceProvider, target)))
        {
            var linked = await WithErrorHandlingAsync(destination.ToDisplayString(), async () =>
            {
                if (overwrite)
                {
                    await target.DeleteAsync(destination, recursive: false, ct).ConfigureAwait(false);
                }
                await fileLinks.CreateSymbolicLinkAsync(destination, source.LinkTarget, isDirectory: false, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (linked && _move)
            {
                await WithErrorHandlingAsync(source.Path.ToDisplayString(),
                    () => sourceProvider.DeleteAsync(source.Path, recursive: false, ct), ct).ConfigureAwait(false);
            }
            Progress.SkipFile(source.Size);
            return;
        }

        // Same provider move: rename instead of copying data.
        if (_move && CanRename(sourceProvider, target))
        {
            try
            {
                _ = await WithErrorHandlingAsync(source.Name, () =>
                    sourceProvider.MoveAsync(source.Path, destination, overwrite, ct), ct).ConfigureAwait(false);
                Progress.SkipFile(source.Size);
                return;
            }
            catch (CrossDeviceMoveException)
            {
                // copy + delete below
            }
        }

        Progress.BeginItem(source.Name, source.Size);
        long attemptBytes = 0;
        byte[]? sourceDigest = null;
        string? unreadable = null;
        var copied = await WithErrorHandlingAsync(source.Path.ToDisplayString(), async () =>
        {
            Progress.AddBytes(-attemptBytes);
            attemptBytes = 0;
            var outputCreated = false;
            Stream opened;
            try
            {
                opened = await sourceProvider.OpenReadAsync(source.Path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (_options.SkipUnreadable && ex is IOException or UnauthorizedAccessException)
            {
                unreadable = ex.Message; // skipped without asking (Skip all which cannot be opened for reading)
                return;
            }
            try
            {
                await using var input = opened;
                if (_options.PreserveTimestamps && target is IUploadTimeSupport uploadTime && source.LastWriteTime.Year >= 1980)
                {
                    uploadTime.PrepareUpload(destination, source.LastWriteTime);
                }
                var compatible = IsCompatibilityPath(source.Path) || IsCompatibilityPath(destination);
                var big = !compatible && _options.BigFileThreshold > 0 && source.Size >= _options.BigFileThreshold && destination.IsLocal;
                await using var output = big
                    ? new FileStream(destination.Path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                        FileOptions.WriteThrough | FileOptions.SequentialScan)
                    : await target.OpenWriteAsync(destination, overwrite, ct).ConfigureAwait(false);
                outputCreated = true;
                var buffer = new byte[compatible ? CompatibilityBufferSize : big ? Math.Max(64 * 1024, _options.BigFileBufferSize) : Math.Max(4096, _options.BufferSize)];
                // Verification hashes the source while copying (BLAKE3), so only the target is read again.
                using var hasher = _options.Verify ? new Tools.Checksums.Blake3Hasher() : null;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher?.Append(buffer.AsSpan(0, read));
                    attemptBytes += read;
                    Progress.AddBytes(read);
                    await ThrottleAsync(read, ct).ConfigureAwait(false);
                }
                sourceDigest = hasher?.Digest();
            }
            catch (Exception ex) when (outputCreated)
            {
                // Only remove a partial file we created/truncated ourselves (a damaged one stays when wanted).
                if (!(_options.KeepDamagedFiles && ex is InvalidDataException))
                {
                    await TryDeleteAsync(target, destination).ConfigureAwait(false);
                }
                throw;
            }
        }, ct).ConfigureAwait(false);

        if (!copied || unreadable is not null)
        {
            if (unreadable is not null)
            {
                AddError(source.Path.ToDisplayString(), unreadable);
            }
            Progress.AddBytes(-attemptBytes);
            Progress.SkipFile(source.Size);
            return;
        }

        if (_options.PropagateMarkOfTheWeb && destination.IsLocal && source.Path.Container is not null && source.Path.LocalAnchor is { IsLocal: true } archive)
        {
            Tools.MarkOfTheWeb.Propagate(archive.Path, destination.Path);
        }
        if (_options.PreserveTimestamps)
        {
            await TrySetTimesAsync(target, destination, source, ct).ConfigureAwait(false);
        }
        if (_options.PreserveAttributes)
        {
            await TrySetAttributesAsync(target, destination, source, ct).ConfigureAwait(false);
        }
        CopyPermissionsOf(source.Path, destination, directory: false);

        if (_options.Verify && !await VerifyAsync(sourceDigest, target, destination, ct).ConfigureAwait(false))
        {
            AddError(source.Name, "Verification failed: target differs from source.");
            Progress.CompleteFile();
            return; // never delete the source of a failed move
        }

        if (_move)
        {
            await WithErrorHandlingAsync(source.Path.ToDisplayString(),
                () => sourceProvider.DeleteAsync(source.Path, recursive: false, ct), ct).ConfigureAwait(false);
        }
        Progress.CompleteFile();
    }

    private static async Task TrySetTimesAsync(IFileSystemProvider provider, VfsPath path, VfsEntry source, CancellationToken ct)
    {
        if (!provider.Capabilities.HasFlag(ProviderCapabilities.SetTimes) || source.LastWriteTime.Year < 1980)
        {
            return; // unknown time (e.g. folders implied by archive paths)
        }
        var created = source.CreationTime is { Year: >= 1980 } c ? c : (DateTime?)null;
        try
        {
            await provider.SetTimesAsync(path, created, source.LastWriteTime, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentOutOfRangeException)
        {
            // timestamps are best effort
        }
    }

    private static async Task TrySetAttributesAsync(IFileSystemProvider provider, VfsPath path, VfsEntry source, CancellationToken ct)
    {
        if (provider is not IAttributeSupport attributes)
        {
            return;
        }
        // Archive bit is set by the OS on new files anyway; only carry the meaningful flags.
        var flags = source.Attributes & (EntryAttributes.ReadOnly | EntryAttributes.Hidden | EntryAttributes.System);
        if (flags == EntryAttributes.None && source.UnixMode is null)
        {
            return;
        }
        try
        {
            await attributes.SetAttributesAsync(path, flags, source.UnixMode, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // attributes are best effort (e.g. FAT/exFAT targets)
        }
    }

    private static async Task TryDeleteAsync(IFileSystemProvider provider, VfsPath path)
    {
        try
        {
            await provider.DeleteAsync(path, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // ignore: partial file cleanup is best effort
        }
    }

    /// <summary>Block size of the compatibility mode.</summary>
    public const int CompatibilityBufferSize = 64 * 1024;

    private bool IsCompatibilityPath(VfsPath path)
    {
        if (_options.CompatibilityPaths.Count == 0 || !path.IsLocal)
        {
            return false;
        }
        var full = path.Path;
        return _options.CompatibilityPaths.Any(p => p.Length > 0
            && (full.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || (p.Length == 1 && char.IsLetter(p[0]) && full.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Reads the target back and compares its BLAKE3 digest with the one taken while copying.</summary>
    private static async Task<bool> VerifyAsync(byte[]? sourceDigest, IFileSystemProvider tp, VfsPath t, CancellationToken ct)
    {
        if (sourceDigest is null)
        {
            return false;
        }
        using var hasher = new Tools.Checksums.Blake3Hasher();
        await using var stream = await tp.OpenReadAsync(t, ct).ConfigureAwait(false);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, n));
        }
        return hasher.Digest().AsSpan().SequenceEqual(sourceDigest);
    }
}
