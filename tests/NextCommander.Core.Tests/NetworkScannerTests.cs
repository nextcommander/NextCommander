using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Platform;

namespace NextCommander.Core.Tests;

/// <summary>Finding machines on the local network (connection manager): ARP lists, subnets, mDNS answers, port tries.</summary>
public sealed class NetworkScannerTests
{
    [Fact]
    public void Arp_listings_of_windows_and_linux_give_the_neighbours()
    {
        var windows = """
            Interface: 192.168.1.20 --- 0x7
              Internet Address      Physical Address      Type
              192.168.1.1           aa-bb-cc-dd-ee-ff     dynamic
              192.168.1.35          11-22-33-44-55-66     dynamic
              192.168.1.255         ff-ff-ff-ff-ff-ff     static
              224.0.0.22            01-00-5e-00-00-16     static
              20.76.167.242         00-00-00-00-00-00     static
            """;
        Assert.Equal(["192.168.1.20", "192.168.1.1", "192.168.1.35"], NetworkScanner.ParseNeighbours(windows).Select(a => a.ToString()));
        var linux = """
            IP address       HW type     Flags       HW address            Mask     Device
            10.0.0.7         0x1         0x2         52:54:00:12:34:56     *        eth0
            """;
        Assert.Equal(["10.0.0.7"], NetworkScanner.ParseNeighbours(linux).Select(a => a.ToString()));
    }

    [Fact]
    public void Subnet_sweep_covers_at_most_the_slash_24()
    {
        var hosts = NetworkScanner.SubnetHosts(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")).ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.254", hosts[^1].ToString());
        // a /16 is not swept whole: only the /24 around the address
        Assert.Equal(254, NetworkScanner.SubnetHosts(IPAddress.Parse("10.4.7.9"), IPAddress.Parse("255.255.0.0")).Count());
        Assert.Equal(["10.0.0.1", "10.0.0.2"], NetworkScanner.SubnetHosts(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.252")).Select(a => a.ToString()));
    }

    [Fact]
    public void Mdns_query_and_answers()
    {
        var query = NetworkScanner.BuildQuery(["_smb._tcp.local"]);
        Assert.Equal(1, query[4] << 8 | query[5]); // one question
        Assert.Equal(12 + 1 + 4 + 1 + 4 + 1 + 5 + 1 + 4, query.Length);
        Assert.Equal([0, 12, 0x80, 1], query[^4..]); // PTR, IN with the unicast-response bit

        // answer: PTR _smb._tcp.local -> nas._smb._tcp.local, SRV nas._smb._tcp.local -> nas.local:445, A nas.local = 192.168.1.40
        var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 3, 0, 0, 0, 0 };
        void Name(string name)
        {
            foreach (var label in name.Split('.'))
            {
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(label));
            }
            packet.Add(0);
        }
        void Record(string name, int type, byte[] data)
        {
            Name(name);
            packet.AddRange([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length]);
            packet.AddRange(data);
        }
        var target = new List<byte>();
        foreach (var label in "nas.local".Split('.'))
        {
            target.Add((byte)label.Length);
            target.AddRange(Encoding.ASCII.GetBytes(label));
        }
        target.Add(0);
        var instance = new List<byte>();
        foreach (var label in "nas._smb._tcp.local".Split('.'))
        {
            instance.Add((byte)label.Length);
            instance.AddRange(Encoding.ASCII.GetBytes(label));
        }
        instance.Add(0);
        Record("_smb._tcp.local", 12, [.. instance]);
        Record("nas._smb._tcp.local", 33, [0, 0, 0, 0, 1, 189, .. target]);
        Record("nas.local", 1, [192, 168, 1, 40]);

        var records = new NetworkScanner.MdnsRecords();
        records.Add([.. packet]);
        records.Add([1, 2, 3]); // garbage is ignored
        var host = Assert.Single(records.Hosts());
        Assert.Equal("192.168.1.40", host.Address.ToString());
        Assert.Equal("nas", host.Name);
        Assert.Equal([(NetworkService.Smb, 445)], host.Services);
    }

    [Fact]
    public async Task Port_try_finds_a_listening_server()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await NetworkScanner.IsOpenAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(2), CancellationToken.None));
        listener.Stop();
        Assert.False(await NetworkScanner.IsOpenAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    /// <summary>A real scan of the local network; runs only with FM_TEST_NETSCAN=1 (prints what it finds).</summary>
    [Fact]
    public async Task Real_network_scan_when_asked()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_NETSCAN") != "1")
        {
            return;
        }
        var started = DateTime.UtcNow;
        var hosts = await NetworkScanner.ScanAsync(null, CancellationToken.None);
        Console.WriteLine($"NETSCAN {hosts.Count} machines in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        foreach (var host in hosts)
        {
            Console.WriteLine("NETSCAN " + host);
        }
    }
}
