using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>What to do when the target file already exists.</summary>
public enum ConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    /// <summary>Overwrite when the target is older, skip otherwise.</summary>
    OverwriteOlder,
    /// <summary>Copy under a new name: "name (2).ext".</summary>
    AutoRenameSource,
    /// <summary>Rename an older existing target to "name (2).ext", skip when the target is newer.</summary>
    RenameOlderTarget,
    /// <summary>Rename every existing target to "name (2).ext" and copy the new file under its own name.</summary>
    AutoRenameTarget,
    /// <summary>Copy when the source is larger (overwrite smaller targets), skip otherwise.</summary>
    OverwriteSmaller,
    /// <summary>Copy when the source is smaller (overwrite larger targets), skip otherwise.</summary>
    OverwriteLarger,
}

public enum ConflictAction
{
    Overwrite,
    Skip,
    OverwriteOlder,
    AutoRename,
    RenameTo,
    Cancel,
}

public sealed record ConflictRequest(VfsEntry Source, VfsEntry Target);

public sealed record ConflictResponse(ConflictAction Action, bool ApplyToAll = false, string? NewName = null);

public enum ErrorAction
{
    Retry,
    Skip,
    SkipAll,
    Cancel,
}

public sealed record JobError(string Item, string Message);

/// <summary>Bridge to the UI for questions asked during an operation. Implementations marshal to the UI thread.</summary>
public interface IJobInteraction
{
    Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken);

    Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken);
}

/// <summary>Non-interactive defaults; useful for tests and background jobs.</summary>
public sealed class AutoInteraction(ConflictAction conflictAction = ConflictAction.Skip, ErrorAction errorAction = ErrorAction.Skip)
    : IJobInteraction
{
    public Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ConflictResponse(conflictAction, ApplyToAll: true));

    public Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken) =>
        Task.FromResult(errorAction);
}
