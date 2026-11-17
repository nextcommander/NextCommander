using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// Microsoft OneDrive (<c>onedrive://account@onedrive.live.com/folder/file</c>) over Microsoft Graph: items are
/// addressed by path, big files go up in an upload session, deleted items go to the OneDrive recycle bin, the
/// modification time is the item's file system time (kept when copying). With "SharePoint" on (work / school accounts)
/// the root also has a <see cref="SharePointFolder"/> folder: the SharePoint sites of the organization, in them their
/// document libraries, which work like the user's own drive.
/// </summary>
public sealed class OneDriveFileSystemProvider(RemoteHooks hooks) : CloudProviderBase(hooks)
{
    /// <summary>Graph address of the signed-in user's drive.</summary>
    public string ApiBase { get; init; } = "https://graph.microsoft.com/v1.0/me/drive";

    /// <summary>Files up to this size go up in one request; bigger ones in an upload session.</summary>
    public long SimpleUploadLimit { get; init; } = 4L * 1024 * 1024;

    /// <summary>Upload session chunk (a multiple of 320 KiB).</summary>
    public int ChunkSize { get; init; } = 32 * 320 * 1024;

    /// <summary>The folder at the root with the SharePoint sites.</summary>
    public const string SharePointFolder = "SharePoint";

    private const string Fields = "id,name,size,lastModifiedDateTime,fileSystemInfo,folder,file,package";

    /// <summary>Site name → id and "site/library" → drive id, per account.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _ids = new(StringComparer.Ordinal);

    public override string Scheme => "onedrive";

    protected override OAuthService Service => OAuthService.Microsoft;

    /// <summary>SharePoint needs the organization's sites and every file the user may write to.</summary>
    protected override OAuthService ServiceFor(RemoteConnection connection) => connection.IncludeSharePoint
        ? OAuthService.Microsoft with { Scope = "Files.ReadWrite.All Sites.Read.All offline_access" }
        : OAuthService.Microsoft;

    /// <summary>The Graph root ("https://graph.microsoft.com/v1.0").</summary>
    private string GraphRoot => ApiBase.EndsWith("/me/drive", StringComparison.Ordinal) ? ApiBase[..^"/me/drive".Length] : ApiBase;

    private enum Kind
    {
        /// <summary>A path in a drive (the user's own or a document library).</summary>
        Drive,
        /// <summary>"/SharePoint": the sites.</summary>
        Sites,
        /// <summary>"/SharePoint/site": the document libraries of a site.</summary>
        Site,
    }

    /// <summary>Where a path lives: the drive's Graph address and the path inside it (or a list of sites / libraries).</summary>
    private sealed record Location(Kind Kind, string DriveBase, string Path, string? SiteId = null);

    private ConcurrentDictionary<string, string> IdsOf(VfsPath path) => _ids.GetOrAdd(path.Authority, _ => new(StringComparer.OrdinalIgnoreCase));

    private bool UsesSharePoint(VfsPath path) => ConnectionOf(path).IncludeSharePoint;

    private async Task<Location> LocateAsync(CloudClient client, VfsPath path, CancellationToken ct)
    {
        var parts = Relative(path.Path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != SharePointFolder || !UsesSharePoint(path))
        {
            return new Location(Kind.Drive, ApiBase, path.Path);
        }
        if (parts.Length == 1)
        {
            return new Location(Kind.Sites, string.Empty, string.Empty);
        }
        var ids = IdsOf(path);
        if (!ids.TryGetValue(parts[1], out var siteId))
        {
            _ = await SitesAsync(client, path, ct).ConfigureAwait(false);
            siteId = ids.TryGetValue(parts[1], out var found) ? found : throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path.Path}");
        }
        if (parts.Length == 2)
        {
            return new Location(Kind.Site, string.Empty, string.Empty, siteId);
        }
        var key = parts[1] + "/" + parts[2];
        if (!ids.TryGetValue(key, out var driveId))
        {
            _ = await LibrariesAsync(client, path, parts[1], siteId, ct).ConfigureAwait(false);
            driveId = ids.TryGetValue(key, out var found) ? found : throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path.Path}");
        }
        return new Location(Kind.Drive, GraphRoot + "/drives/" + Uri.EscapeDataString(driveId), "/" + string.Join('/', parts.Skip(3)));
    }

    /// <summary>Sites and libraries are listed, not changed.</summary>
    private static Location Writable(Location location, VfsPath path) => location.Kind == Kind.Drive
        ? location
        : throw new IOException($"OneDrive: SharePoint sites and libraries are not added or removed from here: {path.Path}");

    /// <summary>The SharePoint sites the user can reach (by display name; the first of equal names).</summary>
    private async Task<List<(string Name, JsonNode Item)>> SitesAsync(CloudClient client, VfsPath path, CancellationToken ct)
    {
        var ids = IdsOf(path);
        var result = new List<(string, JsonNode)>();
        await foreach (var site in PagesAsync(client, GraphRoot + "/sites?search=*&$top=200&$select=id,displayName,name,lastModifiedDateTime", path.Path, ct).ConfigureAwait(false))
        {
            var name = site["displayName"]?.GetValue<string>() is { Length: > 0 } display ? display : site["name"]?.GetValue<string>();
            if (name is null || name.Contains('/', StringComparison.Ordinal) || result.Any(r => string.Equals(r.Item1, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (site["id"]?.GetValue<string>() is { } id)
            {
                ids[name] = id;
                result.Add((name, site));
            }
        }
        return result;
    }

    /// <summary>The document libraries (drives) of a site.</summary>
    private async Task<List<(string Name, JsonNode Item)>> LibrariesAsync(CloudClient client, VfsPath path, string siteName, string siteId, CancellationToken ct)
    {
        var ids = IdsOf(path);
        var result = new List<(string, JsonNode)>();
        await foreach (var drive in PagesAsync(client, GraphRoot + "/sites/" + Uri.EscapeDataString(siteId) + "/drives?$select=id,name,lastModifiedDateTime", path.Path, ct)
            .ConfigureAwait(false))
        {
            if (drive["name"]?.GetValue<string>() is { Length: > 0 } name && !name.Contains('/', StringComparison.Ordinal) && drive["id"]?.GetValue<string>() is { } id
                && !result.Any(r => string.Equals(r.Item1, name, StringComparison.OrdinalIgnoreCase)))
            {
                ids[siteName + "/" + name] = id;
                result.Add((name, drive));
            }
        }
        return result;
    }

    /// <summary>The items of every page of a Graph collection.</summary>
    private static async IAsyncEnumerable<JsonNode> PagesAsync(CloudClient client, string first, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = first;
        while (next is not null)
        {
            var url = next;
            JsonNode? json;
            using (var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false))
            {
                await EnsureAsync(response, path).ConfigureAwait(false);
                json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            }
            foreach (var item in json?["value"]?.AsArray() ?? [])
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
            next = json?["@odata.nextLink"]?.GetValue<string>();
        }
    }

    /// <summary>Graph address of an item of the user's drive by path: "/" → .../root, "/a b/c" → .../root:/a%20b/c: (+ <paramref name="suffix"/>).</summary>
    public string ItemUri(string path, string suffix = "") => ItemUri(ApiBase, path, suffix);

    private static string ItemUri(string driveBase, string path, string suffix)
    {
        var relative = Relative(path);
        var tail = suffix.Length > 0 ? "/" + suffix.TrimStart('/') : string.Empty;
        if (relative.Length == 0)
        {
            return driveBase + "/root" + tail;
        }
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return driveBase + "/root:/" + escaped + ":" + tail;
    }

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        var json = await ReadJsonAsync(response, CancellationToken.None).ConfigureAwait(false);
        var code = json?["error"]?["code"]?.GetValue<string>();
        var message = json?["error"]?["message"]?.GetValue<string>();
        var status = code is "itemNotFound" ? HttpStatusCode.NotFound : response.StatusCode;
        return new HttpStatusException(status, $"{path}: {(int)response.StatusCode} {code} {message}".Trim(), code);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOf(response, path).ConfigureAwait(false);
        }
    }

    private VfsEntry Entry(VfsPath anyPath, string fullPath, JsonNode item)
    {
        var isFolder = item["folder"] is not null || item["package"] is not null;
        var modified = TimeOf(item["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>() ?? item["lastModifiedDateTime"]?.GetValue<string>());
        var size = item["size"]?.GetValue<long>() ?? 0;
        return ToEntry(anyPath, fullPath, item["name"]?.GetValue<string>() ?? NameOf(fullPath), isFolder, isLink: false, size, modified);
    }

    private VfsEntry Folder(VfsPath anyPath, string fullPath, string name, JsonNode? item) =>
        ToEntry(anyPath, fullPath, name, isDirectory: true, isLink: false, 0, TimeOf(item?["lastModifiedDateTime"]?.GetValue<string>()));

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var location = await LocateAsync(client, directory, cancellationToken).ConfigureAwait(false);
            var result = new List<VfsEntry>();
            switch (location.Kind)
            {
                case Kind.Sites:
                    foreach (var (name, site) in await SitesAsync(client, directory, cancellationToken).ConfigureAwait(false))
                    {
                        result.Add(Folder(directory, Combine(directory.Path, name), name, site));
                    }
                    return result;
                case Kind.Site:
                    var siteName = Relative(directory.Path).Split('/')[1];
                    foreach (var (name, drive) in await LibrariesAsync(client, directory, siteName, location.SiteId!, cancellationToken).ConfigureAwait(false))
                    {
                        result.Add(Folder(directory, Combine(directory.Path, name), name, drive));
                    }
                    return result;
            }
            await foreach (var item in PagesAsync(client, ItemUri(location.DriveBase, location.Path, "children") + "?$top=1000&$select=" + Fields, directory.Path, cancellationToken)
                .ConfigureAwait(false))
            {
                if (item["name"]?.GetValue<string>() is { } name)
                {
                    result.Add(Entry(directory, Combine(directory.Path, name), item));
                }
            }
            if (Relative(directory.Path).Length == 0 && UsesSharePoint(directory)
                && !result.Any(e => string.Equals(e.Name, SharePointFolder, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(Folder(directory, "/" + SharePointFolder, SharePointFolder, null));
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        try
        {
            return await UseAsync(path, async client =>
            {
                var location = await LocateAsync(client, path, cancellationToken).ConfigureAwait(false);
                if (location.Kind != Kind.Drive)
                {
                    return Folder(path, path.Path, path.Name, null);
                }
                using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUri(location.DriveBase, location.Path, string.Empty) + "?$select=" + Fields),
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                await EnsureAsync(response, path.Path).ConfigureAwait(false);
                var item = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                if (item is null)
                {
                    return null;
                }
                var entry = Entry(path, path.Path, item);
                return Relative(location.Path).Length == 0 ? entry with { Name = Relative(path.Path).Length == 0 ? path.Authority : path.Name, IsDirectory = true } : entry;
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
            var location = Writable(await LocateAsync(client, path, cancellationToken).ConfigureAwait(false), path);
            var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUri(location.DriveBase, location.Path, "content")), cancellationToken,
                headersOnly: true).ConfigureAwait(false);
            try
            {
                await EnsureAsync(response, path.Path).ConfigureAwait(false);
                return BodyStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
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
        var conflict = overwrite ? "replace" : "fail";
        return UploadOnClose((data, ct) => UseAsync(path, async client =>
        {
            var location = Writable(await LocateAsync(client, path, ct).ConfigureAwait(false), path);
            if (Relative(location.Path).Length == 0)
            {
                throw new IOException($"OneDrive: no file can be written here: {path.Path}");
            }
            await (data.Length <= SimpleUploadLimit
                ? SimpleUploadAsync(client, location, path.Path, data, conflict, ct)
                : SessionUploadAsync(client, location, path.Path, data, conflict, ct)).ConfigureAwait(false);
        }, ct), cancellationToken);
    }

    private static async Task SimpleUploadAsync(CloudClient client, Location location, string path, Stream data, string conflict, CancellationToken ct)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Put,
            ItemUri(location.DriveBase, location.Path, "content") + "?@microsoft.graph.conflictBehavior=" + conflict) { Content = WholeContent(data) }, ct).ConfigureAwait(false);
        await EnsureAsync(response, path).ConfigureAwait(false);
    }

    /// <summary>createUploadSession, then the chunks to the session address (which needs no token).</summary>
    private async Task SessionUploadAsync(CloudClient client, Location location, string path, Stream data, string conflict, CancellationToken ct)
    {
        var body = new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = conflict } };
        string uploadUrl;
        using (var session = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ItemUri(location.DriveBase, location.Path, "createUploadSession"))
            { Content = JsonContent(body) }, ct).ConfigureAwait(false))
        {
            await EnsureAsync(session, path).ConfigureAwait(false);
            uploadUrl = (await ReadJsonAsync(session, ct).ConfigureAwait(false))?["uploadUrl"]?.GetValue<string>()
                ?? throw new IOException($"OneDrive: could not start an upload session: {path}");
        }
        var total = data.Length;
        for (long offset = 0; offset < total;)
        {
            var length = (int)Math.Min(ChunkSize, total - offset);
            var start = offset;
            using var response = await client.SendAsync(() =>
            {
                var content = ChunkContent(data, start, length);
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, total);
                return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
            }, ct, authorize: false).ConfigureAwait(false);
            await EnsureAsync(response, path).ConfigureAwait(false);
            offset += length;
        }
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            var location = await LocateAsync(client, path, cancellationToken).ConfigureAwait(false);
            if (Relative(path.Path).Length == 0 || location.Kind != Kind.Drive || Relative(location.Path).Length == 0)
            {
                return; // the root, the sites, a site or a library: already there (an unknown one is "not found")
            }
            var body = new JsonObject { ["name"] = NameOf(location.Path), ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = "fail" };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ItemUri(location.DriveBase, ParentOf(location.Path), "children"))
                { Content = JsonContent(body) }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return; // already there
            }
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Deleted items go to the recycle bin (a folder with everything in it).</summary>
    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var location = Writable(await UseAsync(path, client => LocateAsync(client, path, cancellationToken), cancellationToken).ConfigureAwait(false), path);
        if (Relative(location.Path).Length == 0)
        {
            throw new IOException($"OneDrive: this folder cannot be deleted: {path.Path}");
        }
        if (entry.IsDirectory && !recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await UseAsync(path, async client =>
        {
            if (client.Session.Connection.DeletePermanently)
            {
                // work / school accounts: removed at once; personal accounts do not have it (the recycle bin then, as usual)
                using var permanent = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ItemUri(location.DriveBase, location.Path, "permanentDelete")), cancellationToken)
                    .ConfigureAwait(false);
                if (permanent.IsSuccessStatusCode)
                {
                    return;
                }
                if (permanent.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                {
                    await EnsureAsync(permanent, path.Path).ConfigureAwait(false);
                }
            }
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUri(location.DriveBase, location.Path, string.Empty)), cancellationToken)
                .ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        CheckSameAccount(source, destination);
        var (from, to) = await UseAsync(source, async client => (
            Writable(await LocateAsync(client, source, cancellationToken).ConfigureAwait(false), source),
            Writable(await LocateAsync(client, destination, cancellationToken).ConfigureAwait(false), destination)), cancellationToken).ConfigureAwait(false);
        if (from.DriveBase != to.DriveBase || Relative(from.Path).Length == 0 || Relative(to.Path).Length == 0)
        {
            throw new CrossDeviceMoveException("Moving between libraries is done by copying.");
        }
        if (!string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase)
            && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!overwrite || existing.IsDirectory)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await DeleteAsync(destination, recursive: false, cancellationToken).ConfigureAwait(false);
        }
        await UseAsync(source, async client =>
        {
            var parent = Relative(ParentOf(to.Path));
            var body = new JsonObject
            {
                ["name"] = NameOf(to.Path),
                ["parentReference"] = new JsonObject { ["path"] = parent.Length == 0 ? "/drive/root:" : "/drive/root:/" + parent },
            };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ItemUri(from.DriveBase, from.Path, string.Empty)) { Content = JsonContent(body) },
                cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (lastWriteTime is not { } time || time.Year < 1980 || Relative(path.Path).Length == 0)
        {
            return Task.CompletedTask;
        }
        var info = new JsonObject { ["lastModifiedDateTime"] = IsoTime(time) };
        if (creationTime is { Year: >= 1980 } created)
        {
            info["createdDateTime"] = IsoTime(created);
        }
        return UseAsync(path, async client =>
        {
            var location = await LocateAsync(client, path, cancellationToken).ConfigureAwait(false);
            if (location.Kind != Kind.Drive || Relative(location.Path).Length == 0)
            {
                return;
            }
            var body = new JsonObject { ["fileSystemInfo"] = info.DeepClone() };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ItemUri(location.DriveBase, location.Path, string.Empty))
                { Content = JsonContent(body) }, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Signs in and checks that the drive answers.</summary>
    protected override async Task<CloudClient> ConnectClientAsync(CloudClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "?$select=id"), cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "/").ConfigureAwait(false);
        return client;
    }
}
