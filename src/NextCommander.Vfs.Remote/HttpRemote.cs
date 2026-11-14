using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using NextCommander.Core.Config;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// One HTTP "connection" of the WebDAV and S3 providers: an <see cref="HttpClient"/> for one server with the TLS
/// certificate check of the FTPS provider (unknown certificates are asked once, then trusted by fingerprint).
/// </summary>
public sealed class HttpRemoteClient : IDisposable
{
    private readonly string? _trustedFingerprint;
    private string? _seenFingerprint;
    private string? _seenDescription;

    private HttpRemoteClient(RemoteConnection connection, string? password, string? trustedFingerprint, bool useCredentials, HttpMessageHandler? testHandler)
    {
        Connection = connection;
        Password = password;
        _trustedFingerprint = trustedFingerprint;
        var secure = connection.Protocol switch
        {
            RemoteProtocol.WebDav => false,
            RemoteProtocol.S3 or RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles => !connection.PlainHttp,
            _ => true,
        };
        BaseUri = new UriBuilder(secure ? "https" : "http", connection.Host, connection.EffectivePort).Uri;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        NextCommander.Core.Net.WebProxyConfig.Apply(handler); // Configuration > Operation > Proxy
        if (useCredentials && !string.IsNullOrEmpty(connection.User))
        {
            // Basic, Digest and NTLM / Negotiate as the server asks
            handler.Credentials = new NetworkCredential(connection.User, password ?? string.Empty);
            handler.PreAuthenticate = true;
        }
        if (secure)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None || connection.AcceptAnyCertificate)
                    {
                        return true;
                    }
                    if (certificate is null)
                    {
                        return false;
                    }
                    _seenFingerprint = FtpFileSystemProvider.Fingerprint(certificate);
                    _seenDescription = $"{certificate.Subject} ({errors})";
                    return string.Equals(_seenFingerprint, _trustedFingerprint, StringComparison.Ordinal);
                },
            };
        }
        if (testHandler is not null)
        {
            handler.Dispose();
        }
        Http = new HttpClient(testHandler ?? handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NextCommander", "1.0"));
    }

    public RemoteConnection Connection { get; }

    public string? Password { get; }

    /// <summary>S3: session token of temporary keys (x-amz-security-token).</summary>
    public string? SessionToken { get; init; }

    public HttpClient Http { get; }

    /// <summary>scheme://host:port/ of the server.</summary>
    public Uri BaseUri { get; }

    /// <param name="testHandler">Tests: the handler of all requests (a fake service) instead of the network.</param>
    public static HttpRemoteClient Create(RemoteConnection connection, string? password, string? trustedFingerprint, bool useCredentials,
        string? sessionToken = null, HttpMessageHandler? testHandler = null) =>
        new(connection, password, trustedFingerprint, useCredentials, testHandler) { SessionToken = sessionToken };

    /// <summary>Sends a request; a certificate that was not trusted becomes <see cref="UntrustedServerException"/>.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool headersOnly = false)
    {
        try
        {
            return await Http.SendAsync(request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (_seenFingerprint is not null && !string.Equals(_seenFingerprint, _trustedFingerprint, StringComparison.Ordinal)
            && (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError))
        {
            throw new UntrustedServerException(_seenFingerprint, $"{Connection.Host}:{Connection.EffectivePort} — {_seenDescription}",
                changed: _trustedFingerprint is not null);
        }
    }

    /// <summary>A path of the server ("/a b/c") as an escaped absolute URI under <see cref="BaseUri"/>.</summary>
    public Uri UriOf(string path, string? query = null, string? subdomain = null)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (!escaped.StartsWith('/'))
        {
            escaped = "/" + escaped;
        }
        var server = subdomain is null
            ? BaseUri.GetLeftPart(UriPartial.Authority)
            : new UriBuilder(BaseUri.Scheme, subdomain + "." + BaseUri.Host, BaseUri.Port).Uri.GetLeftPart(UriPartial.Authority);
        return new Uri(server + escaped + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query));
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>An HTTP status that is not success (with the S3 error code when the server sent one).</summary>
public sealed class HttpStatusException(HttpStatusCode status, string message, string? errorCode = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>S3 error code ("NoSuchKey", "AccessDenied", "SignatureDoesNotMatch" ...).</summary>
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>A response body stream that disposes its response when closed.</summary>
internal sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }
}
