using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Amazon S3 and compatible storage (<c>s3://accesskey@endpoint/bucket/folder/file</c>): MinIO, Cloudflare R2, Wasabi,
/// Backblaze B2, DigitalOcean Spaces ... The root lists the buckets; "/" in object keys makes the folders (a folder
/// made with F7 is an empty "name/" object). Requests are path-style and signed with AWS Signature Version 4; the user
/// is the access key id, the password the secret access key. Big files are uploaded in parts.
/// </summary>
public sealed partial class S3FileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<HttpRemoteClient>(hooks)
{
    /// <summary>Files from this size on are uploaded in parts (multipart upload).</summary>
    public long MultipartThreshold { get; set; } = 64L * 1024 * 1024;

    /// <summary>Size of one part (S3: at least 5 MB except the last).</summary>
    public long PartSize { get; set; } = 32L * 1024 * 1024;

    /// <summary>Region of each bucket when it differs from the connection's (learnt from redirects).</summary>
    private readonly ConcurrentDictionary<string, string> _bucketRegions = new(StringComparer.Ordinal);

    public override string Scheme => "s3";

    /// <summary>No times (S3 keeps the upload time) and no real rename (copy + delete).</summary>
    public override ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>
    /// The modification time kept in the object's metadata (<c>x-amz-meta-mtime</c>, Unix seconds; the same as
    /// rclone), since S3 itself only knows the upload time.
    /// </summary>
    public const string MtimeHeader = "x-amz-meta-mtime";

    /// <summary>
    /// A listing reads the kept times (one HEAD request per file) when the folder has at most this many files;
    /// bigger folders show the upload times. 0: never.
    /// </summary>
    public int StoredTimesLimit { get; set; } = 300;

    /// <summary>The kept modification time of a HEAD answer; null when the object has none.</summary>
    internal static DateTime? StoredTime(HttpResponseMessage head) =>
        head.Headers.TryGetValues(MtimeHeader, out var values) && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime
            : null;

    /// <summary>Unix seconds as stored in <see cref="MtimeHeader"/>.</summary>
    internal static string MtimeValue(DateTime time) =>
        (new DateTimeOffset(time.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>With an AWS profile the keys come from the AWS CLI's files.</summary>
    protected override bool NeedsPassword(RemoteConnection connection) => string.IsNullOrWhiteSpace(connection.AwsProfile);

    /// <summary>A bucket name that can be a host name part (virtual-hosted addresses; with TLS also without dots).</summary>
    public static bool IsDnsBucket(string bucket, bool secure) =>
        bucket.Length is >= 3 and <= 63 && bucket.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' || c == '.' && !secure)
        && char.IsAsciiLetterOrDigit(bucket[0]) && char.IsAsciiLetterOrDigit(bucket[^1]);

    /// <summary>Signing region: the connection's, one in an amazonaws.com host name, otherwise us-east-1.</summary>
    public static string RegionOf(RemoteConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Region))
        {
            return connection.Region.Trim();
        }
        var match = AwsHost().Match(connection.Host);
        return match.Success ? match.Groups["region"].Value : "us-east-1";
    }

    [GeneratedRegex(@"^(?:[^.]+\.)?s3[.-](?:dualstack\.)?(?<region>[a-z]{2}(?:-gov)?-[a-z]+-\d)\.amazonaws\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex AwsHost();

    protected override async Task<HttpRemoteClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        string? token = null;
        if (!string.IsNullOrWhiteSpace(connection.AwsProfile))
        {
            var profile = connection.AwsProfile.Trim();
            var keys = AwsProfiles.Load(profile)
                ?? throw new IOException($"S3: AWS profile not found or without keys: {profile} ({AwsProfiles.CredentialsFile})");
            connection = connection.Clone();
            connection.User = keys.AccessKey;
            if (string.IsNullOrWhiteSpace(connection.Region) && keys.Region is { } region)
            {
                connection.Region = region;
            }
            password = keys.SecretKey;
            token = keys.SessionToken;
        }
        var client = HttpRemoteClient.Create(connection, password, trustedFingerprint, useCredentials: false, token);
        try
        {
            // Checks the keys (and the certificate): the bucket list. AccessDenied is fine (keys valid, no list right).
            using var response = await SendAsync(client, HttpMethod.Get, null, string.Empty, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ErrorOf(response, "/").ConfigureAwait(false);
                if (error.ErrorCode is not "AccessDenied" and not "AllAccessDisabled")
                {
                    throw error;
                }
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    protected override bool IsAlive(HttpRemoteClient client) => true;

    protected override ValueTask CloseAsync(HttpRemoteClient client)
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override bool IsAuthenticationFailure(Exception ex) => ex is HttpStatusException
    {
        ErrorCode: "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidSecurity" or "InvalidToken" or "AuthorizationHeaderMalformed" or "MissingSecurityHeader",
    } or HttpStatusException { Status: HttpStatusCode.Unauthorized };

    protected override bool IsConnectionFailure(Exception ex) => ex is HttpRequestException or IOException and not FileNotFoundException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        HttpStatusException { Status: HttpStatusCode.NotFound } => new FileNotFoundException($"Not found: {path}", path, ex),
        HttpStatusException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        HttpStatusException => new IOException($"S3: {path}: {ex.Message}", ex),
        HttpRequestException => new IOException($"S3: {ex.Message}", ex),
        IOException io when io is not FileNotFoundException and not UntrustedServerException => new IOException($"S3: {ex.Message}", ex),
        _ => null,
    };

    // ---------------------------------------------------------------- requests

    /// <summary>"/bucket/a/b.txt" → ("bucket", "a/b.txt"); "/" → ("", "").</summary>
    public static (string Bucket, string Key) Split(string path)
    {
        var trimmed = path.Trim('/');
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    private static string Query(params (string Name, string? Value)[] parameters) =>
        string.Join("&", parameters.Where(p => p.Value is not null).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => S3Signer.Encode(p.Name) + "=" + S3Signer.Encode(p.Value!)));

    /// <summary>Signs and sends a request for a bucket / key; a redirect to another region is followed once.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRemoteClient client, HttpMethod method, string? bucket, string key, string? query,
        CancellationToken ct, Func<HttpContent?>? content = null, string? payloadHash = null, Action<HttpRequestMessage>? configure = null, bool headersOnly = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            Uri uri;
            if (client.Connection.S3VirtualHost && !string.IsNullOrEmpty(bucket) && IsDnsBucket(bucket, client.BaseUri.Scheme == "https"))
            {
                uri = client.UriOf("/" + key, query, subdomain: bucket); // bucket.host/key
            }
            else
            {
                var path = string.IsNullOrEmpty(bucket) ? "/" : "/" + bucket + (key.Length > 0 ? "/" + key : string.Empty);
                uri = client.UriOf(path, query);
            }
            var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() };
            configure?.Invoke(request);
            if (client.SessionToken is { Length: > 0 } token)
            {
                request.Headers.TryAddWithoutValidation("x-amz-security-token", token); // signed with the x-amz-* headers
            }
            var region = bucket is not null && _bucketRegions.TryGetValue(bucket, out var known) ? known : RegionOf(client.Connection);
            S3Signer.Sign(request, client.Connection.User, client.Password ?? string.Empty, region, payloadHash ?? S3Signer.EmptyPayloadHash, DateTime.UtcNow);
            var response = await client.SendAsync(request, ct, headersOnly).ConfigureAwait(false);
            if (attempt == 0 && bucket is { Length: > 0 }
                && response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.BadRequest or HttpStatusCode.TemporaryRedirect
                && response.Headers.TryGetValues("x-amz-bucket-region", out var values) && values.FirstOrDefault() is { Length: > 0 } other && other != region)
            {
                _bucketRegions[bucket] = other;
                response.Dispose();
                continue;
            }
            return response;
        }
    }

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        string? code = null;
        string? message = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (text.Length > 0)
            {
                var root = XDocument.Parse(text).Root;
                code = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
                message = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            }
        }
        catch (System.Xml.XmlException)
        {
        }
        var status = response.StatusCode;
        if (code is "NoSuchKey" or "NoSuchBucket")
        {
            status = HttpStatusCode.NotFound;
        }
        return new HttpStatusException(status, $"{path}: {(int)response.StatusCode} {code} {message}".Trim(), code);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOf(response, path).ConfigureAwait(false);
        }
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);

    private static DateTime TimeOf(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.LocalDateTime : DateTime.MinValue;

    /// <summary>One page after another of ListObjectsV2.</summary>
    private async Task<List<XElement>> ListPagesAsync(HttpRemoteClient client, string bucket, string prefix, bool delimited, int? maxKeys, CancellationToken ct)
    {
        var pages = new List<XElement>();
        string? token = null;
        do
        {
            var query = Query(("list-type", "2"), ("prefix", prefix.Length > 0 ? prefix : null), ("delimiter", delimited ? "/" : null),
                ("continuation-token", token), ("max-keys", maxKeys?.ToString(CultureInfo.InvariantCulture)));
            using var response = await SendAsync(client, HttpMethod.Get, bucket, string.Empty, query, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "/" + bucket + "/" + prefix).ConfigureAwait(false);
            var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            pages.Add(root);
            token = Child(root, "IsTruncated")?.Value == "true" && maxKeys is null ? Child(root, "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return pages;
    }

    /// <summary>All keys under a prefix (no folders).</summary>
    private async Task<List<string>> AllKeysAsync(HttpRemoteClient client, string bucket, string prefix, CancellationToken ct) =>
        (await ListPagesAsync(client, bucket, prefix, delimited: false, null, ct).ConfigureAwait(false))
            .SelectMany(p => Children(p, "Contents")).Select(c => Child(c, "Key")?.Value).OfType<string>().ToList();

    // ---------------------------------------------------------------- file system

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var (bucket, key) = Split(directory.Path);
            var result = new List<VfsEntry>();
            if (bucket.Length == 0)
            {
                using var response = await SendAsync(client, HttpMethod.Get, null, string.Empty, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "/").ConfigureAwait(false);
                var root = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Root!;
                foreach (var b in root.Descendants().Where(e => e.Name.LocalName == "Bucket"))
                {
                    var name = Child(b, "Name")?.Value;
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add(ToEntry(directory, "/" + name, name, isDirectory: true, isLink: false, 0, TimeOf(Child(b, "CreationDate")?.Value)));
                    }
                }
                return result;
            }
            var prefix = key.Length > 0 ? key.TrimEnd('/') + "/" : string.Empty;
            foreach (var page in await ListPagesAsync(client, bucket, prefix, delimited: true, null, cancellationToken).ConfigureAwait(false))
            {
                foreach (var folder in Children(page, "CommonPrefixes"))
                {
                    var p = Child(folder, "Prefix")?.Value;
                    if (p is null || p.Length <= prefix.Length)
                    {
                        continue;
                    }
                    var name = p[prefix.Length..].TrimEnd('/');
                    result.Add(ToEntry(directory, "/" + bucket + "/" + p.TrimEnd('/'), name, isDirectory: true, isLink: false, 0, DateTime.MinValue));
                }
                foreach (var item in Children(page, "Contents"))
                {
                    var k = Child(item, "Key")?.Value;
                    if (k is null || k.Length <= prefix.Length || k.EndsWith('/'))
                    {
                        continue; // the folder's own marker object
                    }
                    long.TryParse(Child(item, "Size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    result.Add(ToEntry(directory, "/" + bucket + "/" + k, k[prefix.Length..], isDirectory: false, isLink: false, size,
                        TimeOf(Child(item, "LastModified")?.Value)));
                }
            }
            var files = result.Where(e => !e.IsDirectory).ToList();
            if (files.Count > 0 && files.Count <= StoredTimesLimit)
            {
                // the kept modification times (a listing only has the upload times)
                var times = new DateTime?[files.Count];
                await Parallel.ForAsync(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (i, ct) =>
                {
                    var (b, k) = Split(files[i].Path.Path);
                    using var head = await SendAsync(client, HttpMethod.Head, b, k, null, ct).ConfigureAwait(false);
                    times[i] = head.IsSuccessStatusCode ? StoredTime(head) : null;
                }).ConfigureAwait(false);
                for (var i = 0; i < files.Count; i++)
                {
                    if (times[i] is { } time)
                    {
                        result[result.IndexOf(files[i])] = files[i] with { LastWriteTime = time };
                    }
                }
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path.Path);
        if (bucket.Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return await UseAsync(path, async client =>
            {
                if (key.Length == 0)
                {
                    using var head = await SendAsync(client, HttpMethod.Head, bucket, string.Empty, null, cancellationToken).ConfigureAwait(false);
                    return head.IsSuccessStatusCode || head.StatusCode == HttpStatusCode.Forbidden
                        ? ToEntry(path, path.Path, bucket, isDirectory: true, isLink: false, 0, DateTime.MinValue)
                        : null;
                }
                using (var head = await SendAsync(client, HttpMethod.Head, bucket, key, null, cancellationToken).ConfigureAwait(false))
                {
                    if (head.IsSuccessStatusCode && !key.EndsWith('/'))
                    {
                        var modified = StoredTime(head) ?? head.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue;
                        return ToEntry(path, path.Path, path.Name, isDirectory: false, isLink: false, head.Content.Headers.ContentLength ?? 0, modified);
                    }
                    if (head.StatusCode is not HttpStatusCode.NotFound and not HttpStatusCode.Forbidden && !head.IsSuccessStatusCode)
                    {
                        await EnsureSuccessAsync(head, path.Path).ConfigureAwait(false);
                    }
                }
                // a folder: anything under "key/"
                var pages = await ListPagesAsync(client, bucket, key.TrimEnd('/') + "/", delimited: true, maxKeys: 1, cancellationToken).ConfigureAwait(false);
                var any = pages.Any(p => Children(p, "Contents").Any() || Children(p, "CommonPrefixes").Any());
                return any ? ToEntry(path, path.Path, path.Name, isDirectory: true, isLink: false, 0, DateTime.MinValue) : null;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, async client =>
        {
            var (bucket, key) = Split(path.Path);
            var response = await SendAsync(client, HttpMethod.Get, bucket, key, null, cancellationToken, headersOnly: true).ConfigureAwait(false);
            try
            {
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return (Stream)new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }, afterClose: null, cancellationToken);

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path.Path);
        if (bucket.Length == 0 || key.Length == 0)
        {
            throw new IOException("S3: files are written into a bucket.");
        }
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UseAsync(path, client => data.Length >= MultipartThreshold
            ? UploadInPartsAsync(client, bucket, key, data, ct)
            : PutAsync(client, bucket, key, data, ct), ct), cancellationToken);
    }

    private async Task PutAsync(HttpRemoteClient client, string bucket, string key, Stream data, CancellationToken ct)
    {
        using var response = await SendAsync(client, HttpMethod.Put, bucket, key, null, ct, () =>
        {
            data.Position = 0;
            var content = new StreamContent(new NonDisposingStream(data));
            content.Headers.ContentLength = data.Length;
            return content;
        }, S3Signer.UnsignedPayload).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/" + bucket + "/" + key).ConfigureAwait(false);
    }

    /// <summary>Multipart upload: start, parts of <see cref="PartSize"/>, complete (aborted on an error).</summary>
    private async Task UploadInPartsAsync(HttpRemoteClient client, string bucket, string key, Stream data, CancellationToken ct)
    {
        string uploadId;
        using (var start = await SendAsync(client, HttpMethod.Post, bucket, key, "uploads=", ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(start, "/" + bucket + "/" + key).ConfigureAwait(false);
            var root = XDocument.Parse(await start.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            uploadId = Child(root, "UploadId")?.Value ?? throw new IOException("S3: no UploadId.");
        }
        try
        {
            var etags = new List<string>();
            var buffer = new byte[PartSize];
            for (var number = 1; ; number++)
            {
                var read = 0;
                while (read < buffer.Length && await data.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false) is var n and > 0)
                {
                    read += n;
                }
                if (read == 0 && number > 1)
                {
                    break;
                }
                var length = read;
                var query = Query(("partNumber", number.ToString(CultureInfo.InvariantCulture)), ("uploadId", uploadId));
                using var part = await SendAsync(client, HttpMethod.Put, bucket, key, query, ct,
                    () => new ByteArrayContent(buffer, 0, length), S3Signer.Hash(buffer[..length])).ConfigureAwait(false);
                await EnsureSuccessAsync(part, "/" + bucket + "/" + key).ConfigureAwait(false);
                etags.Add(part.Headers.ETag?.Tag ?? (part.Headers.TryGetValues("ETag", out var v) ? v.First() : string.Empty));
                if (read < buffer.Length)
                {
                    break;
                }
            }
            var xml = new StringBuilder("<CompleteMultipartUpload>");
            for (var i = 0; i < etags.Count; i++)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<Part><PartNumber>{i + 1}</PartNumber><ETag>{System.Security.SecurityElement.Escape(etags[i])}</ETag></Part>");
            }
            xml.Append("</CompleteMultipartUpload>");
            var body = Encoding.UTF8.GetBytes(xml.ToString());
            using var complete = await SendAsync(client, HttpMethod.Post, bucket, key, Query(("uploadId", uploadId)), ct,
                () => new ByteArrayContent(body), S3Signer.Hash(body)).ConfigureAwait(false);
            await EnsureSuccessAsync(complete, "/" + bucket + "/" + key).ConfigureAwait(false);
            // an error can also come inside a 200 answer
            var text = await complete.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Contains("<Error>", StringComparison.Ordinal))
            {
                throw new HttpStatusException(HttpStatusCode.InternalServerError, text);
            }
        }
        catch
        {
            using var abort = await SendAsync(client, HttpMethod.Delete, bucket, key, Query(("uploadId", uploadId)), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path.Path);
        if (bucket.Length == 0)
        {
            return;
        }
        if (key.Length == 0)
        {
            // a new bucket
            await UseAsync(path, async client =>
            {
                var region = RegionOf(client.Connection);
                var body = region is "us-east-1" ? [] : Encoding.UTF8.GetBytes(
                    $"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{region}</LocationConstraint></CreateBucketConfiguration>");
                using var response = await SendAsync(client, HttpMethod.Put, bucket, string.Empty, null, cancellationToken,
                    body.Length > 0 ? () => new ByteArrayContent(body) : null, S3Signer.Hash(body)).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Conflict) // BucketAlreadyOwnedByYou
                {
                    await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        // a folder: an empty "key/" object
        await UseAsync(path, async client =>
        {
            using var response = await SendAsync(client, HttpMethod.Put, bucket, key.TrimEnd('/') + "/", null, cancellationToken,
                () => new ByteArrayContent([]), S3Signer.EmptyPayloadHash).ConfigureAwait(false);
            await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var (bucket, key) = Split(path.Path);
        await UseAsync(path, async client =>
        {
            if (!entry.IsDirectory)
            {
                using var response = await SendAsync(client, HttpMethod.Delete, bucket, key, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return;
            }
            var prefix = key.Length > 0 ? key.TrimEnd('/') + "/" : string.Empty;
            var keys = await AllKeysAsync(client, bucket, prefix, cancellationToken).ConfigureAwait(false);
            if (!recursive && keys.Any(k => k != prefix))
            {
                throw new IOException($"Folder is not empty: {path.Name}");
            }
            foreach (var k in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var response = await SendAsync(client, HttpMethod.Delete, bucket, k, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "/" + bucket + "/" + k).ConfigureAwait(false);
            }
            if (key.Length == 0)
            {
                using var response = await SendAsync(client, HttpMethod.Delete, bucket, string.Empty, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        var (fromBucket, fromKey) = Split(source.Path);
        var (toBucket, toKey) = Split(destination.Path);
        if (fromKey.Length == 0 || toKey.Length == 0)
        {
            throw new IOException("S3: buckets cannot be renamed.");
        }
        var entry = await GetEntryAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {source.Path}");
        if (!string.Equals(source.Path, destination.Path, StringComparison.Ordinal) && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await UseAsync(source, async client =>
        {
            var moves = new List<(string From, string To)>();
            if (entry.IsDirectory)
            {
                var prefix = fromKey.TrimEnd('/') + "/";
                var target = toKey.TrimEnd('/') + "/";
                moves.AddRange((await AllKeysAsync(client, fromBucket, prefix, cancellationToken).ConfigureAwait(false)).Select(k => (k, target + k[prefix.Length..])));
            }
            else
            {
                moves.Add((fromKey, toKey));
            }
            foreach (var (from, to) in moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copySource = "/" + fromBucket + "/" + string.Join('/', from.Split('/').Select(Uri.EscapeDataString));
                using (var copy = await SendAsync(client, HttpMethod.Put, toBucket, to, null, cancellationToken,
                    configure: r => r.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource)).ConfigureAwait(false))
                {
                    await EnsureSuccessAsync(copy, "/" + toBucket + "/" + to).ConfigureAwait(false);
                    var text = await copy.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (text.Contains("<Error>", StringComparison.Ordinal))
                    {
                        throw new HttpStatusException(HttpStatusCode.InternalServerError, text);
                    }
                }
                using var delete = await SendAsync(client, HttpMethod.Delete, fromBucket, from, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(delete, "/" + fromBucket + "/" + from).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the time in the object's metadata: the object is copied onto itself with new metadata (server side;
    /// objects over 5 GB, which a single copy cannot take, and folders keep the upload time).
    /// </summary>
    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path.Path);
        if (lastWriteTime is not { } time || time.Year < 1980 || bucket.Length == 0 || key.Length == 0 || key.EndsWith('/'))
        {
            return;
        }
        await UseAsync(path, async client =>
        {
            string? contentType;
            using (var head = await SendAsync(client, HttpMethod.Head, bucket, key, null, cancellationToken).ConfigureAwait(false))
            {
                if (!head.IsSuccessStatusCode || (head.Content.Headers.ContentLength ?? 0) > 5L * 1024 * 1024 * 1024)
                {
                    return; // a folder, or too big for a single copy
                }
                contentType = head.Content.Headers.ContentType?.ToString();
            }
            var copySource = "/" + bucket + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
            using var copy = await SendAsync(client, HttpMethod.Put, bucket, key, null, cancellationToken,
                content: () =>
                {
                    var empty = new ByteArrayContent([]);
                    if (contentType is not null)
                    {
                        empty.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    }
                    return empty;
                },
                configure: r =>
                {
                    r.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource);
                    r.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "REPLACE");
                    r.Headers.TryAddWithoutValidation(MtimeHeader, MtimeValue(time));
                }).ConfigureAwait(false);
            await EnsureSuccessAsync(copy, path.Path).ConfigureAwait(false);
            var text = await copy.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Contains("<Error>", StringComparison.Ordinal))
            {
                throw new HttpStatusException(HttpStatusCode.InternalServerError, text);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Request bodies must not close the upload buffer (a redirect sends it again).</summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
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
}
