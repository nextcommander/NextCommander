namespace NextCommander.Vfs.Remote;

/// <summary>
/// A few connections to one server. FTP / SFTP clients do one thing at a time; with a small pool a panel can list a
/// folder while a copy job streams a file. Leases are returned with <see cref="Lease.DisposeAsync"/>; a lease that saw
/// a broken connection is discarded.
/// </summary>
public sealed class ClientPool<T> : IAsyncDisposable
    where T : class
{
    private readonly Func<CancellationToken, Task<T>> _connect;
    private readonly Func<T, bool> _isAlive;
    private readonly Func<T, ValueTask> _close;
    private readonly SemaphoreSlim _slots;
    private readonly Stack<T> _idle = new();
    private readonly object _lock = new();
    private bool _disposed;

    public ClientPool(Func<CancellationToken, Task<T>> connect, Func<T, bool> isAlive, Func<T, ValueTask> close, int maxConnections)
    {
        _connect = connect;
        _isAlive = isAlive;
        _close = close;
        _slots = new SemaphoreSlim(Math.Max(1, maxConnections));
    }

    /// <summary>Clients created so far (tests, diagnostics).</summary>
    public int Created { get; private set; }

    public async Task<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                T? idle = null;
                lock (_lock)
                {
                    if (_idle.Count > 0)
                    {
                        idle = _idle.Pop();
                    }
                }
                if (idle is null)
                {
                    var client = await _connect(cancellationToken).ConfigureAwait(false);
                    Created++;
                    return new Lease(this, client);
                }
                if (_isAlive(idle))
                {
                    return new Lease(this, idle);
                }
                await _close(idle).ConfigureAwait(false); // dropped by the server: reconnect
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private async ValueTask ReturnAsync(T client, bool broken)
    {
        if (broken || _disposed)
        {
            await _close(client).ConfigureAwait(false);
        }
        else
        {
            lock (_lock)
            {
                _idle.Push(client);
            }
        }
        _slots.Release();
    }

    /// <summary>Closes every idle connection (disconnect).</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        T[] idle;
        lock (_lock)
        {
            idle = [.. _idle];
            _idle.Clear();
        }
        foreach (var client in idle)
        {
            await _close(client).ConfigureAwait(false);
        }
    }

    /// <summary>A rented client.</summary>
    public sealed class Lease(ClientPool<T> pool, T client) : IAsyncDisposable
    {
        private int _returned;

        public T Client { get; } = client;

        /// <summary>The connection failed: close it instead of reusing it.</summary>
        public bool Broken { get; set; }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _returned, 1) == 0 ? pool.ReturnAsync(Client, Broken) : ValueTask.CompletedTask;
    }
}

/// <summary>Callbacks from the remote providers to the application (passwords, host keys, certificates).</summary>
public sealed class RemoteHooks
{
    /// <summary>Saved connection for a path authority, or one built from the authority itself.</summary>
    public Func<string, string, Core.Config.RemoteConnection> ResolveConnection { get; set; } = Core.Config.RemoteConnection.FromAuthority;

    /// <summary>Password (or key passphrase): (connection, previous one was rejected) → password or null to cancel.</summary>
    public Func<Core.Config.RemoteConnection, bool, Task<string?>>? GetPassword { get; set; }

    /// <summary>Login succeeded with this password (the application may store it).</summary>
    public Func<Core.Config.RemoteConnection, string?, Task>? LoginSucceeded { get; set; }

    /// <summary>Trusted SSH host key / TLS certificate fingerprint saved for the connection, or null.</summary>
    public Func<Core.Config.RemoteConnection, string?>? KnownFingerprint { get; set; }

    /// <summary>
    /// Unknown or changed fingerprint: (connection, fingerprint, description, changed) → trust it (the application saves it).
    /// </summary>
    public Func<Core.Config.RemoteConnection, string, string, bool, Task<bool>>? ConfirmFingerprint { get; set; }

    /// <summary>Cloud drives: the saved refresh token of the account, or null (then the browser sign-in starts).</summary>
    public Func<Core.Config.RemoteConnection, Task<string?>>? LoadToken { get; set; }

    /// <summary>Cloud drives: a new refresh token to keep (null: forget the saved one).</summary>
    public Func<Core.Config.RemoteConnection, string?, Task>? SaveToken { get; set; }

    /// <summary>Cloud drives: opens the sign-in page in the browser.</summary>
    public Func<Uri, Task>? OpenBrowser { get; set; }
}

/// <summary>A server key or certificate that was not trusted yet; the connection is retried after the user accepts it.</summary>
public sealed class UntrustedServerException(string fingerprint, string description, bool changed)
    : IOException(changed ? "Server key has changed!" : "Server key is not recognized.")
{
    public string Fingerprint { get; } = fingerprint;

    public string Description { get; } = description;

    public bool Changed { get; } = changed;
}
