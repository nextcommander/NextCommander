using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>
/// PC link (<c>link://computer/share/folder</c>): the folders another NextCommander shares, over a direct cable (USB or
/// Ethernet: the operating system sees a network interface) or the network. <c>link:///</c> lists the computers that
/// answer the probe of <see cref="LinkDiscovery"/>. Connections are TLS with this installation's identity; the first
/// time, both screens show the same pairing code and the user accepts the other computer's fingerprint (stored like a
/// server key); the other computer's user allows this one.
/// </summary>
public sealed class LinkFileSystemProvider : RemoteProviderBase<LinkClient>, IUploadTimeSupport, IVolumeSpaceProvider
{
    public const string LinkScheme = "link";

    /// <summary>The computers found: <c>link:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(LinkScheme, string.Empty, "/");

    private readonly LinkDiscovery _discovery;
    private readonly Func<CancellationToken, Task<X509Certificate2>> _identity;
    private readonly Func<string> _name;
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

    /// <param name="hooks">Fingerprint confirmation (the pairing code) and the known fingerprints.</param>
    /// <param name="discovery">Finds the computers and their addresses by name.</param>
    /// <param name="identity">This installation's certificate (created on first use).</param>
    /// <param name="name">The name this computer gives itself.</param>
    public LinkFileSystemProvider(RemoteHooks hooks, LinkDiscovery discovery, Func<CancellationToken, Task<X509Certificate2>> identity, Func<string> name)
        : base(hooks)
    {
        _discovery = discovery;
        _identity = identity;
        _name = name;
    }

    public override string Scheme => LinkScheme;

    /// <summary>How long a connection may take before the other computer counts as unreachable.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the computers are waited for in <c>link:///</c>.</summary>
    public TimeSpan ProbeTime { get; set; } = TimeSpan.FromSeconds(1.5);

    public LinkDiscovery Discovery => _discovery;

    /// <summary>The path of a computer found by the probe.</summary>
    public static VfsPath ComputerRoot(string name) => VfsPath.Create(LinkScheme, name, "/");

    protected override bool NeedsPassword(RemoteConnection connection) => false;

    protected override async Task<LinkClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        var endPoints = await EndPointsAsync(connection, cancellationToken).ConfigureAwait(false);
        var identity = await _identity(cancellationToken).ConfigureAwait(false);
        return await LinkClient.ConnectAsync(endPoints, connection.Host, identity, _name(), trustedFingerprint, ConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a computer is: the address of its last answer to the probe (probed again when unknown), else the host as
    /// an address or a name the network resolves.
    /// </summary>
    private async Task<IReadOnlyList<IPEndPoint>> EndPointsAsync(RemoteConnection connection, CancellationToken cancellationToken)
    {
        var host = connection.Host;
        if (IPAddress.TryParse(host, out var literal))
        {
            return [new IPEndPoint(literal, connection.EffectivePort)];
        }
        if (_discovery.Find(host) is null)
        {
            await _discovery.ProbeAsync(ProbeTime, cancellationToken).ConfigureAwait(false);
        }
        if (_discovery.Find(host) is { } peer)
        {
            return [peer.EndPoint];
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length > 0)
            {
                return [.. addresses.Select(a => new IPEndPoint(a, connection.EffectivePort))];
            }
        }
        catch (SocketException)
        {
            // not a name of the network either
        }
        throw new IOException($"{host}: computer not found. Is PC link sharing turned on there, and are both computers connected by the cable?");
    }

    protected override bool IsAlive(LinkClient client) => client.IsAlive;

    protected override ValueTask CloseAsync(LinkClient client) => client.DisposeAsync();

    protected override bool IsAuthenticationFailure(Exception ex) => false;

    protected override bool IsConnectionFailure(Exception ex) =>
        ex is SocketException or EndOfStreamException or InvalidDataException or AuthenticationException or ObjectDisposedException
        || (ex is IOException && ex.InnerException is SocketException);

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        // the other side's errors are already file errors
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or CrossDeviceMoveException or NotSupportedException => null,
        IOException when ex.InnerException is null => null,
        SocketException or EndOfStreamException or AuthenticationException or InvalidDataException or ObjectDisposedException or IOException =>
            new IOException($"PC link: the connection was lost ({ex.Message}).", ex),
        _ => null,
    };

    private static string RemotePath(VfsPath path) => path.Path.Length == 0 ? "/" : path.Path;

    private static void ThrowIfRoot(VfsPath path)
    {
        if (path.Authority.Length == 0)
        {
            throw new UnauthorizedAccessException("PC link: choose a computer first.");
        }
    }

    public override async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (directory.Authority.Length == 0)
        {
            // the computers found on the cables and networks
            var peers = await _discovery.ProbeAsync(ProbeTime, cancellationToken).ConfigureAwait(false);
            return [.. peers.Select(p => new VfsEntry { Name = p.Name, Path = ComputerRoot(p.Name), IsDirectory = true, LastWriteTime = p.Seen })];
        }
        return await UseAsync(directory, async client =>
        {
            var answer = await client.RequestAsync(new JsonObject { ["op"] = "list", ["path"] = RemotePath(directory) }, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<VfsEntry>)[.. (answer["entries"] as JsonArray ?? []).OfType<JsonObject>().Select(e => LinkProtocol.FromJson(e, directory))];
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "PC link", Path = Root, IsDirectory = true };
        }
        if (path.IsRoot)
        {
            // the computer itself: its folders are asked for when it is listed
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true };
        }
        return await UseAsync(path, async client =>
        {
            var answer = await client.RequestAsync(new JsonObject { ["op"] = "stat", ["path"] = RemotePath(path) }, cancellationToken).ConfigureAwait(false);
            return answer["entry"] is JsonObject entry ? LinkProtocol.FromJson(entry, path.Parent ?? path) with { Path = path } : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        ThrowIfRoot(path);
        return OpenStreamAsync(path, client => client.OpenReadAsync(RemotePath(path), cancellationToken), null, cancellationToken);
    }

    public override Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        ThrowIfRoot(path);
        DateTime? time = _uploadTimes.TryRemove(path, out var t) ? t : null;
        return OpenStreamAsync(path, client => client.OpenWriteAsync(RemotePath(path), overwrite, time, cancellationToken), null, cancellationToken);
    }

    /// <summary>The time is sent with the file (the other side sets it when the file is complete).</summary>
    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        Simple(path, new JsonObject { ["op"] = "mkdir" }, cancellationToken);

    public override Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) =>
        Simple(path, new JsonObject { ["op"] = "delete", ["recursive"] = recursive }, cancellationToken);

    public override Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Authority, destination.Authority, StringComparison.OrdinalIgnoreCase) || destination.Scheme != Scheme)
        {
            throw new CrossDeviceMoveException("Another computer: copied and deleted.");
        }
        return Simple(source, new JsonObject { ["op"] = "move", ["to"] = RemotePath(destination), ["overwrite"] = overwrite }, cancellationToken);
    }

    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["op"] = "settimes" };
        if (creationTime is { } c)
        {
            request["c"] = LinkProtocol.Ticks(c);
        }
        if (lastWriteTime is { } m)
        {
            request["m"] = LinkProtocol.Ticks(m);
        }
        return Simple(path, request, cancellationToken);
    }

    private Task Simple(VfsPath path, JsonObject request, CancellationToken cancellationToken)
    {
        ThrowIfRoot(path);
        request["path"] = RemotePath(path);
        return UseAsync(path, client => client.RequestAsync(request, cancellationToken), cancellationToken);
    }

    /// <summary>The free space of the disk a shared folder is on, in the panel's footer.</summary>
    public async Task<VolumeSpace?> GetSpaceAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0 || path.IsRoot)
        {
            return null;
        }
        var answer = await UseAsync(path, client => client.RequestAsync(new JsonObject { ["op"] = "space", ["path"] = RemotePath(path) }, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return answer["total"]?.GetValue<long>() is { } total && answer["free"]?.GetValue<long>() is { } free
            ? new VolumeSpace(answer["label"]?.GetValue<string>(), total, free)
            : null;
    }
}
