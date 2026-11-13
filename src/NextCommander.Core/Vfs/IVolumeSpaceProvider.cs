namespace NextCommander.Core.Vfs;

/// <summary>The size and free space of the volume a path is on, with its name ("Internal storage").</summary>
public sealed record VolumeSpace(string? Label, long Total, long Free);

/// <summary>
/// Optional provider feature: the free space of the volume behind a path that is not a local drive (a phone's storage),
/// shown in the panel's footer like a drive's.
/// </summary>
public interface IVolumeSpaceProvider
{
    /// <summary>The space of the volume of <paramref name="path"/>; null when it is not known.</summary>
    Task<VolumeSpace?> GetSpaceAsync(VfsPath path, CancellationToken cancellationToken);
}
