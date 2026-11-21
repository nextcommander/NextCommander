using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>PuTTY and MobaXterm sessions, and NextCommander's own connections file.</summary>
public sealed class SessionImportTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Putty_sessions_become_ssh_telnet_rlogin_and_serial_connections()
    {
        var ssh = ConnectionImport.FromPuttySession("Prod%20web", Values(("HostName", "deploy@web.example.com"), ("PortNumber", "2222"), ("Protocol", "ssh"),
            ("PublicKeyFile", @"C:\keys\deploy.ppk"), ("RemoteCommand", "htop"), ("TerminalType", "xterm-256color"), ("PingIntervalSecs", "30"),
            ("ProxyMethod", "2"), ("ProxyHost", "proxy.local"), ("ProxyPort", "1080"), ("ProxyUsername", "me")))!.Connection;
        Assert.Equal(("Prod web", "PuTTY", RemoteProtocol.Ssh, "web.example.com", 2222, "deploy"), (ssh.Name, ssh.Folder, ssh.Protocol, ssh.Host, ssh.Port, ssh.User));
        Assert.Equal((@"C:\keys\deploy.ppk", false, "htop", "xterm-256color", 30), (ssh.KeyFile, ssh.UseSshAgent, ssh.RemoteCommand, ssh.TerminalType, ssh.KeepAliveSeconds));
        Assert.Equal((ConnectionProxy.Custom, ProxyKind.Socks5, "proxy.local", 1080, "me"), (ssh.Proxy, ssh.ProxyKind, ssh.ProxyHost, ssh.ProxyPort, ssh.ProxyUser));

        var plain = ConnectionImport.FromPuttySession("box", Values(("HostName", "box"), ("PortNumber", "22"), ("UserName", "root"), ("Protocol", "ssh"), ("TerminalType", "xterm")))!.Connection;
        Assert.Equal((0, "root", true, null, ConnectionProxy.Default), (plain.Port, plain.User, plain.UseSshAgent, plain.TerminalType, plain.Proxy));

        var telnet = ConnectionImport.FromPuttySession("switch", Values(("HostName", "10.0.0.2"), ("PortNumber", "23"), ("Protocol", "telnet")))!.Connection;
        Assert.Equal((RemoteProtocol.Telnet, 0), (telnet.Protocol, telnet.Port));
        var rlogin = ConnectionImport.FromPuttySession("old", Values(("HostName", "sun"), ("Protocol", "rlogin"), ("LocalUserName", "me")))!.Connection;
        Assert.Equal((RemoteProtocol.Rsh, "me"), (rlogin.Protocol, rlogin.LocalUser));

        var serial = ConnectionImport.FromPuttySession("console", Values(("Protocol", "serial"), ("SerialLine", "COM3"), ("SerialSpeed", "115200"),
            ("SerialDataBits", "7"), ("SerialStopHalfbits", "4"), ("SerialParity", "2"), ("SerialFlowControl", "2")))!.Connection;
        Assert.Equal((RemoteProtocol.Serial, "COM3", 115200, 7, SerialStopBits.Two, SerialParity.Even, SerialFlowControl.RtsCts),
            (serial.Protocol, serial.Host, serial.BaudRate, serial.DataBits, serial.StopBits, serial.Parity, serial.FlowControl));

        Assert.Null(ConnectionImport.FromPuttySession("Default%20Settings", Values(("HostName", "x"))));
        Assert.Null(ConnectionImport.FromPuttySession("raw", Values(("HostName", "x"), ("Protocol", "raw"))));
        Assert.Null(ConnectionImport.FromPuttySession("empty", Values(("Protocol", "ssh"))));
    }

    [Fact]
    public void Putty_session_files_of_linux_are_read()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fm-putty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "My%20server"), "HostName=srv.example.com\nUserName=me\nPortNumber=22\nProtocol=ssh\n");
            File.WriteAllText(Path.Combine(folder, "Default%20Settings"), "HostName=\nProtocol=ssh\n");
            var imported = Assert.Single(ConnectionImport.FromPuttyFolder(folder));
            Assert.Equal(("My server", "srv.example.com", "me"), (imported.Connection.Name, imported.Connection.Host, imported.Connection.User));
            Assert.Empty(ConnectionImport.FromPuttyFolder(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MobaXterm_sessions_keep_their_folders_logins_and_gateways()
    {
        var ssh = string.Join('%', ["0", "web.example.com", "2222", "deploy", "", "-1", "0", "cd /var/www__PTVIRG__ ls", "bastion__PIPE__second", "2200__PIPE__22",
            "admin__PIPE__x", "0", "0", "0", "_CurrentDrive_:\\keys\\deploy.ppk", "", "-1", "0", "0", "3", "proxy.local", "8080", "me", "0", "0", "1", "", "0",
            "", "", "", "0", "0", "-1", "0"]);
        var sftp = string.Join('%', ["7", "files.example.com", "22", "up", "-1", "0", "/srv/data", "0", "0", "", "0", "", "1080", ""]);
        var rdp = string.Join('%', ["4", "desk.example.com", "3389", "admin", "-1", "0", "-1", "0", "-1", "0", "0", "-1", "", "", "", "", "0", "0", "", "-1",
            "gw.example.com", "0", "0", "0", "0", "-1", "0", "0", "0", "0", "0"]);
        var text = string.Join("\r\n", [
            "[Bookmarks]", "SubRep=", "ImgNum=42",
            $"Web=#109#{ssh}#MobaFont%10%0%0%-1%15%236,236,236#0# #-1",
            "Shell=#97#10%%#MobaFont%10#0# #-1",
            "[Bookmarks_1]", "SubRep=Work\\Servers", "ImgNum=41",
            $"Files=#140#{sftp}#MobaFont#0# #-1",
            $"Desk=#91#{rdp}#MobaFont#0# #-1",
            "Viewer=#128#5%vnc.example.com%5901%-1%-1%%%%%0#MobaFont#0# #-1",
            "Old switch=#98#1%10.0.0.2%23%#MobaFont#0# #-1",
            "[Misc]", "Other=1",
        ]);
        var imported = ConnectionImport.FromMobaXterm(text).Select(i => i.Connection).ToList();
        Assert.Equal(["Web", "Files", "Desk", "Viewer", "Old switch"], imported.Select(c => c.Name)); // the shell has no counterpart

        var web = imported[0];
        Assert.Equal(("MobaXterm", RemoteProtocol.Ssh, "web.example.com", 2222, "deploy"), (web.Folder, web.Protocol, web.Host, web.Port, web.User));
        Assert.Equal(("cd /var/www; ls", "admin@bastion:2200"), (web.RemoteCommand, web.JumpHost));
        Assert.EndsWith(":\\keys\\deploy.ppk", web.KeyFile, StringComparison.Ordinal);
        Assert.DoesNotContain("_CurrentDrive_", web.KeyFile, StringComparison.Ordinal);
        Assert.True(web.UseSshAgent);
        Assert.Equal((ConnectionProxy.Custom, ProxyKind.Http, "proxy.local", 8080, "me"), (web.Proxy, web.ProxyKind, web.ProxyHost, web.ProxyPort, web.ProxyUser));

        var files = imported[1];
        Assert.Equal(("MobaXterm/Work/Servers", RemoteProtocol.Sftp, 0, "up", "/srv/data", null), (files.Folder, files.Protocol, files.Port, files.User, files.InitialPath, files.KeyFile));
        var desk = imported[2];
        Assert.Equal((RemoteProtocol.Rdp, true, true, false, true, "gw.example.com", true), (desk.Protocol, desk.RdpAdmin, desk.RdpDrives, desk.RdpPrinters, desk.RdpClipboard, desk.RdpGateway, desk.RdpMicrophone));
        Assert.Equal((RemoteProtocol.Vnc, 5901, true), (imported[3].Protocol, imported[3].Port, imported[3].VncViewOnly));
        Assert.Equal((RemoteProtocol.Telnet, "10.0.0.2", 0), (imported[4].Protocol, imported[4].Host, imported[4].Port));
    }

    [Fact]
    public void An_exported_file_has_no_secrets_and_comes_back_with_free_names()
    {
        var settings = new AppSettings();
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Work", Color = "#E5484D" });
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Home" });
        settings.Connections.Add(new RemoteConnection { Name = "web", Folder = "Work", Protocol = RemoteProtocol.Sftp, Host = "web", User = "me", SavePassword = true });
        settings.Connections.Add(new RemoteConnection { Name = "drive", Folder = "Work", Protocol = RemoteProtocol.Box, ClientId = "id", ClientSecret = "very secret" });
        settings.Connections.Add(new RemoteConnection { Name = "nas", Folder = "Home", Protocol = RemoteProtocol.Smb, Host = "nas" });

        var all = ConnectionImport.Export(settings);
        Assert.Contains(ConnectionImport.ExportFormat, all, StringComparison.Ordinal);
        Assert.DoesNotContain("very secret", all, StringComparison.Ordinal);
        var work = ConnectionImport.Export(settings, "Work");
        Assert.DoesNotContain("nas", work, StringComparison.Ordinal);

        // into the same settings: taken names get a number; into empty ones: as they were
        var again = ConnectionImport.FromExport(work, settings).Select(i => i.Connection).ToList();
        Assert.Equal(["web (2)", "drive (2)"], again.Select(c => c.Name));
        Assert.All(again, c => Assert.False(c.SavePassword));
        Assert.Equal("id", again[1].ClientId);
        var fresh = ConnectionImport.FromExport(all, new AppSettings(), "Imported").Select(i => i.Connection).ToList();
        Assert.Equal([("web", "Imported/Work"), ("drive", "Imported/Work"), ("nas", "Imported/Home")], fresh.Select(c => (c.Name, c.Folder)));
        Assert.Contains(ConnectionImport.FoldersOfExport(work), f => f is { Path: "Work", Color: "#E5484D" });

        Assert.Throws<FormatException>(() => ConnectionImport.FromExport("{\"Format\":\"other\"}", settings));
        Assert.Throws<FormatException>(() => ConnectionImport.FromExport("not json", settings));
    }
}
