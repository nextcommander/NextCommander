using System.Net;
using System.Net.Sockets;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace NextCommander.Vfs.Remote;

/// <summary>One logged-in SMB2/3 session and its open shares (tree connections).</summary>
public sealed class SmbSession : IDisposable
{
    internal SmbSession(SmbClient client, NextCommander.Core.Net.ProxyForwarder? forwarder = null)
    {
        Client = client;
        Forwarder = forwarder;
    }

    internal SmbClient Client { get; }

    /// <summary>The local port leading through the proxy (Configuration > Proxy), when the session uses one.</summary>
    internal NextCommander.Core.Net.ProxyForwarder? Forwarder { get; }

    /// <summary>The hidden administrative shares (C$, ADMIN$) are listed too (IPC$ never: it holds no files).</summary>
    internal bool ShowHiddenShares { get; init; }

    internal Dictionary<string, ISMBFileStore> Shares { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var share in Shares.Values)
        {
            try
            {
                share.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
            {
            }
        }
        Shares.Clear();
        try
        {
            Client.Logoff();
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
        }
        Client.Disconnect();
        Forwarder?.Dispose();
    }
}

/// <summary>SMB2Client that can connect to another port than 445 (test servers, port forwarding).</summary>
internal sealed class SmbClient : SMB2Client
{
    public bool ConnectTo(IPAddress address, int port) => Connect(address, SMBTransportType.DirectTCPTransport, port);
}

/// <summary>An SMB error status as an exception.</summary>
public sealed class SmbStatusException(NTStatus status, string message) : IOException(message)
{
    public NTStatus Status { get; } = status;
}

/// <summary>
/// SMB / CIFS shares (<c>smb://[domain;]user@host[:port]/share/folder/file</c>) with an SMB2/3 client of its own
/// (SMBLibrary), so they open the same way on Windows, Linux and macOS without mounting. The root lists the shares
/// of the machine; an empty user logs in as guest. Windows opens \\host\share paths itself; this is the way on the
/// other systems (network view, connection manager).
/// </summary>
public sealed class SmbFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<SmbSession>(hooks)
{
    public override string Scheme => "smb";

    protected override bool NeedsPassword(RemoteConnection connection) => !string.IsNullOrEmpty(connection.User);

    /// <summary>"DOMAIN\user", "DOMAIN;user" or "user@domain" → (domain, user).</summary>
    public static (string Domain, string User) SplitUser(string user)
    {
        var separator = user.IndexOfAny(['\\', ';']);
        if (separator > 0)
        {
            return (user[..separator], user[(separator + 1)..]);
        }
        var at = user.LastIndexOf('@');
        return at > 0 ? (user[(at + 1)..], user[..at]) : (string.Empty, user);
    }

    protected override async Task<SmbSession> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        // through its own proxy or the one of Configuration > Proxy: the SMB client cannot use one, so it talks to a local port that leads
        // there; the proxy resolves the server's name
        NextCommander.Core.Net.ProxyForwarder? forwarder = null;
        IPAddress address;
        var port = connection.EffectivePort;
        if (NextCommander.Core.Net.WebProxyConfig.TunnelProxyOf(connection) is { } proxy)
        {
            forwarder = new NextCommander.Core.Net.ProxyForwarder(proxy, NextCommander.Core.Net.WebProxyConfig.PasswordOf(proxy), connection.Host, connection.EffectivePort);
            address = forwarder.LocalEndPoint.Address;
            port = forwarder.LocalEndPoint.Port;
        }
        else
        {
            address = IPAddress.TryParse(connection.Host, out var ip) ? ip
                : (await Dns.GetHostAddressesAsync(connection.Host, cancellationToken).ConfigureAwait(false))
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()
                  ?? throw new IOException($"SMB: server not found: {connection.Host}");
        }
        return await Task.Run(() =>
        {
            var client = new SmbClient();
            if (!client.ConnectTo(address, port))
            {
                forwarder?.Dispose();
                throw new IOException(forwarder is null
                    ? $"SMB: could not connect to {connection.Host}:{connection.EffectivePort}."
                    : $"SMB: could not connect to {connection.Host}:{connection.EffectivePort} through the proxy.");
            }
            var (domain, user) = SplitUser(connection.User);
            var status = client.Login(domain, user, password ?? string.Empty);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                client.Disconnect();
                forwarder?.Dispose();
                throw new SmbStatusException(status, $"{connection.Host}: login failed ({status}).");
            }
            return new SmbSession(client, forwarder) { ShowHiddenShares = connection.SmbShowHiddenShares };
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override bool IsAlive(SmbSession client) => client.Client.IsConnected;

    protected override ValueTask CloseAsync(SmbSession client)
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override bool IsAuthenticationFailure(Exception ex) => ex is SmbStatusException
    {
        Status: NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD or NTStatus.STATUS_ACCOUNT_RESTRICTION
            or NTStatus.STATUS_PASSWORD_EXPIRED or NTStatus.STATUS_PASSWORD_MUST_CHANGE,
    };

    protected override bool IsConnectionFailure(Exception ex) => ex is SocketException or ObjectDisposedException
        || ex is IOException and not FileNotFoundException and not SmbStatusException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        SmbStatusException s => Error(s.Status, path),
        SocketException => new IOException($"SMB: {ex.Message}", ex),
        _ => null,
    };

    private static Exception Error(NTStatus status, string path) => status switch
    {
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE or NTStatus.STATUS_BAD_NETWORK_NAME
            or NTStatus.STATUS_NOT_FOUND => new FileNotFoundException($"Not found: {path}", path),
        NTStatus.STATUS_ACCESS_DENIED => new UnauthorizedAccessException($"Access denied: {path}"),
        NTStatus.STATUS_OBJECT_NAME_COLLISION => new IOException($"Already exists: {path}"),
        NTStatus.STATUS_DIRECTORY_NOT_EMPTY => new IOException($"Folder is not empty: {path}"),
        NTStatus.STATUS_SHARING_VIOLATION => new IOException($"File in use: {path}"),
        NTStatus.STATUS_DISK_FULL => new IOException($"Disk full: {path}"),
        _ => new SmbStatusException(status, $"SMB: {path}: {status}"),
    };

    private static void Check(NTStatus status, string path)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Error(status, path);
        }
    }

    /// <summary>"/share/a/b.txt" → ("share", @"a\b.txt").</summary>
    public static (string Share, string Name) Split(string path)
    {
        var (share, name) = S3FileSystemProvider.Split(path);
        return (share, name.Replace('/', '\\').TrimEnd('\\'));
    }

    private static ISMBFileStore Share(SmbSession session, string share, string path)
    {
        if (session.Shares.TryGetValue(share, out var store))
        {
            return store;
        }
        store = session.Client.TreeConnect(share, out var status);
        Check(status, path);
        session.Shares[share] = store;
        return store;
    }

    private static object Open(ISMBFileStore store, string name, AccessMask access, CreateDisposition disposition, CreateOptions options, string path,
        FileAttributes attributes = FileAttributes.Normal)
    {
        var status = store.CreateFile(out var handle, out _, name, access | AccessMask.SYNCHRONIZE, attributes,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, disposition, options | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        Check(status, path);
        return handle;
    }

    private static DateTime Local(DateTime? time) => time is { } t && t.Year > 1601 ? t.ToLocalTime() : DateTime.MinValue;

    private static EntryAttributes AttributesOf(FileAttributes attributes) =>
        (attributes.HasFlag(FileAttributes.ReadOnly) ? EntryAttributes.ReadOnly : 0)
        | (attributes.HasFlag(FileAttributes.Hidden) ? EntryAttributes.Hidden : 0)
        | (attributes.HasFlag(FileAttributes.System) ? EntryAttributes.System : 0)
        | (attributes.HasFlag(FileAttributes.Archive) ? EntryAttributes.Archive : 0);

    // ---------------------------------------------------------------- file system

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, session => Task.Run<IReadOnlyList<VfsEntry>>(() =>
        {
            var (share, name) = Split(directory.Path);
            var result = new List<VfsEntry>();
            if (share.Length == 0)
            {
                var shares = session.Client.ListShares(out var status);
                Check(status, "/");
                foreach (var s in shares.Where(s => !s.EndsWith('$') || (session.ShowHiddenShares && !s.Equals("IPC$", StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(ToEntry(directory, "/" + s, s, isDirectory: true, isLink: false, 0, DateTime.MinValue));
                }
                return result;
            }
            var store = Share(session, share, directory.Path);
            var handle = Open(store, name, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, directory.Path);
            try
            {
                var status = store.QueryDirectory(out var items, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (status is not NTStatus.STATUS_SUCCESS and not NTStatus.STATUS_NO_MORE_FILES)
                {
                    Check(status, directory.Path);
                }
                var parent = directory.Path.TrimEnd('/');
                foreach (var item in items.OfType<FileDirectoryInformation>())
                {
                    if (item.FileName is "." or "..")
                    {
                        continue;
                    }
                    var isDirectory = item.FileAttributes.HasFlag(FileAttributes.Directory);
                    var entry = ToEntry(directory, parent + "/" + item.FileName, item.FileName, isDirectory, isLink: item.FileAttributes.HasFlag(FileAttributes.ReparsePoint),
                        isDirectory ? 0 : item.EndOfFile, Local(item.LastWriteTime));
                    result.Add(entry with { CreationTime = Local(item.CreationTime), Attributes = AttributesOf(item.FileAttributes) });
                }
            }
            finally
            {
                store.CloseFile(handle);
            }
            return result;
        }, cancellationToken), cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (share.Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        try
        {
            return await UseAsync(path, session => Task.Run(() =>
            {
                var store = Share(session, share, path.Path);
                if (name.Length == 0)
                {
                    return ToEntry(path, path.Path, share, isDirectory: true, isLink: false, 0, DateTime.MinValue);
                }
                var handle = Open(store, name, (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES, CreateDisposition.FILE_OPEN, 0, path.Path);
                try
                {
                    Check(store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation), path.Path);
                    var info = (FileNetworkOpenInformation)information;
                    var isDirectory = info.FileAttributes.HasFlag(FileAttributes.Directory);
                    return ToEntry(path, path.Path, path.Name, isDirectory, isLink: false, isDirectory ? 0 : info.EndOfFile, Local(info.LastWriteTime))
                        with { CreationTime = Local(info.CreationTime), Attributes = AttributesOf(info.FileAttributes) };
                }
                finally
                {
                    store.CloseFile(handle);
                }
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, session => Task.Run<Stream>(() =>
        {
            var (share, name) = Split(path.Path);
            var store = Share(session, share, path.Path);
            var handle = Open(store, name, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, path.Path);
            return new SmbFileStream(store, handle, (int)Math.Min(session.Client.MaxReadSize, 1024 * 1024), writing: false, path.Path);
        }, cancellationToken), afterClose: null, cancellationToken);

    public override Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, session => Task.Run<Stream>(() =>
        {
            var (share, name) = Split(path.Path);
            if (share.Length == 0 || name.Length == 0)
            {
                throw new IOException("SMB: files are written into a share.");
            }
            var store = Share(session, share, path.Path);
            var handle = Open(store, name, AccessMask.GENERIC_WRITE, overwrite ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_NON_DIRECTORY_FILE, path.Path);
            return new SmbFileStream(store, handle, (int)Math.Min(session.Client.MaxWriteSize, 1024 * 1024), writing: true, path.Path);
        }, cancellationToken), afterClose: null, cancellationToken);

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (share.Length == 0 || name.Length == 0)
        {
            return; // shares are made on the server
        }
        await UseAsync(path, session => Task.Run(() =>
        {
            var store = Share(session, share, path.Path);
            // the chain a\b\c, each part when it is missing
            var parts = name.Split('\\');
            for (var i = 1; i <= parts.Length; i++)
            {
                var part = string.Join('\\', parts[..i]);
                // open, else create (some servers fail FILE_OPEN_IF on an existing folder)
                var options = CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT;
                var access = AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE;
                var status = store.CreateFile(out var handle, out _, part, access, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
                    CreateDisposition.FILE_OPEN, options, null);
                if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE)
                {
                    status = store.CreateFile(out handle, out _, part, access, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
                        CreateDisposition.FILE_CREATE, options, null);
                }
                if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                {
                    continue; // made by someone else meanwhile
                }
                Check(status, path.Path);
                store.CloseFile(handle);
            }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var (share, name) = Split(path.Path);
        if (name.Length == 0)
        {
            throw new UnauthorizedAccessException("SMB: shares are deleted on the server.");
        }
        if (entry.IsDirectory)
        {
            var children = await ListAsync(path, cancellationToken).ConfigureAwait(false);
            if (children.Count > 0 && !recursive)
            {
                throw new IOException($"Folder is not empty: {path.Name}");
            }
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteAsync(child.Path, recursive: true, cancellationToken).ConfigureAwait(false);
            }
        }
        await UseAsync(path, session => Task.Run(() =>
        {
            var store = Share(session, share, path.Path);
            var handle = Open(store, name, AccessMask.DELETE, CreateDisposition.FILE_OPEN,
                entry.IsDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE, path.Path);
            try
            {
                Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), path.Path);
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        var (fromShare, fromName) = Split(source.Path);
        var (toShare, toName) = Split(destination.Path);
        if (!CanHandle(destination) || source.Authority != destination.Authority || !string.Equals(fromShare, toShare, StringComparison.OrdinalIgnoreCase))
        {
            throw new CrossDeviceMoveException("Moving between shares is done by copying.");
        }
        if (fromName.Length == 0 || toName.Length == 0)
        {
            throw new IOException("SMB: shares cannot be renamed.");
        }
        await UseAsync(source, session => Task.Run(() =>
        {
            var store = Share(session, fromShare, source.Path);
            var handle = Open(store, fromName, AccessMask.DELETE, CreateDisposition.FILE_OPEN, 0, source.Path);
            try
            {
                Check(store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = toName, ReplaceIfExists = overwrite }), destination.Path);
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (name.Length == 0 || (creationTime is null && lastWriteTime is null))
        {
            return;
        }
        await UseAsync(path, session => Task.Run(() =>
        {
            var store = Share(session, share, path.Path);
            var handle = Open(store, name, (AccessMask)FileAccessMask.FILE_WRITE_ATTRIBUTES, CreateDisposition.FILE_OPEN, 0, path.Path);
            try
            {
                var information = new FileBasicInformation
                {
                    CreationTime = new SetFileTime(creationTime?.ToUniversalTime()),
                    LastWriteTime = new SetFileTime(lastWriteTime?.ToUniversalTime()),
                    LastAccessTime = new SetFileTime(null),
                    // the change time with it: some servers (impacket, older Samba builds) take the modification time from it
                    ChangeTime = new SetFileTime(lastWriteTime?.ToUniversalTime()),
                };
                Check(store.SetFileInformation(handle, information), path.Path);
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file on a share read or written in pieces of at most the server's read / write size.</summary>
    private sealed class SmbFileStream(ISMBFileStore store, object handle, int chunk, bool writing, string path) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => !writing;

        public override bool CanSeek => false;

        public override bool CanWrite => writing;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var status = store.ReadFile(out var data, handle, _position, Math.Min(count, chunk));
            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                return 0;
            }
            Check(status, path);
            data.CopyTo(buffer, offset);
            _position += data.Length;
            return data.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[Math.Min(buffer.Length, chunk)];
            var read = await Task.Run(() => Read(array, 0, array.Length), cancellationToken).ConfigureAwait(false);
            array.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var piece = buffer.AsSpan(offset, Math.Min(count, chunk)).ToArray();
                Check(store.WriteFile(out var written, handle, _position, piece), path);
                if (written <= 0)
                {
                    throw new IOException($"SMB: could not write: {path}");
                }
                _position += written;
                offset += written;
                count -= written;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Write(buffer, offset, count), cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = buffer.ToArray();
            return new ValueTask(Task.Run(() => Write(array, 0, array.Length), cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                store.CloseFile(handle);
            }
            base.Dispose(disposing);
        }
    }
}
