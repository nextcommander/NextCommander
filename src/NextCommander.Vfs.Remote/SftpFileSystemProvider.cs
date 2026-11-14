using System.Security.Cryptography;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace NextCommander.Vfs.Remote;

/// <summary>SFTP (SSH) file system: <c>sftp://user@host:port/path</c>. Password or private key login, host key check.</summary>
public sealed class SftpFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<SftpClient>(hooks)
{
    public override string Scheme => "sftp";

    /// <summary>SHA256 fingerprint in OpenSSH notation ("SHA256:base64").</summary>
    public static string Fingerprint(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    protected override async Task<SftpClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken) =>
        (await SshLogin.ConnectAsync(connection, password, trustedFingerprint, Hooks,
            info => new SftpClient(info) { OperationTimeout = OperationTimeoutOf(connection) }, 1, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>How long one SFTP request may take: the connection's timeout when longer, else two minutes.</summary>
    internal static TimeSpan OperationTimeoutOf(RemoteConnection connection) =>
        TimeSpan.FromSeconds(Math.Max(120, connection.TimeoutSeconds));

    /// <summary>No password with the SSH agent or a key file (asked only when they are refused).</summary>
    protected override bool NeedsPassword(RemoteConnection connection) => string.IsNullOrEmpty(connection.KeyFile) && !connection.UseSshAgent;

    /// <summary>The connection: directly, or through the proxy of Configuration > Proxy (HTTP CONNECT or SOCKS5) when it is meant for file transfers too.</summary>
    internal static ConnectionInfo CreateConnectionInfo(RemoteConnection connection, AuthenticationMethod[] methods)
    {
        if (NextCommander.Core.Net.WebProxyConfig.FileTransferProxy(connection.Host) is not { Address: { } address } proxy)
        {
            return new ConnectionInfo(connection.Host, connection.EffectivePort, connection.User, methods);
        }
        var kind = proxy.EffectiveKind == NextCommander.Core.Net.ProxyKind.Socks5 ? ProxyTypes.Socks5 : ProxyTypes.Http;
        return new ConnectionInfo(connection.Host, connection.EffectivePort, connection.User, kind, address.Host, address.Port,
            proxy.User ?? string.Empty, NextCommander.Core.Net.WebProxyConfig.Password ?? string.Empty, methods);
    }

    protected override bool IsAlive(SftpClient client) => client.IsConnected;

    protected override ValueTask CloseAsync(SftpClient client)
    {
        try
        {
            client.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            // already gone
        }
        client.Dispose();
        SshLogin.Release(client); // the jump host it went through
        return ValueTask.CompletedTask;
    }

    protected override bool IsAuthenticationFailure(Exception ex) => ex is SshAuthenticationException;

    protected override bool IsConnectionFailure(Exception ex) =>
        ex is SshConnectionException or System.Net.Sockets.SocketException or ObjectDisposedException
        || (ex is SshException && ex is not (SftpPathNotFoundException or SftpPermissionDeniedException));

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        SftpPathNotFoundException => new FileNotFoundException($"Not found: {path}", path, ex),
        SftpPermissionDeniedException => new UnauthorizedAccessException($"Access denied: {path}", ex),
        SshAuthenticationException => new UnauthorizedAccessException(ex.Message, ex),
        SshException or System.Net.Sockets.SocketException => new IOException($"SFTP: {ex.Message}", ex),
        _ => null,
    };

    private VfsEntry ToEntry(VfsPath context, ISftpFile file, bool? linkIsDirectory = null) =>
        ToEntry(context, file.FullName, file.Name, linkIsDirectory ?? file.IsDirectory, file.IsSymbolicLink, file.Length, file.LastWriteTime);

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var result = new List<VfsEntry>();
            await foreach (var file in client.ListDirectoryAsync(directory.Path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }
                bool? linkIsDirectory = null;
                if (file.IsSymbolicLink)
                {
                    try
                    {
                        // stat follows the link: folders behind links can be entered.
                        linkIsDirectory = (await client.GetAttributesAsync(file.FullName, cancellationToken).ConfigureAwait(false)).IsDirectory;
                    }
                    catch (SshException)
                    {
                        linkIsDirectory = false; // dangling link
                    }
                }
                result.Add(ToEntry(directory, file, linkIsDirectory));
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return await UseAsync(path, async client =>
            {
                var file = await client.GetAsync(path.Path, cancellationToken).ConfigureAwait(false);
                return (VfsEntry?)ToEntry(path, file);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, async client => await client.OpenAsync(path.Path, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false),
            null, cancellationToken);

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return await OpenStreamAsync(path, async client =>
            await client.OpenAsync(path.Path, FileMode.Create, FileAccess.Write, cancellationToken).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false);
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            var current = string.Empty;
            foreach (var part in path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + part;
                if (!await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                {
                    await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken);

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (!entry.IsDirectory || entry.IsLink)
        {
            await UseAsync(path, c => c.DeleteFileAsync(path.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!recursive)
        {
            await UseAsync(path, c => c.DeleteDirectoryAsync(path.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        await DeleteTreeAsync(path,
            p => UseAsync(p, c => c.DeleteFileAsync(p.Path, cancellationToken), cancellationToken),
            p => UseAsync(p, c => c.DeleteDirectoryAsync(p.Path, cancellationToken), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!overwrite || existing.IsDirectory)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await UseAsync(destination, c => c.DeleteFileAsync(destination.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        await UseAsync(source, c => c.RenameFileAsync(source.Path, destination.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
        lastWriteTime is not { } time
            ? Task.CompletedTask
            : UseAsync(path, client =>
            {
                client.SetLastWriteTimeUtc(path.Path, time.ToUniversalTime());
                return Task.CompletedTask;
            }, cancellationToken);
}
