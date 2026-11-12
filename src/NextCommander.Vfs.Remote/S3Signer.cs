using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NextCommander.Vfs.Remote;

/// <summary>AWS Signature Version 4 for S3 requests (header based).</summary>
public static class S3Signer
{
    /// <summary>Hash of an empty body (GET, HEAD, DELETE).</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Body not hashed (uploads over HTTPS; S3 and compatible servers accept it).</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// Adds x-amz-date, x-amz-content-sha256 and Authorization to <paramref name="request"/>. Signed: host, range
    /// and every x-amz-* header.
    /// </summary>
    public static void Sign(HttpRequestMessage request, string accessKey, string secretKey, string region, string payloadHash, DateTime utcNow)
    {
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        var uri = request.RequestUri!;
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = uri.Authority };
        foreach (var (name, values) in request.Headers)
        {
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("x-amz-", StringComparison.Ordinal) || lower == "range")
            {
                headers[lower] = string.Join(",", values).Trim();
            }
        }
        var authorization = Authorization(request.Method.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), headers, payloadHash, amzDate, region, accessKey, secretKey);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    /// <summary>The Authorization header value for a canonical request (also the test vector entry point).</summary>
    public static string Authorization(string method, string path, string query, IReadOnlyDictionary<string, string> headers, string payloadHash,
        string amzDate, string region, string accessKey, string secretKey)
    {
        var sorted = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(sorted.Select(h => h.Key + ":" + h.Value.Trim() + "\n"));
        var signedHeaders = string.Join(";", sorted.Select(h => h.Key));
        var canonicalQuery = string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Contains('=', StringComparison.Ordinal) ? p : p + "=")
            .OrderBy(p => p, StringComparer.Ordinal));
        var canonicalRequest = string.Join("\n", method, string.IsNullOrEmpty(path) ? "/" : path, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        var date = amzDate[..8];
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hash(Encoding.UTF8.GetBytes(canonicalRequest))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, stringToSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>Query value as S3 wants it (RFC 3986 unreserved characters only).</summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);
}
