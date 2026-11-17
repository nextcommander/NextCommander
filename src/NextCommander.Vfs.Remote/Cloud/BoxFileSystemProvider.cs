using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// Box (<c>box://account@box.com/folder/file</c>) over the Box API 2.0. Box addresses items by id (the root folder is
/// "0"): paths are resolved part by part from the folder listings and the ids kept. Names are unique in a folder
/// without regard to case. Files up to <see cref="SimpleUploadLimit"/> go up in one request, bigger ones in an upload
/// session of parts; the modification time is given with the upload (<see cref="PrepareUpload"/>), as Box cannot
/// change it later. Deleted items go to the Box trash.
/// </summary>
public sealed class BoxFileSystemProvider(RemoteHooks hooks) : CloudProviderBase(hooks), IUploadTimeSupport
{
    public string ApiBase { get; init; } = "https://api.box.com/2.0";

    public string UploadBase { get; init; } = "https://upload.box.com/api/2.0";

    /// <summary>Files up to this size go up in one request (Box takes sessions from 20 MB on).</summary>
    public long SimpleUploadLimit { get; init; } = 50L * 1024 * 1024;

    /// <summary>How long to wait before asking again when Box is still putting the parts of a session together.</summary>
    public TimeSpan CommitRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private const string RootId = "0";

    private const string Fields = "id,type,name,size,content_modified_at,modified_at";

    private sealed record Item(string Id, bool IsFolder);

    /// <summary>Path → item, per account (names are compared without regard to case, as Box does).</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Item>> _ids = new(StringComparer.Ordinal);

    /// <summary>Times to send with the next upload of a path (the copy announces them before writing).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _pendingTimes = new(StringComparer.OrdinalIgnoreCase);

    public override string Scheme => "box";

    protected override OAuthService Service => OAuthService.Box;

    public override ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename;

    private ConcurrentDictionary<string, Item> IdsOf(VfsPath path) => _ids.GetOrAdd(path.Authority, _ => new(StringComparer.OrdinalIgnoreCase));

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        var json = await ReadJsonAsync(response, CancellationToken.None).ConfigureAwait(false);
        var code = json?["code"]?.GetValue<string>();
        var message = json?["message"]?.GetValue<string>();
        return new HttpStatusException(response.StatusCode, $"{path}: {(int)response.StatusCode} {code} {message}".Trim(), code);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOf(response, path).ConfigureAwait(false);
        }
    }

    private static string Kind(bool folder) => folder ? "/folders/" : "/files/";

    private static bool IsFolder(JsonNode item) => item["type"]?.GetValue<string>() == "folder";

    private VfsEntry Entry(VfsPath anyPath, string fullPath, JsonNode item)
    {
        var folder = IsFolder(item);
        var size = folder ? 0 : item["size"]?.GetValue<long>() ?? 0;
        var modified = TimeOf(item["content_modified_at"]?.GetValue<string>() ?? item["modified_at"]?.GetValue<string>());
        return ToEntry(anyPath, fullPath, NameOf(fullPath), folder, isLink: false, size, modified);
    }

    /// <summary>All files and folders of a folder (every page; web links are left out).</summary>
    private async Task<List<JsonNode>> ChildrenAsync(CloudClient client, string folderId, string path, CancellationToken ct)
    {
        var result = new List<JsonNode>();
        var offset = 0;
        while (true)
        {
            var url = ApiBase + "/folders/" + Uri.EscapeDataString(folderId) + "/items?limit=1000&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&fields=" + Uri.EscapeDataString(Fields);
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            await EnsureAsync(response, path).ConfigureAwait(false);
            var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            var entries = (json?["entries"]?.AsArray() ?? []).OfType<JsonNode>().ToList();
            result.AddRange(entries.Where(e => e["type"]?.GetValue<string>() is "file" or "folder"));
            offset += entries.Count;
            if (entries.Count == 0 || offset >= (json?["total_count"]?.GetValue<int>() ?? 0))
            {
                return result;
            }
        }
    }

    /// <summary>The item of a path (null: not there). Resolved part by part; found ids are kept.</summary>
    private async Task<(Item Item, JsonNode? Json)?> ResolveAsync(CloudClient client, VfsPath path, CancellationToken ct)
    {
        var ids = IdsOf(path);
        var relative = Relative(path.Path);
        if (relative.Length == 0)
        {
            return (new Item(RootId, true), null);
        }
        if (ids.TryGetValue(relative, out var known))
        {
            return (known, null);
        }
        var parent = new Item(RootId, true);
        var walked = string.Empty;
        JsonNode? json = null;
        foreach (var part in relative.Split('/'))
        {
            walked = walked.Length == 0 ? part : walked + "/" + part;
            if (ids.TryGetValue(walked, out var item))
            {
                parent = item;
                json = null;
                continue;
            }
            if (!parent.IsFolder)
            {
                return null;
            }
            var match = (await ChildrenAsync(client, parent.Id, path.Path, ct).ConfigureAwait(false))
                .FirstOrDefault(c => string.Equals(c["name"]?.GetValue<string>(), part, StringComparison.OrdinalIgnoreCase));
            if (match?["id"]?.GetValue<string>() is not { } id)
            {
                return null;
            }
            parent = ids[walked] = new Item(id, IsFolder(match));
            json = match;
        }
        return (parent, json);
    }

    private async Task<Item> RequireAsync(CloudClient client, VfsPath path, CancellationToken ct) =>
        (await ResolveAsync(client, path, ct).ConfigureAwait(false))?.Item ?? throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path.Path}");

    private async Task<JsonNode> InfoAsync(CloudClient client, Item item, string path, CancellationToken ct)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + Kind(item.IsFolder) + Uri.EscapeDataString(item.Id)
            + "?fields=" + Uri.EscapeDataString(Fields)), ct).ConfigureAwait(false);
        await EnsureAsync(response, path).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false) ?? throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path}");
    }

    /// <summary>Forgets the ids of a path and everything under it (after a move or delete).</summary>
    private void Forget(VfsPath path)
    {
        var ids = IdsOf(path);
        var relative = Relative(path.Path);
        foreach (var key in ids.Keys.Where(k => string.Equals(k, relative, StringComparison.OrdinalIgnoreCase)
            || k.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ids.TryRemove(key, out _);
        }
    }

    private static VfsPath Parent(VfsPath path) => VfsPath.Create(path.Scheme, path.Authority, ParentOf(path.Path));

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var folder = await RequireAsync(client, directory, cancellationToken).ConfigureAwait(false);
            if (!folder.IsFolder)
            {
                throw new IOException($"Not a folder: {directory.Path}");
            }
            var ids = IdsOf(directory);
            var relative = Relative(directory.Path);
            var result = new List<VfsEntry>();
            foreach (var item in await ChildrenAsync(client, folder.Id, directory.Path, cancellationToken).ConfigureAwait(false))
            {
                if (item["name"]?.GetValue<string>() is not { Length: > 0 } name || item["id"]?.GetValue<string>() is not { } id)
                {
                    continue;
                }
                ids[relative.Length == 0 ? name : relative + "/" + name] = new Item(id, IsFolder(item));
                result.Add(Entry(directory, Combine(directory.Path, name), item));
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (Relative(path.Path).Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return await UseAsync(path, async client =>
            {
                if (await ResolveAsync(client, path, cancellationToken).ConfigureAwait(false) is not { } found)
                {
                    return null;
                }
                var json = found.Json ?? await InfoAsync(client, found.Item, path.Path, cancellationToken).ConfigureAwait(false);
                return Entry(path, path.Path, json);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            Forget(path);
            return null;
        }
    }

    /// <summary>The download answers with a redirect to a signed address (followed; the token is not sent there).</summary>
    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, async client =>
        {
            var item = await RequireAsync(client, path, cancellationToken).ConfigureAwait(false);
            if (item.IsFolder)
            {
                throw new IOException($"Not a file: {path.Path}");
            }
            var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/files/" + Uri.EscapeDataString(item.Id) + "/content"),
                cancellationToken, headersOnly: true).ConfigureAwait(false);
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

    /// <summary>The modification time the next upload of <paramref name="path"/> gets.</summary>
    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _pendingTimes[path.Path] = lastWriteTime;

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return UploadOnClose((data, ct) => UseAsync(path, client => UploadAsync(client, path, data, existing is not null, ct), ct), cancellationToken);
    }

    /// <summary>A new file or a new version of an existing one; in one request or in an upload session.</summary>
    private async Task UploadAsync(CloudClient client, VfsPath path, Stream data, bool replace, CancellationToken ct)
    {
        var parent = await RequireAsync(client, Parent(path), ct).ConfigureAwait(false);
        var fileId = replace ? (await RequireAsync(client, path, ct).ConfigureAwait(false)).Id : null;
        var attributes = new JsonObject();
        if (fileId is null)
        {
            attributes["name"] = path.Name;
            attributes["parent"] = new JsonObject { ["id"] = parent.Id };
        }
        if (_pendingTimes.TryRemove(path.Path, out var time) && time.Year >= 1980)
        {
            attributes["content_modified_at"] = IsoTime(time);
        }
        var file = data.Length <= SimpleUploadLimit
            ? await SimpleUploadAsync(client, path.Path, fileId, attributes, data, ct).ConfigureAwait(false)
            : await SessionUploadAsync(client, path, parent.Id, fileId, attributes, data, ct).ConfigureAwait(false);
        if (file?["id"]?.GetValue<string>() is { } id)
        {
            IdsOf(path)[Relative(path.Path)] = new Item(id, false);
        }
    }

    private async Task<JsonNode?> SimpleUploadAsync(CloudClient client, string path, string? fileId, JsonObject attributes, Stream data, CancellationToken ct)
    {
        var url = UploadBase + (fileId is null ? "/files/content" : "/files/" + Uri.EscapeDataString(fileId) + "/content");
        using var response = await client.SendAsync(() =>
        {
            // the attributes come before the file (Box reads them first)
            var form = new MultipartFormDataContent
            {
                { JsonContent(attributes), "attributes" },
                { WholeContent(data), "file", attributes["name"]?.GetValue<string>() ?? NameOf(path) },
            };
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new IOException($"Already exists: {NameOf(path)}");
        }
        await EnsureAsync(response, path).ConfigureAwait(false);
        return (await ReadJsonAsync(response, ct).ConfigureAwait(false))?["entries"]?[0];
    }

    /// <summary>SHA-1 digest header value of a part or the whole file.</summary>
    private static string Digest(Stream data, long offset, long length)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920];
        data.Position = offset;
        var left = length;
        while (left > 0)
        {
            var read = data.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0)
            {
                break;
            }
            sha.AppendData(buffer, 0, read);
            left -= read;
        }
        return "sha=" + Convert.ToBase64String(sha.GetHashAndReset());
    }

    /// <summary>An upload session: parts of the size Box gives, each with its SHA-1, then a commit with the whole file's SHA-1.</summary>
    private async Task<JsonNode?> SessionUploadAsync(CloudClient client, VfsPath path, string parentId, string? fileId, JsonObject attributes, Stream data, CancellationToken ct)
    {
        var total = data.Length;
        var start = fileId is null
            ? new JsonObject { ["folder_id"] = parentId, ["file_size"] = total, ["file_name"] = path.Name }
            : new JsonObject { ["file_size"] = total };
        var startUrl = UploadBase + (fileId is null ? "/files/upload_sessions" : "/files/" + Uri.EscapeDataString(fileId) + "/upload_sessions");
        string sessionId;
        long partSize;
        using (var started = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, startUrl) { Content = JsonContent(start) }, ct).ConfigureAwait(false))
        {
            if (started.StatusCode == HttpStatusCode.Conflict)
            {
                throw new IOException($"Already exists: {path.Name}");
            }
            await EnsureAsync(started, path.Path).ConfigureAwait(false);
            var session = await ReadJsonAsync(started, ct).ConfigureAwait(false);
            sessionId = session?["id"]?.GetValue<string>() ?? throw new IOException($"Box: could not start an upload session: {path.Path}");
            partSize = session["part_size"]?.GetValue<long>() is > 0 and var size ? size : throw new IOException($"Box: could not start an upload session: {path.Path}");
        }
        var sessionUrl = UploadBase + "/files/upload_sessions/" + Uri.EscapeDataString(sessionId);
        var parts = new JsonArray();
        for (long offset = 0; offset < total; offset += partSize)
        {
            var at = offset;
            var length = (int)Math.Min(partSize, total - offset);
            var digest = Digest(data, at, length);
            using var put = await client.SendAsync(() =>
            {
                var content = ChunkContent(data, at, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(at, at + length - 1, total);
                var request = new HttpRequestMessage(HttpMethod.Put, sessionUrl) { Content = content };
                request.Headers.TryAddWithoutValidation("Digest", digest);
                return request;
            }, ct).ConfigureAwait(false);
            await EnsureAsync(put, path.Path).ConfigureAwait(false);
            var part = (await ReadJsonAsync(put, ct).ConfigureAwait(false))?["part"] ?? throw new IOException($"Box: a part was not taken: {path.Path}");
            parts.Add(part.DeepClone());
        }
        var whole = Digest(data, 0, total);
        var commit = new JsonObject { ["parts"] = parts, ["attributes"] = attributes.DeepClone() };
        for (var attempt = 0; ; attempt++)
        {
            using var done = await client.SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, sessionUrl + "/commit") { Content = JsonContent(commit) };
                request.Headers.TryAddWithoutValidation("Digest", whole);
                return request;
            }, ct).ConfigureAwait(false);
            if (done.StatusCode == HttpStatusCode.Accepted && attempt < 30)
            {
                await Task.Delay(done.Headers.RetryAfter?.Delta ?? CommitRetryDelay, ct).ConfigureAwait(false); // the parts are still being put together
                continue;
            }
            await EnsureAsync(done, path.Path).ConfigureAwait(false);
            return (await ReadJsonAsync(done, ct).ConfigureAwait(false))?["entries"]?[0];
        }
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            if (Relative(path.Path).Length == 0 || await ResolveAsync(client, path, cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
            var parent = await RequireAsync(client, Parent(path), cancellationToken).ConfigureAwait(false);
            var body = new JsonObject { ["name"] = path.Name, ["parent"] = new JsonObject { ["id"] = parent.Id } };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + "/folders?fields=id") { Content = JsonContent(body) },
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return; // made meanwhile
            }
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
            if ((await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false))?["id"]?.GetValue<string>() is { } id)
            {
                IdsOf(path)[Relative(path.Path)] = new Item(id, true);
            }
        }, cancellationToken);

    /// <summary>Moves the item into the Box trash (a folder with everything in it).</summary>
    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        if (Relative(path.Path).Length == 0)
        {
            throw new IOException($"Box: this folder cannot be changed: {path.Path}");
        }
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (entry.IsDirectory && !recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await UseAsync(path, async client =>
        {
            var item = await RequireAsync(client, path, cancellationToken).ConfigureAwait(false);
            var url = ApiBase + Kind(item.IsFolder) + Uri.EscapeDataString(item.Id) + (item.IsFolder ? "?recursive=true" : string.Empty);
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
            if (client.Session.Connection.DeletePermanently)
            {
                // out of the Box trash too
                var trash = ApiBase + Kind(item.IsFolder) + Uri.EscapeDataString(item.Id) + "/trash";
                using var purge = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, trash), cancellationToken).ConfigureAwait(false);
                await EnsureAsync(purge, path.Path).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        Forget(path);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        CheckSameAccount(source, destination);
        if (Relative(source.Path).Length == 0)
        {
            throw new IOException($"Box: this folder cannot be changed: {source.Path}");
        }
        var sameName = string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase);
        if (!sameName && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!overwrite || existing.IsDirectory)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await DeleteAsync(destination, recursive: false, cancellationToken).ConfigureAwait(false);
        }
        await UseAsync(source, async client =>
        {
            var item = await RequireAsync(client, source, cancellationToken).ConfigureAwait(false);
            var toParent = await RequireAsync(client, Parent(destination), cancellationToken).ConfigureAwait(false);
            var body = new JsonObject { ["name"] = destination.Name, ["parent"] = new JsonObject { ["id"] = toParent.Id } };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, ApiBase + Kind(item.IsFolder) + Uri.EscapeDataString(item.Id) + "?fields=id")
                { Content = JsonContent(body) }, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Forget(source);
        Forget(destination);
    }

    /// <summary>Box keeps the time given at upload only (<see cref="PrepareUpload"/>); a later change is not possible.</summary>
    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<CloudClient> ConnectClientAsync(CloudClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/users/me?fields=id"), cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "/").ConfigureAwait(false);
        return client;
    }
}
