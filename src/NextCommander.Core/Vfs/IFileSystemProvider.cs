namespace NextCommander.Core.Vfs;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,
    /// <summary>Rename/move inside the same provider without copying data.</summary>
    Rename = 4,
    Watch = 8,
    SetTimes = 16,
    Links = 32,
    Trash = 64,
}

/// <summary>
/// One file system: local disk, an archive, an SFTP server, a plugin... The operation engine only talks to
/// this interface, so any combination of source and target providers works.
/// </summary>
public interface IFileSystemProvider
{
    string Scheme { get; }

    ProviderCapabilities Capabilities { get; }

    bool CanHandle(VfsPath path) => path.Scheme == Scheme;

    Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken);

    /// <summary>Returns the entry or null when it does not exist.</summary>
    Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken);

    Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Creates the directory and any missing parents.</summary>
    Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken);

    /// <summary>Deletes a file or an empty directory (recursive=false) / directory tree (recursive=true).</summary>
    Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Rename/move inside this provider. Throws <see cref="CrossDeviceMoveException"/> when the
    /// provider cannot do it without copying (e.g. different volumes).</summary>
    Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken);

    Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken);

    /// <summary>Watches a directory for changes; returns null when unsupported.</summary>
    IDisposable? Watch(VfsPath directory, Action onChanged) => null;
}

public sealed class CrossDeviceMoveException : IOException
{
    public CrossDeviceMoveException()
    {
    }

    public CrossDeviceMoveException(string message)
        : base(message)
    {
    }

    public CrossDeviceMoveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
