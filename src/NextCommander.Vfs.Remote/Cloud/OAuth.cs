using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// An OAuth 2.0 service of a cloud drive: where the user signs in, where codes become tokens, what is asked for. The
/// application has no client id of its own: each user registers a (free) application with the service and enters its
/// client id (Google also gives a client secret, which is not secret for desktop applications).
/// </summary>
/// <param name="RedirectHost">Host of the loopback redirect address ("localhost" or "127.0.0.1").</param>
/// <param name="ExtraAuthorizeParameters">Added to the sign-in address (offline access, consent).</param>
public sealed record OAuthService(
    string Name,
    string AuthorizeEndpoint,
    string TokenEndpoint,
    string? Scope,
    string RedirectHost,
    string ExtraAuthorizeParameters)
{
    /// <summary>Port of the loopback redirect address; it must be registered with the service as http://localhost:53682/.</summary>
    public const int RedirectPort = 53682;

    public string RedirectUri => $"http://{RedirectHost}:{RedirectPort}/";

    public static OAuthService Google { get; } = new("Google Drive",
        "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
        "https://www.googleapis.com/auth/drive", "127.0.0.1", "access_type=offline&prompt=consent");

    public static OAuthService Microsoft { get; } = new("OneDrive",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "Files.ReadWrite offline_access", "localhost", string.Empty);

    public static OAuthService Dropbox { get; } = new("Dropbox",
        "https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token",
        null, "localhost", "token_access_type=offline");

    /// <summary>Box: the application's client secret is needed too; refresh tokens are used once (each refresh gives a new one).</summary>
    public static OAuthService Box { get; } = new("Box",
        "https://account.box.com/api/oauth2/authorize", "https://api.box.com/oauth2/token",
        null, "localhost", string.Empty);

    /// <summary>The public client id of the Azure CLI: Entra ID sign-in to storage without registering an application.</summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>Azure Storage with a Microsoft Entra ID account (Blob and Files), in a tenant ("organizations": the account's own).</summary>
    public static OAuthService AzureStorage(string? tenant)
    {
        var directory = string.IsNullOrWhiteSpace(tenant) ? "organizations" : Uri.EscapeDataString(tenant.Trim());
        return new OAuthService("Azure",
            $"https://login.microsoftonline.com/{directory}/oauth2/v2.0/authorize", $"https://login.microsoftonline.com/{directory}/oauth2/v2.0/token",
            "https://storage.azure.com/user_impersonation offline_access", "localhost", string.Empty);
    }
}

/// <summary>Tokens of a signed-in account.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTime ExpiresUtc);

/// <summary>The service refused a token request (a revoked or expired refresh token: "invalid_grant").</summary>
public sealed class OAuthException(string error, string message) : IOException(message)
{
    public string Error { get; } = error;
}

/// <summary>Authorization code flow with PKCE and a loopback redirect (RFC 8252): the browser signs in, the code comes back to a local port.</summary>
public static class OAuthClient
{
    /// <summary>How long the browser sign-in may take.</summary>
    public static TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>PKCE verifier (43-128 characters) and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Uri AuthorizeUri(OAuthService service, string clientId, string state, string challenge)
    {
        var query = new StringBuilder()
            .Append("client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&response_type=code")
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(service.RedirectUri))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&code_challenge=").Append(challenge)
            .Append("&code_challenge_method=S256");
        if (service.Scope is { Length: > 0 } scope)
        {
            query.Append("&scope=").Append(Uri.EscapeDataString(scope));
        }
        if (service.ExtraAuthorizeParameters.Length > 0)
        {
            query.Append('&').Append(service.ExtraAuthorizeParameters);
        }
        return new Uri(service.AuthorizeEndpoint + "?" + query);
    }

    /// <summary>
    /// Signs in in the browser: listens on the redirect port, opens the sign-in page, waits for the code and exchanges
    /// it for tokens.
    /// </summary>
    public static async Task<OAuthTokens> LoginAsync(OAuthService service, string clientId, string? clientSecret, Func<Uri, Task> openBrowser,
        HttpClient http, CancellationToken cancellationToken)
    {
        var (verifier, challenge) = CreatePkce();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var receiver = LoopbackReceiver.Start(service.RedirectHost, OAuthService.RedirectPort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoginTimeout);
        var waiting = receiver.WaitAsync(state, timeout.Token); // accepting before the browser opens
        Dictionary<string, string> answer;
        try
        {
            await openBrowser(AuthorizeUri(service, clientId, state, challenge)).ConfigureAwait(false);
            answer = await waiting.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{service.Name}: the sign-in in the browser timed out.");
        }
        if (answer.TryGetValue("error", out var error))
        {
            throw new OAuthException(error, $"{service.Name}: sign-in refused ({error}{(answer.TryGetValue("error_description", out var d) ? ": " + d : string.Empty)}).");
        }
        if (!answer.TryGetValue("code", out var code))
        {
            throw new IOException($"{service.Name}: no sign-in code came from the browser.");
        }
        return await TokenRequestAsync(service, http, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = service.RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, clientSecret, previousRefreshToken: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new access token for a refresh token (the service may send a new refresh token too).</summary>
    public static Task<OAuthTokens> RefreshAsync(OAuthService service, string clientId, string? clientSecret, string refreshToken, HttpClient http,
        CancellationToken cancellationToken) =>
        TokenRequestAsync(service, http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, clientSecret, refreshToken, cancellationToken);

    private static async Task<OAuthTokens> TokenRequestAsync(OAuthService service, HttpClient http, Dictionary<string, string> form, string? clientSecret,
        string? previousRefreshToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, service.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement root;
        try
        {
            root = JsonElement.Parse(text);
        }
        catch (JsonException)
        {
            throw new IOException($"{service.Name}: could not get a token ({(int)response.StatusCode}).");
        }
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var description = root.TryGetProperty("error_description", out var ed) ? ed.GetString() : null;
            throw new OAuthException(error, $"{service.Name}: could not get a token ({error}{(description is null ? string.Empty : ": " + description)}).");
        }
        var expires = root.TryGetProperty("expires_in", out var e2) && e2.TryGetInt32(out var seconds) ? seconds : 3600;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        return new OAuthTokens(access.GetString()!, refresh ?? previousRefreshToken, DateTime.UtcNow.AddSeconds(expires));
    }
}

/// <summary>
/// Receives the browser's redirect on the loopback port (a few lines of HTTP over a socket: works the same on every
/// platform and needs no URL reservation). "localhost" listens on both IPv4 and IPv6.
/// </summary>
internal sealed class LoopbackReceiver : IDisposable
{
    private readonly List<TcpListener> _listeners = [];

    private LoopbackReceiver()
    {
    }

    public static LoopbackReceiver Start(string host, int port)
    {
        var receiver = new LoopbackReceiver();
        IPAddress[] addresses = host == "localhost" ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : [IPAddress.Parse(host)];
        foreach (var address in addresses)
        {
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                receiver._listeners.Add(listener);
            }
            catch (SocketException) when (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // no IPv6 on this machine
            }
            catch (SocketException ex)
            {
                receiver.Dispose();
                throw new IOException($"Could not open local port {port} for the sign-in (another program may be using it): {ex.Message}", ex);
            }
        }
        return receiver;
    }

    /// <summary>The query of the first request that carries <paramref name="state"/> (other requests, like a favicon, get a 404).</summary>
    public async Task<Dictionary<string, string>> WaitAsync(string state, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(Dispose);
        var pending = _listeners.Select(l => AcceptAsync(l, state, cancellationToken)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                return done.Result;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("No answer from the browser.");
    }

    private static async Task<Dictionary<string, string>> AcceptAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var line = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
            // "GET /?code=...&state=... HTTP/1.1"
            var target = line?.Split(' ') is { Length: >= 2 } parts ? parts[1] : string.Empty;
            var query = Parse(target.Contains('?', StringComparison.Ordinal) ? target[(target.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
            var ours = query.TryGetValue("state", out var s) && s == state;
            var ok = ours && query.ContainsKey("code");
            var body = ours
                ? ok ? "<h2>NextCommander: sign-in complete.</h2><p>You can close this tab and return to the application.</p>"
                     : "<h2>NextCommander: sign-in failed.</h2><p>Return to the application.</p>"
                : "<p>NextCommander</p>";
            var html = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>NextCommander</title></head><body style=\"font-family:sans-serif;margin:3em\">"
                + body + "</body></html>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(html, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            if (ours)
            {
                return query;
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        // read up to the end of the headers (or 16 KB): only the first line is needed
        var buffer = new byte[16 * 1024];
        var length = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (length < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                length += n;
                if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // a preconnected socket without a request
        }
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end > 0 ? text[..end] : null;
    }

    /// <summary>"a=1&amp;b=x%20y" → {a: 1, b: "x y"}.</summary>
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[name] = value;
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }
        }
    }
}
