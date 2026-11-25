using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NextCommander.Plugin.Repositories;

/// <summary>
/// GitLab's REST API v4 (gitlab.com or a self-hosted server): projects of the user, of groups (with their subgroups) and
/// of users, branches, tags, repository/tree (recursive), repository/files/raw, releases with their asset links.
/// </summary>
internal sealed class GitLabHost(HttpClient http, RepositoryOptions options) : RepositoryHost(http, options)
{
    /// <summary>The most pages of a recursive tree read at once (100 entries each); a bigger one is read folder by folder.</summary>
    internal const int TreePages = 20;

    public override string Title => "GitLab";

    public override string CloneUser => "oauth2";

    public override string WebUrl => Options.BaseUrl;

    protected override string ApiBase => Options.BaseUrl + "/api/v4";

    public override string PageUrl(string owner, string repository, string? kind = null, string? reference = null, string? path = null, bool folder = false)
    {
        var page = $"{WebUrl}/{owner}/{repository}";
        return kind switch
        {
            null => page,
            RepositoryFileSystemProvider.Releases => reference is null ? page + "/-/releases" : page + "/-/releases/" + Uri.EscapeDataString(reference),
            _ when reference is null => page + "/-/" + kind,
            _ when string.IsNullOrEmpty(path) => page + "/-/tree/" + EscapePath(reference),
            _ => page + (folder ? "/-/tree/" : "/-/blob/") + EscapePath(reference) + "/" + EscapePath(path),
        };
    }

    protected override void Authorize(HttpRequestMessage request, string token) => request.Headers.Add("PRIVATE-TOKEN", token);

    protected override DateTime? RateLimitReset(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ? EpochHeader(response, "RateLimit-Reset") ?? RetryAfter(response) : null;

    /// <summary>A project's address in the API: its full path, escaped ("group%2Fsub%2Fproject").</summary>
    private static string Project(string owner, string name) => "/projects/" + Uri.EscapeDataString(owner + "/" + name);

    private static RepositoryInfo? ReadProject(JsonElement element)
    {
        var full = Text(element, "path_with_namespace");
        var name = Text(element, "path");
        var owner = Text(Child(element, "namespace"), "full_path") ?? (full is not null && full.LastIndexOf('/') is > 0 and var slash ? full[..slash] : null);
        return owner is not null && name is not null
            ? new RepositoryInfo(owner, name, Text(element, "default_branch"), Time(Text(element, "last_activity_at")))
            : null;
    }

    public override async Task<IReadOnlyList<RepositoryInfo>> MyRepositoriesAsync(CancellationToken cancellationToken)
    {
        if (await TokenAsync().ConfigureAwait(false) is null)
        {
            return [];
        }
        var (items, _) = await GetPagesAsync("/projects?membership=true&simple=true&per_page=100&order_by=path&sort=asc", "your projects", ReadProject, cancellationToken)
            .ConfigureAwait(false);
        return items;
    }

    public override async Task<IReadOnlyList<RepositoryInfo>> OwnerRepositoriesAsync(string owner, CancellationToken cancellationToken)
    {
        var id = Uri.EscapeDataString(owner);
        try
        {
            var (projects, _) = await GetPagesAsync($"/groups/{id}/projects?simple=true&per_page=100&include_subgroups=false&order_by=path&sort=asc", owner,
                ReadProject, cancellationToken).ConfigureAwait(false);
            var (groups, _) = await GetPagesAsync($"/groups/{id}/subgroups?per_page=100", owner,
                e => Text(e, "path") is { } path ? new RepositoryInfo(owner, path, null, DateTime.MinValue, IsGroup: true) : null, cancellationToken).ConfigureAwait(false);
            return [.. groups, .. projects];
        }
        catch (DirectoryNotFoundException)
        {
            // not a group: a user's projects
            var (projects, _) = await GetPagesAsync($"/users/{id}/projects?simple=true&per_page=100&order_by=path&sort=asc", owner, ReadProject, cancellationToken)
                .ConfigureAwait(false);
            return projects;
        }
    }

    public override async Task<RepositoryInfo> RepositoryAsync(string owner, string name, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(Project(owner, name), owner + "/" + name, cancellationToken).ConfigureAwait(false);
        return ReadProject(json.RootElement) ?? throw new DirectoryNotFoundException(owner + "/" + name);
    }

    private static RefInfo? ReadRef(JsonElement element) =>
        Text(element, "name") is { } name ? new RefInfo(name, Time(Text(Child(element, "commit"), "committed_date"))) : null;

    public override async Task<IReadOnlyList<RefInfo>> BranchesAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Project(owner, name) + "/repository/branches?per_page=100", owner + "/" + name, ReadRef, cancellationToken).ConfigureAwait(false)).Items;

    public override async Task<IReadOnlyList<RefInfo>> TagsAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Project(owner, name) + "/repository/tags?per_page=100&order_by=updated", owner + "/" + name, ReadRef, cancellationToken)
            .ConfigureAwait(false)).Items;

    public override async Task<CommitInfo> CommitAsync(string owner, string name, string reference, bool tag, CancellationToken cancellationToken)
    {
        var what = $"{owner}/{name} {reference}";
        using var json = await GetJsonAsync($"{Project(owner, name)}/repository/{(tag ? "tags" : "branches")}/{Uri.EscapeDataString(reference)}", what, cancellationToken)
            .ConfigureAwait(false);
        var commit = Child(json.RootElement, "commit");
        return Text(commit, "id") is { } sha
            ? new CommitInfo(sha, Time(Text(commit, "committed_date") ?? Text(commit, "created_at")), null)
            : throw new DirectoryNotFoundException(what);
    }

    private static TreeItem? ReadItem(JsonElement element)
    {
        // a submodule ("commit") is another repository: left out
        if (Text(element, "path") is not { } path || Text(element, "type") is not ("blob" or "tree") || Text(element, "id") is not { } sha)
        {
            return null;
        }
        var isDirectory = Text(element, "type") == "tree";
        return new TreeItem(path, isDirectory, Text(element, "mode") == "120000", isDirectory ? 0 : null, sha);
    }

    public override async Task<TreeListing> TreeAsync(string owner, string name, CommitInfo commit, CancellationToken cancellationToken)
    {
        var (items, complete) = await GetPagesAsync($"{Project(owner, name)}/repository/tree?ref={commit.Sha}&recursive=true&per_page=100", owner + "/" + name,
            ReadItem, cancellationToken, TreePages).ConfigureAwait(false);
        return new TreeListing(items, complete);
    }

    public override async Task<IReadOnlyList<TreeItem>> FolderAsync(string owner, string name, CommitInfo commit, string path, string? sha, CancellationToken cancellationToken)
    {
        var folder = path.Length == 0 ? string.Empty : "&path=" + Uri.EscapeDataString(path);
        return (await GetPagesAsync($"{Project(owner, name)}/repository/tree?ref={commit.Sha}{folder}&per_page=100", owner + "/" + name + "/" + path, ReadItem,
            cancellationToken).ConfigureAwait(false)).Items;
    }

    private static string FileAddress(string owner, string name, TreeItem file) =>
        $"{Project(owner, name)}/repository/files/{Uri.EscapeDataString(file.Path)}";

    public override Task<Stream> OpenFileAsync(string owner, string name, CommitInfo commit, TreeItem file, CancellationToken cancellationToken) =>
        OpenStreamAsync(Request(HttpMethod.Get, FileAddress(owner, name, file) + "/raw?ref=" + commit.Sha, "*/*"), file.Path, cancellationToken);

    /// <summary>The tree has no sizes: a HEAD request for the one file asked for (X-Gitlab-Size).</summary>
    public override async Task<long?> FileSizeAsync(string owner, string name, CommitInfo commit, TreeItem file, CancellationToken cancellationToken)
    {
        if (file.Size is not null || file.IsDirectory)
        {
            return file.Size;
        }
        using var response = await SendAsync(Request(HttpMethod.Head, FileAddress(owner, name, file) + "?ref=" + commit.Sha), file.Path, cancellationToken)
            .ConfigureAwait(false);
        return long.TryParse(Header(response, "X-Gitlab-Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    private static ReleaseInfo? ReadRelease(JsonElement element)
    {
        if (Text(element, "tag_name") is not { } tag)
        {
            return null;
        }
        var time = Time(Text(element, "released_at") ?? Text(element, "created_at"));
        var assets = new List<AssetInfo>();
        if (Child(Child(element, "assets"), "links") is { ValueKind: JsonValueKind.Array } links)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (Text(link, "name") is { } name && (Text(link, "direct_asset_url") ?? Text(link, "url")) is { } url)
                {
                    assets.Add(new AssetInfo(name, null, time, url));
                }
            }
        }
        return new ReleaseInfo(tag, Text(element, "name") ?? tag, time, assets);
    }

    public override async Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await GetPagesAsync(Project(owner, name) + "/releases?per_page=100", owner + "/" + name, ReadRelease, cancellationToken, maxPages: 5).ConfigureAwait(false)).Items;

    /// <summary>A release's linked file (on this server the token goes with it, elsewhere not).</summary>
    public override Task<Stream> OpenAssetAsync(AssetInfo asset, CancellationToken cancellationToken) =>
        OpenStreamAsync(Request(HttpMethod.Get, asset.Url, "*/*"), asset.Name, cancellationToken);
}
