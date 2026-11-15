using System.Net.Http.Headers;
using System.Text.Json;

namespace NextCommander.Core.Tools;

/// <summary>A newer version announced by the update address.</summary>
public sealed record UpdateInfo(Version Version, string? DownloadUrl, string? Notes);

/// <summary>
/// Optional update check (off unless an address is configured). The address returns either a small manifest
/// <c>{"version":"1.2.0","url":"...","notes":"..."}</c> or a GitHub "latest release" response (tag_name, html_url, body).
/// </summary>
public static class UpdateChecker
{
    /// <summary>The newer version, or null when <paramref name="current"/> is up to date.</summary>
    /// <exception cref="IOException">Address unreachable or the answer is not understood.</exception>
    public static async Task<UpdateInfo?> CheckAsync(Uri address, Version current, HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NextCommander", current.ToString()));
        request.Headers.Accept.ParseAdd("application/json");
        string json;
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Update URL did not respond: HTTP {(int)response.StatusCode}");
            }
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("Could not reach the update URL: " + ex.Message, ex);
        }
        var info = Parse(json) ?? throw new IOException("Could not parse update information.");
        return info.Version > Normalize(current) ? info : null;
    }

    /// <summary>Reads a manifest or a GitHub release; null when it has no usable version.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var versionText = Text(root, "version") ?? Text(root, "tag_name");
            if (ParseVersion(versionText) is not { } version)
            {
                return null;
            }
            return new UpdateInfo(version, Text(root, "url") ?? Text(root, "html_url"), Text(root, "notes") ?? Text(root, "body"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.2", "1.2.3-beta", "release-2.0" → 1.2(.0.0) etc.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var start = text.AsSpan().IndexOfAnyInRange('0', '9');
        if (start < 0)
        {
            return null;
        }
        var end = start;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }
        var core = text[start..end].Trim('.');
        if (!core.Contains('.', StringComparison.Ordinal))
        {
            core += ".0";
        }
        return Version.TryParse(core, out var v) ? Normalize(v) : null;
    }

    /// <summary>Missing parts as 0 so 1.2 == 1.2.0.0.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Version of the application as shown to the user: YY.MM.NNNN (e.g. 26.11.0071; NNNN starts at 0000 each month).</summary>
    public static string DisplayVersion(System.Reflection.Assembly assembly) =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Version of the running application (assembly informational version without build metadata).</summary>
    public static Version CurrentVersion(System.Reflection.Assembly assembly) =>
        ParseVersion(assembly.GetName().Version?.ToString(4)) ?? new Version(0, 0, 0, 0);
}
