using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Azure Blob Storage (<c>azblob://account@account.blob.core.windows.net/container/folder/file</c>): containers at the
/// root, folders as name prefixes ("a/b/"), block blobs as files. The secret is the account key (Shared Key) or a SAS
/// token. A host that does not start with the account name (Azurite, an IP address) is addressed path style
/// (host/account/container). Modification times are kept in the blob's metadata (x-ms-meta-mtime).
/// </summary>
public sealed class AzureBlobFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<HttpRemoteClient>(hooks)
{
    /// <summary>Files from this size are sent in blocks.</summary>
    public long BlockThreshold { get; set; } = 64L * 1024 * 1024;

    /// <summary>Size of one block.</summary>
    public int BlockSize { get; set; } = 8 * 1024 * 1024;

    private const string MtimeHeader = "x-ms-meta-mtime";

    public override string Scheme => "azblob";

    protected override bool NeedsPassword(RemoteConnection connection) => true;

    /// <summary>"/container/a/b.txt" → ("container", "a/b.txt").</summary>
    public static (string Container, string Name) Split(string path) => S3FileSystemProvider.Split(path);

    /// <summary>Path style (host/account/...) when the host is not "account.something" (Azurite, an IP address).</summary>
    public static bool IsPathStyle(RemoteConnection connection) =>
        !connection.Host.StartsWith(connection.User + ".", StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpRemoteClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        if (!AzureSigner.IsSas(password) && !AzureSigner.IsAccountKey(password))
        {
            throw new HttpStatusException(HttpStatusCode.Forbidden, "Azure: an account key (base64) or a SAS token (sv=...&sig=...) is needed.", "AuthenticationFailed");
        }
        var client = HttpRemoteClient.Create(connection, password?.Trim().TrimStart('?'), trustedFingerprint, useCredentials: false);
        try
        {
            // checks the key: the container list (a SAS for one container may not list them: that is fine)
            using var response = await SendAsync(client, HttpMethod.Get, null, string.Empty, "comp=list&maxresults=1", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ErrorOf(response, "/").ConfigureAwait(false);
                if (!AzureSigner.IsSas(client.Password) || error.ErrorCode is "AuthenticationFailed")
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

    protected override bool IsAuthenticationFailure(Exception ex) =>
        ex is HttpStatusException { ErrorCode: "AuthenticationFailed" or "InvalidAuthenticationInfo" } or HttpStatusException { Status: HttpStatusCode.Unauthorized };

    protected override bool IsConnectionFailure(Exception ex) => ex is HttpRequestException or IOException and not FileNotFoundException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        HttpStatusException { Status: HttpStatusCode.NotFound } => new FileNotFoundException($"Not found: {path}", path, ex),
        HttpStatusException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        HttpStatusException => new IOException($"Azure: {path}: {ex.Message}", ex),
        HttpRequestException => new IOException($"Azure: {ex.Message}", ex),
        IOException io when io is not FileNotFoundException and not UntrustedServerException => new IOException($"Azure: {ex.Message}", ex),
        _ => null,
    };

    // ---------------------------------------------------------------- requests

    private static string Query(params (string Name, string? Value)[] parameters) =>
        string.Join("&", parameters.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value!)));

    /// <summary>The address of a container / blob (with the SAS token when there is one).</summary>
    private static Uri UriOf(HttpRemoteClient client, string? container, string name, string? query)
    {
        var path = (IsPathStyle(client.Connection) ? "/" + client.Connection.User : string.Empty)
            + (string.IsNullOrEmpty(container) ? "/" : "/" + container + (name.Length > 0 ? "/" + name : string.Empty));
        if (AzureSigner.IsSas(client.Password))
        {
            query = string.IsNullOrEmpty(query) ? client.Password : query + "&" + client.Password;
        }
        return client.UriOf(path, query);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRemoteClient client, HttpMethod method, string? container, string name, string? query,
        CancellationToken ct, Func<HttpContent?>? content = null, Action<HttpRequestMessage>? configure = null, bool headersOnly = false)
    {
        var request = new HttpRequestMessage(method, UriOf(client, container, name, query)) { Content = content?.Invoke() };
        if (request.Content is null && (method == HttpMethod.Put || method == HttpMethod.Post))
        {
            request.Content = new ByteArrayContent([]); // Content-Length: 0
        }
        configure?.Invoke(request);
        AzureSigner.Sign(request, client.Connection.User, AzureSigner.IsSas(client.Password) ? null : client.Password, DateTime.UtcNow);
        return await client.SendAsync(request, ct, headersOnly).ConfigureAwait(false);
    }

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        var code = response.Headers.TryGetValues("x-ms-error-code", out var values) ? values.FirstOrDefault() : null;
        string? message = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (text.Length > 0 && text.TrimStart().StartsWith('<'))
            {
                var root = XDocument.Parse(text).Root;
                code ??= root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
                message = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value?.Split('\n')[0];
            }
        }
        catch (System.Xml.XmlException)
        {
        }
        var status = code is "BlobNotFound" or "ContainerNotFound" ? HttpStatusCode.NotFound : response.StatusCode;
        return new HttpStatusException(status, $"{path}: {(int)response.StatusCode} {code} {message}".Trim(), code);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOf(response, path).ConfigureAwait(false);
        }
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static DateTime TimeOf(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.LocalDateTime : DateTime.MinValue;

    /// <summary>The kept modification time (Unix seconds, as S3 / rclone); null when there is none.</summary>
    private static DateTime? StoredTime(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime
            : null;

    /// <summary>All pages of a list request (containers or blobs).</summary>
    private static async Task<List<XElement>> ListPagesAsync(HttpRemoteClient client, string? container, Func<string?, string> query, string what, CancellationToken ct)
    {
        var pages = new List<XElement>();
        string? marker = null;
        do
        {
            using var response = await SendAsync(client, HttpMethod.Get, container, string.Empty, query(marker), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, what).ConfigureAwait(false);
            var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            pages.Add(root);
            marker = Child(root, "NextMarker")?.Value is { Length: > 0 } next ? next : null;
        }
        while (marker is not null);
        return pages;
    }

    private static Task<List<XElement>> ListBlobsAsync(HttpRemoteClient client, string container, string prefix, bool delimited, int? max, CancellationToken ct) =>
        ListPagesAsync(client, container, marker => Query(("restype", "container"), ("comp", "list"), ("prefix", prefix.Length > 0 ? prefix : null),
            ("delimiter", delimited ? "/" : null), ("include", "metadata"), ("maxresults", max?.ToString(CultureInfo.InvariantCulture)),
            ("marker", max is null ? marker : null)), "/" + container + "/" + prefix, ct);

    private static IEnumerable<XElement> Blobs(XElement page) => Children(Child(page, "Blobs"), "Blob");

    private static IEnumerable<XElement> Prefixes(XElement page) => Children(Child(page, "Blobs"), "BlobPrefix");

    // ---------------------------------------------------------------- file system

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var (container, name) = Split(directory.Path);
            var result = new List<VfsEntry>();
            if (container.Length == 0)
            {
                var pages = await ListPagesAsync(client, null, marker => Query(("comp", "list"), ("marker", marker)), "/", cancellationToken).ConfigureAwait(false);
                foreach (var c in pages.SelectMany(p => Children(Child(p, "Containers"), "Container")))
                {
                    if (Child(c, "Name")?.Value is { Length: > 0 } n)
                    {
                        result.Add(ToEntry(directory, "/" + n, n, isDirectory: true, isLink: false, 0, TimeOf(Child(Child(c, "Properties"), "Last-Modified")?.Value)));
                    }
                }
                return result;
            }
            var prefix = name.Length > 0 ? name.TrimEnd('/') + "/" : string.Empty;
            foreach (var page in await ListBlobsAsync(client, container, prefix, delimited: true, null, cancellationToken).ConfigureAwait(false))
            {
                foreach (var folder in Prefixes(page))
                {
                    var p = Child(folder, "Name")?.Value;
                    if (p is not null && p.Length > prefix.Length)
                    {
                        result.Add(ToEntry(directory, "/" + container + "/" + p.TrimEnd('/'), p[prefix.Length..].TrimEnd('/'), isDirectory: true, isLink: false, 0, DateTime.MinValue));
                    }
                }
                foreach (var blob in Blobs(page))
                {
                    var n = Child(blob, "Name")?.Value;
                    if (n is null || n.Length <= prefix.Length || n.EndsWith('/'))
                    {
                        continue; // the folder's own marker blob
                    }
                    var properties = Child(blob, "Properties");
                    long.TryParse(Child(properties, "Content-Length")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    var modified = StoredTime(Child(Child(blob, "Metadata"), "mtime")?.Value) ?? TimeOf(Child(properties, "Last-Modified")?.Value);
                    result.Add(ToEntry(directory, "/" + container + "/" + n, n[prefix.Length..], isDirectory: false, isLink: false, size, modified));
                }
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (container, name) = Split(path.Path);
        if (container.Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return await UseAsync(path, async client =>
            {
                if (name.Length == 0)
                {
                    using var head = await SendAsync(client, HttpMethod.Head, container, string.Empty, "restype=container", cancellationToken).ConfigureAwait(false);
                    return head.IsSuccessStatusCode || head.StatusCode == HttpStatusCode.Forbidden
                        ? ToEntry(path, path.Path, container, isDirectory: true, isLink: false, 0, head.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue)
                        : null;
                }
                using (var head = await SendAsync(client, HttpMethod.Head, container, name, null, cancellationToken).ConfigureAwait(false))
                {
                    if (head.IsSuccessStatusCode && !name.EndsWith('/'))
                    {
                        var stored = head.Headers.TryGetValues(MtimeHeader, out var values) ? StoredTime(values.FirstOrDefault()) : null;
                        var modified = stored ?? head.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue;
                        return ToEntry(path, path.Path, path.Name, isDirectory: false, isLink: false, head.Content.Headers.ContentLength ?? 0, modified);
                    }
                    if (head.StatusCode is not HttpStatusCode.NotFound and not HttpStatusCode.Forbidden && !head.IsSuccessStatusCode)
                    {
                        await EnsureSuccessAsync(head, path.Path).ConfigureAwait(false);
                    }
                }
                // a folder: anything under "name/"
                var pages = await ListBlobsAsync(client, container, name.TrimEnd('/') + "/", delimited: true, max: 1, cancellationToken).ConfigureAwait(false);
                return pages.Any(p => Blobs(p).Any() || Prefixes(p).Any())
                    ? ToEntry(path, path.Path, path.Name, isDirectory: true, isLink: false, 0, DateTime.MinValue)
                    : null;
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
            var (container, name) = Split(path.Path);
            var response = await SendAsync(client, HttpMethod.Get, container, name, null, cancellationToken, headersOnly: true).ConfigureAwait(false);
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
        var (container, name) = Split(path.Path);
        if (container.Length == 0 || name.Length == 0)
        {
            throw new IOException("Azure: files are written into a container.");
        }
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UseAsync(path, client => data.Length >= BlockThreshold
            ? UploadInBlocksAsync(client, container, name, data, ct)
            : PutAsync(client, container, name, data, ct), ct), cancellationToken);
    }

    private static async Task PutAsync(HttpRemoteClient client, string container, string name, Stream data, CancellationToken ct)
    {
        using var response = await SendAsync(client, HttpMethod.Put, container, name, null, ct, () =>
        {
            data.Position = 0;
            var content = new StreamContent(new NonClosingStream(data));
            content.Headers.ContentLength = data.Length;
            return content;
        }, r => r.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob")).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/" + container + "/" + name).ConfigureAwait(false);
    }

    /// <summary>Put Block for each <see cref="BlockSize"/> part, then Put Block List.</summary>
    private async Task UploadInBlocksAsync(HttpRemoteClient client, string container, string name, Stream data, CancellationToken ct)
    {
        var ids = new List<string>();
        var buffer = new byte[BlockSize];
        while (true)
        {
            var read = 0;
            while (read < buffer.Length && await data.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false) is var n and > 0)
            {
                read += n;
            }
            if (read == 0)
            {
                break;
            }
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(ids.Count.ToString("D6", CultureInfo.InvariantCulture)));
            var length = read;
            using var block = await SendAsync(client, HttpMethod.Put, container, name, Query(("comp", "block"), ("blockid", id)), ct,
                () => new ByteArrayContent(buffer, 0, length)).ConfigureAwait(false);
            await EnsureSuccessAsync(block, "/" + container + "/" + name).ConfigureAwait(false);
            ids.Add(id);
            if (read < buffer.Length)
            {
                break;
            }
        }
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var id in ids)
        {
            xml.Append("<Latest>").Append(id).Append("</Latest>");
        }
        xml.Append("</BlockList>");
        var body = Encoding.UTF8.GetBytes(xml.ToString());
        using var list = await SendAsync(client, HttpMethod.Put, container, name, "comp=blocklist", ct, () => new ByteArrayContent(body)).ConfigureAwait(false);
        await EnsureSuccessAsync(list, "/" + container + "/" + name).ConfigureAwait(false);
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (container, name) = Split(path.Path);
        if (container.Length == 0)
        {
            return;
        }
        await UseAsync(path, async client =>
        {
            if (name.Length == 0)
            {
                using var response = await SendAsync(client, HttpMethod.Put, container, string.Empty, "restype=container", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Conflict) // ContainerAlreadyExists
                {
                    await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                }
                return;
            }
            // a folder: an empty "name/" blob
            using var marker = await SendAsync(client, HttpMethod.Put, container, name.TrimEnd('/') + "/", null, cancellationToken,
                configure: r => r.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob")).ConfigureAwait(false);
            await EnsureSuccessAsync(marker, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All blob names under a prefix (folder markers included).</summary>
    private static async Task<List<string>> AllNamesAsync(HttpRemoteClient client, string container, string prefix, CancellationToken ct) =>
        (await ListBlobsAsync(client, container, prefix, delimited: false, null, ct).ConfigureAwait(false))
            .SelectMany(Blobs).Select(b => Child(b, "Name")?.Value).OfType<string>().ToList();

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var (container, name) = Split(path.Path);
        await UseAsync(path, async client =>
        {
            if (!entry.IsDirectory)
            {
                using var response = await SendAsync(client, HttpMethod.Delete, container, name, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return;
            }
            var prefix = name.Length > 0 ? name.TrimEnd('/') + "/" : string.Empty;
            var names = await AllNamesAsync(client, container, prefix, cancellationToken).ConfigureAwait(false);
            if (!recursive && names.Any(n => n != prefix))
            {
                throw new IOException($"Folder is not empty: {path.Name}");
            }
            if (name.Length == 0)
            {
                // the container goes with everything in it
                using var response = await SendAsync(client, HttpMethod.Delete, container, string.Empty, "restype=container", cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return;
            }
            foreach (var n in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var response = await SendAsync(client, HttpMethod.Delete, container, n, null, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    await EnsureSuccessAsync(response, "/" + container + "/" + n).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        var (fromContainer, fromName) = Split(source.Path);
        var (toContainer, toName) = Split(destination.Path);
        if (fromName.Length == 0 || toName.Length == 0)
        {
            throw new IOException("Azure: containers cannot be renamed.");
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
                var prefix = fromName.TrimEnd('/') + "/";
                var target = toName.TrimEnd('/') + "/";
                moves.AddRange((await AllNamesAsync(client, fromContainer, prefix, cancellationToken).ConfigureAwait(false)).Select(n => (n, target + n[prefix.Length..])));
            }
            else
            {
                moves.Add((fromName, toName));
            }
            foreach (var (from, to) in moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyBlobAsync(client, fromContainer, from, toContainer, to, cancellationToken).ConfigureAwait(false);
                using var delete = await SendAsync(client, HttpMethod.Delete, fromContainer, from, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(delete, "/" + fromContainer + "/" + from).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copy Blob inside the account (the metadata goes along); a copy still pending is waited for.</summary>
    private static async Task CopyBlobAsync(HttpRemoteClient client, string fromContainer, string from, string toContainer, string to, CancellationToken ct)
    {
        var source = UriOf(client, fromContainer, from, null).AbsoluteUri;
        string? status;
        using (var copy = await SendAsync(client, HttpMethod.Put, toContainer, to, null, ct,
            configure: r => r.Headers.TryAddWithoutValidation("x-ms-copy-source", source)).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(copy, "/" + toContainer + "/" + to).ConfigureAwait(false);
            status = copy.Headers.TryGetValues("x-ms-copy-status", out var values) ? values.FirstOrDefault() : "success";
        }
        while (status == "pending")
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            using var head = await SendAsync(client, HttpMethod.Head, toContainer, to, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(head, "/" + toContainer + "/" + to).ConfigureAwait(false);
            status = head.Headers.TryGetValues("x-ms-copy-status", out var values) ? values.FirstOrDefault() : "success";
        }
        if (status is not (null or "success"))
        {
            throw new IOException($"Azure: could not copy ({status}): /{toContainer}/{to}");
        }
    }

    /// <summary>Keeps the modification time in the blob's metadata (Set Blob Metadata; folders keep none).</summary>
    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var (container, name) = Split(path.Path);
        if (lastWriteTime is not { } time || time.Year < 1980 || container.Length == 0 || name.Length == 0 || name.EndsWith('/'))
        {
            return;
        }
        await UseAsync(path, async client =>
        {
            using var response = await SendAsync(client, HttpMethod.Put, container, name, "comp=metadata", cancellationToken,
                configure: r => r.Headers.TryAddWithoutValidation(MtimeHeader, S3FileSystemProvider.MtimeValue(time))).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound) // a folder
            {
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Request bodies must not close the upload buffer.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
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
