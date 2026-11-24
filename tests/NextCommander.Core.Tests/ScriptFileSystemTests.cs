using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Scripting;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>File systems written in JavaScript (user's request, 24 November 2026).</summary>
public sealed class ScriptFileSystemTests : IDisposable
{
    private const string Memory = """
        var title = "Memory";
        var files = { "/a.txt": "hello", "/docs/b.txt": "bee" };
        var dirs = { "/": true, "/docs": true };
        function parentOf(p) { return p.substring(0, p.lastIndexOf("/")) || "/"; }
        function list(path) {
          if (!dirs[path]) throw new Error("No folder " + path);
          var r = [];
          for (var d in dirs) if (d !== "/" && parentOf(d) === path) r.push({ name: d.substring(d.lastIndexOf("/") + 1), isDir: true });
          for (var f in files) if (parentOf(f) === path) r.push({ name: f.substring(f.lastIndexOf("/") + 1), size: files[f].length, modified: "2025-01-02T03:04:05Z" });
          return r;
        }
        function read(path) { if (files[path] === undefined) throw new Error("No file " + path); return files[path]; }
        function write(path, data) { files[path] = data.text; }
        function mkdir(path) { dirs[path] = true; }
        function remove(path, isDir) { delete files[path]; delete dirs[path]; }
        function rename(from, to) { files[to] = files[from]; delete files[from]; }
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<string> TextOf(IFileSystemProvider fs, VfsPath path)
    {
        await using var stream = await fs.OpenReadAsync(path, CancellationToken.None);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    [Fact]
    public async Task A_script_lists_reads_writes_and_changes_its_place()
    {
        var fs = ScriptFileSystemProvider.Load(Path.Combine(_dir.Root, "My Memory.js"), Memory);
        Assert.Equal("js-my-memory", fs.Scheme);
        Assert.Equal("Memory", fs.EntryName);
        Assert.Equal("JavaScript", fs.EntryGroup);
        Assert.True(fs.Capabilities.HasFlag(ProviderCapabilities.Write | ProviderCapabilities.Rename));
        var root = fs.EntryRoot;
        var listed = await fs.ListAsync(root, CancellationToken.None);
        Assert.Equal(["a.txt", "docs"], listed.Select(e => e.Name).Order());
        var a = listed.Single(e => e.Name == "a.txt");
        Assert.Equal(5, a.Size);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime(), a.LastWriteTime);
        Assert.True(listed.Single(e => e.Name == "docs").IsDirectory);
        Assert.Equal("hello", await TextOf(fs, a.Path));
        Assert.True((await fs.GetEntryAsync(root.Combine("docs"), CancellationToken.None))!.IsDirectory);
        Assert.Null(await fs.GetEntryAsync(root.Combine("none"), CancellationToken.None));

        // copied in from this computer through the copy job, folders made, renamed, deleted
        _dir.Write("src/c.txt", "çay ☕");
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local, fs]);
        var source = (await local.GetEntryAsync(_dir.VPath("src", "c.txt"), CancellationToken.None))!;
        var copy = new CopyMoveJob(registry, [source], root.Combine("docs"), move: false);
        await copy.RunAsync();
        Assert.Equal(JobState.Completed, copy.State);
        Assert.Equal("çay ☕", await TextOf(fs, root.Combine("docs").Combine("c.txt")));
        await fs.CreateDirectoryAsync(root.Combine("new"), CancellationToken.None);
        await fs.MoveAsync(root.Combine("a.txt"), root.Combine("new").Combine("a2.txt"), false, CancellationToken.None);
        Assert.Equal(["a2.txt"], (await fs.ListAsync(root.Combine("new"), CancellationToken.None)).Select(e => e.Name));
        await fs.DeleteAsync(root.Combine("new").Combine("a2.txt"), false, CancellationToken.None);
        Assert.Empty(await fs.ListAsync(root.Combine("new"), CancellationToken.None));

        // the script's error comes back with its message
        var error = await Assert.ThrowsAsync<IOException>(() => fs.ListAsync(root.Combine("nowhere"), CancellationToken.None));
        Assert.Contains("No folder /nowhere", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_without_write_is_read_only()
    {
        var fs = ScriptFileSystemProvider.Load("ro.js", "function list(p) { return [{ name: 'x.bin', size: 3 }]; } function read(p) { return { base64: toBase64('abc') }; }");
        Assert.Equal(ProviderCapabilities.Read, fs.Capabilities);
        Assert.Equal("ro", fs.EntryName);
        Assert.Equal("abc", await TextOf(fs, fs.EntryRoot.Combine("x.bin")));
        await Assert.ThrowsAsync<NotSupportedException>(() => fs.OpenWriteAsync(fs.EntryRoot.Combine("y"), true, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => fs.CreateDirectoryAsync(fs.EntryRoot.Combine("d"), CancellationToken.None));
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() => fs.MoveAsync(fs.EntryRoot.Combine("x.bin"), fs.EntryRoot.Combine("z"), false, CancellationToken.None));
    }

    [Fact]
    public void Scripts_that_do_not_load_are_reported()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.Root, "filesystems")).FullName;
        File.WriteAllText(Path.Combine(folder, "good.js"), "function list() { return []; } function read() { return ''; }");
        File.WriteAllText(Path.Combine(folder, "half.js"), "function list() { return []; }");
        File.WriteAllText(Path.Combine(folder, "broken.js"), "function list( {");
        var errors = new List<string>();
        var loaded = ScriptFileSystemProvider.LoadAll(folder, (file, _) => errors.Add(Path.GetFileName(file)));
        Assert.Equal(["js-good"], loaded.Select(f => f.Scheme));
        Assert.Equal(["broken.js", "half.js"], errors.Order());
    }

    [Fact]
    public async Task A_script_reads_a_web_service_through_http()
    {
        // a tiny web service: GET /files gives a list, GET /files/name its text, POST echoes
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var request = await reader.ReadLineAsync(stop.Token) ?? string.Empty;
                string? line;
                var auth = string.Empty;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                {
                    if (line.StartsWith("X-Token:", StringComparison.OrdinalIgnoreCase))
                    {
                        auth = line[8..].Trim();
                    }
                }
                var body = request.StartsWith("GET /files/notes.txt", StringComparison.Ordinal) ? "remote notes"
                    : request.StartsWith("GET /files", StringComparison.Ordinal) ? """[{"name":"notes.txt","size":12}]"""
                    : "?";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {(auth == "secret" ? 200 : 401)} OK\r\nContent-Type: text/plain\r\nX-Kind: test\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
        });
        try
        {
            var fs = ScriptFileSystemProvider.Load("web.js", $$"""
                var base = "http://127.0.0.1:{{port}}/files";
                function get(url) {
                  var r = http("GET", url, null, { "X-Token": "secret" });
                  if (r.status !== 200) throw new Error("HTTP " + r.status);
                  if (r.headers["x-kind"] !== "test") throw new Error("headers");
                  return r.body;
                }
                function list(path) { return JSON.parse(get(base)); }
                function read(path) { return get(base + path); }
                """);
            var entry = Assert.Single(await fs.ListAsync(fs.EntryRoot, CancellationToken.None));
            Assert.Equal(("notes.txt", 12L), (entry.Name, entry.Size));
            Assert.Equal("remote notes", await TextOf(fs, entry.Path));
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            try
            {
                await server;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
