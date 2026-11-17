using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using NextCommander.Platform;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>The SSH, VNC and rsh session types: their table entries, the rsh / rlogin handshake, the VNC viewers' arguments, a real SSH login.</summary>
public sealed class ShellSessionTests
{
    [Fact]
    public void The_types_are_sessions_with_their_ports()
    {
        Assert.Equal((22, true), (RemoteConnection.DefaultPort(RemoteProtocol.Ssh), RemoteProtocols.Of(RemoteProtocol.Ssh).IsSession));
        Assert.Equal((5900, true), (RemoteConnection.DefaultPort(RemoteProtocol.Vnc), RemoteProtocols.Of(RemoteProtocol.Vnc).IsSession));
        Assert.Equal(RemoteProtocol.Rsh, RemoteProtocols.FromScheme("rlogin")!.Protocol);
        Assert.Equal(RemoteProtocol.Ssh, RemoteConnection.FromAuthority("ssh", "root@10.0.0.1:2222").Protocol);
        Assert.NotEqual(new RemoteConnection { Protocol = RemoteProtocol.Ssh, Host = "h", User = "u" }.SecretId,
            new RemoteConnection { Protocol = RemoteProtocol.Sftp, Host = "h", User = "u" }.SecretId);
    }

    [Fact]
    public void Rlogin_without_a_command_and_rsh_with_one()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Rsh, Host = "old.example.com", User = "root", LocalUser = "ali", TerminalType = "VT100" };
        Assert.True(RshProtocol.IsLogin(c));
        Assert.Equal(513, RshProtocol.PortOf(c));
        Assert.Equal("\0ali\0root\0vt100/38400\0", Encoding.UTF8.GetString(RshProtocol.Handshake(c)));

        c.RemoteCommand = " uname -a ";
        Assert.Equal(514, RshProtocol.PortOf(c));
        Assert.Equal("0\0ali\0root\0uname -a\0", Encoding.UTF8.GetString(RshProtocol.Handshake(c)));

        c.Port = 2514; // its own port for both
        Assert.Equal(2514, RshProtocol.PortOf(c));
        c.RemoteCommand = null;
        Assert.Equal(2514, RshProtocol.PortOf(c));

        // no user on the server: the same as here
        Assert.Equal(RshProtocol.LocalUserOf(new RemoteConnection()), RshProtocol.RemoteUserOf(new RemoteConnection()));
    }

    [Fact]
    public void Vnc_viewers_get_the_address_and_options_their_way()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Vnc, Host = "pc.example.com", Port = 5901, FullScreen = true, VncViewOnly = true };
        Assert.Equal(["-FullScreen", "-ViewOnly", "pc.example.com::5901"], VncViewer.Arguments(VncViewerKind.VncViewer, c));
        Assert.Equal(["-host=pc.example.com", "-port=5901", "-fullscreen=yes", "-viewonly=yes"], VncViewer.Arguments(VncViewerKind.TightVnc, c));
        Assert.Equal(["-FullScreen=1", "-SendKeyEvents=0", "-SendPointerEvents=0", "pc.example.com::5901"], VncViewer.Arguments(VncViewerKind.RealVnc, c));
        Assert.Equal(["--enable-fullscreen", "-c", "vnc://pc.example.com:5901"], VncLauncher.ArgumentsFor("/usr/bin/remmina", VncViewerKind.Url, c));
        Assert.Equal(["vnc://pc.example.com:5901"], VncLauncher.ArgumentsFor("/usr/bin/krdc", VncViewerKind.Url, c));

        var plain = new RemoteConnection { Protocol = RemoteProtocol.Vnc, Host = "fe80::1", User = "me" };
        Assert.Equal(["[fe80::1]::5900"], VncViewer.Arguments(VncViewerKind.VncViewer, plain));
        Assert.Equal("vnc://me@[fe80::1]:5900", VncViewer.Url(plain));
    }

    /// <summary>A real login and a command through the shell, when FM_TEST_SFTP names a server (the SFTP test server takes SSH sessions too).</summary>
    [Fact]
    public async Task An_ssh_session_logs_in_and_runs_a_command()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_SFTP") is not { Length: > 0 } url)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var connection = new RemoteConnection
        {
            Protocol = RemoteProtocol.Ssh, Host = uri.Host, Port = uri.IsDefaultPort ? 0 : uri.Port, User = Uri.UnescapeDataString(userInfo[0]), Proxy = ConnectionProxy.None,
        };
        var hooks = new RemoteHooks
        {
            GetPassword = (_, retry) => Task.FromResult<string?>(retry ? null : Uri.UnescapeDataString(userInfo[1])),
            ConfirmFingerprint = (_, _, _, _) => Task.FromResult(true),
        };
        using var client = await SshSession.ConnectAsync(connection, hooks, CancellationToken.None);
        Assert.True(client.IsConnected);
        using var shell = client.CreateShellStream("xterm-256color", 80, 24, 0, 0, 4096);
        var text = new StringBuilder();
        var buffer = new byte[1024];
        async Task WaitForAsync(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!text.ToString().Contains(expected, StringComparison.Ordinal))
            {
                var read = await shell.ReadAsync(buffer, timeout.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        await WaitForAsync("$ ");
        Assert.Contains("xterm-256color", text.ToString(), StringComparison.Ordinal); // the terminal type reached the server
        shell.Write(Encoding.UTF8.GetBytes("echo hello over ssh\r"));
        shell.Flush();
        await WaitForAsync("hello over ssh\r\n");
    }
}
