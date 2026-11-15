using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>A computer that answered: its name, where to connect and its certificate's fingerprint.</summary>
public sealed record LinkPeerInfo(string Name, IPEndPoint EndPoint, string Fingerprint, DateTime Seen);

/// <summary>
/// Finding the computers that share over PC link: a UDP probe to the broadcast address of every network interface —
/// also the link-local ones a direct cable gets (169.254.x.x, fe80::) — and the answers of their
/// <see cref="LinkResponder"/>. The last answers are kept, so <c>link://name/</c> finds its computer again.
/// </summary>
public sealed class LinkDiscovery
{
    /// <summary>The probe; the answer is JSON (<see cref="LinkResponder"/>).</summary>
    public static readonly byte[] Probe = Encoding.ASCII.GetBytes("NCLINK?1");

    private readonly Func<int> _port;
    private readonly ConcurrentDictionary<string, LinkPeerInfo> _peers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="port">The UDP port of the responders (the PC link port of the settings).</param>
    public LinkDiscovery(Func<int> port) => _port = port;

    /// <summary>The fingerprint of this computer: its own answer (when it shares too) is left out.</summary>
    public Func<string?>? OwnFingerprint { get; set; }

    /// <summary>Tests: the addresses probed instead of the broadcast addresses of the interfaces.</summary>
    public IReadOnlyList<IPEndPoint>? Targets { get; set; }

    /// <summary>The computers that answered last time.</summary>
    public IReadOnlyList<LinkPeerInfo> Known => [.. _peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>A computer by the name in its path (as answered before), or null.</summary>
    public LinkPeerInfo? Find(string name) => _peers.TryGetValue(name, out var peer) ? peer : null;

    /// <summary>Probes and waits for answers; returns the computers that answered (the known list is renewed).</summary>
    public async Task<IReadOnlyList<LinkPeerInfo>> ProbeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var port = _port();
        var found = new ConcurrentDictionary<string, LinkPeerInfo>(StringComparer.OrdinalIgnoreCase);
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var (local, targets) in Senders(port))
            {
                UdpClient udp;
                try
                {
                    udp = new UdpClient(local) { EnableBroadcast = local.AddressFamily == AddressFamily.InterNetwork };
                }
                catch (SocketException)
                {
                    continue; // an interface that went away
                }
                sockets.Add(udp);
                foreach (var target in targets)
                {
                    try
                    {
                        await udp.SendAsync(Probe, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // no route on this interface
                    }
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            await Task.WhenAll(sockets.Select(udp => ReceiveAsync(udp, found, timeout.Token))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var udp in sockets)
            {
                udp.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var peer in found.Values)
        {
            _peers[peer.Name] = peer;
        }
        return [.. found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task ReceiveAsync(UdpClient udp, ConcurrentDictionary<string, LinkPeerInfo> found, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            if (Parse(received.Buffer, received.RemoteEndPoint) is { } peer && peer.Fingerprint != OwnFingerprint?.Invoke())
            {
                // the first answer of a computer wins (it may answer on several interfaces)
                found.TryAdd(peer.Name, peer);
            }
        }
    }

    /// <summary>An answer of a responder, or null for anything else.</summary>
    public static LinkPeerInfo? Parse(byte[] data, IPEndPoint from)
    {
        try
        {
            if (JsonNode.Parse(data) is not JsonObject json || json["nclink"]?.GetValue<int>() is not >= 1)
            {
                return null;
            }
            var name = json["name"]?.GetValue<string>();
            var port = json["port"]?.GetValue<int>() ?? 0;
            var fingerprint = json["fp"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) || port is <= 0 or > 65535 || string.IsNullOrEmpty(fingerprint))
            {
                return null;
            }
            var address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
            return new LinkPeerInfo(LinkProtocol.SafeName(name), new IPEndPoint(address, port), fingerprint, DateTime.Now);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Per local address: the probe's targets (the interface's broadcast, all-nodes for IPv6 link-local).</summary>
    private List<(IPEndPoint Local, List<IPEndPoint> Targets)> Senders(int port)
    {
        if (Targets is { } fixedTargets)
        {
            return [.. fixedTargets.GroupBy(t => t.AddressFamily).Select(g =>
                (new IPEndPoint(g.Key == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0), g.ToList()))];
        }
        var senders = new List<(IPEndPoint, List<IPEndPoint>)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
                    if (DirectedBroadcast(address, unicast.IPv4Mask) is { } directed)
                    {
                        targets.Add(new IPEndPoint(directed, port));
                    }
                    senders.Add((new IPEndPoint(address, 0), targets));
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
                {
                    // all nodes of the link: a direct cable often has only an IPv6 link-local address (Linux, macOS)
                    senders.Add((new IPEndPoint(address, 0), [new IPEndPoint(new IPAddress(IPAddress.Parse("ff02::1").GetAddressBytes(), address.ScopeId), port)]));
                }
            }
        }
        return senders;
    }

    /// <summary>The broadcast address of an IPv4 network (address | ~mask); null without a usable mask.</summary>
    public static IPAddress? DirectedBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (m.All(b => b == 0))
        {
            return null;
        }
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            b[i] = (byte)(a[i] | ~m[i]);
        }
        return new IPAddress(b);
    }
}

/// <summary>
/// The sharing side of finding computers: answers PC link probes on the UDP port (IPv4 and IPv6) with this
/// computer's name, TCP port and fingerprint.
/// </summary>
public sealed class LinkResponder : IDisposable
{
    private readonly List<UdpClient> _sockets = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _answer;

    public LinkResponder(int port, string name, int tcpPort, string fingerprint, bool loopbackOnly = false)
    {
        _answer = Encoding.UTF8.GetBytes(new JsonObject { ["nclink"] = LinkProtocol.Version, ["name"] = name, ["port"] = tcpPort, ["fp"] = fingerprint }.ToJsonString());
        Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port));
        if (!loopbackOnly && Socket.OSSupportsIPv6)
        {
            Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        if (_sockets.Count == 0)
        {
            throw new IOException($"PC link: UDP port {port} cannot be opened.");
        }
        foreach (var udp in _sockets)
        {
            _ = Task.Run(() => AnswerLoopAsync(udp));
        }
    }

    /// <summary>The UDP port answered on (the first socket's; for tests with port 0).</summary>
    public int Port => ((IPEndPoint)_sockets[0].Client.LocalEndPoint!).Port;

    private void Bind(IPEndPoint endPoint)
    {
        var udp = new UdpClient(endPoint.AddressFamily);
        try
        {
            if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                udp.Client.DualMode = false; // IPv4 has its own socket
            }
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(endPoint);
            _sockets.Add(udp);
        }
        catch (SocketException)
        {
            udp.Dispose(); // e.g. no IPv6, or the port taken
        }
    }

    private async Task AnswerLoopAsync(UdpClient udp)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (received.Buffer.AsSpan().SequenceEqual(LinkDiscovery.Probe))
                {
                    await udp.SendAsync(_answer, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // e.g. a "port unreachable" of an earlier answer on Windows: go on
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var udp in _sockets)
        {
            udp.Dispose();
        }
        _stop.Dispose();
    }
}
