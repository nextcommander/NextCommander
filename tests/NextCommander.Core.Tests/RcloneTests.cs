using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>
/// rclone remotes: the provider against a fake of rclone's rc API (two remotes in memory; local paths are this
/// machine's), and against the real rclone when FM_TEST_RCLONE names a remote (e.g. a "local" or "memory" remote).
/// </summary>
public sealed class RcloneTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>The rc API over remotes "box1" and "box2" kept in memory ("remote:path" → item).</summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        public sealed record Item(bool IsDir, byte[] Data, DateTime ModTime);

        public SortedDictionary<string, Item> Items { get; } = new(StringComparer.Ordinal) { ["box1:"] = new(true, [], DateTime.UtcNow), ["box2:"] = new(true, [], DateTime.UtcNow) };

        public List<string> Calls { get; } = [];

        private static string Key(string fs, string remote) => fs.EndsWith(':') ? fs + remote.Trim('/') : fs.TrimEnd('/') + "/" + remote.Trim('/');

        private static bool IsLocal(string fs) => !fs.EndsWith(':') && !fs.Contains(":/", StringComparison.Ordinal) || Path.IsPathRooted(fs);

        private static string ParentOf(string key) => key.Contains('/', StringComparison.Ordinal) ? key[..key.LastIndexOf('/')] : key[..(key.IndexOf(':') + 1)];

        private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(string message, HttpStatusCode status) =>
            Json(new JsonObject { ["error"] = message, ["status"] = (int)status }, status);

        private JsonObject ItemJson(string key) => new()
        {
            ["Path"] = key[(key.IndexOf(':') + 1)..], ["Name"] = key[(Math.Max(key.LastIndexOf('/'), key.IndexOf(':')) + 1)..],
            ["Size"] = Items[key].IsDir ? -1 : Items[key].Data.Length, ["IsDir"] = Items[key].IsDir,
            ["ModTime"] = Items[key].ModTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
        };

        private void Put(string key, Item item)
        {
            for (var parent = ParentOf(key); !Items.ContainsKey(parent); parent = ParentOf(parent))
            {
                Items[parent] = new Item(true, [], DateTime.UtcNow); // rclone makes the folders on the way
            }
            Items[key] = item;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var command = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add(command);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            string S(string name) => body[name]?.GetValue<string>() ?? string.Empty;
            switch (command)
            {
                case "config/listremotes":
                    return Json(new JsonObject { ["remotes"] = new JsonArray("box1", "box2") });
                case "operations/list":
                    var folder = Key(S("fs"), S("remote"));
                    if (!Items.TryGetValue(folder, out var dir) || !dir.IsDir)
                    {
                        return Error("error in ListJSON: directory not found", HttpStatusCode.InternalServerError);
                    }
                    return Json(new JsonObject { ["list"] = new JsonArray(Items.Keys.Where(k => k != folder && ParentOf(k) == folder).Select(k => (JsonNode)ItemJson(k)).ToArray()) });
                case "operations/stat":
                    var key = Key(S("fs"), S("remote"));
                    return Json(new JsonObject { ["item"] = Items.ContainsKey(key) ? ItemJson(key) : null });
                case "operations/mkdir":
                    Put(Key(S("fs"), S("remote")), new Item(true, [], DateTime.UtcNow));
                    return Json(new JsonObject());
                case "operations/copyfile" or "operations/movefile":
                    byte[] data;
                    DateTime time;
                    if (IsLocal(S("srcFs")))
                    {
                        var local = Path.Combine(S("srcFs"), S("srcRemote"));
                        (data, time) = (File.ReadAllBytes(local), File.GetLastWriteTimeUtc(local));
                    }
                    else if (Items.TryGetValue(Key(S("srcFs"), S("srcRemote")), out var source) && !source.IsDir)
                    {
                        (data, time) = (source.Data, source.ModTime);
                        if (command == "operations/movefile")
                        {
                            Items.Remove(Key(S("srcFs"), S("srcRemote")));
                        }
                    }
                    else
                    {
                        return Error("object not found", HttpStatusCode.NotFound);
                    }
                    if (IsLocal(S("dstFs")))
                    {
                        var target = Path.Combine(S("dstFs"), S("dstRemote"));
                        File.WriteAllBytes(target, data);
                        File.SetLastWriteTimeUtc(target, time);
                    }
                    else
                    {
                        Put(Key(S("dstFs"), S("dstRemote")), new Item(false, data, time)); // the time is kept
                    }
                    return Json(new JsonObject());
                case "operations/deletefile" or "operations/rmdir" or "operations/purge":
                    var target2 = Key(S("fs"), S("remote"));
                    var under = Items.Keys.Where(k => k.StartsWith(target2 + "/", StringComparison.Ordinal)).ToList();
                    if (command == "operations/rmdir" && under.Count > 0)
                    {
                        return Error("rmdir failed: directory not empty", HttpStatusCode.InternalServerError);
                    }
                    under.ForEach(k => Items.Remove(k));
                    Items.Remove(target2);
                    return Json(new JsonObject());
                case "sync/move":
                    var from = S("srcFs");
                    var to = S("dstFs");
                    foreach (var k in Items.Keys.Where(k => k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
                    {
                        var moved = Items[k];
                        Items.Remove(k);
                        Put(to + k[from.Length..], moved);
                    }
                    if (!Items.ContainsKey(to))
                    {
                        Put(to, new Item(true, [], DateTime.UtcNow));
                    }
                    return Json(new JsonObject());
                case "operations/rmdirs":
                    var root = Key(S("fs"), S("remote"));
                    if (!Items.ContainsKey(root))
                    {
                        return Error("directory not found", HttpStatusCode.NotFound);
                    }
                    Assert.DoesNotContain(Items.Keys, k => k.StartsWith(root + "/", StringComparison.Ordinal) && !Items[k].IsDir);
                    foreach (var k in Items.Keys.Where(k => k == root || k.StartsWith(root + "/", StringComparison.Ordinal)).ToList())
                    {
                        Items.Remove(k);
                    }
                    return Json(new JsonObject());
            }
            return Error("unknown command " + command, HttpStatusCode.NotFound);
        }
    }

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

    /// <summary>Files and folders on one remote: write, list, read, rename, move a folder, delete; times kept by a copy.</summary>
    private async Task RoundTripAsync(RcloneFileSystemProvider provider, VfsPath folder)
    {
        var ct = CancellationToken.None;
        VfsPath P(string relative) => folder.Combine(relative.TrimStart('/'));
        await provider.CreateDirectoryAsync(P("Belgeler"), ct);
        var data = Encoding.UTF8.GetBytes("küçük dosya");
        await WriteAsync(provider, P("Belgeler/ü ş.txt"), data);
        await WriteAsync(provider, P("Belgeler/n1.txt"), [1]);
        await Assert.ThrowsAsync<IOException>(() => WriteAsync(provider, P("Belgeler/n1.txt"), [9], overwrite: false));
        Assert.Equal(["n1.txt", "ü ş.txt"], (await provider.ListAsync(P("Belgeler"), ct)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(data, await ReadAsync(provider, P("Belgeler/ü ş.txt")));
        Assert.Equal(data.Length, (await provider.GetEntryAsync(P("Belgeler/ü ş.txt"), ct))!.Size);
        Assert.True((await provider.GetEntryAsync(P("Belgeler"), ct))!.IsDirectory);
        Assert.Null(await provider.GetEntryAsync(P("yok.txt"), ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAsync(provider, P("yok.txt")));

        await provider.MoveAsync(P("Belgeler/n1.txt"), P("Belgeler/yeni ad.txt"), overwrite: false, ct);
        Assert.Equal([1], await ReadAsync(provider, P("Belgeler/yeni ad.txt")));
        await provider.MoveAsync(P("Belgeler"), P("Arşiv"), overwrite: false, ct);
        Assert.Null(await provider.GetEntryAsync(P("Belgeler"), ct));
        Assert.Equal(data, await ReadAsync(provider, P("Arşiv/ü ş.txt")));
        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync(P("Arşiv"), recursive: false, ct));

        // a copy from the computer keeps the time
        var local = new LocalFileSystemProvider();
        var file = _dir.Write("kaynak.txt", "içerik", new DateTime(2020, 2, 3, 4, 5, 6));
        var job = new CopyMoveJob(new ProviderRegistry([local, provider]), [(await local.GetEntryAsync(VfsPath.Local(file), ct))!], P("Arşiv"), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString() ?? string.Join("; ", job.Errors.Select(e => e.Message)));
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), (await provider.GetEntryAsync(P("Arşiv/kaynak.txt"), ct))!.LastWriteTime);

        await provider.DeleteAsync(P("Arşiv"), recursive: true, ct);
        Assert.Null(await provider.GetEntryAsync(P("Arşiv"), ct));
    }

    [Fact]
    public async Task Remotes_and_files_through_the_rc_api()
    {
        var fake = new FakeRclone();
        using var provider = new RcloneFileSystemProvider(tool: null) { HandlerFactory = () => fake };
        Assert.True(provider.IsInstalled);
        var remotes = await provider.ListAsync(RcloneFileSystemProvider.Root, CancellationToken.None);
        Assert.Equal(["box1", "box2"], remotes.Select(r => r.Name));
        Assert.Equal(VfsPath.Create("rclone", "box1", "/"), remotes[0].Path);
        await RoundTripAsync(provider, VfsPath.Create("rclone", "box1", "/"));
        Assert.Equal(["box1:", "box2:"], fake.Items.Keys);
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() =>
            provider.MoveAsync(VfsPath.Create("rclone", "box1", "/a"), VfsPath.Create("rclone", "box2", "/a"), false, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "object not found", typeof(FileNotFoundException))]
    [InlineData(HttpStatusCode.InternalServerError, "directory not found", typeof(FileNotFoundException))]
    [InlineData(HttpStatusCode.InternalServerError, "googleapi: Error 403: Forbidden", typeof(UnauthorizedAccessException))]
    [InlineData(HttpStatusCode.InternalServerError, "something else", typeof(IOException))]
    public void Errors_become_exceptions(HttpStatusCode status, string message, Type type) =>
        Assert.IsType(type, RcloneFileSystemProvider.ErrorOf(status, message, "/x"));

    [Fact]
    public void Paths_of_a_remote()
    {
        var path = VfsPath.Create("rclone", "gdrive", "/a b/c.txt");
        Assert.Equal(("gdrive:", "a b/c.txt"), (RcloneFileSystemProvider.FsOf(path), RcloneFileSystemProvider.RemoteOf(path)));
        Assert.Equal(string.Empty, RcloneFileSystemProvider.RemoteOf(VfsPath.Create("rclone", "gdrive", "/")));
    }

    /// <summary>The real rclone: FM_TEST_RCLONE=remote (a test remote; a folder is made in it and deleted again).</summary>
    [Fact]
    public async Task Real_rclone_when_configured()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_RCLONE") is not { Length: > 0 } remote || RcloneFileSystemProvider.FindTool() is not { } tool)
        {
            return;
        }
        using var provider = new RcloneFileSystemProvider(tool);
        Assert.Contains(await provider.ListAsync(RcloneFileSystemProvider.Root, CancellationToken.None), r => r.Name == remote);
        await RoundTripAsync(provider, VfsPath.Create("rclone", remote, "/fm-test-" + Guid.NewGuid().ToString("N")[..6]));
    }
}
