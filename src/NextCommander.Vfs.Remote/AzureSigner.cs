using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NextCommander.Vfs.Remote;

/// <summary>Azure Storage Shared Key authorization (Blob service, version 2015-02-21 and later).</summary>
public static class AzureSigner
{
    /// <summary>The REST API version sent with every request.</summary>
    public const string Version = "2021-08-06";

    /// <summary>Adds x-ms-date, x-ms-version and (with a key) the Authorization header.</summary>
    public static void Sign(HttpRequestMessage request, string account, string? accountKey, DateTime utcNow)
    {
        request.Headers.Remove("x-ms-date");
        request.Headers.Remove("x-ms-version");
        request.Headers.TryAddWithoutValidation("x-ms-date", utcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", Version);
        if (string.IsNullOrEmpty(accountKey))
        {
            return; // SAS: the token in the query authorizes
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(",", values);
        }
        if (request.Content is { } content)
        {
            foreach (var (name, values) in content.Headers)
            {
                headers[name] = string.Join(",", values);
            }
            if (content.Headers.ContentLength is { } length)
            {
                headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
            }
        }
        var signature = Signature(request.Method.Method, request.RequestUri!, headers, account, accountKey);
        request.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {account}:{signature}");
    }

    /// <summary>The signature of a request (its headers by name, case-insensitive).</summary>
    public static string Signature(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string account, string accountKey)
    {
        string H(string name) => headers.TryGetValue(name, out var v) ? v.Trim() : string.Empty;
        var length = H("Content-Length");
        var text = new StringBuilder();
        text.Append(method).Append('\n')
            .Append(H("Content-Encoding")).Append('\n')
            .Append(H("Content-Language")).Append('\n')
            .Append(length == "0" ? string.Empty : length).Append('\n')
            .Append(H("Content-MD5")).Append('\n')
            .Append(H("Content-Type")).Append('\n')
            .Append('\n') // Date: x-ms-date is used
            .Append(H("If-Modified-Since")).Append('\n')
            .Append(H("If-Match")).Append('\n')
            .Append(H("If-None-Match")).Append('\n')
            .Append(H("If-Unmodified-Since")).Append('\n')
            .Append(H("Range")).Append('\n');
        foreach (var (name, value) in headers.Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim())).OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            text.Append(name).Append(':').Append(value).Append('\n');
        }
        text.Append('/').Append(account).Append(uri.AbsolutePath);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]).ToLowerInvariant(), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var parameter in query)
        {
            text.Append('\n').Append(parameter.Key).Append(':').Append(string.Join(",", parameter.Order(StringComparer.Ordinal)));
        }
        var key = Convert.FromBase64String(accountKey);
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A SAS token (sv=...&amp;sig=...) instead of an account key.</summary>
    public static bool IsSas(string? secret) =>
        secret is not null && secret.TrimStart('?').Contains("sig=", StringComparison.Ordinal);

    /// <summary>An account key is base64; anything else cannot sign.</summary>
    public static bool IsAccountKey(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }
        Span<byte> buffer = new byte[secret.Length];
        return Convert.TryFromBase64String(secret, buffer, out _);
    }
}
