using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>The connection manager's folders (paths in the connections, colors and empty ones in the settings) and its checks.</summary>
public sealed class ConnectionFoldersTests
{
    private static AppSettings Sample()
    {
        var settings = new AppSettings();
        settings.Connections.Add(new RemoteConnection { Name = "a", Host = "a", Folder = "Work/Prod" });
        settings.Connections.Add(new RemoteConnection { Name = "b", Host = "b", Folder = "Work" });
        settings.Connections.Add(new RemoteConnection { Name = "c", Host = "c" });
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Work/Prod", Color = "#ED4245" });
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Empty/Inner" });
        return settings;
    }

    [Fact]
    public void Folders_come_from_connections_and_kept_entries()
    {
        var settings = Sample();
        Assert.Equal(["Empty", "Empty/Inner", "Work", "Work/Prod"], ConnectionFolders.All(settings));
        var (folders, connections) = ConnectionFolders.ChildrenOf(settings, "Work");
        Assert.Equal(["Work/Prod"], folders);
        Assert.Equal(["b"], connections.Select(c => c.Name));
        Assert.Equal(["a", "b"], ConnectionFolders.ConnectionsIn(settings, "Work").Select(c => c.Name).Order());
        Assert.Equal("New folder (2)", ConnectionFolders.FreeName(new AppSettings { ConnectionFolders = [new ConnectionFolder { Path = "New folder" }] }, string.Empty, "New folder"));
    }

    [Fact]
    public void A_folder_moves_and_is_deleted_with_its_content()
    {
        var settings = Sample();
        Assert.False(ConnectionFolders.Move(settings, "Work", "Work/Prod/Deeper")); // not into itself
        Assert.False(ConnectionFolders.Move(settings, "Work", "Empty/Inner")); // taken
        Assert.True(ConnectionFolders.Move(settings, "Work", "Clients/Work"));
        Assert.Equal(["Clients/Work/Prod", "Clients/Work", string.Empty], settings.Connections.Select(c => c.Folder));
        Assert.Equal("#ED4245", ConnectionFolders.ColorOf(settings, "Clients/Work/Prod"));

        var (connections, folders) = ConnectionFolders.Delete(settings, "Clients");
        Assert.Equal(["a", "b"], connections.Select(c => c.Name));
        Assert.Single(folders, f => f.Path == "Clients/Work/Prod");
        Assert.Equal(["c"], settings.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Checks_and_default_names()
    {
        Assert.Equal("Enter a valid server name (e.g. server.example.com).", ConnectionChecks.Problem(new RemoteConnection()));
        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Host = "h", User = "u" }));
        Assert.Equal("Enter the jump host as user@host:port.", ConnectionChecks.Problem(new RemoteConnection { Host = "h", User = "u", JumpHost = "a b" }));
        Assert.Equal("S3 needs an access key id or an AWS profile.", ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "s3" }));
        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "s3", AwsProfile = "dev" }));
        Assert.Equal("Box needs the client secret of the application too.", ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Box, ClientId = "id" }));
        Assert.Equal("deploy@h", ConnectionChecks.DefaultName(new RemoteConnection { Host = "h", User = "deploy" }, "SFTP"));
        Assert.Equal("Box (me)", ConnectionChecks.DefaultName(new RemoteConnection { Protocol = RemoteProtocol.Box, User = "me" }, "Box"));
        Assert.Equal("sftp://deploy@h:2222/var/www", ConnectionChecks.Address(new RemoteConnection { Host = "h", User = "deploy", Port = 2222, InitialPath = "/var/www" }));
    }

    [Fact]
    public void What_must_be_filled_in_is_checked_field_by_field()
    {
        // an empty SFTP connection: the server and the user, each at its field
        var sftp = new RemoteConnection();
        Assert.Equal([ConnectionField.Host, ConnectionField.User], ConnectionChecks.Problems(sftp).Select(p => p.Field));
        sftp.Host = "files.example.com";
        Assert.Equal("Enter the user name (an SSH login always has one).", ConnectionChecks.ProblemOf(sftp, ConnectionField.User));
        Assert.Null(ConnectionChecks.ProblemOf(sftp, ConnectionField.Host));
        sftp.User = "deploy";
        Assert.Empty(ConnectionChecks.Problems(sftp));

        // the key file chosen as the login: chosen, and there
        sftp.KeyFile = string.Empty;
        Assert.Equal("Choose the private key file (or another login method).", ConnectionChecks.ProblemOf(sftp, ConnectionField.KeyFile));
        sftp.KeyFile = Path.Combine(Path.GetTempPath(), "nc-no-such-key-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("The private key file was not found.", ConnectionChecks.ProblemOf(sftp, ConnectionField.KeyFile));
        var key = Path.GetTempFileName();
        try
        {
            sftp.KeyFile = key;
            Assert.Null(ConnectionChecks.Problem(sftp));
        }
        finally
        {
            File.Delete(key);
        }

        // an SSH session needs a user too; Telnet, RDP, FTP and SMB do not
        Assert.Equal(ConnectionField.User, Assert.Single(ConnectionChecks.Problems(new RemoteConnection { Protocol = RemoteProtocol.Ssh, Host = "h" })).Field);
        foreach (var protocol in new[] { RemoteProtocol.Telnet, RemoteProtocol.Rdp, RemoteProtocol.Ftp, RemoteProtocol.Smb, RemoteProtocol.WebDav })
        {
            Assert.Empty(ConnectionChecks.Problems(new RemoteConnection { Protocol = protocol, Host = "h" }));
        }

        // a cloud drive: the application id (and Box's secret)
        Assert.Equal([ConnectionField.ClientId, ConnectionField.ClientSecret], ConnectionChecks.Problems(new RemoteConnection { Protocol = RemoteProtocol.Box }).Select(p => p.Field));
        // the own proxy and the port at their fields
        var proxied = new RemoteConnection { Host = "h", User = "u", Port = 70000, Proxy = NextCommander.Core.Net.ConnectionProxy.Custom };
        Assert.Equal([ConnectionField.Proxy, ConnectionField.Port], ConnectionChecks.Problems(proxied).Select(p => p.Field));
    }
}
