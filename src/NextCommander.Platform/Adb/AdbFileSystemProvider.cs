using System.Collections.Concurrent;
using System.Globalization;
using NextCommander.Core.Vfs;
using static NextCommander.Core.Vfs.PosixShell;

namespace NextCommander.Platform.Adb;

/// <summary>
/// The files of an Android phone over ADB: <c>adb://serial/sdcard/DCIM/...</c> (the path is the phone's own; the drive
/// opens at /sdcard, the shared storage). Folders are listed with the phone's shell (toybox stat), files are read
/// with exec-out cat and written with push (which keeps the modification time); changes are shell commands.
/// <c>adb:///</c> lists the connected devices.
/// </summary>
public sealed class AdbFileSystemProvider(AdbBridge bridge) : IFileSystemProvider, IUploadTimeSupport, IVolumeSpaceProvider
{
    public const string AdbScheme = "adb";

    /// <summary>The device list: <c>adb:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(AdbScheme, string.Empty, "/");

    /// <summary>Where a phone opens: its shared storage.</summary>
    public static VfsPath DeviceRoot(string serial) => VfsPath.Create(AdbScheme, serial, "/sdcard");

    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

    public string Scheme => AdbScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    private IAdbRunner Runner => bridge.Runner
        ?? throw new IOException("ADB: adb is not installed (Android SDK platform-tools; or set FM_ADB to adb's path).");

    /// <summary>The message of an adb failure as the matching exception.</summary>
    internal static Exception ErrorOf(AdbResult result, VfsPath path)
    {
        var text = (result.Error + "\n" + result.Output).Trim();
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        if (text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new UnauthorizedAccessException("ADB: allow USB debugging for this computer on the phone.");
        }
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no devices", StringComparison.OrdinalIgnoreCase) || text.Contains("device offline", StringComparison.OrdinalIgnoreCase))
        {
            return new IOException($"ADB: the phone is not connected: {path.Authority}");
        }
        if (text.Contains("No such file", StringComparison.OrdinalIgnoreCase) || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return new FileNotFoundException($"Not found: {path.Path}", path.Path);
        }
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase))
        {
            return new UnauthorizedAccessException($"Access denied: {path.Path}");
        }
        return new IOException(line.Length > 0 ? $"ADB: {line}" : $"ADB: the command failed (exit code {result.ExitCode}).");
    }

    private async Task<string> ShellAsync(VfsPath path, string command, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(["-s", path.Authority, "shell", command], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : throw ErrorOf(result, path);
    }

    private static VfsEntry EntryOf(VfsPath path, (string Name, bool IsDirectory, bool IsLink, long Size, DateTime Modified) item) => new()
    {
        Name = item.Name,
        Path = path,
        IsDirectory = item.IsDirectory,
        IsLink = item.IsLink,
        Size = item.IsDirectory ? 0 : item.Size,
        LastWriteTime = item.Modified,
    };

    private IEnumerable<AdbDevice> ReadyDevices() => bridge.GetDevices().Where(d => d.IsReady);

    // ---------------------------------------------------------------- reading

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (directory.Authority.Length == 0)
        {
            return [.. ReadyDevices().Select(d => new VfsEntry { Name = d.Name, Path = DeviceRoot(d.Serial), IsDirectory = true, LastWriteTime = DateTime.MinValue })];
        }
        var output = await ShellAsync(directory, ListCommand(directory.Path), cancellationToken).ConfigureAwait(false);
        return [.. ParseListing(output).Select(item => EntryOf(directory.Combine(item.Name), item))];
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "Android devices", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        try
        {
            var output = (await ShellAsync(path, StatCommand(path.Path), cancellationToken).ConfigureAwait(false)).Trim();
            return output.Length > 0 && ParseLine(output) is { } item ? EntryOf(path, item) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        if (entry.IsDirectory)
        {
            throw new IOException($"Not a file: {path.Path}");
        }
        return await Runner.OpenOutputAsync(["-s", path.Authority, "exec-out", "cat -- " + Quote(path.Path)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The data is collected in a temporary file and pushed when the stream is closed; the time announced by the copy
    /// (<see cref="PrepareUpload"/>) is set on it first, as push keeps it.
    /// </summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => PushAsync(path, data, ct), cancellationToken);
    }

    private async Task PushAsync(VfsPath path, Stream data, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "fm-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, "upload");
        try
        {
            await using (var file = File.Create(local))
            {
                data.Position = 0;
                await data.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            if (_uploadTimes.TryRemove(path, out var time) && time.Year >= 1980)
            {
                File.SetLastWriteTimeUtc(local, time.ToUniversalTime());
            }
            var result = await Runner.RunAsync(["-s", path.Authority, "push", local, path.Path], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw ErrorOf(result, path);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    // ---------------------------------------------------------------- changes

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        ShellAsync(path, "mkdir -p -- " + Quote(path.Path), cancellationToken);

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        var command = !entry.IsDirectory || entry.IsLink ? "rm -f -- " + Quote(path.Path)
            : recursive ? "rm -rf -- " + Quote(path.Path)
            : "rmdir -- " + Quote(path.Path);
        await ShellAsync(path, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between devices is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.Ordinal)
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await ShellAsync(source, $"mv -f -- {Quote(source.Path)} {Quote(destination.Path)}", cancellationToken).ConfigureAwait(false);
    }

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
        lastWriteTime is { Year: >= 1980 } time ? ShellAsync(path, TouchCommand(path.Path, time), cancellationToken) : Task.CompletedTask;

    // ---------------------------------------------------------------- space

    /// <summary>The last line of "df -kP": filesystem, 1K blocks, used, available, use%, mount point.</summary>
    public static VolumeSpace? ParseDf(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        var parts = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 6 } || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
        {
            return null;
        }
        return new VolumeSpace(parts[^1], total * 1024, free * 1024);
    }

    public async Task<VolumeSpace?> GetSpaceAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Scheme != AdbScheme || path.Authority.Length == 0 || bridge.Runner is null)
        {
            return null;
        }
        try
        {
            return ParseDf(await ShellAsync(path, "df -kP -- " + Quote(path.Path), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
