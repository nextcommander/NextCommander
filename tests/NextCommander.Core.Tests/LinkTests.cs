using System.Net;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;
using NextCommander.Vfs.Remote.Link;

namespace NextCommander.Core.Tests;

/// <summary>
/// PC link: two NextCommanders over a cable. A real server and client on the loopback interface: the shared folders,
/// files both ways, read-only shares, paths that leave a share, the pairing code and the computers that are allowed.
/// </summary>
public sealed class LinkTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _serverIdentity = LinkIdentity.Create("server-pc");
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _clientIdentity = LinkIdentity.Create("client-pc");
    private readonly List<LinkApprovalRequest> _asked = [];
    private readonly HashSet<string> _trustedClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _confirmations = [];
    private bool _allow = true;
    private bool _acceptServer = true;
    private LinkServer? _server;

    public LinkTests()
    {
        _dir.Write(Path.Combine("photos", "a.jpg"), "picture", new DateTime(2024, 5, 6, 7, 8, 9));
        _dir.Write(Path.Combine("photos", "sub", "b.txt"), "beta");
        _dir.Write(Path.Combine("docs", "readme.md"), "# read me");
        _dir.Write(Path.Combine("secret", "password.txt"), "not shared");
    }

    public void Dispose()
    {
        _server?.Dispose();
        _serverIdentity.Dispose();
        _clientIdentity.Dispose();
        _dir.Dispose();
    }

    private List<LinkShare> Shares =>
    [
        new() { Name = "Photos", Path = Path.Combine(_dir.Root, "photos") },
        new() { Name = "Docs", Path = Path.Combine(_dir.Root, "docs"), ReadOnly = true },
    ];

    private LinkServer StartServer()
    {
        _server = new LinkServer(new LinkServerOptions
        {
            Identity = _serverIdentity,
            Name = "server-pc",
            Port = 0,
            LoopbackOnly = true,
            Shares = () => Shares,
            IsTrusted = fp => _trustedClients.Contains(fp),
            Approve = request =>
            {
                _asked.Add(request);
                if (_allow)
                {
                    _trustedClients.Add(request.Fingerprint);
                }
                return Task.FromResult(_allow);
            },
            Local = new LocalFileSystemProvider(),
        });
        _server.Start();
        return _server;
    }

    private LinkFileSystemProvider NewClient() => new(new RemoteHooks
    {
        KnownFingerprint = c => _knownServers.GetValueOrDefault(c.Host),
        ConfirmFingerprint = (c, fingerprint, description, changed) =>
        {
            _confirmations.Add(description);
            if (_acceptServer)
            {
                _knownServers[c.Host] = fingerprint;
            }
            return Task.FromResult(_acceptServer);
        },
    }, new LinkDiscovery(() => 0), _ => Task.FromResult(_clientIdentity), () => "client-pc");

    private VfsPath P(string path) => VfsPath.Create("link", "127.0.0.1:" + _server!.Port, path);

    private static async Task<string> ReadAllAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteAllAsync(IFileSystemProvider provider, VfsPath path, string text, bool overwrite = false)
    {
        await using var stream = await provider.OpenWriteAsync(path, overwrite, CancellationToken.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task Shared_folders_files_both_ways_and_read_only_shares()
    {
        StartServer();
        await using var client = NewClient();
        var ct = CancellationToken.None;

        // the shares at the root; what is not shared is not there
        var roots = await client.ListAsync(P("/"), ct);
        Assert.Equal(["Docs", "Photos"], roots.Select(e => e.Name).Order());
        Assert.All(roots, e => Assert.True(e.IsDirectory));

        var photos = await client.ListAsync(P("/Photos"), ct);
        Assert.Equal(["a.jpg", "sub"], photos.Select(e => e.Name).Order());
        var a = photos.Single(e => e.Name == "a.jpg");
        Assert.Equal(7, a.Size);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), a.LastWriteTime);
        Assert.Equal(P("/Photos/a.jpg"), a.Path);
        Assert.Equal("picture", await ReadAllAsync(client, P("/Photos/a.jpg")));
        Assert.Equal("beta", (await client.GetEntryAsync(P("/Photos/sub/b.txt"), ct)) is { Size: 4 } ? await ReadAllAsync(client, P("/Photos/sub/b.txt")) : null);
        Assert.Null(await client.GetEntryAsync(P("/Photos/nothing.txt"), ct));

        // a bigger file than one data frame, with its time (sent with the upload)
        var big = string.Concat(Enumerable.Repeat("0123456789abcdef", 50_000)); // 800 KB
        client.PrepareUpload(P("/Photos/big.bin"), new DateTime(2023, 1, 2, 3, 4, 5));
        await WriteAllAsync(client, P("/Photos/big.bin"), big);
        Assert.Equal(big, File.ReadAllText(Path.Combine(_dir.Root, "photos", "big.bin")));
        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), File.GetLastWriteTime(Path.Combine(_dir.Root, "photos", "big.bin")));
        Assert.Equal(big, await ReadAllAsync(client, P("/Photos/big.bin")));
        await Assert.ThrowsAsync<IOException>(() => WriteAllAsync(client, P("/Photos/big.bin"), "again")); // exists, no overwrite
        await WriteAllAsync(client, P("/Photos/big.bin"), "small", overwrite: true);
        Assert.Equal("small", File.ReadAllText(Path.Combine(_dir.Root, "photos", "big.bin")));

        // folders, renames, times, delete
        await client.CreateDirectoryAsync(P("/Photos/new/deeper"), ct);
        Assert.True(Directory.Exists(Path.Combine(_dir.Root, "photos", "new", "deeper")));
        await client.MoveAsync(P("/Photos/big.bin"), P("/Photos/new/moved.bin"), false, ct);
        Assert.True(File.Exists(Path.Combine(_dir.Root, "photos", "new", "moved.bin")));
        await client.SetTimesAsync(P("/Photos/new/moved.bin"), null, new DateTime(2022, 2, 2, 2, 2, 2), ct);
        Assert.Equal(new DateTime(2022, 2, 2, 2, 2, 2), File.GetLastWriteTime(Path.Combine(_dir.Root, "photos", "new", "moved.bin")));
        await client.DeleteAsync(P("/Photos/new"), recursive: true, ct);
        Assert.False(Directory.Exists(Path.Combine(_dir.Root, "photos", "new")));
        Assert.NotNull(await client.GetSpaceAsync(P("/Photos"), ct));

        // the read-only share can be read, not changed; the shared folder itself is not changed
        Assert.Equal("# read me", await ReadAllAsync(client, P("/Docs/readme.md")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WriteAllAsync(client, P("/Docs/x.txt"), "x"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DeleteAsync(P("/Docs/readme.md"), false, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DeleteAsync(P("/Photos"), true, ct));
        Assert.True(File.Exists(Path.Combine(_dir.Root, "docs", "readme.md")));

        // a copy job from the local disk to the other computer: the contents and the time arrive
        var local = new LocalFileSystemProvider();
        var source = _dir.Write("upload.txt", "uploaded", new DateTime(2020, 2, 3, 4, 5, 6));
        var job = new CopyMoveJob(new ProviderRegistry([local, client]), [(await local.GetEntryAsync(VfsPath.Local(source), ct))!], P("/Photos"), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString() ?? string.Join("; ", job.Errors.Select(e => e.Message)));
        Assert.Equal("uploaded", await ReadAllAsync(client, P("/Photos/upload.txt")));
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), (await client.GetEntryAsync(P("/Photos/upload.txt"), ct))!.LastWriteTime);
    }

    [Theory]
    [InlineData("/Photos/../secret/password.txt")]
    [InlineData("/Photos/..")]
    [InlineData("/Secret/password.txt")]
    [InlineData("/Photos/C:/Windows")]
    public async Task Paths_that_leave_a_shared_folder_are_refused(string path)
    {
        StartServer();
        await using var client = NewClient();
        var error = await Record.ExceptionAsync(() => ReadAllAsync(client, P(path)));
        Assert.True(error is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException, error?.ToString());
    }

    [Fact]
    public async Task A_link_inside_a_share_to_elsewhere_is_refused()
    {
        var link = Path.Combine(_dir.Root, "photos", "escape");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_dir.Root, "secret"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no right to create links here (Windows without developer mode)
        }
        StartServer();
        await using var client = NewClient();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadAllAsync(client, P("/Photos/escape/password.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ListAsync(P("/Photos/escape"), CancellationToken.None));
    }

    [Fact]
    public async Task First_connection_compares_the_pairing_code_and_is_remembered()
    {
        StartServer();
        await using (var client = NewClient())
        {
            await client.ListAsync(P("/"), CancellationToken.None);
        }
        // the client accepted the server's fingerprint with a code, the server's user saw the same code
        var description = Assert.Single(_confirmations);
        var request = Assert.Single(_asked);
        Assert.Equal("client-pc", request.Name);
        Assert.Contains("Code: " + request.Code, description, StringComparison.Ordinal);
        Assert.Matches(@"^\d{3} \d{3}$", request.Code);
        Assert.Equal(LinkProtocol.Fingerprint(_clientIdentity), request.Fingerprint);

        // the next time: no question on either side
        await using (var again = NewClient())
        {
            await again.ListAsync(P("/Photos"), CancellationToken.None);
        }
        Assert.Single(_confirmations);
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Not_allowed_by_either_side_means_no_connection()
    {
        StartServer();
        _acceptServer = false;
        await using (var client = NewClient())
        {
            await Assert.ThrowsAsync<IOException>(() => client.ListAsync(P("/"), CancellationToken.None));
        }
        Assert.Empty(_asked); // the server was never told

        _acceptServer = true;
        _allow = false;
        await using (var client = NewClient())
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ListAsync(P("/"), CancellationToken.None));
            Assert.Contains("did not allow", error.Message, StringComparison.Ordinal);
        }
        Assert.Single(_asked);
    }

    [Fact]
    public void Pairing_code_is_the_same_both_ways_and_differs_for_another_computer()
    {
        var a = LinkProtocol.Fingerprint(_serverIdentity);
        var b = LinkProtocol.Fingerprint(_clientIdentity);
        Assert.Equal(LinkProtocol.PairingCode(a, b), LinkProtocol.PairingCode(b, a));
        using var other = LinkIdentity.Create("other");
        Assert.NotEqual(LinkProtocol.PairingCode(a, b), LinkProtocol.PairingCode(a, LinkProtocol.Fingerprint(other)));
        // the identity survives the secret store
        using var again = LinkIdentity.Import(LinkIdentity.Export(_serverIdentity));
        Assert.Equal(a, LinkProtocol.Fingerprint(again));
        Assert.True(again.HasPrivateKey);
    }

    [Fact]
    public async Task Computers_are_found_by_their_answer_to_the_probe()
    {
        using var responder = new LinkResponder(0, "server pc", 47290, "SHA256:AB", loopbackOnly: true);
        var discovery = new LinkDiscovery(() => responder.Port) { Targets = [new IPEndPoint(IPAddress.Loopback, responder.Port)] };
        var found = await discovery.ProbeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var peer = Assert.Single(found);
        Assert.Equal("server-pc", peer.Name); // a name usable in a path
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 47290), peer.EndPoint);
        Assert.Same(peer, discovery.Find("SERVER-PC"));

        // its own answer is left out
        discovery.OwnFingerprint = () => "SHA256:AB";
        Assert.Empty(await discovery.ProbeAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None));
    }

    [Fact]
    public void Broadcast_address_of_a_link_local_network()
    {
        Assert.Equal(IPAddress.Parse("169.254.255.255"), LinkDiscovery.DirectedBroadcast(IPAddress.Parse("169.254.12.34"), IPAddress.Parse("255.255.0.0")));
        Assert.Equal(IPAddress.Parse("192.168.1.255"), LinkDiscovery.DirectedBroadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        Assert.Null(LinkDiscovery.Parse(Encoding.UTF8.GetBytes("hello"), new IPEndPoint(IPAddress.Loopback, 1)));
    }

    [Theory]
    [InlineData("", "The name is empty")]
    [InlineData("a/b", "The name may not contain")]
    [InlineData("Docs", "The folder is empty")]
    public void Share_entries_are_checked(string name, string problem)
    {
        var share = new LinkShare { Name = name, Path = name == "Docs" ? string.Empty : _dir.Root };
        Assert.StartsWith(problem, share.Problem(), StringComparison.Ordinal);
        Assert.Null(new LinkShare { Name = "Docs", Path = _dir.Root }.Problem());
    }
}
