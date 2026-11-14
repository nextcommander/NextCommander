using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>Azure Files against an in-memory fake of the File service REST API (shares, folders, ranges, renames, times).</summary>
public sealed class AzureFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>The File service of one account ("acct", path style: files.test/acct/share/...); lists come in pages of two.</summary>
    private sealed class FakeFileService : HttpMessageHandler
    {
        public sealed class Item
        {
            public bool IsFolder { get; init; }

            public byte[] Data { get; set; } = [];

            public DateTime LastWrite { get; set; } = DateTime.UtcNow;
        }

        /// <summary>"share" (a share), "share/a/b.txt" → item.</summary>
        public SortedDictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);

        public List<string> Ranges { get; } = [];

        private const int PageSize = 2;

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            return response;
        }

        private static string Iso(DateTime time) => AzureFilesFileSystemProvider.FileTime(time);

        private static string ParentOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

        private IEnumerable<string> ChildrenOf(string folder) => Items.Keys.Where(k => ParentOf(k) == folder);

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.StartsWith("SharedKey acct:", Header(request, "Authorization"), StringComparison.Ordinal);
            Assert.NotEmpty(Header(request, "x-ms-version"));
            var url = request.RequestUri!;
            Assert.StartsWith("/acct/", url.AbsolutePath, StringComparison.Ordinal); // path style for a host without the account name
            var path = Uri.UnescapeDataString(url.AbsolutePath["/acct/".Length..]).TrimEnd('/');
            var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var restype = query.GetValueOrDefault("restype");
            var comp = query.GetValueOrDefault("comp");
            var method = request.Method.Method;
            var item = Items.GetValueOrDefault(path);

            if (path.Length == 0 && comp == "list")
            {
                var shares = new XElement("Shares", Items.Keys.Where(k => !k.Contains('/', StringComparison.Ordinal))
                    .Select(k => new XElement("Share", new XElement("Name", k), new XElement("Properties", new XElement("Last-Modified", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture))))));
                return Xml(new XElement("EnumerationResults", shares, new XElement("NextMarker")));
            }
            if (restype == "share")
            {
                switch (method)
                {
                    case "HEAD":
                        return item is null ? Error(HttpStatusCode.NotFound, "ShareNotFound") : new HttpResponseMessage(HttpStatusCode.OK);
                    case "PUT":
                        if (item is not null)
                        {
                            return Error(HttpStatusCode.Conflict, "ShareAlreadyExists");
                        }
                        Items[path] = new Item { IsFolder = true };
                        return new HttpResponseMessage(HttpStatusCode.Created);
                    case "DELETE":
                        foreach (var key in Items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                        {
                            Items.Remove(key);
                        }
                        return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
            }
            if (restype == "directory" && comp == "list")
            {
                if (item is not { IsFolder: true })
                {
                    return Error(HttpStatusCode.NotFound, "ResourceNotFound");
                }
                Assert.Equal("Timestamps", query.GetValueOrDefault("include"));
                var all = ChildrenOf(path).ToList();
                var offset = query.TryGetValue("marker", out var marker) ? int.Parse(marker, CultureInfo.InvariantCulture) : 0;
                var entries = all.Skip(offset).Take(PageSize).Select(k =>
                {
                    var child = Items[k];
                    var properties = new XElement("Properties", new XElement("LastWriteTime", Iso(child.LastWrite)));
                    if (!child.IsFolder)
                    {
                        properties.AddFirst(new XElement("Content-Length", child.Data.Length));
                    }
                    return new XElement(child.IsFolder ? "Directory" : "File", new XElement("Name", k[(k.LastIndexOf('/') + 1)..]), properties);
                });
                var next = offset + PageSize < all.Count ? (offset + PageSize).ToString(CultureInfo.InvariantCulture) : string.Empty;
                return Xml(new XElement("EnumerationResults", new XElement("Entries", entries), new XElement("NextMarker", next)));
            }
            if (comp == "rename")
            {
                var source = Uri.UnescapeDataString(new Uri(Header(request, "x-ms-file-rename-source")).AbsolutePath["/acct/".Length..]);
                if (!Items.TryGetValue(source, out var renamed))
                {
                    return Error(HttpStatusCode.NotFound, "ResourceNotFound");
                }
                if (Items.ContainsKey(path))
                {
                    if (Header(request, "x-ms-file-rename-replace-if-exists") != "true")
                    {
                        return Error(HttpStatusCode.Conflict, "ResourceAlreadyExists");
                    }
                    Items.Remove(path);
                }
                Assert.Equal(renamed.IsFolder, restype == "directory");
                foreach (var key in Items.Keys.Where(k => k == source || k.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
                {
                    var moved = Items[key];
                    Items.Remove(key);
                    Items[path + key[source.Length..]] = moved;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (comp == "properties")
            {
                if (item is null)
                {
                    return Error(HttpStatusCode.NotFound, "ResourceNotFound");
                }
                Assert.Equal("preserve", Header(request, "x-ms-file-attributes"));
                if (Header(request, "x-ms-file-last-write-time") is var time && time != "preserve")
                {
                    item.LastWrite = DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (comp == "range")
            {
                var range = Header(request, "x-ms-range")["bytes=".Length..].Split('-').Select(long.Parse).ToArray();
                Assert.Equal("update", Header(request, "x-ms-write"));
                Assert.Equal(range[1] - range[0] + 1, body.Length);
                body.CopyTo(item!.Data, range[0]);
                Ranges.Add($"{range[0]}-{range[1]}");
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            var parentExists = Items.TryGetValue(ParentOf(path), out var parent) && parent.IsFolder;
            switch (method)
            {
                case "HEAD":
                    if (item is null || item.IsFolder != (restype == "directory"))
                    {
                        return Error(HttpStatusCode.NotFound, "ResourceNotFound");
                    }
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    head.Content.Headers.ContentLength = item.Data.Length;
                    head.Headers.TryAddWithoutValidation("x-ms-file-last-write-time", Iso(item.LastWrite));
                    return head;
                case "GET":
                    return item is { IsFolder: false } ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Data) } : Error(HttpStatusCode.NotFound, "ResourceNotFound");
                case "PUT" when restype == "directory":
                    Assert.Equal("inherit", Header(request, "x-ms-file-permission"));
                    if (!parentExists)
                    {
                        return Error(HttpStatusCode.NotFound, "ParentNotFound");
                    }
                    if (item is not null)
                    {
                        return Error(HttpStatusCode.Conflict, "ResourceAlreadyExists");
                    }
                    Items[path] = new Item { IsFolder = true };
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "PUT":
                    Assert.Equal("file", Header(request, "x-ms-type"));
                    if (!parentExists)
                    {
                        return Error(HttpStatusCode.NotFound, "ParentNotFound");
                    }
                    Items[path] = new Item { Data = new byte[long.Parse(Header(request, "x-ms-content-length"), CultureInfo.InvariantCulture)] };
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "DELETE":
                    if (item is null || item.IsFolder != (restype == "directory"))
                    {
                        return Error(HttpStatusCode.NotFound, "ResourceNotFound");
                    }
                    if (item.IsFolder && ChildrenOf(path).Any())
                    {
                        return Error(HttpStatusCode.Conflict, "DirectoryNotEmpty");
                    }
                    Items.Remove(path);
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        private static HttpResponseMessage Xml(XElement root) =>
            new(HttpStatusCode.OK) { Content = new StringContent(new XDocument(root).ToString(), Encoding.UTF8, "application/xml") };
    }

    private static (AzureFilesFileSystemProvider Provider, FakeFileService Fake) Create()
    {
        var fake = new FakeFileService();
        var hooks = new RemoteHooks { GetPassword = (_, retry) => Task.FromResult<string?>(retry ? null : Convert.ToBase64String(new byte[32])) };
        var provider = new AzureFilesFileSystemProvider(hooks) { HandlerFactory = () => fake, RangeSize = 1000 };
        return (provider, fake);
    }

    private static VfsPath P(string path) => VfsPath.Create("azfile", "acct@files.test", path);

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

    [Fact]
    public void Scheme_and_times()
    {
        Assert.Equal(RemoteProtocol.AzureFiles, RemoteConnection.FromAuthority("azfile", "acct@acct.file.core.windows.net").Protocol);
        Assert.True(new RemoteConnection { Protocol = RemoteProtocol.AzureFiles }.IsObjectStorage);
        Assert.Equal("2021-05-06T07:08:09.0000000Z", AzureFilesFileSystemProvider.FileTime(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Shares_folders_and_files_round_trip()
    {
        var (provider, fake) = Create();
        var ct = CancellationToken.None;
        await using var _ = provider;

        await provider.CreateDirectoryAsync(P("/docs"), ct); // a share
        await provider.CreateDirectoryAsync(P("/docs"), ct); // already there: fine
        await provider.CreateDirectoryAsync(P("/docs/Belgeler"), ct);
        Assert.Equal(["docs"], (await provider.ListAsync(P("/"), ct)).Select(e => e.Name));

        var small = Encoding.UTF8.GetBytes("küçük dosya");
        var big = Enumerable.Range(0, 2500).Select(i => (byte)i).ToArray();
        await WriteAsync(provider, P("/docs/Belgeler/ü ş.txt"), small);
        await WriteAsync(provider, P("/docs/Belgeler/büyük.bin"), big);
        Assert.Equal(["0-999", "1000-1999", "2000-2499"], fake.Ranges.Skip(1)); // ranges of RangeSize
        await WriteAsync(provider, P("/docs/Belgeler/n0.txt"), [0]);
        await WriteAsync(provider, P("/docs/Belgeler/n1.txt"), [1]);
        await Assert.ThrowsAsync<IOException>(() => WriteAsync(provider, P("/docs/Belgeler/n0.txt"), [9], overwrite: false));

        var list = await provider.ListAsync(P("/docs/Belgeler"), ct); // four entries in pages of two
        Assert.Equal(["büyük.bin", "n0.txt", "n1.txt", "ü ş.txt"], list.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(big.Length, list.Single(e => e.Name == "büyük.bin").Size);
        Assert.Equal(big, await ReadAsync(provider, P("/docs/Belgeler/büyük.bin")));
        Assert.Equal(small, await ReadAsync(provider, P("/docs/Belgeler/ü ş.txt")));
        Assert.True((await provider.GetEntryAsync(P("/docs/Belgeler"), ct))!.IsDirectory);
        Assert.Null(await provider.GetEntryAsync(P("/docs/yok.txt"), ct));

        // times
        var time = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Local);
        await provider.SetTimesAsync(P("/docs/Belgeler/n0.txt"), null, time, ct);
        Assert.Equal(time, (await provider.GetEntryAsync(P("/docs/Belgeler/n0.txt"), ct))!.LastWriteTime);
        Assert.Equal(time, (await provider.ListAsync(P("/docs/Belgeler"), ct)).Single(e => e.Name == "n0.txt").LastWriteTime);

        // rename, a folder with its files, overwrite an existing target
        await provider.MoveAsync(P("/docs/Belgeler/n0.txt"), P("/docs/Belgeler/yeni ad.txt"), overwrite: false, ct);
        await Assert.ThrowsAsync<IOException>(() => provider.MoveAsync(P("/docs/Belgeler/n1.txt"), P("/docs/Belgeler/yeni ad.txt"), overwrite: false, ct));
        await provider.MoveAsync(P("/docs/Belgeler/n1.txt"), P("/docs/Belgeler/yeni ad.txt"), overwrite: true, ct);
        Assert.Equal([1], await ReadAsync(provider, P("/docs/Belgeler/yeni ad.txt")));
        await provider.MoveAsync(P("/docs/Belgeler"), P("/docs/Arşiv"), overwrite: false, ct);
        Assert.Equal(big, await ReadAsync(provider, P("/docs/Arşiv/büyük.bin")));
        Assert.Null(await provider.GetEntryAsync(P("/docs/Belgeler"), ct));
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() => provider.MoveAsync(P("/docs/Arşiv/büyük.bin"), P("/other/büyük.bin"), overwrite: false, ct));

        // delete: a folder that is not empty needs recursive (folders inside go too)
        await provider.CreateDirectoryAsync(P("/docs/Arşiv/Alt"), ct);
        await WriteAsync(provider, P("/docs/Arşiv/Alt/c.txt"), [3]);
        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync(P("/docs/Arşiv"), recursive: false, ct));
        await provider.DeleteAsync(P("/docs/Arşiv"), recursive: true, ct);
        Assert.Equal(["docs"], fake.Items.Keys);
        await provider.DeleteAsync(P("/docs"), recursive: false, ct); // an empty share
        Assert.Empty(fake.Items);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAsync(provider, P("/docs/yok.txt")));
    }

    [Fact]
    public async Task Copy_job_keeps_the_time()
    {
        var (provider, fake) = Create();
        await using var _ = provider;
        fake.Items["docs"] = new FakeFileService.Item { IsFolder = true };
        var local = new LocalFileSystemProvider();
        var file = _dir.Write("kaynak.txt", "içerik", new DateTime(2020, 2, 3, 4, 5, 6));
        var registry = new ProviderRegistry([local, provider]);
        var source = await local.GetEntryAsync(VfsPath.Local(file), CancellationToken.None);
        var job = new CopyMoveJob(registry, [source!], P("/docs"), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString() ?? string.Join("; ", job.Errors.Select(e => e.Message)));
        var copied = await provider.GetEntryAsync(P("/docs/kaynak.txt"), CancellationToken.None);
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), copied!.LastWriteTime);
        Assert.Equal("içerik", Encoding.UTF8.GetString(await ReadAsync(provider, P("/docs/kaynak.txt"))));
    }
}
