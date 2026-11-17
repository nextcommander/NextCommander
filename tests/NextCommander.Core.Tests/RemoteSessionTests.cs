using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using static NextCommander.Core.Net.TelnetProtocol;

namespace NextCommander.Core.Tests;

/// <summary>Remote sessions: the Telnet protocol, and what the RDP clients are given.</summary>
public sealed class RemoteSessionTests
{
    [Fact]
    public void Telnet_text_passes_and_options_are_answered_once()
    {
        var telnet = new TelnetProtocol();
        var reply = new List<byte>();
        byte[] input = [.. "login: "u8, Iac, Will, OptEcho, Iac, Will, OptSuppressGoAhead, Iac, Do, OptTerminalType, Iac, Do, 39 /* NEW-ENVIRON */, Iac, Will, 5 /* STATUS */];
        var text = telnet.Receive(input, reply);
        Assert.Equal("login: ", Encoding.ASCII.GetString(text));
        Assert.Equal(new byte[] { Iac, Do, OptEcho, Iac, Do, OptSuppressGoAhead, Iac, Will, OptTerminalType, Iac, Wont, 39, Iac, Dont, 5 }, reply);
        Assert.True(telnet.RemoteEcho);

        // the same request again: no answer (no loops)
        reply.Clear();
        telnet.Receive([Iac, Will, OptEcho], reply);
        Assert.Empty(reply);
        // the server stops echoing
        telnet.Receive([Iac, Wont, OptEcho], reply);
        Assert.Equal(new byte[] { Iac, Dont, OptEcho }, reply);
        Assert.False(telnet.RemoteEcho);
    }

    [Fact]
    public void Telnet_terminal_type_and_window_size()
    {
        var telnet = new TelnetProtocol();
        var reply = new List<byte>();
        Assert.Empty(telnet.Resize(132, 43)); // not asked yet: kept
        telnet.Receive([Iac, Do, OptWindowSize], reply);
        Assert.Equal(new byte[] { Iac, Will, OptWindowSize, Iac, Sb, OptWindowSize, 0, 132, 0, 43, Iac, Se }, reply);

        // a size with a 255 byte doubles it
        Assert.Equal(new byte[] { Iac, Sb, OptWindowSize, 0, 255, 255, 0, 24, Iac, Se }, telnet.Resize(255, 24));

        reply.Clear();
        telnet.Receive([Iac, Do, OptTerminalType, Iac, Sb, OptTerminalType, 1, Iac, Se], reply);
        byte[] terminalType = [Iac, Will, OptTerminalType, Iac, Sb, OptTerminalType, 0, .. "XTERM"u8, Iac, Se];
        Assert.Equal(terminalType, reply);
    }

    [Fact]
    public void Telnet_escapes_split_input_and_new_lines()
    {
        var telnet = new TelnetProtocol();
        var reply = new List<byte>();
        // IAC IAC is the byte 255; CR NUL a bare CR; a command split over two reads
        var first = telnet.Receive([(byte)'a', Iac, Iac, (byte)'\r', 0, (byte)'b', Iac], reply);
        var second = telnet.Receive([Will, OptEcho, (byte)'\r', (byte)'\n'], reply);
        Assert.Equal(new byte[] { (byte)'a', 255, (byte)'\r', (byte)'b' }, first);
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\n' }, second);
        Assert.Equal(new byte[] { Iac, Do, OptEcho }, reply);

        // typed: Enter (a bare CR) is CR LF, 255 is doubled, CR LF stays
        Assert.Equal(new byte[] { (byte)'l', (byte)'s', (byte)'\r', (byte)'\n' }, Encode("ls\r"u8));
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\n' }, Encode("\r\n"u8));
        Assert.Equal(new byte[] { 255, 255 }, Encode([255]));
    }

    [Fact]
    public void Rdp_file_and_FreeRDP_arguments()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Rdp, Name = "Office PC", Host = "pc.example.com", User = @"CORP\ali" };
        var file = RemoteDesktop.RdpFile(c);
        Assert.Contains("full address:s:pc.example.com\r\n", file, StringComparison.Ordinal);
        Assert.Contains(@"username:s:CORP\ali", file, StringComparison.Ordinal);
        Assert.Contains("screen mode id:i:1", file, StringComparison.Ordinal);
        Assert.Equal(["/v:pc.example.com", "/d:CORP", "/u:ali", "/dynamic-resolution", "+clipboard", "/cert:tofu"], RemoteDesktop.FreeRdpArguments(c, false));

        c.Port = 3390;
        c.User = "ali";
        c.FullScreen = true;
        Assert.Contains("full address:s:pc.example.com:3390", RemoteDesktop.RdpFile(c), StringComparison.Ordinal);
        Assert.Contains("screen mode id:i:2", RemoteDesktop.RdpFile(c), StringComparison.Ordinal);
        var args = RemoteDesktop.FreeRdpArguments(c, true);
        Assert.Equal(["/v:pc.example.com:3390", "/u:ali", "/f", "+clipboard", "/cert:tofu", "/from-stdin:force"], args);
        Assert.DoesNotContain(args, a => a.StartsWith("/p:", StringComparison.Ordinal)); // never on the command line
        Assert.Equal("rdp://ali@pc.example.com:3390", RemoteDesktop.RemminaAddress(c));

        c.Host = "fe80::1";
        c.Port = 0;
        Assert.Equal("[fe80::1]", RemoteDesktop.Address(c));
    }

    [Fact]
    public void Sessions_are_in_the_table()
    {
        Assert.Equal(3389, RemoteConnection.DefaultPort(RemoteProtocol.Rdp));
        Assert.Equal(23, RemoteConnection.DefaultPort(RemoteProtocol.Telnet));
        Assert.True(new RemoteConnection { Protocol = RemoteProtocol.Telnet }.IsSession);
        Assert.False(new RemoteConnection { Protocol = RemoteProtocol.Sftp }.IsSession);
        Assert.Equal(RemoteProtocol.Rdp, RemoteProtocols.FromScheme("RDP")!.Protocol);
        var typed = RemoteConnection.FromAuthority("telnet", "admin@router:2323");
        Assert.Equal((RemoteProtocol.Telnet, "router", 2323, "admin"), (typed.Protocol, typed.Host, typed.Port, typed.User));
    }
}
