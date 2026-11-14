using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>The connection options: FTP encoding, timeouts, S3 encryption, AWS SSO and importing FileZilla / WinSCP sites.</summary>
public sealed class ConnectionOptionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Ftp_encoding_and_timeouts()
    {
        Assert.Null(FtpFileSystemProvider.EncodingOf(new RemoteConnection()));
        Assert.Equal(28599, FtpFileSystemProvider.EncodingOf(new RemoteConnection { FtpEncoding = "iso-8859-9" })!.CodePage);
        Assert.Null(FtpFileSystemProvider.EncodingOf(new RemoteConnection { FtpEncoding = "no-such-encoding" }));
        Assert.All(FtpFileSystemProvider.Encodings.Where(e => e.Length > 0), e => Assert.NotNull(FtpFileSystemProvider.EncodingOf(new RemoteConnection { FtpEncoding = e })));
        Assert.Equal(TimeSpan.FromSeconds(20), SshLogin.TimeoutOf(new RemoteConnection()));
        Assert.Equal(TimeSpan.FromSeconds(45), SshLogin.TimeoutOf(new RemoteConnection { TimeoutSeconds = 45 }));
        Assert.Equal(TimeSpan.FromSeconds(300), SftpFileSystemProvider.OperationTimeoutOf(new RemoteConnection { TimeoutSeconds = 300 }));
    }

    [Theory]
    [InlineData(S3Encryption.None, null, null, null)]
    [InlineData(S3Encryption.Aes256, null, "AES256", null)]
    [InlineData(S3Encryption.Kms, null, "aws:kms", null)]
    [InlineData(S3Encryption.Kms, " arn:aws:kms:eu-central-1:1:key/k ", "aws:kms", "arn:aws:kms:eu-central-1:1:key/k")]
    public void S3_encryption_headers(S3Encryption encryption, string? key, string? expected, string? expectedKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/b/k");
        S3FileSystemProvider.AddEncryption(request, new RemoteConnection { S3Encryption = encryption, KmsKeyId = key });
        Assert.Equal(expected, request.Headers.TryGetValues("x-amz-server-side-encryption", out var v) ? v.Single() : null);
        Assert.Equal(expectedKey, request.Headers.TryGetValues("x-amz-server-side-encryption-aws-kms-key-id", out var k) ? k.Single() : null);
    }

    // ---------------------------------------------------------------- AWS SSO

    private string WriteConfig() => _dir.Write("config", """
        [default]
        region = eu-central-1

        [profile dev]
        sso_session = company
        sso_account_id = 111122223333
        sso_role_name = Developer
        region = eu-west-1

        [profile legacy]
        sso_start_url = https://old.awsapps.com/start
        sso_region = us-east-1
        sso_account_id = 444455556666
        sso_role_name = ReadOnly

        [sso-session company]
        sso_start_url = https://company.awsapps.com/start
        sso_region = eu-central-1
        sso_registration_scopes = sso:account:access
        """);

    [Fact]
    public void Sso_profiles_are_read()
    {
        var config = WriteConfig();
        Assert.Equal(["default", "dev", "legacy"], AwsProfiles.Names(Path.Combine(_dir.Root, "none"), config));
        Assert.Equal(new AwsSsoProfile("https://company.awsapps.com/start", "eu-central-1", "111122223333", "Developer", "company", "eu-west-1"), AwsProfiles.LoadSso("dev", config));
        Assert.Equal(new AwsSsoProfile("https://old.awsapps.com/start", "us-east-1", "444455556666", "ReadOnly", null, null), AwsProfiles.LoadSso("legacy", config));
        Assert.Null(AwsProfiles.LoadSso("default", config));
    }

    /// <summary>The OIDC and portal services of AWS SSO: device sign-in (pending once), refresh and role keys.</summary>
    private sealed class FakeSso : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        private int _polls;

        private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(path);
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            switch (path)
            {
                case "/client/register":
                    return Json(new JsonObject { ["clientId"] = "cid", ["clientSecret"] = "csecret", ["clientSecretExpiresAt"] = DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds() });
                case "/device_authorization":
                    Assert.Equal("https://company.awsapps.com/start", body!["startUrl"]!.GetValue<string>());
                    return Json(new JsonObject { ["deviceCode"] = "dev-code", ["userCode"] = "ABCD", ["verificationUriComplete"] = "https://device.test/?code=ABCD", ["interval"] = 1 });
                case "/token" when body!["grantType"]!.GetValue<string>() == "refresh_token":
                    return body["refreshToken"]!.GetValue<string>() == "refresh-1"
                        ? Json(new JsonObject { ["accessToken"] = "refreshed", ["expiresIn"] = 3600, ["refreshToken"] = "refresh-2" })
                        : Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
                case "/token":
                    return ++_polls < 2
                        ? Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.BadRequest)
                        : Json(new JsonObject { ["accessToken"] = "signed-in", ["expiresIn"] = 3600, ["refreshToken"] = "refresh-1" });
                case "/federation/credentials":
                    var token = request.Headers.GetValues("x-amz-sso_bearer_token").Single();
                    Assert.Contains("account_id=111122223333", request.RequestUri.Query, StringComparison.Ordinal);
                    return Json(new JsonObject { ["roleCredentials"] = new JsonObject { ["accessKeyId"] = "AKIA-" + token, ["secretAccessKey"] = "secret", ["sessionToken"] = "session" } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Sso_signs_in_with_a_device_code_then_uses_and_refreshes_the_token()
    {
        var profile = AwsProfiles.LoadSso("dev", WriteConfig())!;
        var fake = new FakeSso();
        var opened = new List<Uri>();
        using var http = new HttpClient(fake);
        var sso = new AwsSso(http, uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        })
        {
            OidcBase = _ => "https://oidc.test", PortalBase = _ => "https://portal.test", CacheFolder = Path.Combine(_dir.Root, "cache"),
        };

        // no token yet: the browser signs in (the first poll is still pending)
        var keys = await sso.CredentialsAsync(profile, CancellationToken.None);
        Assert.Equal(("AKIA-signed-in", "secret", "session", "eu-west-1"), (keys.AccessKey, keys.SecretKey, keys.SessionToken, keys.Region));
        Assert.Equal([new Uri("https://device.test/?code=ABCD")], opened);
        var cache = JsonNode.Parse(File.ReadAllText(sso.CacheFile(profile)))!;
        Assert.Equal(("signed-in", "refresh-1", "cid"), (cache["accessToken"]!.GetValue<string>(), cache["refreshToken"]!.GetValue<string>(), cache["clientId"]!.GetValue<string>()));
        // the AWS CLI's file name: SHA-1 of the session name
        Assert.Equal("37e6d2ee9a6bd1d0c19d4d3ee457b3a0e1c95a1e".Length, Path.GetFileNameWithoutExtension(sso.CacheFile(profile)).Length);

        // a valid cached token: no sign-in
        fake.Calls.Clear();
        Assert.Equal("AKIA-signed-in", (await sso.CredentialsAsync(profile, CancellationToken.None)).AccessKey);
        Assert.Equal(["/federation/credentials"], fake.Calls);

        // an expired token is refreshed with its refresh token
        cache["expiresAt"] = "2000-01-01T00:00:00Z";
        File.WriteAllText(sso.CacheFile(profile), cache.ToJsonString());
        Assert.Equal("AKIA-refreshed", (await sso.CredentialsAsync(profile, CancellationToken.None)).AccessKey);
        Assert.Single(opened);
    }

    // ---------------------------------------------------------------- importing

    [Fact]
    public void FileZilla_sites_with_folders_and_passwords()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <FileZilla3><Servers>
              <Server><Host>ftp.example.com</Host><Port>21</Port><Protocol>4</Protocol><Logontype>2</Logontype><User>me</User>
                <Pass encoding="base64">xZ5pZnJlMTIz</Pass><Name>Main FTP</Name><RemoteDir>1 0 3 var 3 www</RemoteDir><PasvMode>MODE_ACTIVE</PasvMode>
                <EncodingType>Custom</EncodingType><CustomEncoding>iso-8859-9</CustomEncoding></Server>
              <Folder expanded="1">Work
                <Folder>Prod
                  <Server><Host>10.0.0.5</Host><Port>2222</Port><Protocol>1</Protocol><Logontype>5</Logontype><User>deploy</User><Keyfile>C:\keys\prod.ppk</Keyfile><Name>web 01</Name></Server>
                </Folder>
              </Folder>
              <Server><Host>s3.example.com</Host><Protocol>7</Protocol><Name>Pro only</Name></Server>
            </Servers></FileZilla3>
            """;
        var sites = ConnectionImport.FromFileZilla(xml);
        Assert.Equal(2, sites.Count); // the FileZilla Pro kind is left out
        var ftp = sites[0];
        Assert.Equal(("Main FTP", RemoteProtocol.FtpsExplicit, "ftp.example.com", "me", "FileZilla", "/var/www", false, "iso-8859-9"),
            (ftp.Connection.Name, ftp.Connection.Protocol, ftp.Connection.Host, ftp.Connection.User, ftp.Connection.Folder, ftp.Connection.InitialPath, ftp.Connection.Passive, ftp.Connection.FtpEncoding));
        Assert.Equal("Şifre123", ftp.Password);
        Assert.True(ftp.Connection.SavePassword);
        var sftp = sites[1];
        Assert.Equal(("web 01", RemoteProtocol.Sftp, 2222, @"C:\keys\prod.ppk", "FileZilla/Work/Prod"),
            (sftp.Connection.Name, sftp.Connection.Protocol, sftp.Connection.Port, sftp.Connection.KeyFile, sftp.Connection.Folder));
        Assert.Null(sftp.Password);
        Assert.Equal("/home/a b", ConnectionImport.FileZillaPath("1 0 4 home 3 a b"));
    }

    /// <summary>WinSCP's password obfuscation, the other way (for a test value).</summary>
    private static string Obfuscate(string password, string user, string host)
    {
        var text = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(user + host + password));
        var builder = new StringBuilder();
        void Put(int value) => builder.Append(((~value ^ 0xA3) & 0xFF).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        Put(0xFF);
        Put(0);
        Put(text.Length);
        Put(2); // two padding bytes
        Put(0x41);
        Put(0x42);
        foreach (var c in text)
        {
            Put(c);
        }
        return builder.ToString();
    }

    [Fact]
    public void WinScp_sessions_with_folders_tunnels_and_passwords()
    {
        var ini = $"""
            [Configuration\Interface]
            Theme=dark
            [Sessions\Default%20Settings]
            HostName=
            [Sessions\Work/Prod%20web]
            HostName=10.0.0.5
            UserName=deploy
            PortNumber=22
            FSProtocol=2
            Password={Obfuscate("gizli şifre", "deploy", "10.0.0.5")}
            Tunnel=1
            TunnelHostName=bastion.example.com
            TunnelUserName=me
            TunnelPortNumber=2200
            RemoteDirectory=/var/www
            Timeout=40
            [Sessions\Legacy%20scp]
            HostName=router.lan
            UserName=root
            FSProtocol=0
            [Sessions\Ftp]
            HostName=ftp.example.com
            FSProtocol=5
            Ftps=3
            FtpPasvMode=0
            """;
        var sessions = ConnectionImport.FromWinScpIni(ini);
        Assert.Equal(3, sessions.Count);
        var prod = sessions[0].Connection;
        Assert.Equal(("Prod web", "WinSCP/Work", RemoteProtocol.Sftp, "deploy", "me@bastion.example.com:2200", "/var/www", 40),
            (prod.Name, prod.Folder, prod.Protocol, prod.User, prod.JumpHost, prod.InitialPath, prod.TimeoutSeconds));
        Assert.Equal("gizli şifre", sessions[0].Password);
        Assert.False(prod.UseSshAgent); // a saved password: no agent
        Assert.Equal((RemoteProtocol.Scp, true), (sessions[1].Connection.Protocol, sessions[1].Connection.UseSshAgent));
        Assert.Equal((RemoteProtocol.FtpsExplicit, false), (sessions[2].Connection.Protocol, sessions[2].Connection.Passive));
        Assert.Null(ConnectionImport.WinScpPassword("zz", "u", "h"));
    }
}
