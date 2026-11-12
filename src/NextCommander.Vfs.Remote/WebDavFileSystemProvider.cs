using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// WebDAV (<c>dav://</c> over HTTP, <c>davs://</c> over HTTPS): Nextcloud / ownCloud, SharePoint, IIS, Apache
/// mod_dav, NAS boxes. Folders are listed with PROPFIND (Depth 1); files are read with GET and written with PUT;
/// MKCOL, DELETE and MOVE do the rest. Basic, Digest and NTLM logins as the server asks.
/// </summary>
public sealed class WebDavFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<HttpRemoteClient>(hooks)
{
    private static readonly XNamespace Dav = "DAV:";

    private const string PropfindBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""";

    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");
    private static readonly HttpMethod Proppatch = new("PROPPATCH");

    /// <summary>How a server takes a modification time (PROPPATCH); found with the first file, then kept per server.</summary>
    private enum TimeMethod
    {
        Unknown,

        /// <summary>Nextcloud / ownCloud: {DAV:}lastmodified as Unix seconds.</summary>
        UnixLastModified,

        /// <summary>A writable {DAV:}getlastmodified (RFC 1123): Apache with mod_dav_fs patches, WsgiDAV, rclone serve.</summary>
        GetLastModified,

        /// <summary>IIS and the Windows WebDAV redirector: Win32LastModifiedTime.</summary>
        Win32,

        /// <summary>The server keeps the upload time.</summary>
        None,
    }

    private readonly ConcurrentDictionary<string, TimeMethod> _timeMethods = new(StringComparer.Ordinal);

    public override string Scheme => "dav";

    public override bool CanHandle(VfsPath path) => path.Scheme is "dav" or "davs";

    /// <summary>Times are set with PROPPATCH where the server allows it (otherwise copies get the time of the upload).</summary>
    public override ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>A server without a user name is used anonymously; a 401 asks then.</summary>
    protected override bool NeedsPassword(RemoteConnection connection) => !string.IsNullOrEmpty(connection.User);

    protected override async Task<HttpRemoteClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        var client = HttpRemoteClient.Create(connection, password, trustedFingerprint, useCredentials: true);
        try
        {
            // Checks the login (and the certificate) once: PROPFIND of the start folder.
            var start = string.IsNullOrEmpty(connection.InitialPath) ? "/" : connection.InitialPath;
            using var response = await SendPropfindAsync(client, start, depth: "0", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpStatusException(response.StatusCode, $"{connection.Host}: wrong user name or password.");
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

    protected override bool IsAuthenticationFailure(Exception ex) => ex is HttpStatusException { Status: HttpStatusCode.Unauthorized };

    protected override bool IsConnectionFailure(Exception ex) => ex is HttpRequestException or IOException and not FileNotFoundException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        HttpStatusException { Status: HttpStatusCode.NotFound } => new FileNotFoundException($"Not found: {path}", path, ex),
        HttpStatusException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        HttpStatusException => new IOException($"WebDAV: {path}: {ex.Message}", ex),
        HttpRequestException => new IOException($"WebDAV: {ex.Message}", ex),
        IOException io when io is not FileNotFoundException and not UntrustedServerException => new IOException($"WebDAV: {ex.Message}", ex),
        _ => null,
    };

    private static Task<HttpResponseMessage> SendPropfindAsync(HttpRemoteClient client, string path, string depth, CancellationToken ct)
    {
        var request = new HttpRequestMessage(Propfind, client.UriOf(path))
        {
            Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depth);
        return client.SendAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) : response.ReasonPhrase;
            _ = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            throw new HttpStatusException(response.StatusCode, $"{what}: {(int)response.StatusCode} {reason}");
        }
    }

    /// <summary>The entries of a PROPFIND answer (<paramref name="self"/> is the requested path, left out for Depth 1).</summary>
    private List<VfsEntry> ParseMultistatus(VfsPath context, string xml, string self, bool includeSelf)
    {
        var result = new List<VfsEntry>();
        var document = XDocument.Parse(xml);
        var selfPath = Normalize(self);
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }
            var path = Normalize(Uri.UnescapeDataString(Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href.Split('?')[0]));
            var isSelf = string.Equals(path, selfPath, StringComparison.Ordinal);
            if (isSelf != includeSelf)
            {
                continue;
            }
            // only the properties that came back with 200
            var props = response.Elements(Dav + "propstat")
                .Where(p => (p.Element(Dav + "status")?.Value ?? "200").Contains(" 200", StringComparison.Ordinal))
                .Select(p => p.Element(Dav + "prop")).OfType<XElement>().ToList();
            var isDirectory = props.Any(p => p.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null);
            long.TryParse(props.Select(p => p.Element(Dav + "getcontentlength")?.Value).FirstOrDefault(v => v is not null), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var size);
            var modifiedText = props.Select(p => p.Element(Dav + "getlastmodified")?.Value).FirstOrDefault(v => v is not null);
            var modified = DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m.LocalDateTime : DateTime.MinValue;
            var name = path == "/" ? context.Authority : path[(path.LastIndexOf('/') + 1)..];
            result.Add(ToEntry(context, path, name, isDirectory, isLink: false, size, modified));
        }
        return result;
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            using var response = await SendPropfindAsync(client, directory.Path, "1", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, directory.Path).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseMultistatus(directory, xml, directory.Path, includeSelf: false);
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        try
        {
            return await UseAsync(path, async client =>
            {
                using var response = await SendPropfindAsync(client, path.Path, "0", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseMultistatus(path, xml, path.Path, includeSelf: true).FirstOrDefault();
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
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, client.UriOf(path.Path)), cancellationToken, headersOnly: true)
                .ConfigureAwait(false);
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
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UseAsync(path, async client =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, client.UriOf(path.Path)) { Content = new StreamContent(data) };
            request.Content.Headers.ContentLength = data.Length;
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
        }, ct), cancellationToken);
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return;
        }
        var status = await UseAsync(path, async client =>
        {
            using var response = await client.SendAsync(new HttpRequestMessage(Mkcol, client.UriOf(path.Path)), cancellationToken).ConfigureAwait(false);
            return response.StatusCode;
        }, cancellationToken).ConfigureAwait(false);
        switch (status)
        {
            case HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent:
            case HttpStatusCode.MethodNotAllowed: // exists already
                return;
            case HttpStatusCode.Conflict when path.Parent is { } parent: // the parent is missing: create the chain
                await CreateDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
                await UseAsync(path, async client =>
                {
                    using var response = await client.SendAsync(new HttpRequestMessage(Mkcol, client.UriOf(path.Path)), cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
                    {
                        await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                    }
                }, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw Translate(new HttpStatusException(status, $"{path.Path}: {(int)status}"), path.Path)!;
        }
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (entry.IsDirectory && !recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await UseAsync(path, async client =>
        {
            // a collection is deleted with everything in it by the server
            var uri = client.UriOf(entry.IsDirectory ? path.Path.TrimEnd('/') + "/" : path.Path);
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, uri), cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority || source.Scheme != destination.Scheme)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        var caseOnly = string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase);
        await UseAsync(source, async client =>
        {
            var request = new HttpRequestMessage(Move, client.UriOf(source.Path));
            request.Headers.Add("Destination", client.UriOf(destination.Path).AbsoluteUri);
            request.Headers.Add("Overwrite", overwrite || caseOnly ? "T" : "F");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await EnsureSuccessAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// WebDAV has no standard way to set the modification time: the known server extensions are tried in turn with
    /// the first file, the one that really changes the time (checked with PROPFIND) is kept for the server.
    /// </summary>
    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (lastWriteTime is not { } time || time.Year < 1980)
        {
            return;
        }
        var server = path.Scheme + "://" + path.Authority;
        var known = _timeMethods.GetValueOrDefault(server);
        if (known == TimeMethod.None)
        {
            return;
        }
        TimeMethod[] methods = known == TimeMethod.Unknown ? [TimeMethod.UnixLastModified, TimeMethod.GetLastModified, TimeMethod.Win32] : [known];
        foreach (var method in methods)
        {
            bool accepted;
            try
            {
                accepted = await UseAsync(path, client => ProppatchTimeAsync(client, path.Path, method, time, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (known == TimeMethod.Unknown)
            {
                accepted = false;
            }
            if (!accepted)
            {
                continue;
            }
            if (known != TimeMethod.Unknown)
            {
                return;
            }
            // a server may store an unknown property without using it: check the time once
            if (await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is { } entry
                && Math.Abs((entry.LastWriteTime.ToUniversalTime() - time.ToUniversalTime()).TotalSeconds) < 2)
            {
                _timeMethods[server] = method;
                return;
            }
        }
        if (known == TimeMethod.Unknown)
        {
            _timeMethods[server] = TimeMethod.None;
        }
    }

    /// <summary>The PROPPATCH body that sets the modification time with <paramref name="method"/>.</summary>
    internal static string TimeProppatchBody(int method, DateTime time)
    {
        var utc = time.ToUniversalTime();
        var rfc1123 = utc.ToString("R", CultureInfo.InvariantCulture);
        var prop = (TimeMethod)method switch
        {
            TimeMethod.UnixLastModified => $"<d:lastmodified>{new DateTimeOffset(utc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}</d:lastmodified>",
            TimeMethod.GetLastModified => $"<d:getlastmodified>{rfc1123}</d:getlastmodified>",
            _ => $"<z:Win32LastModifiedTime>{rfc1123}</z:Win32LastModifiedTime>",
        };
        return $"""<?xml version="1.0" encoding="utf-8"?><d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:"><d:set><d:prop>{prop}</d:prop></d:set></d:propertyupdate>""";
    }

    private static async Task<bool> ProppatchTimeAsync(HttpRemoteClient client, string path, TimeMethod method, DateTime time, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(Proppatch, client.UriOf(path))
        {
            Content = new StringContent(TimeProppatchBody((int)method, time), Encoding.UTF8, "application/xml"),
        };
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return true;
        }
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            _ = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return false;
        }
        // 207: every property has its own status
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var statuses = XDocument.Parse(xml).Descendants(Dav + "propstat").Select(p => p.Element(Dav + "status")?.Value ?? string.Empty).ToList();
            return statuses.Count > 0 && statuses.All(s => s.Contains(" 200", StringComparison.Ordinal));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
