using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// Dropbox (<c>dropbox://account@dropbox.com/folder/file</c>) over the Dropbox API v2: paths as they are, big files in
/// an upload session, deleted files stay restorable in Dropbox. The modification time is the "client_modified" time
/// given at upload; Dropbox cannot change it afterwards, so uploads carry it (see <see cref="SetTimesAsync"/>).
/// </summary>
public sealed class DropboxFileSystemProvider(RemoteHooks hooks) : CloudProviderBase(hooks), IUploadTimeSupport
{
    public string ApiBase { get; init; } = "https://api.dropboxapi.com/2";

    public string ContentBase { get; init; } = "https://content.dropboxapi.com/2";

    /// <summary>Files up to this size go up in one request (the limit of /files/upload is 150 MB).</summary>
    public long SimpleUploadLimit { get; init; } = 128L * 1024 * 1024;

    public int ChunkSize { get; init; } = 16 * 1024 * 1024;

    public override string Scheme => "dropbox";

    protected override OAuthService Service => OAuthService.Dropbox;

    public override ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename;

    /// <summary>Path as the API wants it: root "", otherwise "/a/b".</summary>
    public static string ApiPath(string path) => Relative(path).Length == 0 ? string.Empty : "/" + Relative(path);

    /// <summary>
    /// The Dropbox-API-Arg header: JSON with every non-ASCII character escaped (\uXXXX), as HTTP headers are ASCII.
    /// </summary>
    public static string ApiArg(JsonNode arg)
    {
        var json = arg.ToJsonString();
        var text = new StringBuilder(json.Length);
        foreach (var c in json)
        {
            if (c > 0x7E)
            {
                text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        string? summary = null;
        try
        {
            summary = JsonNode.Parse(text)?["error_summary"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            summary = text.Length > 200 ? text[..200] : text;
        }
        var status = summary is not null && summary.Contains("not_found", StringComparison.Ordinal) ? HttpStatusCode.NotFound
            : summary is not null && summary.Contains("conflict", StringComparison.Ordinal) ? HttpStatusCode.Conflict
            : response.StatusCode;
        return new HttpStatusException(status, $"{path}: {(int)response.StatusCode} {summary}".Trim(), summary);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOf(response, path).ConfigureAwait(false);
        }
    }

    private Task<HttpResponseMessage> RpcAsync(CloudClient client, string endpoint, JsonNode body, CancellationToken ct) =>
        client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint) { Content = JsonContent(body) }, ct);

    private VfsEntry Entry(VfsPath anyPath, string fullPath, JsonNode item)
    {
        var folder = item[".tag"]?.GetValue<string>() == "folder";
        var modified = TimeOf(item["client_modified"]?.GetValue<string>() ?? item["server_modified"]?.GetValue<string>());
        return ToEntry(anyPath, fullPath, item["name"]?.GetValue<string>() ?? NameOf(fullPath), folder, isLink: false, item["size"]?.GetValue<long>() ?? 0, modified);
    }

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var result = new List<VfsEntry>();
            var response = await RpcAsync(client, "/files/list_folder", new JsonObject { ["path"] = ApiPath(directory.Path), ["limit"] = 2000 }, cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                JsonNode? json;
                try
                {
                    await EnsureAsync(response, directory.Path).ConfigureAwait(false);
                    json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    response.Dispose();
                }
                foreach (var item in json?["entries"]?.AsArray() ?? [])
                {
                    if (item?[".tag"]?.GetValue<string>() is "file" or "folder" && item["name"]?.GetValue<string>() is { } name)
                    {
                        result.Add(Entry(directory, Combine(directory.Path, name), item));
                    }
                }
                if (json?["has_more"]?.GetValue<bool>() != true || json["cursor"]?.GetValue<string>() is not { } cursor)
                {
                    return result;
                }
                response = await RpcAsync(client, "/files/list_folder/continue", new JsonObject { ["cursor"] = cursor }, cancellationToken).ConfigureAwait(false);
            }
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
                using var response = await RpcAsync(client, "/files/get_metadata", new JsonObject { ["path"] = ApiPath(path.Path) }, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ErrorOf(response, path.Path).ConfigureAwait(false);
                    return error.Status == HttpStatusCode.NotFound ? null : throw error;
                }
                var item = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                return item is null || item[".tag"]?.GetValue<string>() == "deleted" ? null : Entry(path, path.Path, item);
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
            var arg = ApiArg(new JsonObject { ["path"] = ApiPath(path.Path) });
            var response = await client.SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + "/files/download");
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
                return request;
            }, cancellationToken, headersOnly: true).ConfigureAwait(false);
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

    /// <summary>Times to send with the next upload of a path (a copy sets them right after writing, Dropbox only at upload).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pendingTimes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The upload happens when the copy closes the stream; the copy job gives the source's modification time before
    /// (<see cref="PrepareUpload"/>), as Dropbox takes it only with the upload.
    /// </summary>
    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return UploadOnClose((data, ct) => UseAsync(path, client =>
        {
            var commit = new JsonObject
            {
                ["path"] = ApiPath(path.Path),
                ["mode"] = overwrite ? "overwrite" : "add",
                ["autorename"] = false,
                ["mute"] = true,
            };
            if (_pendingTimes.TryRemove(path.Path, out var time))
            {
                commit["client_modified"] = IsoTime(time);
            }
            return data.Length <= SimpleUploadLimit ? SimpleUploadAsync(client, path.Path, data, commit, ct) : SessionUploadAsync(client, path.Path, data, commit, ct);
        }, ct), cancellationToken);
    }

    /// <summary>The modification time the next upload of <paramref name="path"/> gets.</summary>
    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _pendingTimes[path.Path] = lastWriteTime;

    private HttpRequestMessage ContentRequest(string endpoint, JsonNode arg, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", ApiArg(arg));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private async Task SimpleUploadAsync(CloudClient client, string path, Stream data, JsonObject commit, CancellationToken ct)
    {
        using var response = await client.SendAsync(() => ContentRequest("/files/upload", commit, WholeContent(data)), ct).ConfigureAwait(false);
        await EnsureAsync(response, path).ConfigureAwait(false);
    }

    private async Task SessionUploadAsync(CloudClient client, string path, Stream data, JsonObject commit, CancellationToken ct)
    {
        string sessionId;
        using (var start = await client.SendAsync(() => ContentRequest("/files/upload_session/start", new JsonObject { ["close"] = false }, new ByteArrayContent([])), ct)
            .ConfigureAwait(false))
        {
            await EnsureAsync(start, path).ConfigureAwait(false);
            sessionId = (await ReadJsonAsync(start, ct).ConfigureAwait(false))?["session_id"]?.GetValue<string>()
                ?? throw new IOException($"Dropbox: could not start an upload session: {path}");
        }
        long offset = 0;
        var total = data.Length;
        while (offset < total)
        {
            var length = (int)Math.Min(ChunkSize, total - offset);
            var at = offset;
            var arg = new JsonObject { ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = at }, ["close"] = false };
            using var append = await client.SendAsync(() => ContentRequest("/files/upload_session/append_v2", arg, ChunkContent(data, at, length)), ct).ConfigureAwait(false);
            await EnsureAsync(append, path).ConfigureAwait(false);
            offset += length;
        }
        var finish = new JsonObject { ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = total }, ["commit"] = commit.DeepClone() };
        using var done = await client.SendAsync(() => ContentRequest("/files/upload_session/finish", finish, new ByteArrayContent([])), ct).ConfigureAwait(false);
        await EnsureAsync(done, path).ConfigureAwait(false);
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            if (Relative(path.Path).Length == 0)
            {
                return;
            }
            using var response = await RpcAsync(client, "/files/create_folder_v2", new JsonObject { ["path"] = ApiPath(path.Path), ["autorename"] = false }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ErrorOf(response, path.Path).ConfigureAwait(false);
                if (error.Status != HttpStatusCode.Conflict || error.ErrorCode?.Contains("folder", StringComparison.Ordinal) != true)
                {
                    throw error;
                }
            }
        }, cancellationToken);

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (entry.IsDirectory && !recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await UseAsync(path, async client =>
        {
            using var response = await RpcAsync(client, "/files/delete_v2", new JsonObject { ["path"] = ApiPath(path.Path) }, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        CheckSameAccount(source, destination);
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
            using var response = await RpcAsync(client, "/files/move_v2",
                new JsonObject { ["from_path"] = ApiPath(source.Path), ["to_path"] = ApiPath(destination.Path), ["autorename"] = false }, cancellationToken)
                .ConfigureAwait(false);
            await EnsureAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Dropbox keeps the time given at upload only (<see cref="PrepareUpload"/>); a later change is not possible.</summary>
    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<CloudClient> ConnectClientAsync(CloudClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + "/users/get_current_account"), cancellationToken)
            .ConfigureAwait(false);
        await EnsureAsync(response, "/").ConfigureAwait(false);
        return client;
    }
}
