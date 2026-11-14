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
        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Host = "h" }));
        Assert.Equal("Enter the jump host as user@host:port.", ConnectionChecks.Problem(new RemoteConnection { Host = "h", JumpHost = "a b" }));
        Assert.Equal("S3 needs an access key id or an AWS profile.", ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "s3" }));
        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "s3", AwsProfile = "dev" }));
        Assert.Equal("Box needs the client secret of the application too.", ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Box, ClientId = "id" }));
        Assert.Equal("deploy@h", ConnectionChecks.DefaultName(new RemoteConnection { Host = "h", User = "deploy" }, "SFTP"));
        Assert.Equal("Box (me)", ConnectionChecks.DefaultName(new RemoteConnection { Protocol = RemoteProtocol.Box, User = "me" }, "Box"));
        Assert.Equal("sftp://deploy@h:2222/var/www", ConnectionChecks.Address(new RemoteConnection { Host = "h", User = "deploy", Port = 2222, InitialPath = "/var/www" }));
    }
}
