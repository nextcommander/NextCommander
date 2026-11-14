using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// AWS IAM Identity Center (SSO): temporary keys of a profile's account and role. The sign-in token is shared with the
/// AWS CLI (<c>~/.aws/sso/cache</c>, as <c>aws sso login</c> writes it); without a valid one the browser signs in with
/// a device code (OIDC device authorization), and the token is refreshed when it has a refresh token.
/// </summary>
public sealed class AwsSso(HttpClient http, Func<Uri, Task> openBrowser)
{
    /// <summary>Tests: the OIDC and portal addresses of a region (a fake service).</summary>
    public Func<string, string> OidcBase { get; init; } = region => $"https://oidc.{region}.amazonaws.com";

    public Func<string, string> PortalBase { get; init; } = region => $"https://portal.sso.{region}.amazonaws.com";

    /// <summary>The token cache folder (the AWS CLI's).</summary>
    public string CacheFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "sso", "cache");

    /// <summary>How long the browser sign-in may take.</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The cache file of a sign-in: SHA-1 of the session name (sso-session profiles) or of the start URL.</summary>
#pragma warning disable CA5350 // the AWS CLI names its cache files so (not a security use)
    public string CacheFile(AwsSsoProfile profile) =>
        Path.Combine(CacheFolder, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(profile.SessionName ?? profile.StartUrl))) + ".json");
#pragma warning restore CA5350

    /// <summary>Temporary keys of the profile's role (signing in first when there is no valid token).</summary>
    public async Task<AwsCredentials> CredentialsAsync(AwsSsoProfile profile, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(profile, cancellationToken).ConfigureAwait(false);
        var url = PortalBase(profile.SsoRegion) + "/federation/credentials?account_id=" + Uri.EscapeDataString(profile.AccountId)
            + "&role_name=" + Uri.EscapeDataString(profile.RoleName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-amz-sso_bearer_token", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            File.Delete(CacheFile(profile)); // the token was revoked: sign in again next time
        }
        if (!response.IsSuccessStatusCode || json?["roleCredentials"] is not { } role)
        {
            throw new IOException($"AWS SSO: no keys for {profile.AccountId}/{profile.RoleName} ({(int)response.StatusCode} {json?["message"]?.GetValue<string>()}).");
        }
        return new AwsCredentials(role["accessKeyId"]!.GetValue<string>(), role["secretAccessKey"]!.GetValue<string>(),
            role["sessionToken"]?.GetValue<string>(), profile.Region);
    }

    /// <summary>A valid access token: from the cache, refreshed, or from a new sign-in in the browser.</summary>
    public async Task<string> TokenAsync(AwsSsoProfile profile, CancellationToken cancellationToken)
    {
        var file = CacheFile(profile);
        var cache = ReadCache(file);
        if (cache?["accessToken"]?.GetValue<string>() is { } cached && ExpiresAt(cache, "expiresAt") > DateTime.UtcNow.AddMinutes(1))
        {
            return cached;
        }
        if (cache?["refreshToken"]?.GetValue<string>() is { } refresh && cache["clientId"]?.GetValue<string>() is { } id
            && cache["clientSecret"]?.GetValue<string>() is { } secret && ExpiresAt(cache, "registrationExpiresAt") > DateTime.UtcNow)
        {
            var refreshed = await PostAsync(profile.SsoRegion, "/token", new JsonObject
            {
                ["clientId"] = id, ["clientSecret"] = secret, ["grantType"] = "refresh_token", ["refreshToken"] = refresh,
            }, cancellationToken).ConfigureAwait(false);
            if (refreshed.Status == HttpStatusCode.OK && refreshed.Json?["accessToken"] is not null)
            {
                return Save(file, profile, cache, refreshed.Json);
            }
        }
        return await SignInAsync(profile, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The device code sign-in: register this application, open the browser page, wait for the approval.</summary>
    private async Task<string> SignInAsync(AwsSsoProfile profile, string file, CancellationToken cancellationToken)
    {
        var registration = await PostAsync(profile.SsoRegion, "/client/register", new JsonObject
        {
            ["clientName"] = "NextCommander", ["clientType"] = "public", ["scopes"] = new JsonArray("sso:account:access"),
        }, cancellationToken).ConfigureAwait(false);
        if (registration.Json?["clientId"]?.GetValue<string>() is not { } clientId || registration.Json["clientSecret"]?.GetValue<string>() is not { } clientSecret)
        {
            throw new IOException($"AWS SSO: the sign-in could not start ({(int)registration.Status}).");
        }
        var device = await PostAsync(profile.SsoRegion, "/device_authorization", new JsonObject
        {
            ["clientId"] = clientId, ["clientSecret"] = clientSecret, ["startUrl"] = profile.StartUrl,
        }, cancellationToken).ConfigureAwait(false);
        if (device.Json?["deviceCode"]?.GetValue<string>() is not { } deviceCode
            || (device.Json["verificationUriComplete"] ?? device.Json["verificationUri"])?.GetValue<string>() is not { } page)
        {
            throw new IOException($"AWS SSO: the sign-in could not start ({(int)device.Status}).");
        }
        var interval = TimeSpan.FromSeconds(device.Json["interval"]?.GetValue<int>() is > 0 and var s ? s : 5);
        await openBrowser(new Uri(page)).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoginTimeout);
        try
        {
            while (true)
            {
                await Task.Delay(interval, timeout.Token).ConfigureAwait(false);
                var token = await PostAsync(profile.SsoRegion, "/token", new JsonObject
                {
                    ["clientId"] = clientId, ["clientSecret"] = clientSecret, ["deviceCode"] = deviceCode,
                    ["grantType"] = "urn:ietf:params:oauth:grant-type:device_code",
                }, timeout.Token).ConfigureAwait(false);
                if (token.Status == HttpStatusCode.OK && token.Json?["accessToken"] is not null)
                {
                    var cache = new JsonObject
                    {
                        ["startUrl"] = profile.StartUrl, ["region"] = profile.SsoRegion, ["clientId"] = clientId, ["clientSecret"] = clientSecret,
                        ["registrationExpiresAt"] = registration.Json["clientSecretExpiresAt"]?.GetValue<long>() is { } at
                            ? Iso(DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime) : null,
                    };
                    return Save(file, profile, cache, token.Json);
                }
                switch (token.Json?["error"]?.GetValue<string>())
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    default:
                        throw new IOException($"AWS SSO: the sign-in was refused ({token.Json?["error"]?.GetValue<string>() ?? ((int)token.Status).ToString(CultureInfo.InvariantCulture)}).");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("AWS SSO: the sign-in in the browser timed out.");
        }
    }

    /// <summary>Writes the token to the cache (as the AWS CLI does) and returns it.</summary>
    private static string Save(string file, AwsSsoProfile profile, JsonObject cache, JsonNode token)
    {
        var access = token["accessToken"]!.GetValue<string>();
        cache["accessToken"] = access;
        cache["expiresAt"] = Iso(DateTime.UtcNow.AddSeconds(token["expiresIn"]?.GetValue<int>() ?? 3600));
        cache["startUrl"] ??= profile.StartUrl;
        cache["region"] ??= profile.SsoRegion;
        if (token["refreshToken"]?.GetValue<string>() is { } refresh)
        {
            cache["refreshToken"] = refresh;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the token works for this session anyway
        }
        return access;
    }

    private static JsonObject? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTime ExpiresAt(JsonObject cache, string key) =>
        DateTime.TryParse(cache[key]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTime.MinValue;

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<(HttpStatusCode Status, JsonNode? Json)> PostAsync(string region, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(OidcBase(region) + path, content, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await ReadAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
