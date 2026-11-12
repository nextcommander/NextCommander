namespace NextCommander.Core.Vfs;

/// <summary>Optional provider feature: symbolic links.</summary>
public interface ILinkSupport
{
    Task CreateSymbolicLinkAsync(VfsPath link, string target, bool isDirectory, CancellationToken cancellationToken);
}

/// <summary>Optional provider feature: file attributes (Windows) and permission bits (Unix).</summary>
public interface IAttributeSupport
{
    Task SetAttributesAsync(VfsPath path, EntryAttributes attributes, UnixFileMode? unixMode, CancellationToken cancellationToken);
}

/// <summary>
/// Optional provider feature: the modification time is given with the upload (Dropbox cannot change it later); a copy
/// announces the source's time before opening the target for writing.
/// </summary>
public interface IUploadTimeSupport
{
    void PrepareUpload(VfsPath path, DateTime lastWriteTime);
}

/// <summary>Optional platform feature: recycle bin / trash.</summary>
public interface ITrashService
{
    bool IsAvailable { get; }

    /// <summary>Moves a local file or directory to the trash.</summary>
    Task MoveToTrashAsync(string nativePath, CancellationToken cancellationToken);
}
