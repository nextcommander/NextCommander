using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NextCommander.Plugin.Repositories;

/// <summary>
/// GitHub's REST API (github.com through https://api.github.com, GitHub Enterprise Server through
/// https://&lt;server&gt;/api/v3): repositories, branches, tags, the git trees API (recursive) and blobs, releases.
/// </summary>
internal sealed class GitHubHost(HttpClient http, RepositoryOptions options) : RepositoryHost(http, options)
{
    private string? _login;

    public override string Title => "GitHub";

    public override string CloneUser => "x-access-token";

    public override string WebUrl
    {
        get
        {
            if (!Uri.TryCreate(Options.BaseUrl, UriKind.Absolute, out var api))
            {
                return "https://github.com";
            }
            if (api.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return "https://github.com";
            }
            var url = Options.BaseUrl;
            return url.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? url[..^"/api/v3".Length] : api.GetLeftPart(UriPartial.Authority);
        }
    }

    public override string PageUrl(string owner, string repository, string? kind = null, string? reference = null, string? path = null, bool folder = false)
    {
        var page = $"{WebUrl}/{owner}/{repository}";
        return kind switch
        {
            null => page,
            RepositoryFileSystemProvider.Releases => reference is null ? page + "/releases" : page + "/releases/tag/" + EscapePath(reference),
            _ when reference is null => page + "/" + kind,
            _ when string.IsNullOrEmpty(path) => page + "/tree/" + EscapePath(reference),
            _ => page + (folder ? "/tree/" : "/blob/") + EscapePath(reference) + "/" + EscapePath(path),
        };
    }

    protected override void Authorize(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    protected override DateTime? RateLimitReset(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return null;
        }
        if (Header(response, "x-ratelimit-remaining") == "0")
        {
            return EpochHeader(response, "x-ratelimit-reset") ?? DateTime.Now.AddMinutes(1);
        }
        // the secondary limits (too many requests at once) say how long to wait
        return RetryAfter(response);
    }

    private static string Repo(string owner, string name) => $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";

    private static RepositoryInfo? ReadRepository(JsonElement element) =>
        Text(Child(element, "owner"), "login") is { } owner && Text(element, "name") is { } name
            ? new RepositoryInfo(owner, name, Text(element, "default_branch"), Time(Text(element, "pushed_at") ?? Text(element, "updated_at")))
            : null;

    public override async Task<IReadOnlyList<RepositoryInfo>> MyRepositoriesAsync(CancellationToken cancellationToken)
    {
        if (await TokenAsync().ConfigureAwait(false) is null)
        {
            return [];
        }
        var (items, _) = await GetPagesAsync("/user/repos?per_page=100&sort=full_name&affiliation=owner,collaborator,organization_member",
            "your repositories", ReadRepository, cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>The signed-in user's login (asked once).</summary>
    private async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        if (_login is null && await TokenAsync().ConfigureAwait(false) is not null)
        {
            using var json = await GetJsonAsync("/user", "your account", cancellationToken).ConfigureAwait(false);
            _login = Text(json.RootElement, "login");
        }
        return _login;
    }

    public override async Task<IReadOnlyList<RepositoryInfo>> OwnerRepositoriesAsync(string owner, CancellationToken cancellationToken)
    {
        string type;
        using (var json = await GetJsonAsync("/users/" + Uri.EscapeDataString(owner), owner, cancellationToken).ConfigureAwait(false))
        {
            type = Text(json.RootElement, "type") ?? "User";
        }
        string path;
        if (type == "Organization")
        {
            path = $"/orgs/{Uri.EscapeDataString(owner)}/repos?per_page=100&type=all&sort=full_name";
        }
        else if (string.Equals(await LoginAsync(cancellationToken).ConfigureAwait(false), owner, StringComparison.OrdinalIgnoreCase))
        {
            path = "/user/repos?per_page=100&affiliation=owner&sort=full_name"; // your own, the private ones too
        }
        else
        {
            path = $"/users/{Uri.EscapeDataString(owner)}/repos?per_page=100&type=owner&sort=full_name";
        }
        var (items, _) = await GetPagesAsync(path, owner, ReadRepository, cancellationToken).ConfigureAwait(false);
        return items;
    }

    public override async Task<RepositoryInfo> RepositoryAsync(string owner, string name, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(Repo(owner, name), owner + "/" + name, cancellationToken).ConfigureAwait(false);
        return ReadRepository(json.RootElement) ?? throw new DirectoryNotFoundException(owner + "/" + name);
    }

    private static RefInfo? ReadRef(JsonElement element) => Text(element, "name") is { } name ? new RefInfo(name, DateTime.MinValue) : null;

    public override async Task<IReadOnlyList<RefInfo>> BranchesAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Repo(owner, name) + "/branches?per_page=100", owner + "/" + name, ReadRef, cancellationToken).ConfigureAwait(false)).Items;

    public override async Task<IReadOnlyList<RefInfo>> TagsAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Repo(owner, name) + "/tags?per_page=100", owner + "/" + name, ReadRef, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>A commit object of the API ("sha", "commit": { "committer": { "date" }, "tree": { "sha" } }).</summary>
    private static CommitInfo ReadCommit(JsonElement commit, string what)
    {
        var inner = Child(commit, "commit");
        return Text(commit, "sha") is { } sha
            ? new CommitInfo(sha, Time(Text(Child(inner, "committer"), "date") ?? Text(Child(inner, "author"), "date")), Text(Child(inner, "tree"), "sha"))
            : throw new DirectoryNotFoundException(what);
    }

    public override async Task<CommitInfo> CommitAsync(string owner, string name, string reference, bool tag, CancellationToken cancellationToken)
    {
        var what = $"{owner}/{name} {reference}";
        if (tag)
        {
            using var json = await GetJsonAsync(Repo(owner, name) + "/commits/" + EscapePath(reference), what, cancellationToken).ConfigureAwait(false);
            return ReadCommit(json.RootElement, what);
        }
        using var branch = await GetJsonAsync(Repo(owner, name) + "/branches/" + EscapePath(reference), what, cancellationToken).ConfigureAwait(false);
        return ReadCommit(Child(branch.RootElement, "commit"), what);
    }

    private static List<TreeItem> ReadTree(JsonElement root, string prefix)
    {
        var items = new List<TreeItem>();
        if (Child(root, "tree") is { ValueKind: JsonValueKind.Array } tree)
        {
            foreach (var element in tree.EnumerateArray())
            {
                // a submodule ("commit") is another repository: left out
                if (Text(element, "path") is not { } path || Text(element, "type") is not ("blob" or "tree") || Text(element, "sha") is not { } sha)
                {
                    continue;
                }
                var isDirectory = Text(element, "type") == "tree";
                items.Add(new TreeItem(prefix + path, isDirectory, Text(element, "mode") == "120000", isDirectory ? 0 : Number(element, "size"), sha));
            }
        }
        return items;
    }

    public override async Task<TreeListing> TreeAsync(string owner, string name, CommitInfo commit, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"{Repo(owner, name)}/git/trees/{commit.TreeSha ?? commit.Sha}?recursive=1", owner + "/" + name, cancellationToken)
            .ConfigureAwait(false);
        return new TreeListing(ReadTree(json.RootElement, string.Empty), !Flag(json.RootElement, "truncated"));
    }

    public override async Task<IReadOnlyList<TreeItem>> FolderAsync(string owner, string name, CommitInfo commit, string path, string? sha, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"{Repo(owner, name)}/git/trees/{sha ?? commit.TreeSha ?? commit.Sha}", owner + "/" + name + "/" + path, cancellationToken)
            .ConfigureAwait(false);
        return ReadTree(json.RootElement, path.Length == 0 ? string.Empty : path + "/");
    }

    public override Task<Stream> OpenFileAsync(string owner, string name, CommitInfo commit, TreeItem file, CancellationToken cancellationToken) =>
        OpenStreamAsync(Request(HttpMethod.Get, $"{Repo(owner, name)}/git/blobs/{file.Sha}", "application/vnd.github.raw"), file.Path, cancellationToken);

    private static ReleaseInfo? ReadRelease(JsonElement element)
    {
        if (Text(element, "tag_name") is not { } tag)
        {
            return null;
        }
        var assets = new List<AssetInfo>();
        if (Child(element, "assets") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var asset in list.EnumerateArray())
            {
                if (Text(asset, "name") is { } name && Text(asset, "url") is { } url)
                {
                    assets.Add(new AssetInfo(name, Number(asset, "size"), Time(Text(asset, "updated_at") ?? Text(asset, "created_at")), url));
                }
            }
        }
        return new ReleaseInfo(tag, Text(element, "name") ?? tag, Time(Text(element, "published_at") ?? Text(element, "created_at")), assets);
    }

    public override async Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Repo(owner, name) + "/releases?per_page=100", owner + "/" + name, ReadRelease, cancellationToken, maxPages: 5).ConfigureAwait(false)).Items;

    /// <summary>A release asset through the API (it sends the file's address on the storage server, where the token is not sent).</summary>
    public override Task<Stream> OpenAssetAsync(AssetInfo asset, CancellationToken cancellationToken) =>
        OpenStreamAsync(Request(HttpMethod.Get, asset.Url, "application/octet-stream"), asset.Name, cancellationToken);
}
