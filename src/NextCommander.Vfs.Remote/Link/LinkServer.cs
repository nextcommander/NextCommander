using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>A computer asking to connect: its name, certificate fingerprint and the pairing code both screens show.</summary>
public sealed record LinkApprovalRequest(string Name, string Fingerprint, string Code, IPEndPoint From);

/// <summary>What the sharing side needs: its identity and name, the shared folders, who may connect.</summary>
public sealed class LinkServerOptions
{
    public required X509Certificate2 Identity { get; init; }

    public required string Name { get; init; }

    /// <summary>TCP port (0: any free one, for tests).</summary>
    public int Port { get; init; } = AppSettings.DefaultLinkPort;

    /// <summary>Only the loopback address (tests).</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>The shared folders, read again for every request (a change in the settings applies at once).</summary>
    public required Func<IReadOnlyList<LinkShare>> Shares { get; init; }

    /// <summary>Whether a fingerprint was allowed before (no question then).</summary>
    public required Func<string, bool> IsTrusted { get; init; }

    /// <summary>Asks this computer's user about an unknown computer (true: allowed; remembering it is the caller's part).</summary>
    public required Func<LinkApprovalRequest, Task<bool>> Approve { get; init; }

    /// <summary>The local file system the shares are on.</summary>
    public required IFileSystemProvider Local { get; init; }

    /// <summary>A line for the program log.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The sharing side of PC link: a TLS server on a TCP port that offers the shared folders of the settings to other
/// NextCommanders. Paths of the other side are "/share/rest"; nothing outside a shared folder is reachable ("..", other
/// roots and links out are refused), read-only shares refuse changes. One task per connection.
/// </summary>
public sealed class LinkServer : IDisposable
{
    private readonly LinkServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _approvals = new(1, 1);
    private readonly string _fingerprint;
    private int _connections;

    public LinkServer(LinkServerOptions options)
    {
        _options = options;
        _fingerprint = LinkProtocol.Fingerprint(options.Identity);
        if (options.LoopbackOnly)
        {
            _listener = new TcpListener(IPAddress.Loopback, options.Port);
        }
        else
        {
            _listener = new TcpListener(IPAddress.IPv6Any, options.Port);
            _listener.Server.DualMode = true; // IPv4 (169.254.x.x on a direct cable) and IPv6 (fe80::)
        }
    }

    /// <summary>The port the server listens on.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Computers connected now (connections, several per computer).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Starts listening; an <see cref="IOException"/> when the port is in use.</summary>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            throw new IOException($"PC link: port {_options.Port} cannot be opened ({ex.Message}).", ex);
        }
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(tcp));
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        Interlocked.Increment(ref _connections);
        var from = tcp.Client.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        try
        {
            using (tcp)
            {
                tcp.NoDelay = true;
                X509Certificate? clientCertificate = null;
                await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(20));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _options.Identity,
                        ClientCertificateRequired = true,
                        // self-signed on both sides: the fingerprint and the pairing code decide (in HelloAsync)
                        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        {
                            clientCertificate = certificate;
                            return certificate is not null;
                        },
                    }, handshake.Token).ConfigureAwait(false);
                }
                if (clientCertificate is null || !await HelloAsync(ssl, LinkProtocol.Fingerprint(clientCertificate), from).ConfigureAwait(false))
                {
                    return;
                }
                await RequestLoopAsync(ssl).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or AuthenticationException
            or OperationCanceledException or ObjectDisposedException or SocketException or System.Text.Json.JsonException)
        {
            // the other side went away or spoke something else
            _options.Log?.Invoke($"PC link: connection from {from} ended: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    /// <summary>The client's hello: a known computer goes on at once, an unknown one after this computer's user allowed it.</summary>
    private async Task<bool> HelloAsync(SslStream ssl, string clientFingerprint, IPEndPoint from)
    {
        var hello = await LinkProtocol.ReadJsonAsync(ssl, _stop.Token).ConfigureAwait(false);
        var name = hello["name"]?.GetValue<string>() is { Length: > 0 } n ? n : from.Address.ToString();
        if (hello["op"]?.GetValue<string>() != "hello")
        {
            return false;
        }
        if (!_options.IsTrusted(clientFingerprint))
        {
            await LinkProtocol.WriteJsonAsync(ssl, new JsonObject { ["pending"] = true }, _stop.Token).ConfigureAwait(false);
            bool allowed;
            await _approvals.WaitAsync(_stop.Token).ConfigureAwait(false); // one question at a time
            try
            {
                // a second connection of a computer just allowed is not asked again
                allowed = _options.IsTrusted(clientFingerprint)
                    || await _options.Approve(new LinkApprovalRequest(name, clientFingerprint, LinkProtocol.PairingCode(clientFingerprint, _fingerprint), from)).ConfigureAwait(false);
            }
            finally
            {
                _approvals.Release();
            }
            if (!allowed)
            {
                _options.Log?.Invoke($"PC link: {name} ({from}) was not allowed.");
                await LinkProtocol.WriteJsonAsync(ssl, LinkProtocol.Error(new UnauthorizedAccessException($"{_options.Name} did not allow this computer to connect.")), _stop.Token)
                    .ConfigureAwait(false);
                return false;
            }
        }
        _options.Log?.Invoke($"PC link: {name} ({from}) connected.");
        var ok = LinkProtocol.Ok();
        ok["name"] = _options.Name;
        ok["version"] = LinkProtocol.Version;
        await LinkProtocol.WriteJsonAsync(ssl, ok, _stop.Token).ConfigureAwait(false);
        return true;
    }

    private async Task RequestLoopAsync(SslStream ssl)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            JsonObject request;
            try
            {
                request = await LinkProtocol.ReadJsonAsync(ssl, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return; // the other side closed the connection
            }
            var op = request["op"]?.GetValue<string>();
            var path = request["path"]?.GetValue<string>() ?? "/";
            switch (op)
            {
                case "read":
                    await ReadAsync(ssl, path, ct).ConfigureAwait(false);
                    continue;
                case "write":
                    await WriteAsync(ssl, path, request["overwrite"]?.GetValue<bool>() == true, request["m"]?.GetValue<long>(), ct).ConfigureAwait(false);
                    continue;
            }
            JsonObject answer;
            try
            {
                answer = op switch
                {
                    "list" => await ListAsync(path, ct).ConfigureAwait(false),
                    "stat" => await StatAsync(path, ct).ConfigureAwait(false),
                    "mkdir" => await MakeDirectoryAsync(path, ct).ConfigureAwait(false),
                    "delete" => await DeleteAsync(path, request["recursive"]?.GetValue<bool>() == true, ct).ConfigureAwait(false),
                    "move" => await MoveAsync(path, request["to"]?.GetValue<string>() ?? throw new IOException("No target."),
                        request["overwrite"]?.GetValue<bool>() == true, ct).ConfigureAwait(false),
                    "settimes" => await SetTimesAsync(path, request["c"]?.GetValue<long>(), request["m"]?.GetValue<long>(), ct).ConfigureAwait(false),
                    "space" => Space(path),
                    _ => throw new NotSupportedException($"PC link: unknown request '{op}'."),
                };
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                answer = LinkProtocol.Error(ex);
            }
            await LinkProtocol.WriteJsonAsync(ssl, answer, ct).ConfigureAwait(false);
        }
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException;

    // ---------------------------------------------------------------- paths

    /// <summary>A path of the other side: the share root ("/"), or a share and the local path inside it.</summary>
    private readonly record struct Target(LinkShare? Share, string? Local)
    {
        public bool IsRoot => Share is null;

        public bool IsShareRoot => Share is not null && Local is not null && string.Equals(Local, FullPath(Share.Path), PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Maps "/share/a/b" to the local path; anything that would leave the shared folder is refused as not found.</summary>
    private Target Resolve(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new Target(null, null);
        }
        var share = _options.Shares().FirstOrDefault(s => string.Equals(s.Name, parts[0], StringComparison.OrdinalIgnoreCase) && s.Problem() is null)
            ?? throw new DirectoryNotFoundException($"No shared folder named {parts[0]}.");
        var root = FullPath(share.Path);
        foreach (var part in parts.Skip(1))
        {
            if (part is "." or ".." || part.IndexOfAny(['\\', ':', '\0']) >= 0)
            {
                throw new UnauthorizedAccessException($"Not allowed: {path}");
            }
        }
        var local = parts.Length == 1 ? root : FullPath(Path.Combine([root, .. parts.Skip(1)]));
        if (!IsInside(local, root) || LeadsOut(local, root))
        {
            throw new UnauthorizedAccessException($"Not allowed: {path}");
        }
        return new Target(share, local);
    }

    private static bool IsInside(string path, string root) =>
        string.Equals(path, root, PathComparison)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>
    /// Whether a symbolic link or junction on the way from the shared folder to <paramref name="local"/> points outside
    /// the shared folder (a link inside it to elsewhere would open the rest of the disk).
    /// </summary>
    private static bool LeadsOut(string local, string root)
    {
        for (var path = local; path is not null && IsInside(path, root) && !string.Equals(path, root, PathComparison); path = Path.GetDirectoryName(path))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (resolved is null || !IsInside(FullPath(resolved), root))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>A change in a share: refused for read-only shares and for the shared folder itself.</summary>
    private Target Writable(string path, bool allowShareRoot = false)
    {
        var target = Resolve(path);
        if (target.IsRoot || (target.IsShareRoot && !allowShareRoot))
        {
            throw new UnauthorizedAccessException("The shared folder itself cannot be changed.");
        }
        if (target.Share!.ReadOnly)
        {
            throw new UnauthorizedAccessException($"{target.Share.Name} is shared read-only.");
        }
        return target;
    }

    // ---------------------------------------------------------------- requests

    private async Task<JsonObject> ListAsync(string path, CancellationToken ct)
    {
        var target = Resolve(path);
        var entries = new JsonArray();
        if (target.IsRoot)
        {
            foreach (var share in _options.Shares().Where(s => s.Problem() is null && Directory.Exists(s.Path)))
            {
                entries.Add(LinkProtocol.ToJson(ShareEntry(share)));
            }
        }
        else
        {
            foreach (var entry in await _options.Local.ListAsync(VfsPath.Local(target.Local!), ct).ConfigureAwait(false))
            {
                entries.Add(LinkProtocol.ToJson(entry));
            }
        }
        var answer = LinkProtocol.Ok();
        answer["entries"] = entries;
        return answer;
    }

    private static VfsEntry ShareEntry(LinkShare share) => new()
    {
        Name = share.Name,
        Path = VfsPath.Local(share.Path),
        IsDirectory = true,
        LastWriteTime = Directory.GetLastWriteTime(share.Path),
        Attributes = share.ReadOnly ? EntryAttributes.ReadOnly : EntryAttributes.None,
    };

    private async Task<JsonObject> StatAsync(string path, CancellationToken ct)
    {
        var target = Resolve(path);
        var answer = LinkProtocol.Ok();
        if (target.IsRoot)
        {
            answer["entry"] = LinkProtocol.ToJson(new VfsEntry { Name = _options.Name, Path = VfsPath.Local("/"), IsDirectory = true });
        }
        else if (target.IsShareRoot)
        {
            answer["entry"] = Directory.Exists(target.Local) ? LinkProtocol.ToJson(ShareEntry(target.Share!)) : null;
        }
        else if (await _options.Local.GetEntryAsync(VfsPath.Local(target.Local!), ct).ConfigureAwait(false) is { } entry)
        {
            answer["entry"] = LinkProtocol.ToJson(entry);
        }
        return answer;
    }

    private async Task<JsonObject> MakeDirectoryAsync(string path, CancellationToken ct)
    {
        var target = Writable(path);
        await _options.Local.CreateDirectoryAsync(VfsPath.Local(target.Local!), ct).ConfigureAwait(false);
        return LinkProtocol.Ok();
    }

    private async Task<JsonObject> DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        var target = Writable(path);
        await _options.Local.DeleteAsync(VfsPath.Local(target.Local!), recursive, ct).ConfigureAwait(false);
        return LinkProtocol.Ok();
    }

    private async Task<JsonObject> MoveAsync(string path, string to, bool overwrite, CancellationToken ct)
    {
        var source = Writable(path);
        var target = Writable(to);
        await _options.Local.MoveAsync(VfsPath.Local(source.Local!), VfsPath.Local(target.Local!), overwrite, ct).ConfigureAwait(false);
        return LinkProtocol.Ok();
    }

    private async Task<JsonObject> SetTimesAsync(string path, long? created, long? modified, CancellationToken ct)
    {
        var target = Writable(path);
        await _options.Local.SetTimesAsync(VfsPath.Local(target.Local!), created is { } c ? LinkProtocol.FromTicks(c) : null,
            modified is { } m ? LinkProtocol.FromTicks(m) : null, ct).ConfigureAwait(false);
        return LinkProtocol.Ok();
    }

    private JsonObject Space(string path)
    {
        var target = Resolve(path);
        var answer = LinkProtocol.Ok();
        if (!target.IsRoot && Path.GetPathRoot(target.Local) is { Length: > 0 } root)
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                answer["label"] = target.Share!.Name;
                answer["total"] = drive.TotalSize;
                answer["free"] = drive.AvailableFreeSpace;
            }
        }
        return answer;
    }

    /// <summary>A file to the other side: "ok" with its size, data frames, an empty frame (or an error frame half-way).</summary>
    private async Task ReadAsync(SslStream ssl, string path, CancellationToken ct)
    {
        Stream source;
        long size;
        try
        {
            var target = Resolve(path);
            if (target.IsRoot || target.IsShareRoot)
            {
                throw new UnauthorizedAccessException("A folder cannot be read as a file.");
            }
            var local = VfsPath.Local(target.Local!);
            size = (await _options.Local.GetEntryAsync(local, ct).ConfigureAwait(false))?.Size ?? -1;
            source = await _options.Local.OpenReadAsync(local, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            await LinkProtocol.WriteJsonAsync(ssl, LinkProtocol.Error(ex), ct).ConfigureAwait(false);
            return;
        }
        await using (source.ConfigureAwait(false))
        {
            var ok = LinkProtocol.Ok();
            ok["size"] = size;
            await LinkProtocol.WriteJsonAsync(ssl, ok, ct).ConfigureAwait(false);
            var buffer = new byte[LinkProtocol.ChunkSize];
            while (true)
            {
                int n;
                try
                {
                    n = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    await LinkProtocol.WriteJsonAsync(ssl, LinkProtocol.Error(ex), ct).ConfigureAwait(false);
                    return;
                }
                await LinkProtocol.WriteDataAsync(ssl, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }
            }
        }
    }

    /// <summary>A file from the other side: "ok" (or the error), its data frames, then the answer after it was written.</summary>
    private async Task WriteAsync(SslStream ssl, string path, bool overwrite, long? modified, CancellationToken ct)
    {
        Stream target;
        VfsPath local;
        try
        {
            local = VfsPath.Local(Writable(path).Local!);
            if (!overwrite && await _options.Local.GetEntryAsync(local, ct).ConfigureAwait(false) is not null)
            {
                throw new IOException($"Already exists: {local.Name}");
            }
            target = await _options.Local.OpenWriteAsync(local, overwrite, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            await LinkProtocol.WriteJsonAsync(ssl, LinkProtocol.Error(ex), ct).ConfigureAwait(false);
            return;
        }
        await LinkProtocol.WriteJsonAsync(ssl, LinkProtocol.Ok(), ct).ConfigureAwait(false);
        Exception? failure = null;
        var buffer = new byte[LinkProtocol.ChunkSize];
        await using (target.ConfigureAwait(false))
        {
            while (true)
            {
                var (kind, length) = await LinkProtocol.ReadHeaderAsync(ssl, ct).ConfigureAwait(false);
                if (kind != LinkProtocol.DataFrame)
                {
                    throw new InvalidDataException("PC link: data was expected.");
                }
                if (length == 0)
                {
                    break;
                }
                await ssl.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                if (failure is null)
                {
                    try
                    {
                        await target.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsFileError(ex))
                    {
                        failure = ex; // the rest is read and dropped, so the connection stays in step
                    }
                }
            }
        }
        if (failure is null && modified is { } m)
        {
            try
            {
                await _options.Local.SetTimesAsync(local, null, LinkProtocol.FromTicks(m), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // the contents arrived; a time that cannot be set is not worth an error
            }
        }
        await LinkProtocol.WriteJsonAsync(ssl, failure is null ? LinkProtocol.Ok() : LinkProtocol.Error(failure), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        _approvals.Dispose();
    }
}
