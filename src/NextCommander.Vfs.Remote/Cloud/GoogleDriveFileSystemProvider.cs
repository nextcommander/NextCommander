using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Cloud;

/// <summary>
/// Google Drive (<c>gdrive://account@drive.google.com/folder/file</c>) over the Drive API v3. The root is "My Drive"
/// with two more folders: <see cref="SharedWithMeFolder"/> (files others shared with the account) and
/// <see cref="SharedDrivesFolder"/> (the shared drives the account belongs to). Drive addresses files by id: paths are
/// resolved part by part and the ids kept. Google Docs, Sheets, Slides and Drawings
/// are shown with an Office / PDF extension and exported when read (read only). Deleted files go to the Drive trash.
/// Of several files with the same name in one folder (Drive allows that) the first is shown.
/// </summary>
public sealed class GoogleDriveFileSystemProvider(RemoteHooks hooks) : CloudProviderBase(hooks)
{
    public string ApiBase { get; init; } = "https://www.googleapis.com/drive/v3";

    public string UploadBase { get; init; } = "https://www.googleapis.com/upload/drive/v3";

    private const string FolderType = "application/vnd.google-apps.folder";

    /// <summary>Folder at the root with the files shared with the account (read; a shared folder can be written to).</summary>
    public const string SharedWithMeFolder = "Shared with me";

    /// <summary>Folder at the root with the shared drives (team drives) of the account.</summary>
    public const string SharedDrivesFolder = "Shared drives";

    private const string SharedWithMeId = "*sharedWithMe";

    private const string SharedDrivesId = "*drives";

    /// <summary>Shared drives are part of every request (the API leaves them out otherwise).</summary>
    private const string AllDrives = "supportsAllDrives=true";

    private const string Fields = "id,name,mimeType,size,modifiedTime,parents";

    /// <summary>Google's own document types: shown with this extension, exported as this type when read (Office formats).</summary>
    public static readonly IReadOnlyDictionary<string, (string Extension, string ExportType)> Exports = ExportsFor(GoogleExport.Office);

    private static readonly IReadOnlyDictionary<string, (string Extension, string ExportType)> s_openDocument = ExportsFor(GoogleExport.OpenDocument);
    private static readonly IReadOnlyDictionary<string, (string Extension, string ExportType)> s_pdf = ExportsFor(GoogleExport.Pdf);

    /// <summary>The extension and export type of each Google document type in a format (drawings are PDF in all of them).</summary>
    public static IReadOnlyDictionary<string, (string Extension, string ExportType)> ExportsFor(GoogleExport format) => format switch
    {
        GoogleExport.OpenDocument => new Dictionary<string, (string, string)>
        {
            ["application/vnd.google-apps.document"] = (".odt", "application/vnd.oasis.opendocument.text"),
            ["application/vnd.google-apps.spreadsheet"] = (".ods", "application/vnd.oasis.opendocument.spreadsheet"),
            ["application/vnd.google-apps.presentation"] = (".odp", "application/vnd.oasis.opendocument.presentation"),
            ["application/vnd.google-apps.drawing"] = (".pdf", "application/pdf"),
        },
        GoogleExport.Pdf => new Dictionary<string, (string, string)>
        {
            ["application/vnd.google-apps.document"] = (".pdf", "application/pdf"),
            ["application/vnd.google-apps.spreadsheet"] = (".pdf", "application/pdf"),
            ["application/vnd.google-apps.presentation"] = (".pdf", "application/pdf"),
            ["application/vnd.google-apps.drawing"] = (".pdf", "application/pdf"),
        },
        _ => new Dictionary<string, (string, string)>
        {
            ["application/vnd.google-apps.document"] = (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ["application/vnd.google-apps.spreadsheet"] = (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ["application/vnd.google-apps.presentation"] = (".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            ["application/vnd.google-apps.drawing"] = (".pdf", "application/pdf"),
        },
    };

    /// <summary>The export formats of a client's connection.</summary>
    private static IReadOnlyDictionary<string, (string Extension, string ExportType)> ExportsOf(CloudClient client) => client.Session.Connection.GoogleExport switch
    {
        GoogleExport.OpenDocument => s_openDocument,
        GoogleExport.Pdf => s_pdf,
        _ => Exports,
    };

    /// <summary>Path → file id, per account ("/" is "root").</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _ids = new(StringComparer.Ordinal);

    public override string Scheme => "gdrive";

    protected override OAuthService Service => OAuthService.Google;

    private ConcurrentDictionary<string, string> IdsOf(VfsPath path) => _ids.GetOrAdd(path.Authority, _ => new(StringComparer.Ordinal));

    /// <summary>A name inside a Drive query string: backslash and quote escaped.</summary>
    public static string QueryString(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The name shown for an item (Google documents get the extension of their export type).</summary>
    public static string DisplayName(string name, string? mimeType, IReadOnlyDictionary<string, (string Extension, string ExportType)>? exports = null) =>
        mimeType is not null && (exports ?? Exports).TryGetValue(mimeType, out var export) && !name.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase)
            ? name + export.Extension
            : name;

    /// <summary>Items that are neither folders, files nor exportable documents (forms, sites, shortcuts ...) are not shown.</summary>
    private static bool Shown(string? mimeType) =>
        mimeType is not null && (mimeType == FolderType || !mimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) || Exports.ContainsKey(mimeType));

    private static async Task<HttpStatusException> ErrorOf(HttpResponseMessage response, string path)
    {
        var json = await ReadJsonAsync(response, CancellationToken.None).ConfigureAwait(false);
        var message = json?["error"]?["message"]?.GetValue<string>();
        var reason = json?["error"]?["errors"]?[0]?["reason"]?.GetValue<string>();
        return new HttpStatusException(response.StatusCode, $"{path}: {(int)response.StatusCode} {reason} {message}".Trim(), reason);
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
        var mime = item["mimeType"]?.GetValue<string>();
        var size = long.TryParse(item["size"]?.GetValue<string>(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
        return ToEntry(anyPath, fullPath, NameOf(fullPath), mime == FolderType, isLink: false, size, TimeOf(item["modifiedTime"]?.GetValue<string>()));
    }

    /// <summary>All children of a folder id (every page).</summary>
    private async Task<List<JsonNode>> ChildrenAsync(CloudClient client, string folderId, string? nameFilter, CancellationToken ct)
    {
        if (folderId == SharedDrivesId)
        {
            return await DrivesAsync(client, ct).ConfigureAwait(false);
        }
        var q = (folderId == SharedWithMeId ? "sharedWithMe = true" : $"{QueryString(folderId)} in parents") + " and trashed = false"
            + (nameFilter is null ? string.Empty : $" and {nameFilter}");
        var result = new List<JsonNode>();
        string? pageToken = null;
        do
        {
            var url = ApiBase + "/files?pageSize=1000&spaces=drive&" + AllDrives + "&includeItemsFromAllDrives=true&corpora=allDrives&fields="
                + Uri.EscapeDataString($"nextPageToken,files({Fields})")
                + "&q=" + Uri.EscapeDataString(q) + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            await EnsureAsync(response, folderId).ConfigureAwait(false);
            var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            result.AddRange((json?["files"]?.AsArray() ?? []).OfType<JsonNode>());
            pageToken = json?["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null);
        return result;
    }

    /// <summary>The shared drives of the account, as folders (the id of a shared drive is the id of its root folder).</summary>
    private async Task<List<JsonNode>> DrivesAsync(CloudClient client, CancellationToken ct)
    {
        var result = new List<JsonNode>();
        string? pageToken = null;
        do
        {
            var url = ApiBase + "/drives?pageSize=100&fields=" + Uri.EscapeDataString("nextPageToken,drives(id,name,createdTime)")
                + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            await EnsureAsync(response, "/" + SharedDrivesFolder).ConfigureAwait(false);
            var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            foreach (var drive in (json?["drives"]?.AsArray() ?? []).OfType<JsonNode>())
            {
                result.Add(new JsonObject
                {
                    ["id"] = drive["id"]?.GetValue<string>(), ["name"] = drive["name"]?.GetValue<string>(), ["mimeType"] = FolderType,
                    ["modifiedTime"] = drive["createdTime"]?.GetValue<string>(),
                });
            }
            pageToken = json?["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null);
        return result;
    }

    /// <summary>The virtual folders at the root (not files of the drive).</summary>
    private static bool IsVirtual(string id) => id.StartsWith('*');

    /// <summary>The id of a path (null: not there). Resolved part by part; found ids are kept.</summary>
    private async Task<(string Id, JsonNode? Item)?> ResolveAsync(CloudClient client, VfsPath path, CancellationToken ct)
    {
        var ids = IdsOf(path);
        var relative = Relative(path.Path);
        if (relative.Length == 0)
        {
            return ("root", null);
        }
        if (ids.TryGetValue(relative, out var known))
        {
            return (known, null);
        }
        var parentId = "root";
        var walked = string.Empty;
        JsonNode? item = null;
        foreach (var part in relative.Split('/'))
        {
            if (walked.Length == 0 && (part == SharedWithMeFolder || part == SharedDrivesFolder))
            {
                walked = part;
                parentId = part == SharedWithMeFolder ? SharedWithMeId : SharedDrivesId;
                item = null;
                continue;
            }
            walked = walked.Length == 0 ? part : walked + "/" + part;
            if (ids.TryGetValue(walked, out var id))
            {
                parentId = id;
                item = null;
                continue;
            }
            // the name itself, or a Google document shown with an extension
            var plain = ExportsOf(client).Values.Select(e => e.Extension).FirstOrDefault(e => part.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } ext
                ? part[..^ext.Length] : null;
            var filter = plain is null ? $"name = {QueryString(part)}" : $"(name = {QueryString(part)} or name = {QueryString(plain)})";
            var match = (await ChildrenAsync(client, parentId, filter, ct).ConfigureAwait(false))
                .FirstOrDefault(c => Shown(c["mimeType"]?.GetValue<string>())
                    && DisplayName(c["name"]?.GetValue<string>() ?? string.Empty, c["mimeType"]?.GetValue<string>(), ExportsOf(client)) == part);
            if (match?["id"]?.GetValue<string>() is not { } found)
            {
                return null;
            }
            ids[walked] = found;
            parentId = found;
            item = match;
        }
        return (parentId, item);
    }

    private async Task<string> RequireIdAsync(CloudClient client, VfsPath path, CancellationToken ct) =>
        (await ResolveAsync(client, path, ct).ConfigureAwait(false))?.Id ?? throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path.Path}");

    private async Task<JsonNode> ItemAsync(CloudClient client, string id, string path, CancellationToken ct)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/files/" + Uri.EscapeDataString(id) + "?" + AllDrives + "&fields=" + Fields), ct)
            .ConfigureAwait(false);
        await EnsureAsync(response, path).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false) ?? throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path}");
    }

    /// <summary>Forgets the ids of a path and everything under it (after a move or delete).</summary>
    private void Forget(VfsPath path)
    {
        var ids = IdsOf(path);
        var relative = Relative(path.Path);
        foreach (var key in ids.Keys.Where(k => k == relative || k.StartsWith(relative + "/", StringComparison.Ordinal)).ToList())
        {
            ids.TryRemove(key, out _);
        }
    }

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var id = await RequireIdAsync(client, directory, cancellationToken).ConfigureAwait(false);
            var ids = IdsOf(directory);
            var relative = Relative(directory.Path);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<VfsEntry>();
            foreach (var item in await ChildrenAsync(client, id, null, cancellationToken).ConfigureAwait(false))
            {
                var mime = item["mimeType"]?.GetValue<string>();
                if (!Shown(mime) || item["name"]?.GetValue<string>() is not { Length: > 0 } name || name.Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }
                var shown = DisplayName(name, mime, ExportsOf(client));
                if (!seen.Add(shown))
                {
                    continue; // a second file of the same name: only the first is reachable by path
                }
                if (item["id"]?.GetValue<string>() is { } childId)
                {
                    ids[relative.Length == 0 ? shown : relative + "/" + shown] = childId;
                }
                result.Add(Entry(directory, Combine(directory.Path, shown), item));
            }
            if (relative.Length == 0)
            {
                foreach (var name in new[] { SharedWithMeFolder, SharedDrivesFolder }.Where(seen.Add))
                {
                    result.Add(ToEntry(directory, "/" + name, name, isDirectory: true, isLink: false, 0, DateTime.MinValue));
                }
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
                if (IsVirtual(found.Id))
                {
                    return ToEntry(path, path.Path, path.Name, isDirectory: true, isLink: false, 0, DateTime.MinValue);
                }
                var item = found.Item ?? await ItemAsync(client, found.Id, path.Path, cancellationToken).ConfigureAwait(false);
                return Entry(path, path.Path, item);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            Forget(path);
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, async client =>
        {
            var found = await ResolveAsync(client, path, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpStatusException(HttpStatusCode.NotFound, $"Not found: {path.Path}");
            var item = found.Item ?? await ItemAsync(client, found.Id, path.Path, cancellationToken).ConfigureAwait(false);
            var mime = item["mimeType"]?.GetValue<string>();
            var url = mime is not null && ExportsOf(client).TryGetValue(mime, out var export)
                ? ApiBase + "/files/" + Uri.EscapeDataString(found.Id) + "/export?mimeType=" + Uri.EscapeDataString(export.ExportType)
                : ApiBase + "/files/" + Uri.EscapeDataString(found.Id) + "?alt=media&" + AllDrives;
            var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, headersOnly: true).ConfigureAwait(false);
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
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return UploadOnClose((data, ct) => UseAsync(path, client => UploadAsync(client, path, data, existing is not null, ct), ct), cancellationToken);
    }

    /// <summary>A resumable upload session, then the whole file in one PUT; an existing file gets a new version.</summary>
    private async Task UploadAsync(CloudClient client, VfsPath path, Stream data, bool replace, CancellationToken ct)
    {
        var parentId = WritableParent(await RequireIdAsync(client, Parent(path), ct).ConfigureAwait(false), path);
        string url;
        HttpMethod method;
        var metadata = new JsonObject();
        if (replace)
        {
            var id = await RequireIdAsync(client, path, ct).ConfigureAwait(false);
            url = UploadBase + "/files/" + Uri.EscapeDataString(id) + "?uploadType=resumable&fields=id&" + AllDrives;
            method = HttpMethod.Patch;
        }
        else
        {
            url = UploadBase + "/files?uploadType=resumable&fields=id&" + AllDrives;
            method = HttpMethod.Post;
            metadata["name"] = path.Name;
            metadata["parents"] = new JsonArray(parentId);
        }
        string location;
        using (var start = await client.SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url) { Content = JsonContent(metadata) };
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, ct).ConfigureAwait(false))
        {
            await EnsureAsync(start, path.Path).ConfigureAwait(false);
            location = start.Headers.Location?.ToString() ?? throw new IOException($"Google Drive: could not start the upload: {path.Path}");
        }
        using var put = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, location) { Content = WholeContent(data) }, ct).ConfigureAwait(false);
        await EnsureAsync(put, path.Path).ConfigureAwait(false);
        if ((await ReadJsonAsync(put, ct).ConfigureAwait(false))?["id"]?.GetValue<string>() is { } newId)
        {
            IdsOf(path)[Relative(path.Path)] = newId;
        }
    }

    private static VfsPath Parent(VfsPath path) => VfsPath.Create(path.Scheme, path.Authority, ParentOf(path.Path));

    /// <summary>A folder id that takes new files; the virtual folders at the root do not.</summary>
    private static string WritableParent(string id, VfsPath path) => IsVirtual(id) || id == string.Empty
        ? throw new IOException($"Google Drive: no file or folder can be added here: {ParentOf(path.Path)}")
        : id;

    /// <summary>The virtual folders themselves cannot be deleted, moved or renamed.</summary>
    private static void CheckNotVirtual(VfsPath path)
    {
        var relative = Relative(path.Path);
        if (relative.Length == 0 || relative == SharedWithMeFolder || relative == SharedDrivesFolder
            || (relative.StartsWith(SharedDrivesFolder + "/", StringComparison.Ordinal) && !relative[(SharedDrivesFolder.Length + 1)..].Contains('/', StringComparison.Ordinal)))
        {
            throw new IOException($"Google Drive: this folder cannot be changed: {path.Path}");
        }
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            if (Relative(path.Path).Length == 0 || await ResolveAsync(client, path, cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
            var parentId = WritableParent(await RequireIdAsync(client, Parent(path), cancellationToken).ConfigureAwait(false), path);
            var body = new JsonObject { ["name"] = path.Name, ["mimeType"] = FolderType, ["parents"] = new JsonArray(parentId) };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + "/files?fields=id&" + AllDrives) { Content = JsonContent(body) },
                cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
            if ((await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false))?["id"]?.GetValue<string>() is { } id)
            {
                IdsOf(path)[Relative(path.Path)] = id;
            }
        }, cancellationToken);

    /// <summary>Moves the item into the Drive trash (a folder with everything in it).</summary>
    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        CheckNotVirtual(path);
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (entry.IsDirectory && !recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await UseAsync(path, async client =>
        {
            var id = await RequireIdAsync(client, path, cancellationToken).ConfigureAwait(false);
            // to the Drive trash, or removed at once when the connection says so
            using var response = client.Session.Connection.DeletePermanently
                ? await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ApiBase + "/files/" + Uri.EscapeDataString(id) + "?" + AllDrives), cancellationToken).ConfigureAwait(false)
                : await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ApiBase + "/files/" + Uri.EscapeDataString(id) + "?fields=id&" + AllDrives)
                    { Content = JsonContent(new JsonObject { ["trashed"] = true }) }, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Forget(path);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        CheckSameAccount(source, destination);
        CheckNotVirtual(source);
        if (!string.Equals(source.Path, destination.Path, StringComparison.Ordinal) && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!overwrite || existing.IsDirectory)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await DeleteAsync(destination, recursive: false, cancellationToken).ConfigureAwait(false);
        }
        await UseAsync(source, async client =>
        {
            var id = await RequireIdAsync(client, source, cancellationToken).ConfigureAwait(false);
            var fromParent = await RequireIdAsync(client, Parent(source), cancellationToken).ConfigureAwait(false);
            if (IsVirtual(fromParent))
            {
                throw new CrossDeviceMoveException("Shared files are copied, not moved.");
            }
            var toParent = WritableParent(await RequireIdAsync(client, Parent(destination), cancellationToken).ConfigureAwait(false), destination);
            var url = ApiBase + "/files/" + Uri.EscapeDataString(id) + "?fields=id&" + AllDrives
                + (fromParent == toParent ? string.Empty : "&addParents=" + Uri.EscapeDataString(toParent) + "&removeParents=" + Uri.EscapeDataString(fromParent));
            var item = await ItemAsync(client, id, source.Path, cancellationToken).ConfigureAwait(false);
            // a Google document keeps its name without the export extension
            var name = item["mimeType"]?.GetValue<string>() is { } mime && ExportsOf(client).TryGetValue(mime, out var export)
                && destination.Name.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase)
                ? destination.Name[..^export.Extension.Length] : destination.Name;
            var body = new JsonObject { ["name"] = name };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent(body) }, cancellationToken)
                .ConfigureAwait(false);
            await EnsureAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Forget(source);
        Forget(destination);
    }

    public override Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (lastWriteTime is not { } time || time.Year < 1980 || Relative(path.Path).Length == 0)
        {
            return Task.CompletedTask;
        }
        return UseAsync(path, async client =>
        {
            var id = await RequireIdAsync(client, path, cancellationToken).ConfigureAwait(false);
            var body = new JsonObject { ["modifiedTime"] = IsoTime(time) };
            using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ApiBase + "/files/" + Uri.EscapeDataString(id) + "?fields=id&" + AllDrives)
                { Content = JsonContent(body) }, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken);
    }

    protected override async Task<CloudClient> ConnectClientAsync(CloudClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/about?fields=user"), cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "/").ConfigureAwait(false);
        return client;
    }
}
