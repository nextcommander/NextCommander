using System.Net;
using System.Text;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>Web server folder listings: the index pages of the common servers, and the read-only provider.</summary>
public sealed class HttpListingTests
{
    private static readonly Uri Folder = new("https://files.example.org/pub/linux/");

    private const string Nginx = """
        <html><head><title>Index of /pub/linux/</title></head><body><h1>Index of /pub/linux/</h1><hr><pre><a href="../">../</a>
        <a href="isos/">isos/</a>                                              05-Jan-2024 10:22                   -
        <a href="readme%20first.txt">readme first.txt</a>                                   06-Feb-2024 11:30                1234
        <a href="kernel.tar.xz">kernel.tar.xz</a>                                      07-Mar-2024 12:00:05          145678901
        </pre><hr></body></html>
        """;

    private const string Apache = """
        <table><tr><th><a href="?C=N;O=D">Name</a></th><th><a href="?C=M;O=A">Last modified</a></th><th><a href="?C=S;O=A">Size</a></th></tr>
        <tr><td valign="top"><img src="/icons/back.gif" alt="[PARENTDIR]"></td><td><a href="/pub/">Parent Directory</a></td><td>&nbsp;</td><td align="right">  - </td></tr>
        <tr><td valign="top"><img src="/icons/folder.gif" alt="[DIR]"></td><td><a href="isos/">isos/</a></td><td align="right">2024-01-05 10:22  </td><td align="right">  - </td></tr>
        <tr><td valign="top"><img src="/icons/text.gif" alt="[TXT]"></td><td><a href="readme%20first.txt">readme first.txt</a></td><td align="right">2024-02-06 11:30  </td><td align="right">1.2K</td></tr>
        <tr><td valign="top"><img src="/icons/compressed.gif" alt="[   ]"></td><td><a href="kernel.tar.xz">kernel.tar.xz</a></td><td align="right">2024-03-07 12:00  </td><td align="right">139M</td></tr>
        </table>
        """;

    private const string Iis = """
        <pre><A HREF="/pub/">[To Parent Directory]</A><br><br> Friday, January 5, 2024 10:22 AM        &lt;dir&gt; <A HREF="/pub/linux/isos/">isos</A><br> Tuesday, February 6, 2024 11:30 AM         1234 <A HREF="/pub/linux/readme%20first.txt">readme first.txt</A><br></pre>
        """;

    private const string PythonServer = """
        <ul>
        <li><a href="isos/">isos/</a></li>
        <li><a href="readme%20first.txt">readme first.txt</a></li>
        <li><a href="https://elsewhere.example.com/x">elsewhere</a></li>
        <li><a href="/other/y.txt">other</a></li>
        </ul>
        """;

    [Fact]
    public void Nginx_listing()
    {
        var items = HttpListingFileSystemProvider.ParseListing(Nginx, Folder);
        Assert.Equal(["isos", "readme first.txt", "kernel.tar.xz"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        Assert.Equal((1234L, new DateTime(2024, 2, 6, 11, 30, 0)), (items[1].Size!.Value, items[1].Modified!.Value));
        Assert.Equal(145678901L, items[2].Size);
        Assert.Equal(new DateTime(2024, 3, 7, 12, 0, 5), items[2].Modified);
    }

    [Fact]
    public void Apache_listing()
    {
        var items = HttpListingFileSystemProvider.ParseListing(Apache, Folder);
        Assert.Equal(["isos", "readme first.txt", "kernel.tar.xz"], items.Select(i => i.Name)); // no parent, no sort links
        Assert.Equal(1229L, items[1].Size); // 1.2K
        Assert.Equal(139L * 1024 * 1024, items[2].Size);
        Assert.Equal(new DateTime(2024, 1, 5, 10, 22, 0), items[0].Modified);
    }

    [Fact]
    public void Iis_listing_has_its_details_before_the_link()
    {
        var items = HttpListingFileSystemProvider.ParseListing(Iis, Folder);
        Assert.Equal(["isos", "readme first.txt"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        Assert.Equal((1234L, new DateTime(2024, 2, 6, 11, 30, 0)), (items[1].Size!.Value, items[1].Modified!.Value));
    }

    [Fact]
    public void Plain_lists_keep_to_the_folder_and_host()
    {
        var items = HttpListingFileSystemProvider.ParseListing(PythonServer, Folder);
        Assert.Equal(["isos", "readme first.txt"], items.Select(i => i.Name));
        Assert.Null(items[1].Size);
        Assert.Null(items[1].Modified);
    }

    /// <summary>A server that answers the nginx page for the folder and the file's bytes.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path switch
            {
                "/pub/linux/" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Nginx, Encoding.UTF8, "text/html") },
                "/pub/linux/readme%20first.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Provider_lists_reads_and_refuses_changes()
    {
        using var provider = new HttpListingFileSystemProvider(new FakeServer());
        var folder = VfsPath.Parse("https://files.example.org/pub/linux");
        Assert.True(provider.CanHandle(folder));
        Assert.True(provider.CanHandle(VfsPath.Parse("http://x/y")));
        var entries = await provider.ListAsync(folder, CancellationToken.None);
        var readme = entries.Single(e => e.Name == "readme first.txt");
        Assert.Equal(VfsPath.Parse("https://files.example.org/pub/linux/readme first.txt"), readme.Path);
        Assert.Same(readme, await provider.GetEntryAsync(readme.Path, CancellationToken.None)); // from the listing
        await using (var stream = await provider.OpenReadAsync(readme.Path, CancellationToken.None))
        {
            Assert.Equal("hello", await new StreamReader(stream).ReadToEndAsync());
        }
        Assert.Null(await provider.GetEntryAsync(folder.Combine("gone.txt"), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.DeleteAsync(readme.Path, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.OpenWriteAsync(folder.Combine("new.txt"), false, CancellationToken.None));
    }
}
