using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NextCommander.Vfs.Remote.Cloud;

namespace NextCommander.Core.Tests;

/// <summary>
/// An in-memory stand-in for the Google Drive, OneDrive, Dropbox and Box APIs (only what the providers use), with a token
/// endpoint. Lists come in pages of two to exercise paging.
/// </summary>
internal sealed partial class FakeCloud : HttpMessageHandler
{
    public sealed class Node
    {
        public required string Id { get; init; }

        public required string Name { get; set; }

        public string? ParentId { get; set; }

        public bool IsFolder { get; init; }

        public byte[] Data { get; set; } = [];

        public DateTime Modified { get; set; } = DateTime.UtcNow;

        public string? MimeType { get; init; }

        public bool Trashed { get; set; }

        /// <summary>Google: shared with the account by someone else (in no folder of the account).</summary>
        public bool SharedWithMe { get; init; }

        /// <summary>Google: the root folder of a shared drive.</summary>
        public bool IsDrive { get; init; }
    }

    /// <summary>Google: a file shared with the account.</summary>
    public Node AddShared(string name, byte[] data)
    {
        var node = new Node { Id = NewId(), Name = name, ParentId = "someone-else", Data = data, SharedWithMe = true };
        _nodes[node.Id] = node;
        return node;
    }

    /// <summary>Google: a shared drive (its root folder id is the drive id).</summary>
    public Node AddDrive(string name)
    {
        var node = new Node { Id = NewId(), Name = name, IsFolder = true, IsDrive = true };
        _nodes[node.Id] = node;
        return node;
    }

    public IEnumerable<Node> ChildrenOf(Node folder) => Children(folder);

    /// <summary>A file in a folder given by node (a shared drive is not under the root).</summary>
    public Node AddInto(Node folder, string name, byte[] data)
    {
        var node = new Node { Id = NewId(), Name = name, ParentId = folder.Id, Data = data };
        _nodes[node.Id] = node;
        return node;
    }

    private readonly Dictionary<string, Node> _nodes = new() { ["root"] = new Node { Id = "root", Name = string.Empty, IsFolder = true } };
    private readonly Dictionary<string, (string? Path, string? NodeId, MemoryStream Data, JsonNode? Meta)> _sessions = [];
    private int _next;

    public const int PageSize = 2;

    public static readonly OAuthService Auth = new("Fake", "https://auth.test/authorize", "https://auth.test/token", "files", "localhost", string.Empty);

    public int Requests { get; private set; }

    public List<string> Log { get; } = [];

    /// <summary>The next API call answers 401 (an access token revoked early) / 429 (throttled).</summary>
    public bool FailNextWith401 { get; set; }

    public bool FailNextWith429 { get; set; }

    public int Refreshes { get; private set; }

    private string NewId() => "id" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- tree helpers

    /// <summary>OneDrive: the drive a request works in ("root": the user's own; otherwise a SharePoint library).</summary>
    private string _driveRoot = "root";

    /// <summary>OneDrive: SharePoint sites with their document libraries (the root folders of their drives).</summary>
    private readonly List<(string Id, string Name, List<Node> Libraries)> _sites = [];

    public Node AddLibrary(string site, string library)
    {
        var index = _sites.FindIndex(x => x.Name == site);
        if (index < 0)
        {
            _sites.Add(("site-" + NewId(), site, []));
            index = _sites.Count - 1;
        }
        var root = new Node { Id = "drive-" + NewId(), Name = library, IsFolder = true };
        _nodes[root.Id] = root;
        _sites[index].Libraries.Add(root);
        return root;
    }

    public Node? ByPath(string path)
    {
        var node = _nodes[_driveRoot];
        foreach (var part in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = _nodes.Values.FirstOrDefault(n => n.ParentId == node.Id && !n.Trashed && string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return null;
            }
        }
        return node;
    }

    public string PathOf(Node node)
    {
        var parts = new List<string>();
        for (var n = node; n.Id != "root"; n = _nodes[n.ParentId!])
        {
            parts.Insert(0, n.Name);
        }
        return "/" + string.Join('/', parts);
    }

    private IEnumerable<Node> Children(Node folder) => _nodes.Values.Where(n => n.ParentId == folder.Id && !n.Trashed).OrderBy(n => n.Name, StringComparer.Ordinal);

    public Node Add(string parentPath, string name, byte[]? data = null, bool folder = false, string? mimeType = null)
    {
        var parent = ByPath(parentPath) ?? throw new InvalidOperationException(parentPath);
        var node = new Node { Id = NewId(), Name = name, ParentId = parent.Id, IsFolder = folder, Data = data ?? [], MimeType = mimeType };
        _nodes[node.Id] = node;
        return node;
    }

    private Node Put(string path, byte[] data)
    {
        var existing = ByPath(path);
        if (existing is not null)
        {
            existing.Data = data;
            existing.Modified = DateTime.UtcNow;
            return existing;
        }
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return Add(slash <= 0 ? "/" : path[..slash], path[(slash + 1)..], data);
    }

    private static HttpResponseMessage Json(JsonNode? body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static string Iso(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- dispatch

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var url = request.RequestUri!;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Log.Add(request.Method + " " + url.GetLeftPart(UriPartial.Path));
        if (url.Host == "auth.test")
        {
            return Token(Encoding.UTF8.GetString(body));
        }
        var preAuthorized = url.Host == "upload.test"; // OneDrive upload session addresses carry their own authorization
        if (!preAuthorized && request.Headers.Authorization?.Parameter?.StartsWith("at", StringComparison.Ordinal) != true)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        if (FailNextWith401 && !preAuthorized)
        {
            FailNextWith401 = false;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        if (FailNextWith429)
        {
            FailNextWith429 = false;
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return throttled;
        }
        return url.Host switch
        {
            "graph.test" or "upload.test" => OneDrive(request, url, body),
            "api.dbx.test" or "content.dbx.test" => Dropbox(request, url, body),
            "g.test" => Google(request, url, body),
            "box.test" or "upload.box.test" => await Box(request, url, body),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Token(string form)
    {
        var values = form.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        if (values["grant_type"] == "authorization_code")
        {
            return values.GetValueOrDefault("code") == "good-code" && values.GetValueOrDefault("code_verifier") is { Length: >= 43 }
                ? Json(new JsonObject { ["access_token"] = "at-1", ["refresh_token"] = "rt-new", ["expires_in"] = 3600 })
                : Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
        }
        Refreshes++;
        return values.GetValueOrDefault("refresh_token") is "rt-new" or "rt-saved"
            ? Json(new JsonObject { ["access_token"] = "at-" + Refreshes, ["expires_in"] = 3600 })
            : Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- OneDrive (graph.test/me/drive)

    private JsonObject OneDriveItem(Node n)
    {
        var item = new JsonObject
        {
            ["id"] = n.Id, ["name"] = n.Name, ["size"] = n.Data.LongLength, ["lastModifiedDateTime"] = Iso(DateTime.UtcNow),
            ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = Iso(n.Modified) },
        };
        item[n.IsFolder ? "folder" : "file"] = new JsonObject();
        return item;
    }

    private HttpResponseMessage OneDrive(HttpRequestMessage request, Uri url, byte[] body)
    {
        if (url.Host == "upload.test")
        {
            var session = _sessions[url.AbsolutePath];
            var range = request.Content!.Headers.ContentRange!;
            Assert.Equal(session.Data.Length, range.From);
            session.Data.Write(body);
            if (session.Data.Length < range.Length)
            {
                return Json(new JsonObject(), HttpStatusCode.Accepted);
            }
            _driveRoot = session.NodeId ?? "root";
            try
            {
                return Json(OneDriveItem(Put(session.Path!, session.Data.ToArray())), HttpStatusCode.Created);
            }
            finally
            {
                _driveRoot = "root";
            }
        }
        var absolute = Uri.UnescapeDataString(url.AbsolutePath);
        if (absolute == "/sites")
        {
            Assert.Contains("search=*", url.Query, StringComparison.Ordinal);
            return Json(new JsonObject { ["value"] = new JsonArray(_sites.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["displayName"] = x.Name }).ToArray()) });
        }
        if (Regex.Match(absolute, "^/sites/([^/]+)/drives$") is { Success: true } siteDrives)
        {
            var site = _sites.Single(x => x.Id == siteDrives.Groups[1].Value);
            return Json(new JsonObject { ["value"] = new JsonArray(site.Libraries.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["name"] = l.Name }).ToArray()) });
        }
        string rest;
        if (Regex.Match(absolute, "^/drives/([^/:]+)(.*)$") is { Success: true } inDrive)
        {
            _driveRoot = inDrive.Groups[1].Value;
            rest = inDrive.Groups[2].Value;
        }
        else
        {
            _driveRoot = "root";
            rest = absolute["/me/drive".Length..];
        }
        try
        {
            return OneDriveItem(request, url, body, rest);
        }
        finally
        {
            _driveRoot = "root";
        }
    }

    private HttpResponseMessage OneDriveItem(HttpRequestMessage request, Uri url, byte[] body, string rest)
    {
        if (rest.Length == 0)
        {
            return Json(new JsonObject { ["id"] = "drive" });
        }
        // "/root[/op]" or "/root:/a/b:[/op]"
        string path, op;
        if (rest.StartsWith("/root:", StringComparison.Ordinal))
        {
            var end = rest.IndexOf(':', 6);
            path = rest[6..end];
            op = rest[(end + 1)..].TrimStart('/');
        }
        else
        {
            path = "/";
            op = rest["/root".Length..].TrimStart('/');
        }
        var node = ByPath(path);
        var query = url.Query;
        HttpResponseMessage NotFound() => Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "itemNotFound", ["message"] = "no" } }, HttpStatusCode.NotFound);
        switch (request.Method.Method, op)
        {
            case ("GET", "children"):
                if (node is null)
                {
                    return NotFound();
                }
                var skip = Regex.Match(query, "skip=(\\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var all = Children(node).ToList();
                var page = new JsonObject { ["value"] = new JsonArray(all.Skip(skip).Take(PageSize).Select(n => (JsonNode)OneDriveItem(n)).ToArray()) };
                if (skip + PageSize < all.Count)
                {
                    page["@odata.nextLink"] = url.GetLeftPart(UriPartial.Path) + "?skip=" + (skip + PageSize);
                }
                return Json(page);
            case ("GET", ""):
                return node is null ? NotFound() : Json(OneDriveItem(node));
            case ("GET", "content"):
                return node is null ? NotFound() : Bytes(node.Data);
            case ("PUT", "content"):
                if (node is not null && query.Contains("conflictBehavior=fail", StringComparison.Ordinal))
                {
                    return Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "nameAlreadyExists" } }, HttpStatusCode.Conflict);
                }
                return Json(OneDriveItem(Put(path, body)), HttpStatusCode.Created);
            case ("POST", "createUploadSession"):
                var key = "/session/" + NewId();
                _sessions[key] = (path, _driveRoot, new MemoryStream(), null);
                return Json(new JsonObject { ["uploadUrl"] = "https://upload.test" + key });
            case ("POST", "children"):
                var create = JsonNode.Parse(body)!;
                var name = create["name"]!.GetValue<string>();
                if (Children(node!).Any(c => c.Name == name))
                {
                    return Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "nameAlreadyExists" } }, HttpStatusCode.Conflict);
                }
                return Json(OneDriveItem(Add(path, name, folder: true)), HttpStatusCode.Created);
            case ("DELETE", ""):
                if (node is null)
                {
                    return NotFound();
                }
                node.Trashed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("PATCH", ""):
                if (node is null)
                {
                    return NotFound();
                }
                var patch = JsonNode.Parse(body)!;
                if (patch["name"]?.GetValue<string>() is { } newName)
                {
                    node.Name = newName;
                }
                if (patch["parentReference"]?["path"]?.GetValue<string>() is { } parentPath)
                {
                    node.ParentId = ByPath(parentPath["/drive/root:".Length..] is { Length: > 0 } p ? p : "/")!.Id;
                }
                if (patch["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>() is { } time)
                {
                    node.Modified = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
                }
                return Json(OneDriveItem(node));
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- Dropbox (api.dbx.test/2, content.dbx.test/2)

    private JsonObject DropboxItem(Node n) => n.IsFolder
        ? new JsonObject { [".tag"] = "folder", ["name"] = n.Name, ["path_display"] = PathOf(n) }
        : new JsonObject { [".tag"] = "file", ["name"] = n.Name, ["path_display"] = PathOf(n), ["size"] = n.Data.LongLength, ["client_modified"] = Iso(n.Modified), ["server_modified"] = Iso(DateTime.UtcNow) };

    private static HttpResponseMessage DropboxError(string summary) => Json(new JsonObject { ["error_summary"] = summary }, HttpStatusCode.Conflict);

    private HttpResponseMessage Dropbox(HttpRequestMessage request, Uri url, byte[] body)
    {
        var endpoint = url.AbsolutePath["/2".Length..];
        JsonNode? arg = null;
        if (request.Headers.TryGetValues("Dropbox-API-Arg", out var values))
        {
            var header = values.Single();
            Assert.True(header.All(c => c < 128), "Dropbox-API-Arg must be ASCII: " + header);
            arg = JsonNode.Parse(header);
        }
        var json = url.Host == "api.dbx.test" && body.Length > 0 ? JsonNode.Parse(body) : null;
        switch (endpoint)
        {
            case "/users/get_current_account":
                return Json(new JsonObject { ["account_id"] = "a" });
            case "/files/list_folder" or "/files/list_folder/continue":
                string path;
                int offset;
                if (endpoint.EndsWith("continue", StringComparison.Ordinal))
                {
                    var cursor = json!["cursor"]!.GetValue<string>().Split('|');
                    (path, offset) = (cursor[0], int.Parse(cursor[1], System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    (path, offset) = (json!["path"]!.GetValue<string>(), 0);
                }
                var folder = ByPath(path);
                if (folder is null)
                {
                    return DropboxError("path/not_found/..");
                }
                var all = Children(folder).ToList();
                return Json(new JsonObject
                {
                    ["entries"] = new JsonArray(all.Skip(offset).Take(PageSize).Select(n => (JsonNode)DropboxItem(n)).ToArray()),
                    ["cursor"] = path + "|" + (offset + PageSize),
                    ["has_more"] = offset + PageSize < all.Count,
                });
            case "/files/get_metadata":
                return ByPath(json!["path"]!.GetValue<string>()) is { } found ? Json(DropboxItem(found)) : DropboxError("path/not_found/...");
            case "/files/download":
                return ByPath(arg!["path"]!.GetValue<string>()) is { IsFolder: false } file ? Bytes(file.Data) : DropboxError("path/not_found/..");
            case "/files/upload":
                return Upload(arg!, body);
            case "/files/upload_session/start":
                var id = NewId();
                _sessions[id] = (null, null, new MemoryStream(), null);
                return Json(new JsonObject { ["session_id"] = id });
            case "/files/upload_session/append_v2":
                var s = _sessions[arg!["cursor"]!["session_id"]!.GetValue<string>()];
                Assert.Equal(s.Data.Length, arg["cursor"]!["offset"]!.GetValue<long>());
                s.Data.Write(body);
                return Json(null);
            case "/files/upload_session/finish":
                var session = _sessions[arg!["cursor"]!["session_id"]!.GetValue<string>()];
                Assert.Equal(session.Data.Length, arg["cursor"]!["offset"]!.GetValue<long>());
                return Upload(arg["commit"]!, session.Data.ToArray());
            case "/files/create_folder_v2":
                var p = json!["path"]!.GetValue<string>();
                if (ByPath(p) is not null)
                {
                    return DropboxError("path/conflict/folder/..");
                }
                var slash = p.LastIndexOf('/');
                return Json(new JsonObject { ["metadata"] = DropboxItem(Add(slash <= 0 ? "/" : p[..slash], p[(slash + 1)..], folder: true)) });
            case "/files/delete_v2":
                if (ByPath(json!["path"]!.GetValue<string>()) is not { } deleted)
                {
                    return DropboxError("path_lookup/not_found/");
                }
                deleted.Trashed = true;
                return Json(new JsonObject { ["metadata"] = DropboxItem(deleted) });
            case "/files/move_v2":
                var from = ByPath(json!["from_path"]!.GetValue<string>())!;
                var to = json["to_path"]!.GetValue<string>();
                var cut = to.LastIndexOf('/');
                from.ParentId = ByPath(cut <= 0 ? "/" : to[..cut])!.Id;
                from.Name = to[(cut + 1)..];
                return Json(new JsonObject { ["metadata"] = DropboxItem(from) });
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private HttpResponseMessage Upload(JsonNode commit, byte[] data)
    {
        var path = commit["path"]!.GetValue<string>();
        if (commit["mode"]?.GetValue<string>() == "add" && ByPath(path) is not null)
        {
            return DropboxError("path/conflict/file/");
        }
        var node = Put(path, data);
        if (commit["client_modified"]?.GetValue<string>() is { } time)
        {
            node.Modified = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        }
        return Json(DropboxItem(node));
    }

    // ---------------------------------------------------------------- Google Drive (g.test/drive/v3, g.test/upload/drive/v3)

    private const string FolderType = "application/vnd.google-apps.folder";

    private static JsonObject GoogleItem(Node n)
    {
        var item = new JsonObject { ["id"] = n.Id, ["name"] = n.Name, ["mimeType"] = n.IsFolder ? FolderType : n.MimeType ?? "text/plain", ["modifiedTime"] = Iso(n.Modified) };
        if (!n.IsFolder && n.MimeType is null)
        {
            item["size"] = n.Data.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return item;
    }

    [GeneratedRegex(@"name = '((?:[^'\\]|\\.)*)'")]
    private static partial Regex NameClause();

    private HttpResponseMessage Google(HttpRequestMessage request, Uri url, byte[] body)
    {
        var path = url.AbsolutePath;
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
        HttpResponseMessage NotFound() => Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "File not found" } }, HttpStatusCode.NotFound);
        if (path.StartsWith("/session/", StringComparison.Ordinal))
        {
            var session = _sessions[path];
            Node node;
            if (session.NodeId is { } existing)
            {
                node = _nodes[existing];
                node.Data = body;
            }
            else
            {
                var parent = _nodes[session.Meta!["parents"]![0]!.GetValue<string>()];
                node = new Node { Id = NewId(), Name = session.Meta["name"]!.GetValue<string>(), ParentId = parent.Id, Data = body };
                _nodes[node.Id] = node;
            }
            node.Modified = DateTime.UtcNow;
            return Json(new JsonObject { ["id"] = node.Id });
        }
        if (path.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal))
        {
            Assert.Equal("resumable", query["uploadType"]);
            var key = "/session/" + NewId();
            var id = path.Length > "/upload/drive/v3/files/".Length ? path["/upload/drive/v3/files/".Length..] : null;
            _sessions[key] = (null, id, new MemoryStream(), JsonNode.Parse(body));
            var started = new HttpResponseMessage(HttpStatusCode.OK);
            started.Headers.Location = new Uri("https://g.test" + key);
            return started;
        }
        var rest = path["/drive/v3".Length..];
        if (rest == "/about")
        {
            return Json(new JsonObject { ["user"] = new JsonObject() });
        }
        if (rest == "/drives")
        {
            return Json(new JsonObject
            {
                ["drives"] = new JsonArray(_nodes.Values.Where(n => n.IsDrive).Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["name"] = n.Name }).ToArray()),
            });
        }
        if (rest == "/files" && request.Method == HttpMethod.Get)
        {
            var q = query["q"];
            Assert.Equal("true", query["supportsAllDrives"]);
            var shared = q.StartsWith("sharedWithMe = true", StringComparison.Ordinal);
            var parentId = Regex.Match(q, @"^'([^']+)' in parents").Groups[1].Value;
            Assert.Contains("trashed = false", q, StringComparison.Ordinal);
            var names = NameClause().Matches(q).Select(m => Regex.Unescape(m.Groups[1].Value)).ToList();
            var all = _nodes.Values.Where(n => (shared ? n.SharedWithMe : n.ParentId == parentId) && !n.Trashed && (names.Count == 0 || names.Contains(n.Name)))
                .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
            var offset = query.TryGetValue("pageToken", out var token) ? int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var page = new JsonObject { ["files"] = new JsonArray(all.Skip(offset).Take(PageSize).Select(n => (JsonNode)GoogleItem(n)).ToArray()) };
            if (offset + PageSize < all.Count)
            {
                page["nextPageToken"] = (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return Json(page);
        }
        if (rest == "/files" && request.Method == HttpMethod.Post)
        {
            var meta = JsonNode.Parse(body)!;
            Assert.Equal(FolderType, meta["mimeType"]!.GetValue<string>());
            var folder = new Node { Id = NewId(), Name = meta["name"]!.GetValue<string>(), ParentId = meta["parents"]![0]!.GetValue<string>(), IsFolder = true };
            _nodes[folder.Id] = folder;
            return Json(new JsonObject { ["id"] = folder.Id });
        }
        var match = Regex.Match(rest, "^/files/([^/]+)(/export)?$");
        if (!match.Success || !_nodes.TryGetValue(Uri.UnescapeDataString(match.Groups[1].Value), out var item) || item.Trashed)
        {
            return NotFound();
        }
        if (match.Groups[2].Success)
        {
            Assert.NotNull(item.MimeType);
            return Bytes(Encoding.UTF8.GetBytes("exported as " + query["mimeType"]));
        }
        if (request.Method == HttpMethod.Get)
        {
            return query.GetValueOrDefault("alt") == "media" ? Bytes(item.Data) : Json(GoogleItem(item));
        }
        if (request.Method == HttpMethod.Patch)
        {
            var patch = JsonNode.Parse(body)!;
            if (patch["trashed"]?.GetValue<bool>() == true)
            {
                item.Trashed = true;
            }
            if (patch["name"]?.GetValue<string>() is { } name)
            {
                item.Name = name;
            }
            if (patch["modifiedTime"]?.GetValue<string>() is { } time)
            {
                item.Modified = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
            }
            if (query.TryGetValue("addParents", out var add))
            {
                Assert.Equal(item.ParentId, query["removeParents"]);
                item.ParentId = add;
            }
            return Json(new JsonObject { ["id"] = item.Id });
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- Box (box.test/2.0, upload.box.test/api/2.0)

    /// <summary>Box: the part size an upload session asks for.</summary>
    public const int BoxPartSize = 1000;

    /// <summary>Box: the first commit of a session answers 202 (the parts are still being put together).</summary>
    private bool _boxCommitPending = true;

    private static string BoxId(Node n) => n.Id == "root" ? "0" : n.Id;

    private Node? BoxNode(string id) => _nodes.TryGetValue(id == "0" ? "root" : id, out var n) && !n.Trashed ? n : null;

    private static JsonObject BoxItem(Node n)
    {
        var item = new JsonObject
        {
            ["type"] = n.IsFolder ? "folder" : "file", ["id"] = BoxId(n), ["name"] = n.Name, ["size"] = n.Data.LongLength,
            ["modified_at"] = Iso(DateTime.UtcNow),
        };
        if (!n.IsFolder)
        {
            item["content_modified_at"] = Iso(n.Modified);
        }
        return item;
    }

    private static HttpResponseMessage BoxError(HttpStatusCode status, string code) =>
        Json(new JsonObject { ["type"] = "error", ["status"] = (int)status, ["code"] = code, ["message"] = code }, status);

    private bool BoxNameTaken(Node folder, string name, Node? except = null) =>
        Children(folder).Any(c => c != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void BoxTime(Node node, JsonNode? attributes)
    {
        node.Modified = attributes?["content_modified_at"]?.GetValue<string>() is { } time
            ? DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
            : DateTime.UtcNow;
    }

#pragma warning disable CA5350 // Box's digest header is SHA-1
    private static string Sha1(byte[] data) => "sha=" + Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(data));
#pragma warning restore CA5350

    /// <summary>Stores an upload: a new file in a folder, or a new version of a file.</summary>
    private HttpResponseMessage BoxStore(string? fileId, string? folderId, string? name, JsonNode? attributes, byte[] data)
    {
        Node node;
        if (fileId is not null)
        {
            node = BoxNode(fileId) ?? throw new InvalidOperationException(fileId);
            node.Data = data;
        }
        else
        {
            var folder = BoxNode(folderId!)!;
            if (BoxNameTaken(folder, name!))
            {
                return BoxError(HttpStatusCode.Conflict, "item_name_in_use");
            }
            node = new Node { Id = NewId(), Name = name!, ParentId = folder.Id, Data = data };
            _nodes[node.Id] = node;
        }
        BoxTime(node, attributes);
        return Json(new JsonObject { ["total_count"] = 1, ["entries"] = new JsonArray(BoxItem(node)) }, HttpStatusCode.Created);
    }

    private static string PartName(HttpContent part) => part.Headers.ContentDisposition!.Name!.Trim('"');

    private async Task<HttpResponseMessage> Box(HttpRequestMessage request, Uri url, byte[] body)
    {
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
        var method = request.Method.Method;
        if (url.Host == "upload.box.test")
        {
            return await BoxUpload(request, path["/api/2.0".Length..], method, body);
        }
        path = path["/2.0".Length..];
        if (path == "/users/me")
        {
            return Json(new JsonObject { ["type"] = "user", ["id"] = "u1" });
        }
        if (method == "GET" && Regex.Match(path, "^/folders/([^/]+)/items$") is { Success: true } items)
        {
            if (BoxNode(items.Groups[1].Value) is not { IsFolder: true } folder)
            {
                return BoxError(HttpStatusCode.NotFound, "not_found");
            }
            Assert.Contains("content_modified_at", query["fields"], StringComparison.Ordinal);
            var offset = int.Parse(query["offset"], System.Globalization.CultureInfo.InvariantCulture);
            // a web link (not a file) is part of the listing and the count
            var all = Children(folder).Select(n => (JsonNode)BoxItem(n))
                .Append(new JsonObject { ["type"] = "web_link", ["id"] = "w1", ["name"] = "link" }).ToList();
            return Json(new JsonObject
            {
                ["total_count"] = all.Count, ["offset"] = offset, ["limit"] = PageSize,
                ["entries"] = new JsonArray(all.Skip(offset).Take(PageSize).ToArray()),
            });
        }
        if (method == "POST" && path == "/folders")
        {
            var json = JsonNode.Parse(body)!;
            var parent = BoxNode(json["parent"]!["id"]!.GetValue<string>())!;
            var name = json["name"]!.GetValue<string>();
            if (BoxNameTaken(parent, name))
            {
                return BoxError(HttpStatusCode.Conflict, "item_name_in_use");
            }
            var folder = new Node { Id = NewId(), Name = name, ParentId = parent.Id, IsFolder = true };
            _nodes[folder.Id] = folder;
            return Json(BoxItem(folder), HttpStatusCode.Created);
        }
        var match = Regex.Match(path, "^/(files|folders)/([^/]+)(/content)?$");
        if (!match.Success || BoxNode(match.Groups[2].Value) is not { } node || node.IsFolder != (match.Groups[1].Value == "folders"))
        {
            return BoxError(HttpStatusCode.NotFound, "not_found");
        }
        switch (method)
        {
            case "GET" when match.Groups[3].Success:
                return Bytes(node.Data);
            case "GET":
                return Json(BoxItem(node));
            case "DELETE":
                if (node.IsFolder && Children(node).Any() && query.GetValueOrDefault("recursive") != "true")
                {
                    return BoxError(HttpStatusCode.BadRequest, "folder_not_empty");
                }
                node.Trashed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "PUT":
                var update = JsonNode.Parse(body)!;
                var target = update["parent"]?["id"]?.GetValue<string>() is { } parentId ? BoxNode(parentId)! : _nodes[node.ParentId!];
                var newName = update["name"]?.GetValue<string>() ?? node.Name;
                if (BoxNameTaken(target, newName, except: node))
                {
                    return BoxError(HttpStatusCode.Conflict, "item_name_in_use");
                }
                (node.ParentId, node.Name) = (target.Id, newName);
                return Json(BoxItem(node));
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> BoxUpload(HttpRequestMessage request, string path, string method, byte[] body)
    {
        if (method == "POST" && Regex.Match(path, "^/files(?:/([^/]+))?/content$") is { Success: true } upload)
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToList();
            Assert.Equal("attributes", PartName(parts[0])); // the attributes first
            var attributes = JsonNode.Parse(await parts[0].ReadAsStringAsync());
            var data = await parts.Single(p => PartName(p) == "file").ReadAsByteArrayAsync();
            return upload.Groups[1].Success
                ? BoxStore(upload.Groups[1].Value, null, null, attributes, data)
                : BoxStore(null, attributes!["parent"]!["id"]!.GetValue<string>(), attributes["name"]!.GetValue<string>(), attributes, data);
        }
        if (method == "POST" && Regex.Match(path, "^/files(?:/([^/]+))?/upload_sessions$") is { Success: true } start)
        {
            var json = JsonNode.Parse(body)!;
            if (!start.Groups[1].Success && BoxNameTaken(BoxNode(json["folder_id"]!.GetValue<string>())!, json["file_name"]!.GetValue<string>()))
            {
                return BoxError(HttpStatusCode.Conflict, "item_name_in_use");
            }
            var id = "s" + NewId();
            var meta = new JsonObject
            {
                ["file_id"] = start.Groups[1].Success ? start.Groups[1].Value : null,
                ["folder_id"] = json["folder_id"]?.GetValue<string>(), ["file_name"] = json["file_name"]?.GetValue<string>(),
                ["file_size"] = json["file_size"]!.GetValue<long>(),
            };
            _sessions[id] = (null, null, new MemoryStream(), meta);
            return Json(new JsonObject { ["id"] = id, ["part_size"] = BoxPartSize }, HttpStatusCode.Created);
        }
        if (Regex.Match(path, "^/files/upload_sessions/([^/]+)(/commit)?$") is { Success: true } inSession)
        {
            var session = _sessions[inSession.Groups[1].Value];
            if (method == "PUT")
            {
                var range = request.Content!.Headers.ContentRange!;
                Assert.Equal(session.Data.Length, range.From);
                Assert.Equal(session.Meta!["file_size"]!.GetValue<long>(), range.Length);
                Assert.True(body.Length == BoxPartSize || range.To == range.Length - 1, "parts are of the session's size");
                Assert.Equal(Sha1(body), request.Headers.GetValues("Digest").Single());
                session.Data.Write(body);
                return Json(new JsonObject { ["part"] = new JsonObject { ["part_id"] = "p" + range.From, ["offset"] = range.From, ["size"] = body.Length } });
            }
            var data = session.Data.ToArray();
            Assert.Equal(Sha1(data), request.Headers.GetValues("Digest").Single());
            var commit = JsonNode.Parse(body)!;
            Assert.Equal((data.Length + BoxPartSize - 1) / BoxPartSize, commit["parts"]!.AsArray().Count);
            if (_boxCommitPending)
            {
                _boxCommitPending = false;
                var pending = new HttpResponseMessage(HttpStatusCode.Accepted);
                pending.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return pending;
            }
            var meta = session.Meta!;
            return BoxStore(meta["file_id"]?.GetValue<string>(), meta["folder_id"]?.GetValue<string>(), meta["file_name"]?.GetValue<string>(), commit["attributes"], data);
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }
}
