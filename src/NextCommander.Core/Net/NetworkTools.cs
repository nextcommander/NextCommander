using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NextCommander.Core.Net;

/// <summary>One answer of a ping (null time: no answer).</summary>
public sealed record PingReply(int Sequence, string Address, long? Milliseconds, int? Ttl, string Status);

/// <summary>An open TCP port found by a scan.</summary>
public sealed record OpenPort(int Port, string Service, long Milliseconds);

/// <summary>What a port said to a TCP connect: open, closed (refused) or filtered (no answer within the time).</summary>
public enum PortState
{
    Open,
    Closed,
    Filtered,
}

/// <summary>One port of a scan.</summary>
public sealed record PortResult(int Port, string Service, PortState State, long Milliseconds);

/// <summary>A broadcast address of a local network (for Wake-on-LAN) and what it is.</summary>
public sealed record BroadcastTarget(string Address, string Description);

/// <summary>
/// The network tools of the Net menu (user's request, 21 November 2026): ping, a TCP port
/// scan and Wake-on-LAN (the magic packet: 6 × 0xFF and the MAC address 16 times, sent as a UDP broadcast).
/// </summary>
public static class NetworkTools
{
    /// <summary>"00:11:22:AA:BB:CC", "00-11-22-aa-bb-cc", "001122AABBCC" as 6 bytes (null: not a MAC address).</summary>
    public static byte[]? ParseMac(string? text)
    {
        var hex = new string([.. (text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or '.'))]);
        if (hex.Length != 12)
        {
            return null;
        }
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }
        return bytes;
    }

    /// <summary>The magic packet that wakes the computer with this network card.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet, 6 + i * 6);
        }
        return packet;
    }

    /// <summary>Sends the magic packet to the broadcast address (255.255.255.255 or the network's, e.g. 192.168.1.255) and port.</summary>
    public static async Task WakeAsync(byte[] mac, string broadcast = "255.255.255.255", int port = 9, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(broadcast, out var address))
        {
            throw new ArgumentException($"Not an IP address: {broadcast}");
        }
        using var udp = new UdpClient(address.AddressFamily) { EnableBroadcast = true };
        var packet = MagicPacket(mac);
        var target = new IPEndPoint(address, port);
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, target, cancellationToken).ConfigureAwait(false); // three times: a lost datagram is not answered
        }
    }

    /// <summary>One ping with the timeout; a failure is a reply without a time.</summary>
    public static async Task<PingReply> PingAsync(string host, int sequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new PingReply(sequence, reply.Address.ToString(), reply.RoundtripTime, reply.Options?.Ttl, "OK")
                : new PingReply(sequence, reply.Address?.ToString() ?? host, null, null, reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return new PingReply(sequence, host, null, null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>The common ports of a quick scan.</summary>
    public static readonly int[] CommonPorts =
        [21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445, 465, 587, 993, 995, 1433, 1521, 2049, 3306, 3389, 5432, 5900, 5985, 6379, 8080, 8443, 9000, 9200, 27017];

    /// <summary>"22, 80, 8000-8100" as a sorted list of ports (an error names the bad part; at most 65535 ports).</summary>
    public static IReadOnlyList<int> ParsePorts(string text, out string? error)
    {
        error = null;
        var ports = new SortedSet<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            int from, to;
            if (dash > 0)
            {
                if (!int.TryParse(part[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out from)
                    || !int.TryParse(part[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out to))
                {
                    error = part;
                    return [];
                }
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out from))
            {
                to = from;
            }
            else
            {
                error = part;
                return [];
            }
            if (from is < 1 or > 65535 || to is < 1 or > 65535 || to < from)
            {
                error = part;
                return [];
            }
            for (var port = from; port <= to; port++)
            {
                ports.Add(port);
            }
        }
        return [.. ports];
    }

    /// <summary>The usual service of a port ("" when it has none known).</summary>
    public static string ServiceName(int port) => port switch
    {
        20 or 21 => "FTP", 22 => "SSH", 23 => "Telnet", 25 => "SMTP", 53 => "DNS", 80 => "HTTP", 110 => "POP3", 111 => "RPC",
        135 => "MS RPC", 139 => "NetBIOS", 143 => "IMAP", 389 => "LDAP", 443 => "HTTPS", 445 => "SMB", 465 => "SMTPS", 513 => "rlogin",
        514 => "rsh", 587 => "SMTP submission", 636 => "LDAPS", 873 => "rsync", 990 => "FTPS", 993 => "IMAPS", 995 => "POP3S",
        1433 => "SQL Server", 1521 => "Oracle", 2049 => "NFS", 2375 or 2376 => "Docker", 3306 => "MySQL", 3389 => "RDP", 5432 => "PostgreSQL",
        5900 or 5901 => "VNC", 5985 or 5986 => "WinRM", 6379 => "Redis", 8080 => "HTTP (alt)", 8443 => "HTTPS (alt)", 9000 => "HTTP (alt)",
        9200 => "Elasticsearch", 11211 => "memcached", 27017 => "MongoDB", 47290 => "PC link",
        _ => string.Empty,
    };

    /// <summary>
    /// TCP connects to the ports (several at a time, each within the timeout); the open ones in port order. The progress
    /// counts the ports tried.
    /// </summary>
    public static async Task<IReadOnlyList<OpenPort>> ScanAsync(string host, IReadOnlyList<int> ports, TimeSpan timeout, int parallel = 64,
        IProgress<int>? progress = null, Action<OpenPort>? found = null, CancellationToken cancellationToken = default)
    {
        var tried = 0;
        var all = await ScanPortsAsync(host, ports, timeout, parallel, result =>
        {
            if (result.State == PortState.Open)
            {
                found?.Invoke(new OpenPort(result.Port, result.Service, result.Milliseconds));
            }
            progress?.Report(Interlocked.Increment(ref tried));
        }, cancellationToken).ConfigureAwait(false);
        return [.. all.Where(r => r.State == PortState.Open).Select(r => new OpenPort(r.Port, r.Service, r.Milliseconds))];
    }

    /// <summary>
    /// TCP connects to the ports (several at a time, each within the timeout); every port with what it said, in port order.
    /// <paramref name="each"/> hears of each port as it is done (from the scanning threads).
    /// </summary>
    public static async Task<IReadOnlyList<PortResult>> ScanPortsAsync(string host, IReadOnlyList<int> ports, TimeSpan timeout, int parallel = 64,
        Action<PortResult>? each = null, CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        var results = new List<PortResult>();
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                var started = System.Diagnostics.Stopwatch.StartNew();
                PortState state;
                try
                {
                    await client.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
                    state = PortState.Open;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    state = PortState.Closed; // the computer answered: nothing listens there
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    state = PortState.Filtered; // no answer in time (a firewall drops it) or unreachable
                }
                var result = new PortResult(port, ServiceName(port), state, started.ElapsedMilliseconds);
                lock (results)
                {
                    results.Add(result);
                }
                each?.Invoke(result);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.OrderBy(p => p.Port)];
    }

    /// <summary>The address of a host (IPv4 first); an <see cref="IOException"/> when the name is not known.</summary>
    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new IOException($"{host}: {ex.Message}", ex);
        }
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new IOException($"{host}: no address");
    }

    /// <summary>A host name or an IP address (letters, digits, dots, dashes; IPv6 in its own form).</summary>
    public static bool IsHostName(string? text)
    {
        var host = (text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            return false;
        }
        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }
        return host.Length <= 253 && !host.Contains("..", StringComparison.Ordinal) && !host.StartsWith('.') && !host.EndsWith('.')
            && !host.StartsWith('-') && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    /// <summary>"a4bb6d123f90", "A4-BB-..." as "A4:BB:6D:12:3F:90" (what is there so far: the box is formatted while typed).</summary>
    public static string FormatMac(string? text)
    {
        var hex = new string([.. (text ?? string.Empty).Where(char.IsAsciiHexDigit).Take(12)]).ToUpperInvariant();
        return string.Join(":", Enumerable.Range(0, (hex.Length + 1) / 2).Select(i => hex.Substring(i * 2, Math.Min(2, hex.Length - i * 2))));
    }

    /// <summary>
    /// Where a magic packet can go: the whole local network (255.255.255.255) and the broadcast address of each local IPv4
    /// network this computer is on, with the network card's name.
    /// </summary>
    public static IReadOnlyList<BroadcastTarget> BroadcastTargets()
    {
        var targets = new List<BroadcastTarget> { new("255.255.255.255", "This network (everyone)") };
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var unicast in card.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask?.GetAddressBytes();
                    if (mask is null || mask.Length != 4 || mask.All(b => b == 0))
                    {
                        continue;
                    }
                    var broadcast = new IPAddress([.. address.Select((b, i) => (byte)(b | ~mask[i]))]).ToString();
                    if (targets.All(t => t.Address != broadcast))
                    {
                        targets.Add(new(broadcast, card.Name));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // the whole network only
        }
        return targets;
    }
}
