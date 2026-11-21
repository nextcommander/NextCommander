using System.Net;
using System.Net.Sockets;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>Ping, port scan and Wake-on-LAN of the Net menu's network tools.</summary>
public sealed class NetworkToolsTests
{
    [Fact]
    public void Mac_addresses_and_the_magic_packet()
    {
        var mac = NetworkTools.ParseMac("00-11-22-aa-BB-cc")!;
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC }, mac);
        Assert.Equal(mac, NetworkTools.ParseMac("0011.22aa.bbcc"));
        Assert.Null(NetworkTools.ParseMac("00:11:22:AA:BB"));
        Assert.Null(NetworkTools.ParseMac("00:11:22:AA:BB:ZZ"));
        var packet = NetworkTools.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(mac, packet[96..]);
    }

    [Fact]
    public void Port_lists_and_service_names()
    {
        Assert.Equal([22, 80, 8000, 8001, 8002], NetworkTools.ParsePorts("80, 22;8000-8002 22", out var error));
        Assert.Null(error);
        Assert.Empty(NetworkTools.ParsePorts("22, http", out error));
        Assert.Equal("http", error);
        Assert.Empty(NetworkTools.ParsePorts("70000", out error));
        Assert.Empty(NetworkTools.ParsePorts("90-80", out error));
        Assert.Equal("SSH", NetworkTools.ServiceName(22));
        Assert.Equal(string.Empty, NetworkTools.ServiceName(12345));
    }

    [Fact]
    public async Task A_scan_finds_the_listening_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var tried = 0;
        var result = await NetworkTools.ScanAsync("127.0.0.1", [closed, open], TimeSpan.FromSeconds(2), progress: new SyncProgress(n => tried = n));
        Assert.Equal([open], result.Select(p => p.Port));
        Assert.Equal(2, tried);
    }

    [Fact]
    public async Task Wake_on_lan_sends_the_packet()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var mac = NetworkTools.ParseMac("00:11:22:33:44:55")!;
        await NetworkTools.WakeAsync(mac, "127.0.0.1", port);
        var received = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NetworkTools.MagicPacket(mac), received.Buffer);
        await Assert.ThrowsAsync<ArgumentException>(() => NetworkTools.WakeAsync(mac, "not an address"));
    }

    [Fact]
    public async Task Ping_answers_from_this_computer()
    {
        var reply = await NetworkTools.PingAsync("127.0.0.1", 1, TimeSpan.FromSeconds(2));
        Assert.Equal("OK", reply.Status);
        Assert.NotNull(reply.Milliseconds);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
