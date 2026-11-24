using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

public sealed class RemoteUnitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Connection_authority_round_trip()
    {
        var c = RemoteConnection.FromAuthority("sftp", "deploy%40corp@files.example.com:2222");
        Assert.Equal(("deploy@corp", "files.example.com", 2222, RemoteProtocol.Sftp), (c.User, c.Host, c.Port, c.Protocol));
        Assert.Equal("deploy%40corp@files.example.com:2222", c.Authority);
        Assert.Equal("sftp://deploy%40corp@files.example.com:2222", c.SecretId);
        var ftp = RemoteConnection.FromAuthority("ftps", "ftp.example.com");
        Assert.Equal((RemoteProtocol.FtpsExplicit, 21, "ftp.example.com"), (ftp.Protocol, ftp.EffectivePort, ftp.Authority));
        var path = VfsPath.Parse("sftp://me@host:2222/home/me/a.txt");
        Assert.Equal(("me@host:2222", "/home/me/a.txt"), (path.Authority, path.Path));
    }

    [Fact]
    public void WebDav_and_S3_addresses()
    {
        var dav = RemoteConnection.FromAuthority("davs", "me@cloud.example.com");
        Assert.Equal((RemoteProtocol.WebDavs, 443, "davs"), (dav.Protocol, dav.EffectivePort, dav.Scheme));
        Assert.Equal((RemoteProtocol.WebDav, 80), (RemoteConnection.FromAuthority("dav", "nas").Protocol, RemoteConnection.FromAuthority("dav", "nas").EffectivePort));
        var s3 = RemoteConnection.FromAuthority("s3", "AKIAEXAMPLE@s3.eu-central-1.amazonaws.com");
        Assert.Equal((RemoteProtocol.S3, "AKIAEXAMPLE", 443), (s3.Protocol, s3.User, s3.EffectivePort));
        Assert.Equal("eu-central-1", S3FileSystemProvider.RegionOf(s3));
        Assert.Equal("us-east-1", S3FileSystemProvider.RegionOf(RemoteConnection.FromAuthority("s3", "key@minio.local:9000")));
        Assert.Equal("auto", S3FileSystemProvider.RegionOf(new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "x.r2.cloudflarestorage.com", Region = "auto" }));
        Assert.Equal(80, new RemoteConnection { Protocol = RemoteProtocol.S3, PlainHttp = true }.EffectivePort);
        Assert.Equal(("bucket", "a/b c.txt"), S3FileSystemProvider.Split("/bucket/a/b c.txt"));
        Assert.Equal(("bucket", ""), S3FileSystemProvider.Split("/bucket"));
        Assert.Equal(("", ""), S3FileSystemProvider.Split("/"));
    }

    /// <summary>The GET Object example of the AWS Signature Version 4 documentation.</summary>
    [Fact]
    public void S3_signature_matches_the_aws_example()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = S3Signer.EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var authorization = S3Signer.Authorization("GET", "/test.txt", string.Empty, headers, S3Signer.EmptyPayloadHash, "20130524T000000Z", "us-east-1",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
            + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", authorization);

        // the same through a request message
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        S3Signer.Sign(request, "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", S3Signer.EmptyPayloadHash,
            new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(authorization, string.Join(",", request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task Pool_reuses_clients_limits_parallelism_and_drops_broken_ones()
    {
        var closed = 0;
        var alive = true;
        var pool = new ClientPool<object>(_ => Task.FromResult(new object()), _ => alive, _ =>
        {
            closed++;
            return ValueTask.CompletedTask;
        }, maxConnections: 2);

        var a = await pool.RentAsync(CancellationToken.None);
        var b = await pool.RentAsync(CancellationToken.None);
        Assert.Equal(2, pool.Created);
        var third = pool.RentAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted); // waits for a free slot
        await a.DisposeAsync();
        var c = await third;
        Assert.Same(a.Client, c.Client);

        b.Broken = true;
        await b.DisposeAsync();
        Assert.Equal(1, closed);
        alive = false; // server dropped the idle connection
        await c.DisposeAsync();
        var d = await pool.RentAsync(CancellationToken.None);
        Assert.Equal(2, closed);
        Assert.Equal(3, pool.Created);
        await d.DisposeAsync();
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task Secret_store_encrypts_with_machine_key_and_master_password()
    {
        var store = new SecretStore(_dir.Root);
        Assert.False(store.IsLocked);
        Assert.True(await store.SetAsync("sftp://me@host", "p@ss"));
        Assert.DoesNotContain("p@ss", File.ReadAllText(Path.Combine(_dir.Root, "secrets.json")), StringComparison.Ordinal);
        Assert.Equal("p@ss", await new SecretStore(_dir.Root).GetAsync("sftp://me@host"));

        store.SetMasterPassword("ana-şifre");
        var reopened = new SecretStore(_dir.Root);
        Assert.True(reopened.IsLocked);
        Assert.Null(await reopened.GetAsync("sftp://me@host")); // no prompt → cancelled
        var asked = new List<bool>();
        reopened.MasterPasswordPrompt = retry =>
        {
            asked.Add(retry);
            return Task.FromResult<string?>(asked.Count == 1 ? "yanlış" : "ana-şifre");
        };
        Assert.Equal("p@ss", await reopened.GetAsync("sftp://me@host"));
        Assert.Equal([false, true], asked);

        reopened.Lock();
        Assert.True(reopened.IsLocked);
        Assert.True(reopened.Unlock("ana-şifre"));
        reopened.SetMasterPassword(null);
        Assert.False(new SecretStore(_dir.Root).IsLocked);
        Assert.Equal("p@ss", await new SecretStore(_dir.Root).GetAsync("sftp://me@host"));
        reopened.Remove("sftp://me@host");
        Assert.False(new SecretStore(_dir.Root).Contains("sftp://me@host"));
    }

    [Fact]
    public void Ssh_fingerprint_matches_openssh_format()
    {
        // OpenSSH prints SHA256 fingerprints as unpadded base64.
        Assert.Equal("SHA256:LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ", SftpFileSystemProvider.Fingerprint(Encoding.ASCII.GetBytes("hello")));
    }
}

/// <summary>
/// Real server round trips. Set FM_TEST_SFTP / FM_TEST_FTP / FM_TEST_WEBDAV / FM_TEST_S3 to
/// "scheme://user:password@host:port/writable/dir" (WebDAV: dav:// or davs://; S3: s3://accesskey:secret@host:port/bucket,
/// plain HTTP for 127.0.0.1 and localhost) to run them; otherwise they are skipped. tests/servers/run-integration-tests.ps1
/// starts local servers for all four.
/// </summary>
public sealed class RemoteIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Servers()
    {
        var data = new TheoryData<string>();
        foreach (var variable in new[] { "FM_TEST_SFTP", "FM_TEST_FTP", "FM_TEST_WEBDAV", "FM_TEST_S3", "FM_TEST_AZURE", "FM_TEST_SMB" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } url)
            {
                data.Add(url);
            }
        }
        if (data.Count == 0)
        {
            data.Add(string.Empty);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Copy_list_rename_delete_round_trip(string url)
    {
        if (url.Length == 0)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var hooks = new RemoteHooks { GetPassword = (_, retry) => Task.FromResult<string?>(retry ? null : Uri.UnescapeDataString(userInfo[1])) };
        hooks.ConfirmFingerprint = (_, _, _, _) => Task.FromResult(true);
        hooks.ResolveConnection = (scheme, authority) =>
        {
            var connection = RemoteConnection.FromAuthority(scheme, authority);
            connection.PlainHttp = connection.Host is "127.0.0.1" or "localhost";
            return connection;
        };
        await using var sftp = new SftpFileSystemProvider(hooks);
        await using var ftp = new FtpFileSystemProvider(hooks);
        await using var dav = new WebDavFileSystemProvider(hooks);
        await using var s3 = new S3FileSystemProvider(hooks) { MultipartThreshold = 200_000, PartSize = 5 * 1024 * 1024 };
        await using var azure = new AzureBlobFileSystemProvider(hooks) { BlockThreshold = 200_000, BlockSize = 100_000 };
        await using var smb = new SmbFileSystemProvider(hooks);
        var registry = new ProviderRegistry([new LocalFileSystemProvider(), sftp, ftp, dav, s3, azure, smb]);
        var authority = Uri.EscapeDataString(Uri.UnescapeDataString(userInfo[0])) + "@" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        var root = VfsPath.Create(uri.Scheme, authority, uri.AbsolutePath.TrimEnd('/') + "/fm-test-" + Guid.NewGuid().ToString("N")[..6]);
        var provider = registry.Resolve(root);
        if (uri.Scheme is "s3" or "azblob")
        {
            await provider.CreateDirectoryAsync(root.Parent!, CancellationToken.None); // the bucket / container
        }
        await provider.CreateDirectoryAsync(root.Combine("sub"), CancellationToken.None);

        _dir.Write(Path.Combine("src", "a.txt"), "hello remote", new DateTime(2021, 6, 7, 8, 9, 10));
        _dir.Write(Path.Combine("src", "deep", "b.bin"), new string('x', 300_000));
        var src = await registry.Resolve(VfsPath.Local(_dir.Root)).ListAsync(VfsPath.Local(Path.Combine(_dir.Root, "src")), CancellationToken.None);
        var upload = new CopyMoveJob(registry, src, root.Combine("sub"), move: false);
        await upload.RunAsync();
        Assert.True(upload.State == JobState.Completed, upload.Failure?.ToString() ?? string.Join("; ", upload.Errors.Select(e => e.Message)));

        var listed = await provider.ListAsync(root.Combine("sub"), CancellationToken.None);
        Assert.Equal(["a.txt", "deep"], listed.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(12, listed.Single(e => e.Name == "a.txt").Size);
        // the modification time is kept (SFTP / FTP MFMT, WebDAV PROPPATCH, S3 / Azure metadata, SMB)
        Assert.InRange((listed.Single(e => e.Name == "a.txt").LastWriteTime - new DateTime(2021, 6, 7, 8, 9, 10)).Duration().TotalSeconds, 0, 2);

        // impacket's SMB server cannot rename a file it holds open when it runs on Windows (real servers can)
        var renamed = uri.Scheme == "smb" && OperatingSystem.IsWindows() ? "a.txt" : "renamed.txt";
        if (renamed != "a.txt")
        {
            await provider.MoveAsync(root.Combine("sub").Combine("a.txt"), root.Combine("sub").Combine(renamed), false, CancellationToken.None);
        }
        var back = Path.Combine(_dir.Root, "back");
        Directory.CreateDirectory(back);
        var download = new CopyMoveJob(registry, await provider.ListAsync(root.Combine("sub"), CancellationToken.None), VfsPath.Local(back), move: false);
        await download.RunAsync();
        Assert.Equal(JobState.Completed, download.State);
        Assert.Equal("hello remote", File.ReadAllText(Path.Combine(back, renamed)));
        Assert.InRange((File.GetLastWriteTime(Path.Combine(back, renamed)) - new DateTime(2021, 6, 7, 8, 9, 10)).Duration().TotalSeconds, 0, 2);
        Assert.Equal(300_000, new FileInfo(Path.Combine(back, "deep", "b.bin")).Length);

        if (provider is IPermissionSupport permissions)
        {
            // SFTP: the mode bits are changed and read back; the owner is a number
            var file = root.Combine("sub").Combine(renamed);
            // (the write bit: a test server on Windows applies only that one)
            await permissions.SetPermissionsAsync(file, false, new PermissionChange { Clear = UnixFileMode.UserWrite }, CancellationToken.None);
            var after = await permissions.GetPermissionsAsync(file, CancellationToken.None);
            Assert.False(after.Mode!.Value.HasFlag(UnixFileMode.UserWrite));
            Assert.True(after.NumericOwner);
            await permissions.SetPermissionsAsync(file, false, new PermissionChange { Set = UnixFileMode.UserWrite }, CancellationToken.None);
            Assert.True((await permissions.GetPermissionsAsync(file, CancellationToken.None)).Mode!.Value.HasFlag(UnixFileMode.UserWrite));
            await Assert.ThrowsAsync<InvalidOperationException>(() => permissions.SetPermissionsAsync(file, false, new PermissionChange { Owner = "root" }, CancellationToken.None));
        }

        await provider.DeleteAsync(root, recursive: true, CancellationToken.None);
        Assert.Null(await provider.GetEntryAsync(root, CancellationToken.None));
    }
}

/// <summary>SMB: user names and, against a test server (FM_TEST_SMB), the share list at the root.</summary>
public sealed class RemoteSmbTests
{
    [Theory]
    [InlineData(@"CORP\ali", "CORP", "ali")]
    [InlineData("CORP;ali", "CORP", "ali")]
    [InlineData("ali@corp.local", "corp.local", "ali")]
    [InlineData("ali", "", "ali")]
    public void User_and_domain(string text, string domain, string user) =>
        Assert.Equal((domain, user), SmbFileSystemProvider.SplitUser(text));

    [Fact]
    public async Task Shares_at_the_root()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_SMB") is not { Length: > 0 } url)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var hooks = new RemoteHooks
        {
            GetPassword = (_, retry) => Task.FromResult<string?>(retry ? null : Uri.UnescapeDataString(userInfo[1])),
            ResolveConnection = RemoteConnection.FromAuthority,
        };
        await using var smb = new SmbFileSystemProvider(hooks);
        var root = VfsPath.Create("smb", userInfo[0] + "@" + uri.Host + ":" + uri.Port, "/");
        var shares = await smb.ListAsync(root, CancellationToken.None);
        Assert.Contains(shares, s => s.IsDirectory && s.Name == uri.AbsolutePath.Trim('/'));
        Assert.DoesNotContain(shares, s => s.Name.EndsWith('$'));
    }
}
