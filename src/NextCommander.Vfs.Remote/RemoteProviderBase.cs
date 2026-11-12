using System.Collections.Concurrent;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Shared part of the FTP, SFTP, WebDAV and S3 providers: one small connection pool per server, the login loop (password,
/// retry on a wrong one, saving it), server key / certificate confirmation and translation of protocol errors into
/// IO exceptions the file operations understand (skip / retry instead of failing the whole job).
/// </summary>
/// <summary>A provider with server connections that can be closed (nc_FtpDisconnect).</summary>
public interface IRemoteProvider
{
    bool CanHandle(VfsPath path);

    Task DisconnectAsync(VfsPath path);

    bool IsConnected(VfsPath path);
}

public abstract class RemoteProviderBase<TClient> : IFileSystemProvider, IRemoteProvider, IAsyncDisposable
    where TClient : class
{
    private readonly ConcurrentDictionary<string, Server> _servers = new(StringComparer.Ordinal);

    protected RemoteProviderBase(RemoteHooks hooks)
    {
        Hooks = hooks;
    }

    public RemoteHooks Hooks { get; }

    public abstract string Scheme { get; }

    public virtual ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>Whether a login asks for a password (SFTP with a key file: only when the key is rejected).</summary>
    protected virtual bool NeedsPassword(RemoteConnection connection) => string.IsNullOrEmpty(connection.KeyFile);

    public virtual bool CanHandle(VfsPath path) => path.Scheme == Scheme;

    private sealed class Server
    {
        public required RemoteConnection Connection { get; init; }

        public ClientPool<TClient>? Pool { get; set; }

        public string? Password { get; set; }

        public bool PasswordKnown { get; set; }

        /// <summary>Fingerprint accepted in this session.</summary>
        public string? TrustedFingerprint { get; set; }

        public SemaphoreSlim LoginLock { get; } = new(1, 1);
    }

    private Server ServerOf(VfsPath path) => _servers.GetOrAdd(path.Scheme + "://" + path.Authority, _ => new Server
    {
        Connection = Hooks.ResolveConnection(path.Scheme, path.Authority),
    });

    /// <summary>The connection settings used for a path.</summary>
    public RemoteConnection ConnectionOf(VfsPath path) => ServerOf(path).Connection;

    /// <summary>Closes the connections to the server of <paramref name="path"/> (next access logs in again).</summary>
    public async Task DisconnectAsync(VfsPath path)
    {
        if (_servers.TryRemove(path.Scheme + "://" + path.Authority, out var server) && server.Pool is { } pool)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    public bool IsConnected(VfsPath path) => _servers.TryGetValue(path.Scheme + "://" + path.Authority, out var s) && s.Pool is not null;

    protected async Task<ClientPool<TClient>.Lease> RentAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var server = ServerOf(path);
        server.Pool ??= new ClientPool<TClient>(ct => LoginAsync(server, ct), IsAlive, CloseAsync, server.Connection.MaxConnections);
        try
        {
            return await server.Pool.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException) && Translate(ex, path.Path) is { } translated)
        {
            // Cannot connect (host unreachable, refused, TLS failure...): an IO error like any other.
            throw new IOException($"Could not connect to {server.Connection.Host}:{server.Connection.EffectivePort}: {translated.Message}", ex);
        }
    }

    private async Task<TClient> LoginAsync(Server server, CancellationToken cancellationToken)
    {
        // One login dialog at a time per server; parallel connections reuse the accepted password.
        await server.LoginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = server.Connection;
            if (!server.PasswordKnown && NeedsPassword(connection))
            {
                server.Password = await AskPasswordAsync(connection, retry: false).ConfigureAwait(false);
                server.PasswordKnown = true;
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var trusted = server.TrustedFingerprint ?? Hooks.KnownFingerprint?.Invoke(connection);
                    var client = await ConnectAsync(connection, server.Password, trusted, cancellationToken).ConfigureAwait(false);
                    if (Hooks.LoginSucceeded is { } succeeded)
                    {
                        await succeeded(connection, server.Password).ConfigureAwait(false);
                    }
                    server.PasswordKnown = true;
                    return client;
                }
                catch (UntrustedServerException ex)
                {
                    var accepted = Hooks.ConfirmFingerprint is { } confirm && await confirm(connection, ex.Fingerprint, ex.Description, ex.Changed).ConfigureAwait(false);
                    if (!accepted)
                    {
                        throw new IOException($"{connection.Host}: server key/certificate was not accepted.", ex);
                    }
                    server.TrustedFingerprint = ex.Fingerprint;
                }
                catch (Exception ex) when (IsAuthenticationFailure(ex))
                {
                    var retry = server.PasswordKnown && (server.Password is not null || !string.IsNullOrEmpty(connection.KeyFile));
                    server.Password = await AskPasswordAsync(connection, retry).ConfigureAwait(false);
                    server.PasswordKnown = true;
                }
            }
        }
        finally
        {
            server.LoginLock.Release();
        }
    }

    private async Task<string?> AskPasswordAsync(RemoteConnection connection, bool retry)
    {
        var password = Hooks.GetPassword is null ? null : await Hooks.GetPassword(connection, retry).ConfigureAwait(false);
        // A connection problem (not an access right of one folder): the panel reports it and stays where it was.
        return password ?? throw new IOException($"{connection.User}@{connection.Host}: login cancelled.");
    }

    /// <summary>Opens and logs in one client. Throws <see cref="UntrustedServerException"/> for keys / certificates not in <paramref name="trustedFingerprint"/>.</summary>
    protected abstract Task<TClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken);

    protected abstract bool IsAlive(TClient client);

    protected abstract ValueTask CloseAsync(TClient client);

    protected abstract bool IsAuthenticationFailure(Exception ex);

    /// <summary>The connection itself failed (the client is discarded instead of reused).</summary>
    protected abstract bool IsConnectionFailure(Exception ex);

    /// <summary>Protocol exception → <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> / not found; null = rethrow as is.</summary>
    protected abstract Exception? Translate(Exception ex, string path);

    /// <summary>Runs an operation on a pooled client.</summary>
    protected async Task<T> UseAsync<T>(VfsPath path, Func<TClient, Task<T>> action, CancellationToken cancellationToken)
    {
        var lease = await RentAsync(path, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            try
            {
                return await action(lease.Client).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Translate(ex, path.Path) is { } translated)
            {
                lease.Broken |= IsConnectionFailure(ex);
                throw translated;
            }
            catch (OperationCanceledException)
            {
                lease.Broken = true; // a transfer interrupted mid-way leaves the connection in an unknown state
                throw;
            }
        }
    }

    protected Task UseAsync(VfsPath path, Func<TClient, Task> action, CancellationToken cancellationToken) =>
        UseAsync(path, async c =>
        {
            await action(c).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>A stream that keeps its client rented until it is closed.</summary>
    protected async Task<Stream> OpenStreamAsync(VfsPath path, Func<TClient, Task<Stream>> open, Func<TClient, Task>? afterClose, CancellationToken cancellationToken)
    {
        var lease = await RentAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await open(lease.Client).ConfigureAwait(false);
            return new LeaseStream(stream, lease, afterClose, ex => Translate(ex, path.Path), IsConnectionFailure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Translate(ex, path.Path) is { } translated)
        {
            lease.Broken |= IsConnectionFailure(ex);
            await lease.DisposeAsync().ConfigureAwait(false);
            throw translated;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected VfsEntry ToEntry(VfsPath parentOrSelf, string fullPath, string name, bool isDirectory, bool isLink, long size, DateTime modified) => new()
    {
        Name = name,
        Path = VfsPath.Create(parentOrSelf.Scheme, parentOrSelf.Authority, fullPath),
        IsDirectory = isDirectory,
        IsLink = isLink,
        Size = isDirectory ? 0 : size,
        LastWriteTime = modified,
        Attributes = name.StartsWith('.') ? EntryAttributes.Hidden : EntryAttributes.None,
    };

    public abstract Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken);

    public abstract Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken);

    public abstract Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken);

    public abstract Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken);

    public abstract Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken);

    public abstract Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken);

    public abstract Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken);

    /// <summary>Deletes a folder tree with the provider's own list / delete operations.</summary>
    protected async Task DeleteTreeAsync(VfsPath directory, Func<VfsPath, Task> deleteFile, Func<VfsPath, Task> deleteEmptyDirectory, CancellationToken ct)
    {
        foreach (var child in await ListAsync(directory, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (child.IsDirectory && !child.IsLink)
            {
                await DeleteTreeAsync(child.Path, deleteFile, deleteEmptyDirectory, ct).ConfigureAwait(false);
            }
            else
            {
                await deleteFile(child.Path).ConfigureAwait(false);
            }
        }
        await deleteEmptyDirectory(directory).ConfigureAwait(false);
    }

    protected static string Combine(string directory, string name) => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            if (server.Pool is { } pool)
            {
                await pool.DisposeAsync().ConfigureAwait(false);
            }
        }
        _servers.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stream bound to a lease: closing it runs the protocol's after-transfer step and returns the client.</summary>
    private sealed class LeaseStream(Stream inner, ClientPool<TClient>.Lease lease, Func<TClient, Task>? afterClose,
        Func<Exception, Exception?> translate, Func<Exception, bool> isConnectionFailure) : Stream
    {
        private bool _closed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Wrap(() => inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception ex) when (Fail(ex) is { } t)
            {
                throw t;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Fail(ex) is { } t)
            {
                throw t;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Wrap(() =>
        {
            inner.Write(buffer, offset, count);
            return 0;
        });

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Fail(ex) is { } t)
            {
                throw t;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        private int Wrap(Func<int> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (Fail(ex) is { } t)
            {
                throw t;
            }
        }

        private Exception? Fail(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                lease.Broken = true;
                return null;
            }
            lease.Broken |= isConnectionFailure(ex);
            return translate(ex);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                    if (afterClose is not null && !lease.Broken)
                    {
                        await afterClose(lease.Client).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lease.Broken = true;
                    if (translate(ex) is { } t)
                    {
                        throw t;
                    }
                    throw;
                }
                finally
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}
