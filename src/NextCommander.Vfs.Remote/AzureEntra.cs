using System.Collections.Concurrent;
using System.Net.Http.Headers;
using NextCommander.Core.Config;
using NextCommander.Vfs.Remote.Cloud;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Azure Storage (Blob and Files) with a Microsoft Entra ID account: the browser signs in once (OAuth with PKCE on
/// localhost:53682, as the cloud drives), the refresh token is kept in the secret store with "remember", and requests
/// carry the access token instead of the account key's signature. The application id is the connection's, else the
/// Azure CLI's public one; the account needs a data role on the storage (Storage Blob / File Data ...).
/// </summary>
public static class AzureEntra
{
    /// <summary>The REST version that takes Entra ID tokens on Azure Files too.</summary>
    public const string Version = "2022-11-02";

    private static readonly ConcurrentDictionary<string, CloudSession> s_sessions = new(StringComparer.Ordinal);
    private static HttpClient? s_http;

    /// <summary>Tests: the sign-in service and its HTTP handler (a fake).</summary>
    public static OAuthService? ServiceOverride { get; set; }

    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private static HttpClient Http => s_http ??= new HttpClient(HandlerFactory?.Invoke() ?? NextCommander.Core.Net.WebProxyConfig.CreateHandler()) { Timeout = TimeSpan.FromMinutes(6) };

    /// <summary>Tests: forgets the signed-in accounts and the HTTP client.</summary>
    public static void Reset()
    {
        s_sessions.Clear();
        s_http = null;
    }

    /// <summary>The access token of the connection's account (shared by its pooled clients; signing in when needed).</summary>
    public static Func<CancellationToken, Task<string>> TokenSource(RemoteConnection connection, RemoteHooks hooks)
    {
        var signedIn = connection.Clone();
        if (string.IsNullOrWhiteSpace(signedIn.ClientId))
        {
            signedIn.ClientId = OAuthService.AzureCliClientId;
        }
        var session = s_sessions.GetOrAdd(connection.SecretId + "|" + connection.TenantId,
            _ => new CloudSession(signedIn, ServiceOverride ?? OAuthService.AzureStorage(connection.TenantId), hooks, Http));
        return session.AccessTokenAsync;
    }

    /// <summary>Puts the token on a request signed without a key (the date and version are already there).</summary>
    public static async Task AuthorizeAsync(HttpRequestMessage request, HttpRemoteClient client, CancellationToken cancellationToken)
    {
        var token = await client.BearerToken!(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("x-ms-version");
        request.Headers.TryAddWithoutValidation("x-ms-version", Version);
        if (client.FileRequestIntent)
        {
            request.Headers.TryAddWithoutValidation("x-ms-file-request-intent", "backup");
        }
    }

    /// <summary>Errors of a check request that still mean the sign-in works (a role on one container or share only).</summary>
    public static bool AllowedCheckError(string? code) => code is "AuthorizationPermissionMismatch" or "AuthorizationFailure";
}
