using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NextCommander.Core.Net;

/// <summary>One answer of a ping (null time: no answer).</summary>
public sealed record PingReply(int Sequence, string Address, long? Milliseconds, int? Ttl, string Status);

/// <summary>An open TCP port found by a scan.</summary>
public sealed record OpenPort(int Port, string Service, long Milliseconds);

/// <summary>
/// The network tools of the Net menu (MobaXterm's network tools, user's request, 21 November 2026): ping, a TCP port
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
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new IOException($"{host}: no address");
        var open = new List<OpenPort>();
        var tried = 0;
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
                try
                {
                    await client.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
                    var result = new OpenPort(port, ServiceName(port), started.ElapsedMilliseconds);
                    lock (open)
                    {
                        open.Add(result);
                    }
                    found?.Invoke(result);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // closed, filtered or too slow
                }
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref tried));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. open.OrderBy(p => p.Port)];
    }
}
