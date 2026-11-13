using NextCommander.Core.Vfs;

namespace NextCommander.Platform;

/// <summary>
/// "Computer" in the drive box (drive overview): <c>computer:///</c> lists every drive as a folder (size =
/// capacity). Opening one goes to the drive's root (<see cref="LocalRootOf"/>). Read only: nothing can be copied, moved or
/// deleted here, so a drive can never be deleted as a whole by mistake.
/// </summary>
public sealed class ComputerFileSystemProvider(Func<IReadOnlyList<DriveItem>> drives) : IFileSystemProvider
{
    public ComputerFileSystemProvider(IPlatformService platform)
        : this(platform.GetDrives)
    {
    }

    public const string ComputerScheme = "computer";

    public static VfsPath Root { get; } = VfsPath.Parse(ComputerScheme + ":///");

    public string Scheme => ComputerScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    /// <summary>The drive root a <c>computer:///&lt;drive&gt;</c> entry stands for; null for the overview itself.</summary>
    public static string? LocalRootOf(VfsPath path) =>
        path.Scheme == ComputerScheme && path.Path.Trim('/').Length > 0 ? Uri.UnescapeDataString(path.Path.Trim('/')) : null;

    /// <summary>The name of a drive in the overview, as Explorer shows it: "System (C:)", or its kind without a label: "Local Disk (D:)".</summary>
    public static string ShownName(DriveItem drive)
    {
        if (drive.Kind == DriveKind.Trash)
        {
            return NextCommander.Core.Localization.Localizer.T(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash");
        }
        var name = drive.Name.TrimEnd('\\') is { Length: > 0 } trimmed ? trimmed : drive.Name;
        return $"{(drive.HasLabel ? drive.Label : NextCommander.Core.Localization.Localizer.T(drive.KindText))} ({name})";
    }

    private VfsEntry EntryOf(DriveItem drive) => new()
    {
        Name = ShownName(drive),
        Path = VfsPath.Create(ComputerScheme, string.Empty, "/" + Uri.EscapeDataString(drive.RootPath)),
        IsDirectory = true,
        Size = drive.TotalSize,
        LastWriteTime = DateTime.MinValue,
    };

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (LocalRootOf(directory) is not null)
        {
            throw new UnauthorizedAccessException("The Computer view shows the drives only: open a drive to work in it.");
        }
        IReadOnlyList<VfsEntry> entries = [.. Ordered(drives()).Select(EntryOf), EntryOf(DriveItem.TrashEntry)];
        return Task.FromResult(entries);
    }

    /// <summary>
    /// The drives as the overview lists them: the system's drives in their order (drive letters on Windows), then
    /// external (removable) disks, then phones and cameras.
    /// </summary>
    public static IEnumerable<DriveItem> Ordered(IEnumerable<DriveItem> drives) =>
        drives.Where(d => d.IsReady).OrderBy(d => d.Kind switch { DriveKind.Removable => 1, DriveKind.Phone => 2, _ => 0 }); // stable: keeps the letter order

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (LocalRootOf(path) is not { } root)
        {
            return Task.FromResult<VfsEntry?>(new VfsEntry { Name = "Computer", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue });
        }
        var drive = drives().Append(DriveItem.TrashEntry).FirstOrDefault(d => string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(drive is null ? null : EntryOf(drive));
    }

    private static UnauthorizedAccessException ReadOnly() => new("Drives cannot be copied, moved or deleted in the Computer view.");

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => throw ReadOnly();
}
