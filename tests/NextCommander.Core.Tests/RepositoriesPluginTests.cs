using System.Globalization;
using System.Net;
using System.Text;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Repositories;

namespace NextCommander.Core.Tests;

/// <summary>
/// The GitHub / GitLab repositories plugin against fake APIs (realistic JSON of both hosts): the listings, the trees,
/// reading files and release assets, the errors (rate limit, a token needed, no network) and the token sent only when
/// set. With FM_TEST_GITHUB=1 also against the real github.com (public repository, no token).
/// </summary>
public sealed class RepositoriesPluginTests
{
    /// <summary>Answers requests by method and path (with the query) and records them.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Requests { get; } = [];

        public Exception? Fail { get; set; }

        public void Json(string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage>? headers = null, string method = "GET") =>
            _routes[method + " " + pathAndQuery] = _ =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                headers?.Invoke(response);
                return response;
            };

        public void Bytes(string pathAndQuery, string content, string method = "GET") =>
            _routes[method + " " + pathAndQuery] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };

        public void Route(string key, Func<HttpRequestMessage, HttpResponseMessage> answer) => _routes[key] = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Fail is { } fail)
            {
                throw fail;
            }
            var key = request.Method.Method + " " + request.RequestUri!.PathAndQuery;
            return Task.FromResult(_routes.TryGetValue(key, out var answer)
                ? answer(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") });
        }

        public HttpRequestMessage Last(string pathStart) => Requests.Last(r => r.RequestUri!.PathAndQuery.StartsWith(pathStart, StringComparison.Ordinal));
    }

    private static RepositoryOptions Options(string url, string? token, params string[] repositories) =>
        new(url, token is null ? null : () => Task.FromResult<string?>(token), repositories);

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
    }

    private static DateTime Local(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).LocalDateTime;

    // ---------------------------------------------------------------- GitHub

    private const string GitHubApi = "https://api.github.com";

    private const string UserRepos = """
        [
          {"id":1,"name":"hello","full_name":"octo/hello","private":false,"owner":{"login":"octo","type":"User"},"html_url":"https://github.com/octo/hello",
           "default_branch":"main","pushed_at":"2026-11-20T10:15:30Z","updated_at":"2026-11-20T10:15:30Z"},
          {"id":2,"name":"secret-tool","full_name":"acme/secret-tool","private":true,"owner":{"login":"acme","type":"Organization"},
           "default_branch":"develop","pushed_at":"2026-11-01T08:00:00Z"}
        ]
        """;

    private const string HelloRepo = """
        {"id":1,"name":"hello","full_name":"octo/hello","private":false,"owner":{"login":"octo","type":"User"},"html_url":"https://github.com/octo/hello",
         "default_branch":"main","pushed_at":"2026-11-20T10:15:30Z"}
        """;

    private static string BranchJson(string name, string sha, string tree, string date) =>
        "{\"name\":\"" + name + "\",\"commit\":{\"sha\":\"" + sha + "\",\"commit\":{\"author\":{\"name\":\"Octo\",\"date\":\"" + date
        + "\"},\"committer\":{\"name\":\"GitHub\",\"date\":\"" + date + "\"},\"message\":\"Update\",\"tree\":{\"sha\":\"" + tree
        + "\",\"url\":\"https://api.github.com/repos/octo/hello/git/trees/" + tree + "\"}}},\"protected\":false}";

    private const string HelloTree = """
        {"sha":"t1","url":"https://api.github.com/repos/octo/hello/git/trees/t1","truncated":false,"tree":[
          {"path":"README.md","mode":"100644","type":"blob","sha":"b-readme","size":12},
          {"path":"releases","mode":"040000","type":"tree","sha":"t-rel"},
          {"path":"releases/notes.txt","mode":"100644","type":"blob","sha":"b-notes","size":5},
          {"path":"src","mode":"040000","type":"tree","sha":"t-src"},
          {"path":"src/app.cs","mode":"100644","type":"blob","sha":"b-app","size":2048},
          {"path":"src/link","mode":"120000","type":"blob","sha":"b-link","size":6},
          {"path":"vendor","mode":"160000","type":"commit","sha":"c-sub"}
        ]}
        """;

    private static (GitHubFileSystemProvider Provider, FakeApi Api) GitHub(string? token = "ghp_secret", params string[] repositories)
    {
        var api = new FakeApi();
        api.Json("/user/repos?per_page=100&sort=full_name&affiliation=owner,collaborator,organization_member", UserRepos);
        api.Json("/repos/octo/hello", HelloRepo);
        api.Json("/repos/octo/hello/branches/main", BranchJson("main", "c1", "t1", "2026-11-20T10:15:30Z"));
        api.Json("/repos/octo/hello/git/trees/t1?recursive=1", HelloTree);
        return (new GitHubFileSystemProvider(Options(GitHubApi, token, repositories), api), api);
    }

    private static VfsPath Hub(string path) => VfsPath.Parse("github://" + path);

    [Fact]
    public async Task GitHub_root_lists_your_repositories_with_the_token_sent()
    {
        var (provider, api) = GitHub("ghp_secret", "dotnet/runtime");

        var root = await provider.ListAsync(provider.EntryRoot, CancellationToken.None);

        Assert.Equal(["dotnet⁄runtime", "octo⁄hello", "acme⁄secret-tool"], root.Select(e => e.Name));
        Assert.Equal(Hub("octo/hello"), root[1].Path);
        Assert.Equal(Local("2026-11-20T10:15:30Z"), root[1].LastWriteTime);
        var request = Assert.Single(api.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_secret", request.Headers.Authorization?.Parameter);
        Assert.Contains("NextCommander", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_without_a_token_sends_none_and_lists_the_repositories_of_the_settings()
    {
        var (provider, api) = GitHub(null, "octo/hello");

        var root = await provider.ListAsync(provider.EntryRoot, CancellationToken.None);
        Assert.Equal(["octo⁄hello"], root.Select(e => e.Name));
        Assert.Empty(api.Requests); // no "your repositories" without a token

        await provider.ListAsync(Hub("octo/hello/"), CancellationToken.None);
        Assert.NotEmpty(api.Requests);
        Assert.All(api.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task GitHub_repository_holds_branches_tags_releases_and_the_default_branch_files()
    {
        var (provider, api) = GitHub();

        var top = await provider.ListAsync(Hub("octo/hello"), CancellationToken.None);
        // the default branch's own "releases" folder is hidden by the releases of the repository; the submodule is left out
        Assert.Equal(["branches", "tags", "releases", "README.md", "src"], top.Select(e => e.Name));
        var readme = top.Single(e => e.Name == "README.md");
        Assert.Equal(12, readme.Size);
        Assert.Equal(Local("2026-11-20T10:15:30Z"), readme.LastWriteTime);

        var src = await provider.ListAsync(Hub("octo/hello/src"), CancellationToken.None);
        Assert.Equal(["app.cs", "link"], src.Select(e => e.Name));
        Assert.True(src[1].IsLink);
        Assert.Equal(2048, src[0].Size);

        // the tree is read once per commit
        Assert.Single(api.Requests, r => r.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal));

        var entry = await provider.GetEntryAsync(Hub("octo/hello/src/app.cs"), CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(2048, entry.Size);
        Assert.Null(await provider.GetEntryAsync(Hub("octo/hello/src/missing.cs"), CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_branches_and_tags_open_their_files_and_a_file_is_read_through_the_blob()
    {
        var (provider, api) = GitHub();
        api.Json("/repos/octo/hello/branches?per_page=100", """[{"name":"main","commit":{"sha":"c1"}},{"name":"feature/login","commit":{"sha":"c2"}}]""");
        api.Json("/repos/octo/hello/tags?per_page=100", """[{"name":"v1.0","commit":{"sha":"c0"},"zipball_url":"x"}]""");
        api.Json("/repos/octo/hello/branches/feature/login", BranchJson("feature/login", "c2", "t2", "2026-11-21T09:00:00Z"));
        api.Json("/repos/octo/hello/git/trees/t2?recursive=1", """{"sha":"t2","truncated":false,"tree":[{"path":"login.cs","mode":"100644","type":"blob","sha":"b-login","size":4}]}""");
        api.Json("/repos/octo/hello/commits/v1.0", """{"sha":"c0","commit":{"committer":{"date":"2026-10-01T00:00:00Z"},"tree":{"sha":"t0"}}}""");
        api.Json("/repos/octo/hello/git/trees/t0?recursive=1", """{"sha":"t0","truncated":false,"tree":[{"path":"old.txt","mode":"100644","type":"blob","sha":"b-old","size":3}]}""");
        api.Bytes("/repos/octo/hello/git/blobs/b-login", "code");

        Assert.Equal(["main", "feature⁄login"], (await provider.ListAsync(Hub("octo/hello/branches"), CancellationToken.None)).Select(e => e.Name));
        Assert.Equal(["v1.0"], (await provider.ListAsync(Hub("octo/hello/tags"), CancellationToken.None)).Select(e => e.Name));

        var files = await provider.ListAsync(Hub("octo/hello/branches/feature⁄login"), CancellationToken.None);
        Assert.Equal(Hub("octo/hello/branches/feature⁄login/login.cs"), Assert.Single(files).Path);
        Assert.Equal(Local("2026-11-21T09:00:00Z"), files[0].LastWriteTime);
        Assert.Equal("old.txt", Assert.Single(await provider.ListAsync(Hub("octo/hello/tags/v1.0"), CancellationToken.None)).Name);

        Assert.Equal("code", await ReadAllAsync(await provider.OpenReadAsync(files[0].Path, CancellationToken.None)));
        Assert.Equal("application/vnd.github.raw", api.Last("/repos/octo/hello/git/blobs/").Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task GitHub_releases_are_folders_of_their_assets()
    {
        var (provider, api) = GitHub();
        api.Json("/repos/octo/hello/releases?per_page=100", """
            [{"tag_name":"v2.0","name":"Version 2","draft":false,"published_at":"2026-11-15T12:00:00Z","assets":[
               {"url":"https://api.github.com/repos/octo/hello/releases/assets/7","name":"hello-win-x64.zip","size":1048576,"updated_at":"2026-11-15T12:05:00Z",
                "browser_download_url":"https://github.com/octo/hello/releases/download/v2.0/hello-win-x64.zip"}]},
             {"tag_name":"release/1","name":null,"published_at":"2026-10-01T00:00:00Z","assets":[]}]
            """);
        api.Bytes("/repos/octo/hello/releases/assets/7", "zipdata");

        var releases = await provider.ListAsync(Hub("octo/hello/releases"), CancellationToken.None);
        Assert.Equal(["v2.0", "release⁄1"], releases.Select(e => e.Name));
        var asset = Assert.Single(await provider.ListAsync(Hub("octo/hello/releases/v2.0"), CancellationToken.None));
        Assert.Equal(("hello-win-x64.zip", 1048576L, false), (asset.Name, asset.Size, asset.IsDirectory));
        Assert.Equal(Local("2026-11-15T12:05:00Z"), asset.LastWriteTime);

        Assert.Equal("zipdata", await ReadAllAsync(await provider.OpenReadAsync(asset.Path, CancellationToken.None)));
        Assert.Equal("application/octet-stream", api.Last("/repos/octo/hello/releases/assets/7").Headers.Accept.Single().MediaType);
        Assert.Empty(await provider.ListAsync(Hub("octo/hello/releases/release⁄1"), CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_tree_cut_short_is_read_folder_by_folder()
    {
        var (provider, api) = GitHub();
        api.Json("/repos/octo/hello/git/trees/t1?recursive=1", """{"sha":"t1","truncated":true,"tree":[{"path":"README.md","mode":"100644","type":"blob","sha":"b","size":1}]}""");
        api.Json("/repos/octo/hello/git/trees/t1", """{"sha":"t1","truncated":false,"tree":[{"path":"README.md","mode":"100644","type":"blob","sha":"b","size":1},{"path":"src","mode":"040000","type":"tree","sha":"t-src"}]}""");
        api.Json("/repos/octo/hello/git/trees/t-src", """{"sha":"t-src","truncated":false,"tree":[{"path":"deep","mode":"040000","type":"tree","sha":"t-deep"}]}""");
        api.Json("/repos/octo/hello/git/trees/t-deep", """{"sha":"t-deep","truncated":false,"tree":[{"path":"x.cs","mode":"100644","type":"blob","sha":"b-x","size":9}]}""");

        var deep = await provider.ListAsync(Hub("octo/hello/src/deep"), CancellationToken.None);

        Assert.Equal(("x.cs", 9L), (Assert.Single(deep).Name, deep[0].Size));
        Assert.Equal(Hub("octo/hello/src/deep/x.cs"), deep[0].Path);
    }

    [Fact]
    public async Task GitHub_rate_limit_says_when_it_resets()
    {
        var (provider, api) = GitHub(null);
        var reset = DateTimeOffset.UtcNow.AddMinutes(17);
        api.Json("/repos/octo/limited", """{"message":"API rate limit exceeded for 203.0.113.9.","documentation_url":"https://docs.github.com/rest"}""",
            HttpStatusCode.Forbidden, r =>
            {
                r.Headers.Add("x-ratelimit-limit", "60");
                r.Headers.Add("x-ratelimit-remaining", "0");
                r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            });

        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.ListAsync(Hub("octo/limited"), CancellationToken.None));

        Assert.Contains("GitHub API rate limit is reached", error.Message, StringComparison.Ordinal);
        Assert.Contains(reset.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("access token", error.Message, StringComparison.Ordinal); // without one: a token raises the limit
    }

    [Fact]
    public async Task GitHub_not_found_says_a_private_repository_needs_a_token()
    {
        var (provider, _) = GitHub(null);
        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => provider.ListAsync(Hub("someone/private-thing"), CancellationToken.None));
        Assert.Contains("needs an access token", error.Message, StringComparison.Ordinal);
        Assert.Null(await provider.GetEntryAsync(Hub("someone/private-thing"), CancellationToken.None));

        var (signedIn, _) = GitHub("ghp_secret");
        var other = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => signedIn.ListAsync(Hub("someone/private-thing"), CancellationToken.None));
        Assert.Contains("the access token has no access", other.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_refused_token_and_no_network_are_said_clearly()
    {
        var (provider, api) = GitHub("expired");
        api.Json("/repos/octo/hello", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.ListAsync(Hub("octo/hello"), CancellationToken.None));
        Assert.Contains("refused the access token", refused.Message, StringComparison.Ordinal);

        api.Fail = new HttpRequestException("No such host is known. (api.github.com:443)");
        var offline = await Assert.ThrowsAsync<IOException>(() => provider.ListAsync(Hub("octo/other"), CancellationToken.None));
        Assert.Contains("GitHub cannot be reached (api.github.com)", offline.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_owner_folder_lists_an_organization_and_goes_back_to_the_root()
    {
        var (provider, api) = GitHub();
        api.Json("/users/acme", """{"login":"acme","type":"Organization"}""");
        api.Json("/orgs/acme/repos?per_page=100&type=all&sort=full_name", """[{"name":"secret-tool","owner":{"login":"acme"},"default_branch":"develop","pushed_at":"2026-11-01T08:00:00Z"}]""");

        var repos = await provider.ListAsync(Hub("acme/"), CancellationToken.None);

        Assert.Equal(Hub("acme/secret-tool"), Assert.Single(repos).Path);
        Assert.Equal((provider.EntryRoot, string.Empty), provider.OriginOf(Hub("acme/")));
    }

    [Fact]
    public async Task GitHub_entries_open_their_page_in_the_browser()
    {
        var (provider, _) = GitHub();
        Assert.Equal("https://github.com/octo/hello", provider.PageOf(Hub("octo/hello"), true));
        Assert.Equal("https://github.com/octo/hello/blob/HEAD/src/app.cs", provider.PageOf(Hub("octo/hello/src/app.cs"), false));
        Assert.Equal("https://github.com/octo/hello/tree/feature/login/src", provider.PageOf(Hub("octo/hello/branches/feature⁄login/src"), true));
        Assert.Equal("https://github.com/octo/hello/releases/tag/v2.0", provider.PageOf(Hub("octo/hello/releases/v2.0/a.zip"), false));
        Assert.Null(provider.PageOf(provider.EntryRoot, true));

        var enterprise = new GitHubFileSystemProvider(Options("https://git.example.com/api/v3", null), new FakeApi());
        Assert.Equal("https://git.example.com/octo/hello", enterprise.PageOf(Hub("octo/hello"), true));

        var opened = new List<string>();
        var previous = RepositoryFileSystemProvider.OpenBrowser;
        RepositoryFileSystemProvider.OpenBrowser = opened.Add;
        try
        {
            var action = Assert.Single(provider.ActionsFor(new VfsEntry { Name = "hello", Path = Hub("octo/hello"), IsDirectory = true }));
            Assert.Equal("Open in browser", action.Name);
            Assert.Null(await action.RunAsync(CancellationToken.None));
            Assert.Equal(["https://github.com/octo/hello"], opened);
        }
        finally
        {
            RepositoryFileSystemProvider.OpenBrowser = previous;
        }
    }

    /// <summary>The dialogs of the program, answered by the test.</summary>
    private sealed class Answers(params string?[] answers) : IPluginDialogs
    {
        private readonly Queue<string?> _answers = new(answers);

        public List<string> Prompts { get; } = [];

        public Task<string?> PromptAsync(string title, string message, string initialText)
        {
            Prompts.Add(message);
            return Task.FromResult(_answers.Dequeue());
        }

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task ShowTextAsync(string fileName, string text) => Task.CompletedTask;
    }

    [Fact]
    public async Task Open_a_repository_lists_it_at_the_root()
    {
        var (provider, _) = GitHub(null);
        var dialogs = new Answers("https://github.com/octo/hello/tree/main/src");
        var actions = provider.ActionsFor(provider.EntryRoot, [], dialogs);
        var open = Assert.Single(actions, a => a.Name == "Open a repository...");

        Assert.Null(await open.RunAsync(CancellationToken.None));

        Assert.Equal(["octo⁄hello"], (await provider.ListAsync(provider.EntryRoot, CancellationToken.None)).Select(e => e.Name));
        Assert.Equal("octo/hello", RepositoryFileSystemProvider.RepositoryName("octo/hello.git", "https://github.com"));
        Assert.Equal("group/sub/project", RepositoryFileSystemProvider.RepositoryName("https://gitlab.example.com/git/group/sub/project/-/tree/main", "https://gitlab.example.com/git"));
        Assert.Null(RepositoryFileSystemProvider.RepositoryName("hello", "https://github.com"));

        // inside a repository (or with one selected) it can be cloned
        Assert.Contains(provider.ActionsFor(Hub("octo/hello/src"), [], dialogs), a => a.Name == "Clone the repository...");
        Assert.Contains(provider.ActionsFor(provider.EntryRoot, [new VfsEntry { Name = "octo⁄hello", Path = Hub("octo/hello"), IsDirectory = true }], dialogs),
            a => a.Name == "Clone the repository...");
    }

    [Fact]
    public void Clone_passes_the_token_through_the_environment_not_the_command_line()
    {
        var info = GitCloner.StartInfo("git", "https://github.com/octo/hello.git", "/tmp/hello", "feature/login", ("x-access-token", "ghp_secret"));

        Assert.Equal(["clone", "--branch", "feature/login", "--", "https://github.com/octo/hello.git", "/tmp/hello"], info.ArgumentList);
        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("ghp_secret", StringComparison.Ordinal));
        Assert.Equal("http.extraHeader", info.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:ghp_secret")), info.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("0", info.Environment["GIT_TERMINAL_PROMPT"]);

        var anonymous = GitCloner.StartInfo("git", "https://gitlab.com/g/p.git", "/tmp/p", null, null);
        Assert.False(anonymous.Environment.ContainsKey("GIT_CONFIG_COUNT"));
    }

    [Fact]
    public async Task Writing_is_refused()
    {
        var (provider, _) = GitHub();
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenWriteAsync(Hub("octo/hello/new.txt"), true, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(Hub("octo/hello/README.md"), false, CancellationToken.None));
    }

    // ---------------------------------------------------------------- GitLab

    private const string GitLabUrl = "https://gitlab.example.com";

    private const string Projects = """
        [{"id":42,"description":null,"name":"Tool","name_with_namespace":"Group / Sub / Tool","path":"tool","path_with_namespace":"group/sub/tool",
          "default_branch":"main","web_url":"https://gitlab.example.com/group/sub/tool","last_activity_at":"2026-11-19T07:30:00.000Z",
          "namespace":{"id":7,"name":"Sub","path":"sub","kind":"group","full_path":"group/sub"}}]
        """;

    private static (GitLabFileSystemProvider Provider, FakeApi Api) GitLab(string? token = "glpat-secret", params string[] repositories)
    {
        var api = new FakeApi();
        api.Json("/api/v4/projects?membership=true&simple=true&per_page=100&order_by=path&sort=asc", Projects);
        api.Json("/api/v4/projects/group%2Fsub%2Ftool", Projects.Trim().TrimStart('[').TrimEnd(']'));
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/repository/branches/main",
            """{"name":"main","merged":false,"protected":true,"default":true,"commit":{"id":"c9","short_id":"c9","title":"Fix","committed_date":"2026-11-19T07:30:00.000+00:00"}}""");
        // the recursive tree over two pages (the Link header's next address)
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/repository/tree?ref=c9&recursive=true&per_page=100", """
            [{"id":"t-docs","name":"docs","type":"tree","path":"docs","mode":"040000"},{"id":"b-readme","name":"README.md","type":"blob","path":"README.md","mode":"100644"}]
            """, headers: r => r.Headers.TryAddWithoutValidation("Link",
                "<https://gitlab.example.com/api/v4/projects/group%2Fsub%2Ftool/repository/tree?ref=c9&recursive=true&per_page=100&page=2>; rel=\"next\", <https://gitlab.example.com/api/v4/x>; rel=\"first\""));
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/repository/tree?ref=c9&recursive=true&per_page=100&page=2", """
            [{"id":"b-guide","name":"guide.md","type":"blob","path":"docs/guide.md","mode":"100644"},{"id":"c-sub","name":"lib","type":"commit","path":"lib","mode":"160000"}]
            """);
        return (new GitLabFileSystemProvider(Options(GitLabUrl, token, repositories), api), api);
    }

    private static VfsPath Lab(string path) => VfsPath.Parse("gitlab://" + path);

    [Fact]
    public async Task GitLab_lists_your_projects_and_their_files_with_the_private_token()
    {
        var (provider, api) = GitLab();

        var root = await provider.ListAsync(provider.EntryRoot, CancellationToken.None);
        Assert.Equal("group⁄sub⁄tool", Assert.Single(root).Name);
        Assert.Equal(Lab("group|sub/tool"), root[0].Path);
        Assert.Equal("glpat-secret", api.Requests[0].Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Null(api.Requests[0].Headers.Authorization);

        var top = await provider.ListAsync(root[0].Path, CancellationToken.None);
        Assert.Equal(["branches", "tags", "releases", "docs", "README.md"], top.Select(e => e.Name));
        Assert.Equal(Local("2026-11-19T07:30:00Z"), top.Single(e => e.Name == "README.md").LastWriteTime);
        Assert.Equal(["guide.md"], (await provider.ListAsync(Lab("group|sub/tool/docs"), CancellationToken.None)).Select(e => e.Name));

        api.Bytes("/api/v4/projects/group%2Fsub%2Ftool/repository/files/docs%2Fguide.md/raw?ref=c9", "# Guide");
        Assert.Equal("# Guide", await ReadAllAsync(await provider.OpenReadAsync(Lab("group|sub/tool/docs/guide.md"), CancellationToken.None)));

        // the tree has no sizes: one HEAD request for the file asked for
        api.Route("HEAD /api/v4/projects/group%2Fsub%2Ftool/repository/files/docs%2Fguide.md?ref=c9", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.Add("X-Gitlab-Size", "7");
            return response;
        });
        Assert.Equal(7, (await provider.GetEntryAsync(Lab("group|sub/tool/docs/guide.md"), CancellationToken.None))!.Size);
    }

    [Fact]
    public async Task GitLab_without_a_token_sends_none()
    {
        var (provider, api) = GitLab(null, "group/sub/tool");

        var root = await provider.ListAsync(provider.EntryRoot, CancellationToken.None);
        Assert.Equal(["group⁄sub⁄tool"], root.Select(e => e.Name));
        await provider.ListAsync(root[0].Path, CancellationToken.None);

        Assert.NotEmpty(api.Requests);
        Assert.All(api.Requests, r => Assert.False(r.Headers.Contains("PRIVATE-TOKEN")));
    }

    [Fact]
    public async Task GitLab_branches_tags_and_releases()
    {
        var (provider, api) = GitLab();
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/repository/branches?per_page=100",
            """[{"name":"main","commit":{"id":"c9","committed_date":"2026-11-19T07:30:00.000+00:00"}},{"name":"fix/crash","commit":{"id":"c8","committed_date":"2026-11-18T07:30:00.000+00:00"}}]""");
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/repository/tags?per_page=100&order_by=updated",
            """[{"name":"v1.2","message":"","target":"c7","commit":{"id":"c7","committed_date":"2026-11-10T00:00:00.000+00:00"}}]""");
        api.Json("/api/v4/projects/group%2Fsub%2Ftool/releases?per_page=100", """
            [{"name":"Tool 1.2","tag_name":"v1.2","released_at":"2026-11-10T12:00:00.000Z","created_at":"2026-11-10T12:00:00.000Z",
              "assets":{"count":1,"sources":[{"format":"zip","url":"https://gitlab.example.com/group/sub/tool/-/archive/v1.2/tool-v1.2.zip"}],
                "links":[{"id":1,"name":"tool-linux.tar.gz","url":"https://gitlab.example.com/group/sub/tool/-/releases/v1.2/downloads/tool-linux.tar.gz",
                          "direct_asset_url":"https://gitlab.example.com/group/sub/tool/-/releases/v1.2/downloads/tool-linux.tar.gz","link_type":"package"}]}}]
            """);
        api.Bytes("/group/sub/tool/-/releases/v1.2/downloads/tool-linux.tar.gz", "tarball");

        var branches = await provider.ListAsync(Lab("group|sub/tool/branches"), CancellationToken.None);
        Assert.Equal(["main", "fix⁄crash"], branches.Select(e => e.Name));
        Assert.Equal(Local("2026-11-18T07:30:00Z"), branches[1].LastWriteTime);
        Assert.Equal(["v1.2"], (await provider.ListAsync(Lab("group|sub/tool/tags"), CancellationToken.None)).Select(e => e.Name));

        var asset = Assert.Single(await provider.ListAsync(Lab("group|sub/tool/releases/v1.2"), CancellationToken.None));
        Assert.Equal("tool-linux.tar.gz", asset.Name);
        Assert.Equal("tarball", await ReadAllAsync(await provider.OpenReadAsync(asset.Path, CancellationToken.None)));

        Assert.Equal("https://gitlab.example.com/group/sub/tool/-/releases/v1.2", provider.PageOf(asset.Path, false));
        Assert.Equal("https://gitlab.example.com/group/sub/tool/-/blob/HEAD/docs/guide.md", provider.PageOf(Lab("group|sub/tool/docs/guide.md"), false));
    }

    [Fact]
    public async Task GitLab_group_lists_its_subgroups_and_projects()
    {
        var (provider, api) = GitLab();
        api.Json("/api/v4/groups/group/projects?simple=true&per_page=100&include_subgroups=false&order_by=path&sort=asc",
            """[{"id":5,"path":"site","path_with_namespace":"group/site","namespace":{"full_path":"group"},"last_activity_at":"2026-11-01T00:00:00Z"}]""");
        api.Json("/api/v4/groups/group/subgroups?per_page=100", """[{"id":7,"name":"Sub","path":"sub","full_path":"group/sub"}]""");

        var items = await provider.ListAsync(Lab("group/"), CancellationToken.None);

        Assert.Equal([Lab("group|sub/"), Lab("group/site")], items.Select(e => e.Path));
        Assert.Equal((Lab("group/"), "sub"), provider.OriginOf(Lab("group|sub/")));
    }

    [Fact]
    public async Task GitLab_rate_limit_says_when_it_resets()
    {
        var (provider, api) = GitLab();
        var reset = DateTimeOffset.UtcNow.AddMinutes(3);
        api.Json("/api/v4/projects/group%2Fsub%2Fbusy", """{"message":"Retry later"}""", HttpStatusCode.TooManyRequests,
            r => r.Headers.Add("RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.ListAsync(Lab("group|sub/busy"), CancellationToken.None));

        Assert.Equal($"The GitLab API rate limit is reached; it resets at {reset.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)}.", error.Message);
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public async Task Settings_keep_the_tokens_as_secrets_and_check_the_addresses()
    {
        var settings = new RepositoriesSettings();
        Assert.Equal(PluginSettingKind.Secret, settings.Settings.Single(s => s.Key == "githubToken").Kind);
        Assert.Equal(PluginSettingKind.Secret, settings.Settings.Single(s => s.Key == "gitlabToken").Kind);

        Assert.NotNull(settings.Validate(new PluginSettingValues(settings.Settings, new Dictionary<string, string> { ["gitlabUrl"] = "gitlab.local" })));
        Assert.Null(settings.Validate(new PluginSettingValues(settings.Settings, new Dictionary<string, string> { ["gitlabUrl"] = "https://gitlab.local/" })));

        var asked = new List<string>();
        settings.Apply(new PluginSettingValues(settings.Settings,
            new Dictionary<string, string> { ["githubUrl"] = "https://git.example.com/api/v3/", ["githubRepositories"] = "octo/hello, dotnet/runtime;bad" },
            key =>
            {
                asked.Add(key);
                return Task.FromResult<string?>(key == "githubToken" ? "ghp_from_store" : null);
            }));
        try
        {
            Assert.Empty(asked); // not asked before it is needed
            var github = new GitHubFileSystemProvider();
            Assert.Equal("https://git.example.com/api/v3", github.Host.WebUrl + "/api/v3");
            Assert.Equal(["octo/hello", "dotnet/runtime"], RepositoriesSettings.GitHub.Repositories);
            Assert.Equal("ghp_from_store", await RepositoriesSettings.GitHub.TokenAsync());
            Assert.Null(await RepositoriesSettings.GitLab.TokenAsync());
            await RepositoriesSettings.GitHub.TokenAsync();
            Assert.Equal(["githubToken", "gitlabToken"], asked); // once each
            Assert.Equal("https://gitlab.com", RepositoriesSettings.GitLab.BaseUrl);
        }
        finally
        {
            settings.Apply(new PluginSettingValues(settings.Settings, null));
        }
    }

    // ---------------------------------------------------------------- the real github.com (FM_TEST_GITHUB=1)

    [Fact]
    public async Task Live_github_public_repository()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_GITHUB") != "1")
        {
            return;
        }
        using var handler = new SocketsHttpHandler();
        var provider = new GitHubFileSystemProvider(Options(GitHubApi, null), handler);
        var top = await provider.ListAsync(Hub("octocat/Hello-World"), CancellationToken.None);
        Assert.Contains(top, e => e.Name == "README");
        Assert.Contains(await provider.ListAsync(Hub("octocat/Hello-World/branches"), CancellationToken.None), e => e.Name == "master");
        var text = await ReadAllAsync(await provider.OpenReadAsync(Hub("octocat/Hello-World/README"), CancellationToken.None));
        Assert.Contains("Hello World", text, StringComparison.Ordinal);
    }
}
