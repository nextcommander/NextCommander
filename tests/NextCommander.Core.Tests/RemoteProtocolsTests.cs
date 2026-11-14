using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>The one table of connection types: every type is in it once, and schemes map back to their type.</summary>
public sealed class RemoteProtocolsTests
{
    [Fact]
    public void Every_protocol_is_in_the_table_once()
    {
        Assert.Equal(Enum.GetValues<RemoteProtocol>().Order(), RemoteProtocols.All.Select(p => p.Protocol).Order());
        Assert.Equal(RemoteProtocols.All.Count, RemoteProtocols.All.Select(p => p.Protocol).Distinct().Count());
    }

    [Fact]
    public void Schemes_map_back_to_their_type()
    {
        foreach (var info in RemoteProtocols.All.Where(p => p.Protocol != RemoteProtocol.FtpsImplicit))
        {
            Assert.Equal(info.Protocol, RemoteProtocols.FromScheme(info.Scheme)!.Protocol);
            Assert.Equal(info.Scheme, RemoteConnection.SchemeOf(info.Protocol));
        }
        // typed ftps: explicit TLS; aliases; any case
        Assert.Equal(RemoteProtocol.FtpsExplicit, RemoteProtocols.FromScheme("ftps")!.Protocol);
        Assert.Equal(RemoteProtocol.WebDavs, RemoteProtocols.FromScheme("WEBDAVS")!.Protocol);
        Assert.Equal(RemoteProtocol.Smb, RemoteProtocols.FromScheme("cifs")!.Protocol);
        Assert.Null(RemoteProtocols.FromScheme("gopher"));
        Assert.Equal(RemoteProtocol.Ftp, RemoteConnection.FromAuthority("gopher", "host").Protocol); // unknown: FTP, as before
    }

    [Fact]
    public void Kinds_ports_and_cloud_hosts()
    {
        Assert.Equal(990, RemoteConnection.DefaultPort(RemoteProtocol.FtpsImplicit));
        Assert.Equal(445, RemoteConnection.DefaultPort(RemoteProtocol.Smb));
        Assert.True(new RemoteConnection { Protocol = RemoteProtocol.Dropbox }.IsCloudDrive);
        Assert.False(new RemoteConnection { Protocol = RemoteProtocol.S3 }.IsCloudDrive);
        Assert.True(new RemoteConnection { Protocol = RemoteProtocol.AzureBlob }.IsObjectStorage);
        Assert.Equal("dropbox.com", RemoteConnection.CloudHost(RemoteProtocol.Dropbox));
        Assert.Equal(string.Empty, RemoteConnection.CloudHost(RemoteProtocol.Sftp));
        Assert.All(RemoteProtocols.All.Where(p => p.IsCloudDrive), p => Assert.False(string.IsNullOrEmpty(p.CloudHost)));
    }
}
