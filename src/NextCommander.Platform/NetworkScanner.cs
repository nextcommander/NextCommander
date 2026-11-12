using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Platform;

/// <summary>A service a machine on the network offers (what the connection manager can connect to).</summary>
public enum NetworkService
{
    Sftp,
    Ftp,
    Smb,
    Http,
    Https,
    WebDav,
}

/// <summary>A machine found on the local network.</summary>
public sealed class NetworkHost(IPAddress address)
{
    public IPAddress Address { get; } = address;

    /// <summary>Host name (mDNS / Bonjour name or reverse DNS); null when unknown.</summary>
    public string? Name { get; set; }

    public HashSet<NetworkService> Services { get; } = [];

    /// <summary>Port of a service when it is not the usual one (from an mDNS announcement).</summary>
    public Dictionary<NetworkService, int> Ports { get; } = [];

    /// <summary>What to connect to: the name when known, otherwise the address.</summary>
    public string Host => string.IsNullOrEmpty(Name) ? Address.ToString() : Name;

    public override string ToString() =>
        (string.IsNullOrEmpty(Name) ? Address.ToString() : $"{Name} ({Address})") + "  —  " + string.Join(", ", Services.Order().Select(Label));

    public static string Label(NetworkService service) => service switch
    {
        NetworkService.Sftp => "SFTP",
        NetworkService.Ftp => "FTP",
        NetworkService.Smb => "SMB",
        NetworkService.Http => "HTTP",
        NetworkService.Https => "HTTPS",
        _ => "WebDAV",
    };
}

/// <summary>
/// Finds machines on the local network for the connection manager: the neighbours the system knows (ARP), mDNS /
/// Bonjour announcements (NAS boxes, Macs, Linux with Avahi) and a short connection try on the ports of the services the
/// application can use (22 SFTP, 21 FTP, 445 SMB, 80 / 443 HTTP for WebDAV) in the subnets of the computer (at most the
/// /24 around each address). Runs only when the user asks for it.
/// </summary>
public static partial class NetworkScanner
{
    /// <summary>Ports tried on every address.</summary>
    public static readonly IReadOnlyList<(NetworkService Service, int Port)> Probes =
    [
        (NetworkService.Sftp, 22), (NetworkService.Ftp, 21), (NetworkService.Smb, 445), (NetworkService.Http, 80), (NetworkService.Https, 443),
    ];

    private static readonly string[] s_mdnsServices =
    [
        "_sftp-ssh._tcp.local", "_ssh._tcp.local", "_smb._tcp.local", "_ftp._tcp.local", "_webdav._tcp.local", "_webdavs._tcp.local",
    ];

    /// <summary>
    /// Scans; <paramref name="found"/> is called (on a worker thread) for every machine as soon as it is known and again
    /// when more is learnt about it. Returns all machines with at least one service, by address.
    /// </summary>
    public static async Task<IReadOnlyList<NetworkHost>> ScanAsync(Action<NetworkHost>? found, CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null)
    {
        var timeout = connectTimeout ?? TimeSpan.FromMilliseconds(400);
        var hosts = new ConcurrentDictionary<IPAddress, NetworkHost>();
        var own = LocalAddresses().Select(a => a.Address).ToHashSet();

        void Report(NetworkHost host)
        {
            if (host.Services.Count > 0)
            {
                found?.Invoke(host);
            }
        }

        // mDNS first (fast, gives names and services), in parallel with the sweep
        var mdns = Task.Run(async () =>
        {
            foreach (var (address, name, services) in await QueryMdnsAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            {
                if (own.Contains(address))
                {
                    continue;
                }
                var host = hosts.GetOrAdd(address, a => new NetworkHost(a));
                lock (host)
                {
                    host.Name ??= name;
                    foreach (var (service, port) in services)
                    {
                        host.Services.Add(service);
                        host.Ports.TryAdd(service, port);
                    }
                }
                Report(host);
            }
        }, cancellationToken);

        var candidates = new HashSet<IPAddress>(Neighbours());
        foreach (var (address, mask) in LocalAddresses())
        {
            foreach (var candidate in SubnetHosts(address, mask))
            {
                candidates.Add(candidate);
            }
        }
        candidates.ExceptWith(own);

        using var gate = new SemaphoreSlim(128);
        var probes = candidates.Select(async address =>
        {
            foreach (var (service, port) in Probes)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                bool open;
                try
                {
                    open = await IsOpenAsync(address, port, timeout, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
                if (open)
                {
                    var host = hosts.GetOrAdd(address, a => new NetworkHost(a));
                    lock (host)
                    {
                        host.Services.Add(service);
                    }
                    Report(host);
                }
            }
        }).ToList();
        await Task.WhenAll(probes).ConfigureAwait(false);
        try
        {
            await mdns.ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // no multicast on this network
        }

        // names for the machines found by address only
        await Task.WhenAll(hosts.Values.Where(h => h.Name is null && h.Services.Count > 0).Select(async host =>
        {
            if (await ReverseLookupAsync(host.Address, cancellationToken).ConfigureAwait(false) is { } name)
            {
                host.Name = name;
                Report(host);
            }
        })).ConfigureAwait(false);

        return hosts.Values.Where(h => h.Services.Count > 0).OrderBy(h => h.Address.GetAddressBytes(), ByteOrder.Instance).ToList();
    }

    /// <summary>Whether a TCP port accepts connections within <paramref name="timeout"/>.</summary>
    public static async Task<bool> IsOpenAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>IPv4 addresses of the active network interfaces (not loopback), with their masks.</summary>
    public static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address)
                    && !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    yield return (unicast.Address, unicast.IPv4Mask);
                }
            }
        }
    }

    /// <summary>The other addresses of a subnet; at most the /24 around <paramref name="address"/> (bigger networks are not swept).</summary>
    public static IEnumerable<IPAddress> SubnetHosts(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        // at least a /24
        if (m[0] != 255 || m[1] != 255 || m[2] != 255)
        {
            m = [255, 255, 255, 0];
        }
        var network = (uint)((a[0] & m[0]) << 24 | (a[1] & m[1]) << 16 | (a[2] & m[2]) << 8 | (a[3] & m[3]));
        var size = (uint)(256 - m[3]);
        for (uint i = 1; i + 1 < size; i++)
        {
            var value = network + i;
            yield return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        }
    }

    /// <summary>Neighbours the system has talked to (ARP table): Windows / macOS "arp -a", Linux /proc/net/arp.</summary>
    public static IReadOnlyList<IPAddress> Neighbours()
    {
        string text;
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/net/arp"))
            {
                text = File.ReadAllText("/proc/net/arp");
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (process is null)
                {
                    return [];
                }
                text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
        return ParseNeighbours(text);
    }

    /// <summary>IPv4 unicast addresses of an ARP listing (broadcast and multicast left out).</summary>
    public static IReadOnlyList<IPAddress> ParseNeighbours(string text)
    {
        var result = new List<IPAddress>();
        foreach (Match match in Ipv4().Matches(text))
        {
            if (IPAddress.TryParse(match.Value, out var address) && address.GetAddressBytes() is var b
                && b[0] is > 0 and < 224 && b[3] is not 0 and not 255 && IsPrivate(b) && !result.Contains(address))
            {
                result.Add(address);
            }
        }
        return result;
    }

    /// <summary>Private (local network) IPv4 ranges: 10/8, 172.16/12, 192.168/16. Public addresses (VPN routes) are not machines here.</summary>
    private static bool IsPrivate(byte[] b) => b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4();

    private static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address.ToString(), cancellationToken);
            var done = await Task.WhenAny(lookup, Task.Delay(1500, cancellationToken)).ConfigureAwait(false);
            if (done == lookup && lookup.Result.HostName is { Length: > 0 } name && name != address.ToString())
            {
                return name;
            }
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
        }
        return null;
    }

    // ---------------------------------------------------------------- mDNS / DNS-SD

    /// <summary>Asks every interface for the file services announced with mDNS; collects the answers for <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<(IPAddress Address, string? Name, List<(NetworkService Service, int Port)> Services)>> QueryMdnsAsync(
        TimeSpan wait, CancellationToken cancellationToken)
    {
        var query = BuildQuery(s_mdnsServices);
        var records = new MdnsRecords();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var (address, _) in LocalAddresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(address, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await udp.SendAsync(query, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), cancellationToken).ConfigureAwait(false);
                    sockets.Add(udp);
                }
                catch (SocketException)
                {
                    // interface without multicast
                }
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(wait);
            await Task.WhenAll(sockets.Select(async udp =>
            {
                try
                {
                    while (true)
                    {
                        var packet = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        lock (records)
                        {
                            records.Add(packet.Buffer, packet.RemoteEndPoint.Address);
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            foreach (var udp in sockets)
            {
                udp.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return records.Hosts();
    }

    /// <summary>A DNS query for PTR records of <paramref name="services"/>, asking for unicast answers (QU).</summary>
    public static byte[] BuildQuery(IEnumerable<string> services)
    {
        var names = services.ToList();
        // header: id, flags, question count, answer / authority / additional counts (2 bytes each)
        var data = new List<byte> { 0, 0, 0, 0, (byte)(names.Count >> 8), (byte)names.Count, 0, 0, 0, 0, 0, 0 };
        foreach (var name in names)
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }
            data.AddRange([0, 0, 12, 0x80, 1]); // end, type PTR, class IN with the unicast-response bit
        }
        return [.. data];
    }

    /// <summary>The records of mDNS answers: names, addresses, service instances.</summary>
    public sealed class MdnsRecords
    {
        private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Instance, string Target, int Port)> _services = [];

        /// <summary>Reads one answer packet (malformed ones are ignored); <paramref name="sender"/> is used when no A record came.</summary>
        public void Add(byte[] packet, IPAddress? sender = null)
        {
            try
            {
                if (packet.Length < 12)
                {
                    return;
                }
                var questions = packet[4] << 8 | packet[5];
                var count = (packet[6] << 8 | packet[7]) + (packet[8] << 8 | packet[9]) + (packet[10] << 8 | packet[11]);
                var pos = 12;
                for (var i = 0; i < questions; i++)
                {
                    ReadName(packet, ref pos);
                    pos += 4;
                }
                for (var i = 0; i < count && pos < packet.Length; i++)
                {
                    var name = ReadName(packet, ref pos);
                    var type = packet[pos] << 8 | packet[pos + 1];
                    var length = packet[pos + 8] << 8 | packet[pos + 9];
                    var data = pos + 10;
                    pos = data + length;
                    if (pos > packet.Length)
                    {
                        return;
                    }
                    switch (type)
                    {
                        case 1 when length == 4: // A
                            _addresses[name] = new IPAddress(packet.AsSpan(data, 4));
                            break;
                        case 33 when length >= 7: // SRV: priority, weight, port, target
                            var port = packet[data + 4] << 8 | packet[data + 5];
                            var at = data + 6;
                            var target = ReadName(packet, ref at);
                            _services.Add((name, target, port));
                            if (sender is not null && !_addresses.ContainsKey(target))
                            {
                                _addresses[target + "#sender"] = sender;
                            }
                            break;
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                // malformed packet
            }
        }

        /// <summary>The machines with their services.</summary>
        public IReadOnlyList<(IPAddress Address, string? Name, List<(NetworkService Service, int Port)> Services)> Hosts()
        {
            var result = new Dictionary<IPAddress, (string? Name, List<(NetworkService, int)> Services)>();
            foreach (var (instance, target, port) in _services)
            {
                if (ServiceOf(instance) is not { } service
                    || !(_addresses.TryGetValue(target, out var address) || _addresses.TryGetValue(target + "#sender", out address)))
                {
                    continue;
                }
                if (!result.TryGetValue(address, out var entry))
                {
                    entry = (target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? target[..^6] : target, []);
                    result[address] = entry;
                }
                entry.Services.Add((service, port));
            }
            return result.Select(r => (r.Key, r.Value.Name, r.Value.Services)).ToList();
        }

        private static NetworkService? ServiceOf(string instance)
        {
            var lower = instance.ToLowerInvariant();
            return lower.Contains("._sftp-ssh._tcp", StringComparison.Ordinal) || lower.Contains("._ssh._tcp", StringComparison.Ordinal) ? NetworkService.Sftp
                : lower.Contains("._smb._tcp", StringComparison.Ordinal) ? NetworkService.Smb
                : lower.Contains("._ftp._tcp", StringComparison.Ordinal) ? NetworkService.Ftp
                : lower.Contains("._webdav", StringComparison.Ordinal) ? NetworkService.WebDav
                : null;
        }

        private static string ReadName(byte[] packet, ref int pos)
        {
            var labels = new List<string>();
            var jumped = false;
            var at = pos;
            for (var guard = 0; guard < 128; guard++)
            {
                var length = packet[at];
                if (length == 0)
                {
                    at++;
                    break;
                }
                if ((length & 0xC0) == 0xC0) // compression pointer
                {
                    var target = (length & 0x3F) << 8 | packet[at + 1];
                    if (!jumped)
                    {
                        pos = at + 2;
                    }
                    jumped = true;
                    at = target;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(packet, at + 1, length));
                at += length + 1;
            }
            if (!jumped)
            {
                pos = at;
            }
            return string.Join('.', labels);
        }
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                if (x[i] != y[i])
                {
                    return x[i].CompareTo(y[i]);
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
