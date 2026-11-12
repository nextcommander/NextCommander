using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// The signed-in state of one cloud account (shared by the pooled clients of the account): the refresh token (from the
/// secret store or a new browser sign-in) and the current access token, renewed before it expires.
/// </summary>
public sealed class CloudSession
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly RemoteHooks _hooks;
    private readonly OAuthService _service;
    private readonly HttpClient _http;
    private OAuthTokens? _tokens;
    private bool _loaded;

    public CloudSession(RemoteConnection connection, OAuthService service, RemoteHooks hooks, HttpClient http)
    {
        Connection = connection;
        _service = service;
        _hooks = hooks;
        _http = http;
    }

    public RemoteConnection Connection { get; }

    /// <summary>An access token valid for at least a minute (refreshing or signing in when needed).</summary>
    public async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens is { } current && current.ExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return current.AccessToken;
        }
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is { } t && t.ExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return t.AccessToken;
            }
            var clientId = Connection.ClientId?.Trim();
            if (string.IsNullOrEmpty(clientId))
            {
                throw new IOException($"{_service.Name}: the connection settings have no application (client) id. See README: cloud drives.");
            }
            var refresh = _tokens?.RefreshToken;
            if (refresh is null && !_loaded)
            {
                _loaded = true;
                refresh = _hooks.LoadToken is { } load ? await load(Connection).ConfigureAwait(false) : null;
            }
            OAuthTokens tokens;
            if (refresh is not null)
            {
                try
                {
                    tokens = await OAuthClient.RefreshAsync(_service, clientId, Connection.ClientSecret, refresh, _http, cancellationToken).ConfigureAwait(false);
                }
                catch (OAuthException ex) when (ex.Error is "invalid_grant" or "invalid_token" or "unauthorized_client" or "invalid_client" or "400" or "401")
                {
                    // revoked, expired or from another application: sign in again
                    tokens = await SignInAsync(clientId, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                tokens = await SignInAsync(clientId, cancellationToken).ConfigureAwait(false);
            }
            if (tokens.RefreshToken != refresh && _hooks.SaveToken is { } save)
            {
                await save(Connection, tokens.RefreshToken).ConfigureAwait(false);
            }
            _tokens = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task<OAuthTokens> SignInAsync(string clientId, CancellationToken cancellationToken)
    {
        var open = _hooks.OpenBrowser ?? throw new IOException($"{_service.Name}: cannot open the browser to sign in.");
        return OAuthClient.LoginAsync(_service, clientId, Connection.ClientSecret, open, _http, cancellationToken);
    }

    /// <summary>The access token was refused before it expired (revoked): the next request gets a new one.</summary>
    public void Invalidate(string accessToken)
    {
        if (_tokens is { } t && t.AccessToken == accessToken)
        {
            _tokens = t with { ExpiresUtc = DateTime.MinValue };
        }
    }
}

/// <summary>One "connection" of a cloud provider: the HTTP client and the account's session.</summary>
public sealed class CloudClient(HttpClient http, CloudSession session)
{
    public HttpClient Http { get; } = http;

    public CloudSession Session { get; } = session;

    /// <summary>Attempts for throttled (429) or unavailable (5xx) answers.</summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// Sends a request built by <paramref name="create"/> (called again for a retry) with the access token: a 401 renews
    /// the token once, 429 / 5xx wait (Retry-After or growing pauses) and try again.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken, bool authorize = true,
        bool headersOnly = false)
    {
        var renewed = false;
        for (var attempt = 1; ; attempt++)
        {
            var request = create();
            string? token = null;
            if (authorize)
            {
                token = await Session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var response = await Http.SendAsync(request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authorize && !renewed)
            {
                renewed = true;
                Session.Invalidate(token!);
                response.Dispose();
                request.Dispose();
                continue;
            }
            if (attempt < MaxAttempts && (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError))
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                response.Dispose();
                request.Dispose();
                await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }
}

/// <summary>
/// Shared part of the Google Drive, OneDrive and Dropbox providers: paths are <c>scheme://account@host/folder/file</c>
/// (the account is a name the user gives; each has its own sign-in), sign-in in the browser on first use, the refresh
/// token kept in the secret store when "save" is on. Errors of the services become IO exceptions.
/// </summary>
public abstract class CloudProviderBase(RemoteHooks hooks) : RemoteProviderBase<CloudClient>(hooks)
{
    private readonly ConcurrentDictionary<string, CloudSession> _sessions = new(StringComparer.Ordinal);
    private HttpClient? _http;
    private int _httpProxyVersion;

    protected abstract OAuthService Service { get; }

    /// <summary>The sign-in service of a connection (OneDrive asks for more with SharePoint).</summary>
    protected virtual OAuthService ServiceFor(RemoteConnection connection) => Service;

    /// <summary>Tests: the HTTP handler of all requests (a fake service).</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }

    /// <summary>Tests: the sign-in endpoints (a fake service).</summary>
    public OAuthService? ServiceOverride { get; init; }


    protected override bool NeedsPassword(RemoteConnection connection) => false;

    /// <summary>The web client; made again when the proxy setting changed (new connections use the new proxy).</summary>
    private HttpClient Http
    {
        get
        {
            if (_http is null || _httpProxyVersion != NextCommander.Core.Net.WebProxyConfig.Version)
            {
                _httpProxyVersion = NextCommander.Core.Net.WebProxyConfig.Version;
                _http = CreateHttp(); // the old one stays with the sessions using it
            }
            return _http;
        }
    }

    private HttpClient CreateHttp()
    {
        // redirects are followed (downloads answer with a pre-signed address; the Authorization header is not sent there)
        HttpMessageHandler handler;
        if (HandlerFactory is { } factory)
        {
            handler = factory();
        }
        else
        {
            var sockets = NextCommander.Core.Net.WebProxyConfig.CreateHandler(); // Configuration > Operation > Proxy
            sockets.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler = sockets;
        }
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NextCommander", "1.0"));
        return http;
    }

    protected override Task<CloudClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(connection.SecretId, _ => new CloudSession(connection, ServiceOverride ?? ServiceFor(connection), Hooks, Http));
        return ConnectClientAsync(new CloudClient(Http, session), cancellationToken);
    }

    /// <summary>Signs in (if needed) and checks the account; the base returns the client as is.</summary>
    protected virtual async Task<CloudClient> ConnectClientAsync(CloudClient client, CancellationToken cancellationToken)
    {
        await client.Session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    protected override bool IsAlive(CloudClient client) => true;

    protected override ValueTask CloseAsync(CloudClient client) => ValueTask.CompletedTask;

    protected override bool IsAuthenticationFailure(Exception ex) => false; // the session signs in again by itself

    protected override bool IsConnectionFailure(Exception ex) => false; // clients hold no connection of their own

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        HttpStatusException { Status: HttpStatusCode.NotFound } => new FileNotFoundException($"Not found: {path}", path, ex),
        HttpStatusException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        HttpStatusException => new IOException($"{Service.Name}: {ex.Message}", ex),
        HttpRequestException => new IOException($"{Service.Name}: {ex.Message}", ex),
        _ => null,
    };

    // ---------------------------------------------------------------- helpers for the providers

    protected static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    protected static StringContent JsonContent(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    protected static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Time from an ISO 8601 text (local time; MinValue when missing).</summary>
    protected static DateTime TimeOf(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.LocalDateTime : DateTime.MinValue;

    /// <summary>A time as the services want it: UTC, ISO 8601, whole seconds.</summary>
    protected static string IsoTime(DateTime time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"/a/b" → "a/b"; root → "".</summary>
    protected static string Relative(string path) => path.Trim('/');

    protected static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    protected static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    protected void CheckSameAccount(VfsPath source, VfsPath destination)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between accounts is done by copying.");
        }
    }

    /// <summary>A stream that reads a response body and disposes the response.</summary>
    protected static Stream BodyStream(Stream inner, HttpResponseMessage response) => new ResponseStream(inner, response);

    /// <summary>Upload stream: data is buffered in a temporary file and sent when the stream is closed.</summary>
    protected static Stream UploadOnClose(Func<FileStream, CancellationToken, Task> upload, CancellationToken cancellationToken) =>
        new UploadOnCloseStream(upload, cancellationToken);

    /// <summary>A view of part of a seekable stream (one chunk of an upload) that does not close it.</summary>
    protected static HttpContent ChunkContent(Stream data, long offset, int length)
    {
        var buffer = new byte[length];
        data.Position = offset;
        data.ReadExactly(buffer);
        return new ByteArrayContent(buffer);
    }

    /// <summary>The whole buffered upload as request content (sent again from the start on a retry).</summary>
    protected static HttpContent WholeContent(Stream data)
    {
        data.Position = 0;
        var content = new StreamContent(new KeepOpenStream(data));
        content.Headers.ContentLength = data.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        _http?.Dispose();
        _http = null;
    }
}
