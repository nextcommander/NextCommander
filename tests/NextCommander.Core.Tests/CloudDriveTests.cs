using System.Text;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;
using NextCommander.Vfs.Remote.Cloud;

namespace NextCommander.Core.Tests;

/// <summary>
/// Google Drive, OneDrive and Dropbox providers against an in-memory fake of their APIs: browser sign-in over the
/// loopback port, token refresh, paging, uploads (single and in sessions), times, moves, deletes.
/// </summary>
public sealed class CloudDriveTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed record Setup(CloudProviderBase Provider, FakeCloud Fake, RemoteHooks Hooks, string Scheme, List<string?> Saved);

    private static Setup Create(RemoteProtocol protocol, string? savedToken)
    {
        var fake = new FakeCloud();
        var saved = new List<string?>();
        var hooks = new RemoteHooks
        {
            ResolveConnection = (scheme, authority) =>
            {
                var c = RemoteConnection.FromAuthority(scheme, authority);
                c.ClientId = "client-1";
                c.SavePassword = true;
                return c;
            },
            LoadToken = _ => Task.FromResult(savedToken),
            SaveToken = (_, token) =>
            {
                saved.Add(token);
                return Task.CompletedTask;
            },
            OpenBrowser = uri => Task.FromException(new InvalidOperationException("no browser in this test")),
        };
        CloudProviderBase provider = protocol switch
        {
            RemoteProtocol.OneDrive => new OneDriveFileSystemProvider(hooks)
            {
                HandlerFactory = () => fake, ServiceOverride = FakeCloud.Auth, ApiBase = "https://graph.test/me/drive", SimpleUploadLimit = 1000, ChunkSize = 300,
            },
            RemoteProtocol.Dropbox => new DropboxFileSystemProvider(hooks)
            {
                HandlerFactory = () => fake, ServiceOverride = FakeCloud.Auth, ApiBase = "https://api.dbx.test/2", ContentBase = "https://content.dbx.test/2",
                SimpleUploadLimit = 1000, ChunkSize = 300,
            },
            RemoteProtocol.Box => new BoxFileSystemProvider(hooks)
            {
                HandlerFactory = () => fake, ServiceOverride = FakeCloud.Auth, ApiBase = "https://box.test/2.0", UploadBase = "https://upload.box.test/api/2.0",
                SimpleUploadLimit = 1000, CommitRetryDelay = TimeSpan.Zero,
            },
            _ => new GoogleDriveFileSystemProvider(hooks)
            {
                HandlerFactory = () => fake, ServiceOverride = FakeCloud.Auth, ApiBase = "https://g.test/drive/v3", UploadBase = "https://g.test/upload/drive/v3",
            },
        };
        return new Setup(provider, fake, hooks, provider.Scheme, saved);
    }

    private static VfsPath P(Setup s, string path) => VfsPath.Create(s.Scheme, "hesap@" + RemoteConnection.CloudHost(RemoteConnection.FromAuthority(s.Scheme, "x").Protocol), path);

    private static async Task WriteAsync(IFileSystemProvider provider, VfsPath path, byte[] data, bool overwrite = true)
    {
        await using var stream = await provider.OpenWriteAsync(path, overwrite, CancellationToken.None);
        await stream.WriteAsync(data);
    }

    private static async Task<byte[]> ReadAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Theory]
    [InlineData(RemoteProtocol.OneDrive)]
    [InlineData(RemoteProtocol.Dropbox)]
    [InlineData(RemoteProtocol.GoogleDrive)]
    [InlineData(RemoteProtocol.Box)]
    public async Task Files_and_folders_round_trip(RemoteProtocol protocol)
    {
        var s = Create(protocol, savedToken: "rt-saved");
        var provider = s.Provider;
        var ct = CancellationToken.None;

        await provider.CreateDirectoryAsync(P(s, "/Belgeler"), ct);
        await provider.CreateDirectoryAsync(P(s, "/Belgeler"), ct); // already there: fine
        var small = Encoding.UTF8.GetBytes("küçük dosya");
        var big = Enumerable.Range(0, 2500).Select(i => (byte)i).ToArray(); // above the single request limit: an upload session
        await WriteAsync(provider, P(s, "/Belgeler/ü ş.txt"), small);
        await WriteAsync(provider, P(s, "/Belgeler/büyük.bin"), big);
        for (var i = 0; i < 3; i++)
        {
            await WriteAsync(provider, P(s, $"/Belgeler/n{i}.txt"), [(byte)i]);
        }
        await Assert.ThrowsAsync<IOException>(() => WriteAsync(provider, P(s, "/Belgeler/n0.txt"), [9], overwrite: false));

        var list = await provider.ListAsync(P(s, "/Belgeler"), ct); // five entries in pages of two
        Assert.Equal(["büyük.bin", "n0.txt", "n1.txt", "n2.txt", "ü ş.txt"], list.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(big.Length, list.Single(e => e.Name == "büyük.bin").Size);
        Assert.Contains(await provider.ListAsync(P(s, "/"), ct), e => e is { Name: "Belgeler", IsDirectory: true });
        Assert.Equal(small, await ReadAsync(provider, P(s, "/Belgeler/ü ş.txt")));
        Assert.Equal(big, await ReadAsync(provider, P(s, "/Belgeler/büyük.bin")));
        Assert.Equal(big, s.Fake.ByPath("/Belgeler/büyük.bin")!.Data);

        // times: set after writing (Dropbox: given with the upload)
        var time = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Local);
        if (provider is IUploadTimeSupport upload)
        {
            upload.PrepareUpload(P(s, "/Belgeler/zaman.txt"), time);
            await WriteAsync(provider, P(s, "/Belgeler/zaman.txt"), [1]);
        }
        else
        {
            await WriteAsync(provider, P(s, "/Belgeler/zaman.txt"), [1]);
            await provider.SetTimesAsync(P(s, "/Belgeler/zaman.txt"), null, time, ct);
        }
        Assert.Equal(time, (await provider.GetEntryAsync(P(s, "/Belgeler/zaman.txt"), ct))!.LastWriteTime);

        // rename, move into a subfolder, overwrite an existing target
        await provider.MoveAsync(P(s, "/Belgeler/n0.txt"), P(s, "/Belgeler/yeni ad.txt"), overwrite: false, ct);
        await provider.CreateDirectoryAsync(P(s, "/Belgeler/Alt"), ct);
        await provider.MoveAsync(P(s, "/Belgeler/yeni ad.txt"), P(s, "/Belgeler/Alt/yeni ad.txt"), overwrite: false, ct);
        await Assert.ThrowsAsync<IOException>(() => provider.MoveAsync(P(s, "/Belgeler/n1.txt"), P(s, "/Belgeler/n2.txt"), overwrite: false, ct));
        await provider.MoveAsync(P(s, "/Belgeler/n1.txt"), P(s, "/Belgeler/n2.txt"), overwrite: true, ct);
        Assert.Equal([1], await ReadAsync(provider, P(s, "/Belgeler/n2.txt")));
        Assert.Null(await provider.GetEntryAsync(P(s, "/Belgeler/n0.txt"), ct));
        Assert.Null(await provider.GetEntryAsync(P(s, "/Belgeler/n1.txt"), ct));
        Assert.Equal([0], await ReadAsync(provider, P(s, "/Belgeler/Alt/yeni ad.txt")));

        // delete: a folder that is not empty needs recursive
        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync(P(s, "/Belgeler/Alt"), recursive: false, ct));
        await provider.DeleteAsync(P(s, "/Belgeler/Alt"), recursive: true, ct);
        await provider.DeleteAsync(P(s, "/Belgeler/ü ş.txt"), recursive: false, ct);
        Assert.Null(await provider.GetEntryAsync(P(s, "/Belgeler/Alt"), ct));
        Assert.Null(await provider.GetEntryAsync(P(s, "/Belgeler/ü ş.txt"), ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAsync(provider, P(s, "/yok.txt")));

        // the saved refresh token was used (no browser); an early 401 and a 429 are retried
        Assert.Equal(1, s.Fake.Refreshes);
        s.Fake.FailNextWith401 = true;
        Assert.NotEmpty(await provider.ListAsync(P(s, "/Belgeler"), ct));
        Assert.Equal(2, s.Fake.Refreshes);
        s.Fake.FailNextWith429 = true;
        Assert.NotEmpty(await provider.ListAsync(P(s, "/Belgeler"), ct));
        Assert.Empty(s.Saved); // the refresh token did not change
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Copy_job_keeps_times_on_every_service()
    {
        var local = new LocalFileSystemProvider();
        var file = _dir.Write("kaynak.txt", "içerik", new DateTime(2020, 2, 3, 4, 5, 6));
        foreach (var protocol in new[] { RemoteProtocol.OneDrive, RemoteProtocol.Dropbox, RemoteProtocol.GoogleDrive, RemoteProtocol.Box })
        {
            var s = Create(protocol, "rt-saved");
            var registry = new ProviderRegistry([local, s.Provider]);
            var source = await local.GetEntryAsync(VfsPath.Local(file), CancellationToken.None);
            await new CopyMoveJob(registry, [source!], P(s, "/"), move: false).RunAsync();
            var copied = await s.Provider.GetEntryAsync(P(s, "/kaynak.txt"), CancellationToken.None);
            Assert.NotNull(copied);
            Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), copied.LastWriteTime);
            Assert.Equal("içerik", Encoding.UTF8.GetString(await ReadAsync(s.Provider, P(s, "/kaynak.txt"))));
        }
    }

    [Fact]
    public async Task Google_documents_are_shown_with_an_office_extension_and_exported()
    {
        var s = Create(RemoteProtocol.GoogleDrive, "rt-saved");
        s.Fake.Add("/", "Rapor", mimeType: "application/vnd.google-apps.document");
        s.Fake.Add("/", "Form", mimeType: "application/vnd.google-apps.form"); // not exportable: hidden
        s.Fake.Add("/", "O'Neil notları.txt", Encoding.UTF8.GetBytes("q")); // a quote in a query
        var names = (await s.Provider.ListAsync(P(s, "/"), CancellationToken.None)).Select(e => e.Name).ToList();
        Assert.Equal(["O'Neil notları.txt", "Rapor.docx", "Shared drives", "Shared with me"], names.Order(StringComparer.Ordinal));
        Assert.StartsWith("exported as application/vnd.openxmlformats-officedocument.wordprocessingml",
            Encoding.UTF8.GetString(await ReadAsync(s.Provider, P(s, "/Rapor.docx"))), StringComparison.Ordinal);

        // a fresh provider resolves the paths from scratch
        var again = new GoogleDriveFileSystemProvider(s.Hooks)
        {
            HandlerFactory = () => s.Fake, ServiceOverride = FakeCloud.Auth, ApiBase = "https://g.test/drive/v3", UploadBase = "https://g.test/upload/drive/v3",
        };
        Assert.Equal("q", Encoding.UTF8.GetString(await ReadAsync(again, P(s, "/O'Neil notları.txt"))));
        Assert.NotNull(await again.GetEntryAsync(P(s, "/Rapor.docx"), CancellationToken.None));
    }

    [Fact]
    public async Task Google_shared_files_and_shared_drives()
    {
        var s = Create(RemoteProtocol.GoogleDrive, "rt-saved");
        var ct = CancellationToken.None;
        s.Fake.AddShared("teklif.pdf", Encoding.UTF8.GetBytes("pdf"));
        var team = s.Fake.AddDrive("Ekip");
        s.Fake.Add("/", "kendi.txt", [1]);
        s.Fake.AddInto(team, "plan.txt", Encoding.UTF8.GetBytes("plan"));

        var shared = await s.Provider.ListAsync(P(s, "/Shared with me"), ct);
        Assert.Equal(["teklif.pdf"], shared.Select(e => e.Name));
        Assert.Equal("pdf", Encoding.UTF8.GetString(await ReadAsync(s.Provider, P(s, "/Shared with me/teklif.pdf"))));
        Assert.True((await s.Provider.GetEntryAsync(P(s, "/Shared drives"), ct))!.IsDirectory);
        Assert.Equal(["Ekip"], (await s.Provider.ListAsync(P(s, "/Shared drives"), ct)).Select(e => e.Name));
        Assert.Equal(["plan.txt"], (await s.Provider.ListAsync(P(s, "/Shared drives/Ekip"), ct)).Select(e => e.Name));
        Assert.Equal("plan", Encoding.UTF8.GetString(await ReadAsync(s.Provider, P(s, "/Shared drives/Ekip/plan.txt"))));

        // writing into a shared drive works; the virtual folders take nothing and cannot be removed
        await WriteAsync(s.Provider, P(s, "/Shared drives/Ekip/yeni.txt"), [7]);
        Assert.Contains(s.Fake.ChildrenOf(team), n => n.Name == "yeni.txt");
        await Assert.ThrowsAnyAsync<IOException>(() => WriteAsync(s.Provider, P(s, "/Shared with me/olmaz.txt"), [1]));
        await Assert.ThrowsAnyAsync<IOException>(() => s.Provider.CreateDirectoryAsync(P(s, "/Shared drives/Yeni ekip"), ct));
        await Assert.ThrowsAnyAsync<IOException>(() => s.Provider.DeleteAsync(P(s, "/Shared drives"), recursive: true, ct));
        await Assert.ThrowsAnyAsync<IOException>(() => s.Provider.DeleteAsync(P(s, "/Shared drives/Ekip"), recursive: true, ct));
    }

    [Fact]
    public async Task OneDrive_sharepoint_sites_and_libraries()
    {
        var s = Create(RemoteProtocol.OneDrive, "rt-saved");
        var ct = CancellationToken.None;
        var resolve = s.Hooks.ResolveConnection;
        s.Hooks.ResolveConnection = (scheme, authority) =>
        {
            var c = resolve(scheme, authority);
            c.IncludeSharePoint = true;
            return c;
        };
        var library = s.Fake.AddLibrary("Pazarlama", "Belgeler");
        s.Fake.AddLibrary("Pazarlama", "Sunumlar");
        s.Fake.AddInto(library, "rapor.txt", Encoding.UTF8.GetBytes("rapor"));
        await WriteAsync(s.Provider, P(s, "/kendi.txt"), [1]);

        Assert.Contains(await s.Provider.ListAsync(P(s, "/"), ct), e => e is { Name: "SharePoint", IsDirectory: true });
        Assert.Equal(["Pazarlama"], (await s.Provider.ListAsync(P(s, "/SharePoint"), ct)).Select(e => e.Name));
        Assert.Equal(["Belgeler", "Sunumlar"], (await s.Provider.ListAsync(P(s, "/SharePoint/Pazarlama"), ct)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["rapor.txt"], (await s.Provider.ListAsync(P(s, "/SharePoint/Pazarlama/Belgeler"), ct)).Select(e => e.Name));
        Assert.Equal("rapor", Encoding.UTF8.GetString(await ReadAsync(s.Provider, P(s, "/SharePoint/Pazarlama/Belgeler/rapor.txt"))));
        Assert.True((await s.Provider.GetEntryAsync(P(s, "/SharePoint/Pazarlama/Belgeler"), ct))!.IsDirectory);

        // inside a library: like the own drive (folders, big uploads, moves)
        await s.Provider.CreateDirectoryAsync(P(s, "/SharePoint/Pazarlama/Belgeler/Alt"), ct);
        var big = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray();
        await WriteAsync(s.Provider, P(s, "/SharePoint/Pazarlama/Belgeler/Alt/büyük.bin"), big);
        Assert.Equal(big, await ReadAsync(s.Provider, P(s, "/SharePoint/Pazarlama/Belgeler/Alt/büyük.bin")));
        await s.Provider.MoveAsync(P(s, "/SharePoint/Pazarlama/Belgeler/rapor.txt"), P(s, "/SharePoint/Pazarlama/Belgeler/Alt/rapor2.txt"), overwrite: false, ct);
        Assert.Equal(["büyük.bin", "rapor2.txt"], (await s.Provider.ListAsync(P(s, "/SharePoint/Pazarlama/Belgeler/Alt"), ct)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Null(s.Fake.ByPath("/Alt")); // nothing landed in the own drive

        // between drives a move is a copy; sites and libraries are not changed from here
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() => s.Provider.MoveAsync(P(s, "/kendi.txt"), P(s, "/SharePoint/Pazarlama/Belgeler/kendi.txt"), overwrite: false, ct));
        await Assert.ThrowsAnyAsync<IOException>(() => s.Provider.DeleteAsync(P(s, "/SharePoint/Pazarlama/Belgeler"), recursive: true, ct));
        await Assert.ThrowsAnyAsync<IOException>(() => WriteAsync(s.Provider, P(s, "/SharePoint/olmaz.txt"), [1]));
        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => s.Provider.ListAsync(P(s, "/SharePoint/Yok"), ct));

        // without the option the root has no SharePoint folder
        var plain = Create(RemoteProtocol.OneDrive, "rt-saved");
        Assert.DoesNotContain(await plain.Provider.ListAsync(P(plain, "/"), ct), e => e.Name == "SharePoint");
    }

    [Fact]
    public void Request_shapes()
    {
        var onedrive = new OneDriveFileSystemProvider(new RemoteHooks()) { ApiBase = "https://g/me/drive" };
        Assert.Equal("https://g/me/drive/root/children", onedrive.ItemUri("/", "children"));
        Assert.Equal("https://g/me/drive/root:/a%20b/%C3%BC%23:/content", onedrive.ItemUri("/a b/ü#", "content"));
        var arg = DropboxFileSystemProvider.ApiArg(new JsonObject { ["path"] = "/ü ş" });
        Assert.True(arg.All(ch => ch < 128), arg);
        Assert.Equal("/ü ş", JsonNode.Parse(arg)!["path"]!.GetValue<string>());
        Assert.Equal(string.Empty, DropboxFileSystemProvider.ApiPath("/"));
        Assert.Equal("/a/b", DropboxFileSystemProvider.ApiPath("/a/b/"));
        Assert.Equal("'O\\'Neil \\\\ x'", GoogleDriveFileSystemProvider.QueryString("O'Neil \\ x"));

        var (verifier, challenge) = OAuthClient.CreatePkce();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('=', challenge);
        var uri = OAuthClient.AuthorizeUri(OAuthService.Google, "id 1", "st", challenge);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2F", uri.Query, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", uri.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=id%201", uri.Query, StringComparison.Ordinal);
        Assert.Equal("http://localhost:53682/", OAuthService.Dropbox.RedirectUri);

        Assert.Equal(RemoteProtocol.GoogleDrive, RemoteConnection.FromAuthority("gdrive", "kisisel@drive.google.com").Protocol);
        var c = RemoteConnection.FromAuthority("onedrive", "is@onedrive.live.com");
        Assert.True(c.IsCloudDrive);
        Assert.Equal("onedrive://is@onedrive.live.com", c.SecretId);
    }

    /// <summary>No saved token: the browser opens, the code comes back to the loopback port, the new refresh token is kept.</summary>
    [Fact]
    public async Task Browser_sign_in_over_the_loopback_port()
    {
        var s = Create(RemoteProtocol.OneDrive, savedToken: null);
        string? deniedState = null;
        using var browser = new HttpClient();
        s.Hooks.OpenBrowser = async uri =>
        {
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("client-1", query["client_id"]);
            var redirect = query["redirect_uri"];
            deniedState = query["state"];
            // a stray request (favicon) and a forged state are ignored; then the real answer
            _ = await browser.GetAsync(redirect + "favicon.ico");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await browser.GetAsync(redirect + "?code=evil&state=other")).StatusCode);
            var page = await browser.GetStringAsync(redirect + "?code=good-code&state=" + Uri.EscapeDataString(query["state"]));
            Assert.Contains("sign-in complete", page, StringComparison.Ordinal);
        };
        Assert.Empty(await s.Provider.ListAsync(P(s, "/"), CancellationToken.None));
        Assert.Equal(["rt-new"], s.Saved);
        Assert.NotNull(deniedState);

        // a refused sign-in
        var refused = Create(RemoteProtocol.Dropbox, savedToken: null);
        refused.Hooks.OpenBrowser = async uri =>
        {
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            await browser.GetStringAsync(query["redirect_uri"] + "?error=access_denied&state=" + Uri.EscapeDataString(query["state"]));
        };
        var error = await Assert.ThrowsAnyAsync<IOException>(() => refused.Provider.ListAsync(P(refused, "/"), CancellationToken.None));
        Assert.Contains("access_denied", error.ToString(), StringComparison.Ordinal);
    }
}
