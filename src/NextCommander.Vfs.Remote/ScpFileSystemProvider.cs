using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.Core.Vfs.PosixShell;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>An SSH session for SCP: a shell for listing and changes, SCP for the file data.</summary>
public sealed class ScpSession(SshClient shell, ScpClient copy) : IDisposable
{
    public SshClient Shell { get; } = shell;

    public ScpClient Copy { get; } = copy;

    public bool IsConnected => Shell.IsConnected && Copy.IsConnected;

    public void Dispose()
    {
        Shell.Dispose();
        Copy.Dispose();
    }
}

/// <summary>
/// SCP over SSH: <c>scp://user@host:port/path</c>, for servers without the SFTP subsystem (embedded devices, old or
/// locked-down servers). The data goes over SCP; folders are listed and changed with POSIX shell commands
/// (<c>stat -c</c> of GNU / BusyBox, <c>stat -f</c> of BSD / macOS). Login and host key check as SFTP.
/// </summary>
public sealed class ScpFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<ScpSession>(hooks)
{
    public override string Scheme => "scp";

    protected override async Task<ScpSession> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        // the shell first, then SCP for the data (paths go to the remote shell in single quotes: no command injection)
        var made = 0;
        var clients = await SshLogin.ConnectAsync<BaseClient>(connection, password, trustedFingerprint, Hooks,
            info => made++ == 0
                ? SshLogin.KeepAlive(new SshClient(info), connection)
                : SshLogin.KeepAlive(new ScpClient(info, RemotePathTransformation.ShellQuote) { OperationTimeout = SftpFileSystemProvider.OperationTimeoutOf(connection) }, connection),
            2, cancellationToken).ConfigureAwait(false);
        return new ScpSession((SshClient)clients[0], (ScpClient)clients[1]);
    }

    /// <summary>No password with the SSH agent or a key file (asked only when they are refused).</summary>
    protected override bool NeedsPassword(RemoteConnection connection) => string.IsNullOrEmpty(connection.KeyFile) && !connection.UseSshAgent;

    protected override bool IsAlive(ScpSession client) => client.IsConnected;

    protected override ValueTask CloseAsync(ScpSession client)
    {
        try
        {
            client.Shell.Disconnect();
            client.Copy.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            // already gone
        }
        client.Dispose();
        SshLogin.Release(client.Shell); // the jump host it went through
        return ValueTask.CompletedTask;
    }

    protected override bool IsAuthenticationFailure(Exception ex) => ex is SshAuthenticationException;

    protected override bool IsConnectionFailure(Exception ex) =>
        ex is SshConnectionException or System.Net.Sockets.SocketException or ObjectDisposedException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        ScpException when ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) => new FileNotFoundException($"Not found: {path}", path, ex),
        ScpException when ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) => new UnauthorizedAccessException($"Access denied: {path}", ex),
        SshAuthenticationException => new UnauthorizedAccessException(ex.Message, ex),
        SshException or System.Net.Sockets.SocketException => new IOException($"SCP: {ex.Message}", ex),
        _ => null,
    };

    // ---------------------------------------------------------------- shell commands (PosixShell)

    /// <summary>Runs a command; a failure becomes the matching exception ("No such file" → not found, "Permission denied" → access).</summary>
    private static string Run(ScpSession session, string command, string path)
    {
        using var run = session.Shell.RunCommand(command);
        if (run.ExitStatus is 0 or null)
        {
            return run.Result;
        }
        var error = run.Error.Trim();
        if (error.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"Not found: {path}", path);
        }
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Access denied: {path}");
        }
        throw new IOException(error.Length > 0 ? $"SCP: {error}" : $"SCP: the command failed (exit code {run.ExitStatus}).");
    }

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, session => Task.Run<IReadOnlyList<VfsEntry>>(() =>
        {
            var output = Run(session, ListCommand(directory.Path), directory.Path);
            return [.. ParseListing(output).Select(item => ToEntry(directory, Combine(directory.Path, item.Name), item.Name, item.IsDirectory, item.IsLink, item.Size, item.Modified))];
        }, cancellationToken), cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        return await UseAsync(path, session => Task.Run(() =>
        {
            var output = Run(session, StatCommand(path.Path), path.Path).Trim();
            return output.Length > 0 && ParseLine(output) is { } item
                ? (VfsEntry?)ToEntry(path, path.Path, item.Name, item.IsDirectory, item.IsLink, item.Size, item.Modified)
                : null;
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file comes over SCP into a temporary file, read from there (deleted when closed).</summary>
    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync<Stream>(path, session => Task.Run<Stream>(() =>
        {
            var temp = new FileStream(Path.Combine(Path.GetTempPath(), "fm-scp-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.DeleteOnClose);
            try
            {
                session.Copy.Download(path.Path, temp);
                temp.Position = 0;
                return temp;
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }, cancellationToken), cancellationToken);

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UseAsync(path, session => Task.Run(() => session.Copy.Upload(data, path.Path), ct), ct), cancellationToken);
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, session => Task.Run(() => Run(session, $"mkdir -p -- {Quote(path.Path)}", path.Path), cancellationToken), cancellationToken);

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var command = !entry.IsDirectory || entry.IsLink ? $"rm -f -- {Quote(path.Path)}"
            : recursive ? $"rm -rf -- {Quote(path.Path)}"
            : $"rmdir -- {Quote(path.Path)}";
        await UseAsync(path, session => Task.Run(() => Run(session, command, path.Path), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase)
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await UseAsync(source, session => Task.Run(() => Run(session, $"mv -f -- {Quote(source.Path)} {Quote(destination.Path)}", source.Path), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>touch -t with the time in UTC (TZ=UTC0: the server's own time zone does not matter).</summary>
    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
        lastWriteTime is not { } time
            ? Task.CompletedTask
            : UseAsync(path, session => Task.Run(() =>
                Run(session, TouchCommand(path.Path, time), path.Path),
                cancellationToken), cancellationToken);
}
