using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>S3 connections: AWS CLI profiles (with session tokens) and virtual-hosted addresses.</summary>
public sealed class S3OptionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Aws_profiles_are_read_from_the_cli_files()
    {
        var credentials = _dir.Write("credentials", """
            [default]
            aws_access_key_id = AKIADEFAULT
            aws_secret_access_key = secret0

            # temporary keys
            [work]
            aws_access_key_id=ASIAWORK
            aws_secret_access_key=secret1
            aws_session_token=token1
            """);
        var config = _dir.Write("config", """
            [default]
            region = eu-west-1

            [profile work]
            region = eu-central-1

            [profile onlyconfig]
            aws_access_key_id = AKIACONFIG
            aws_secret_access_key = secret2

            [sso-session corp]
            sso_region = us-east-1
            """);

        Assert.Equal(["default", "onlyconfig", "work"], AwsProfiles.Names(credentials, config));
        Assert.Equal(new AwsCredentials("AKIADEFAULT", "secret0", null, "eu-west-1"), AwsProfiles.Load("default", credentials, config));
        Assert.Equal(new AwsCredentials("ASIAWORK", "secret1", "token1", "eu-central-1"), AwsProfiles.Load("work", credentials, config));
        Assert.Equal("AKIACONFIG", AwsProfiles.Load("onlyconfig", credentials, config)?.AccessKey);
        Assert.Null(AwsProfiles.Load("corp", credentials, config));
        Assert.Null(AwsProfiles.Load("missing", credentials, config));
    }

    [Theory]
    [InlineData("my-bucket", true, true)]
    [InlineData("my.bucket", false, true)]
    [InlineData("my.bucket", true, false)] // the TLS certificate (*.s3.amazonaws.com) does not cover dots
    [InlineData("My_Bucket", false, false)]
    [InlineData("ab", false, false)]
    public void Bucket_names_for_virtual_hosts(string bucket, bool secure, bool expected) =>
        Assert.Equal(expected, S3FileSystemProvider.IsDnsBucket(bucket, secure));

    [Fact]
    public void Azure_secrets_and_addresses()
    {
        Assert.True(AzureSigner.IsSas("?sv=2021-08-06&ss=b&srt=sco&sp=rl&sig=abc%3D"));
        Assert.False(AzureSigner.IsSas("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="));
        Assert.True(AzureSigner.IsAccountKey("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="));
        Assert.False(AzureSigner.IsAccountKey("not a key!"));
        Assert.False(AzureBlobFileSystemProvider.IsPathStyle(new RemoteConnection { Protocol = RemoteProtocol.AzureBlob, User = "acme", Host = "acme.blob.core.windows.net" }));
        Assert.True(AzureBlobFileSystemProvider.IsPathStyle(new RemoteConnection { Protocol = RemoteProtocol.AzureBlob, User = "devstoreaccount1", Host = "127.0.0.1", Port = 10000 }));
        Assert.Equal(RemoteProtocol.AzureBlob, RemoteConnection.FromAuthority("azblob", "acme@acme.blob.core.windows.net").Protocol);
    }

    [Fact]
    public void Virtual_host_address()
    {
        var connection = new RemoteConnection { Protocol = RemoteProtocol.S3, Host = "s3.eu-central-1.amazonaws.com" };
        using var client = HttpRemoteClient.Create(connection, "x", null, useCredentials: false);
        Assert.Equal("https://my-bucket.s3.eu-central-1.amazonaws.com/a%20b/c.txt?x=1", client.UriOf("/a b/c.txt", "x=1", subdomain: "my-bucket").AbsoluteUri);
        Assert.Equal("https://s3.eu-central-1.amazonaws.com/my-bucket/c.txt", client.UriOf("/my-bucket/c.txt").AbsoluteUri);
    }

    /// <summary>
    /// Against the local S3 test server (FM_TEST_S3): keys and a session token from a profile. Virtual-hosted addresses
    /// would need bucket.localhost, which Windows does not resolve; FM_TEST_S3_VHOST=1 adds them where it does.
    /// </summary>
    [Fact]
    public async Task Profile_token_and_virtual_hosts_on_a_server()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_S3") is not { Length: > 0 } url)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var keys = uri.UserInfo.Split(':', 2);
        var credentials = _dir.Write("credentials", $"[test]\naws_access_key_id = {keys[0]}\naws_secret_access_key = {Uri.UnescapeDataString(keys[1])}\naws_session_token = FwoGZXIvYXdzEXAMPLE\n");
        var previous = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", credentials);
        var proxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new System.Net.WebProxy(); // a system proxy would get bucket.localhost
        try
        {
            foreach (var virtualHost in Environment.GetEnvironmentVariable("FM_TEST_S3_VHOST") == "1" ? new[] { false, true } : [false])
            {
                var asked = 0;
                var hooks = new RemoteHooks
                {
                    GetPassword = (_, _) =>
                    {
                        asked++;
                        return Task.FromResult<string?>(null);
                    },
                    // bucket.localhost resolves to this machine (RFC 6761), so virtual hosts work against the local server
                    ResolveConnection = (scheme, authority) => new RemoteConnection
                    {
                        Protocol = RemoteProtocol.S3, Host = virtualHost ? "localhost" : uri.Host, Port = uri.Port, PlainHttp = true,
                        AwsProfile = "test", S3VirtualHost = virtualHost,
                    },
                };
                await using var s3 = new S3FileSystemProvider(hooks);
                var registry = new ProviderRegistry([new LocalFileSystemProvider(), s3]);
                var bucket = VfsPath.Create("s3", (virtualHost ? "localhost" : uri.Host) + ":" + uri.Port, "/fm-profile-" + (virtualHost ? "vhost" : "path"));
                await s3.CreateDirectoryAsync(bucket, CancellationToken.None);
                var file = _dir.Write(Path.Combine("up", "p.txt"), "profile", new DateTime(2020, 1, 2, 3, 4, 5));
                var source = await registry.Resolve(VfsPath.Local(file)).GetEntryAsync(VfsPath.Local(file), CancellationToken.None);
                var upload = new CopyMoveJob(registry, [source!], bucket, move: false);
                await upload.RunAsync();
                Assert.True(upload.State == JobState.Completed, upload.Failure?.ToString() ?? string.Join("; ", upload.Errors.Select(e => e.Message)));
                var listed = await s3.ListAsync(bucket, CancellationToken.None);
                Assert.Equal("p.txt", Assert.Single(listed).Name);
                await s3.DeleteAsync(bucket, recursive: true, CancellationToken.None);
                Assert.Equal(0, asked); // the keys came from the profile
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", previous);
            HttpClient.DefaultProxy = proxy;
        }
    }
}
