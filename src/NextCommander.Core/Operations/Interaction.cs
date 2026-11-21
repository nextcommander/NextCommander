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
    /// <summary>Skip this one and, without asking, every later error of the same kind.</summary>
    SkipAllOfKind,
    /// <summary>Skip it now; when the operation ends it runs again with administrator rights (access denied).</summary>
    Elevate,
}

/// <summary>What went wrong, as far as the error tells: the error dialog explains each and suggests what to do.</summary>
public enum ErrorKind
{
    Other,
    /// <summary>Another program (or this one) has the file open.</summary>
    InUse,
    AccessDenied,
    DiskFull,
    /// <summary>The connection to a network share or server was lost.</summary>
    NetworkLost,
    NotFound,
}

/// <summary>An error of an operation: the item, the message, its kind and code, where the operation was.</summary>
public sealed record JobError(string Item, string Message)
{
    public ErrorKind Kind { get; init; }

    /// <summary>The system's error number (Windows: the Win32 code, e.g. 32 for a file in use); 0 when unknown.</summary>
    public int Code { get; init; }

    /// <summary>The operation's name ("Copy", "Delete" ...).</summary>
    public string? Operation { get; init; }

    public int DoneFiles { get; init; }

    public int TotalFiles { get; init; }

    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>The kind of an exception and its system error number.</summary>
    public static (ErrorKind Kind, int Code) KindOf(Exception exception)
    {
        var code = exception.HResult & 0xFFFF;
        if ((exception.HResult & unchecked((int)0xFFFF0000)) != unchecked((int)0x80070000) && exception.HResult >= 0)
        {
            code = exception.HResult; // Unix: the errno
        }
        var message = exception.Message;
        var kind = exception switch
        {
            UnauthorizedAccessException => ErrorKind.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => ErrorKind.NotFound,
            IOException when OperatingSystem.IsWindows() => code switch
            {
                5 => ErrorKind.AccessDenied,
                32 or 33 => ErrorKind.InUse,
                39 or 112 => ErrorKind.DiskFull,
                51 or 53 or 59 or 64 or 67 or 121 or 1231 or 1232 => ErrorKind.NetworkLost,
                2 or 3 => ErrorKind.NotFound,
                _ => ErrorKind.Other,
            },
            IOException when message.Contains("No space left", StringComparison.OrdinalIgnoreCase) => ErrorKind.DiskFull,
            IOException when message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) => ErrorKind.AccessDenied,
            IOException when message.Contains("busy", StringComparison.OrdinalIgnoreCase) => ErrorKind.InUse,
            _ => ErrorKind.Other,
        };
        return (kind, code);
    }
}

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
