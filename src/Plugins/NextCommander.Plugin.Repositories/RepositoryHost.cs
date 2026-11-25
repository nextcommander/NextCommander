using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NextCommander.Core.Net;

namespace NextCommander.Plugin.Repositories;

/// <summary>A repository as a host lists it.</summary>
/// <param name="Owner">The owner (GitHub) or namespace (GitLab: "group/subgroup").</param>
/// <param name="Name">The repository's own name (its path, not its title).</param>
/// <param name="IsGroup">A GitLab subgroup in a group's listing (a folder of more projects), not a repository.</param>
internal sealed record RepositoryInfo(string Owner, string Name, string? DefaultBranch, DateTime Updated, bool IsGroup = false)
{
    public string FullName => Owner + "/" + Name;
}

/// <summary>A branch or tag.</summary>
internal sealed record RefInfo(string Name, DateTime Time);

/// <summary>A commit a branch or tag stands for: the files' time and (GitHub) its tree.</summary>
internal sealed record CommitInfo(string Sha, DateTime Time, string? TreeSha);

/// <summary>An entry of a commit's tree; <see cref="Path"/> is relative to the repository ("src/a.cs").</summary>
internal sealed record TreeItem(string Path, bool IsDirectory, bool IsLink, long? Size, string Sha)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>A commit's tree; <see cref="Complete"/> false when the host cut it short (then folders are read one by one).</summary>
internal sealed record TreeListing(IReadOnlyList<TreeItem> Items, bool Complete);

/// <summary>A file of a release; <see cref="Size"/> null when the host does not say.</summary>
internal sealed record AssetInfo(string Name, long? Size, DateTime Time, string Url);

internal sealed record ReleaseInfo(string Tag, string Title, DateTime Time, IReadOnlyList<AssetInfo> Assets);

/// <summary>The rate limit of a host's API is reached (until <see cref="ResetsAt"/>, local time, when it says).</summary>
internal sealed class RateLimitException(string message, DateTime? resetsAt) : IOException(message)
{
    public DateTime? ResetsAt { get; } = resetsAt;
}

/// <summary>
/// A Git hosting service's REST API (GitHub, GitLab): the requests and the answers' JSON, with the errors said so the
/// user knows what to do (the rate limit and when it resets, a token needed for a private repository, no network).
/// Every request carries the User-Agent "NextCommander" and, when one is set, the token.
/// </summary>
internal abstract class RepositoryHost(HttpClient http, RepositoryOptions options)
{
    protected HttpClient Http { get; } = http;

    protected RepositoryOptions Options { get; } = options;

    /// <summary>"GitHub" / "GitLab": in the messages.</summary>
    public abstract string Title { get; }

    /// <summary>The web address of the host's pages ("https://github.com"): for "Open in browser" and cloning.</summary>
    public abstract string WebUrl { get; }

    /// <summary>The address of a page: the repository, a folder or file of a ref, a release.</summary>
    public abstract string PageUrl(string owner, string repository, string? kind = null, string? reference = null, string? path = null, bool folder = false);

    public string CloneUrl(string owner, string repository) => $"{WebUrl}/{owner}/{repository}.git";

    /// <summary>The user name git sends with the token over https ("x-access-token", "oauth2").</summary>
    public abstract string CloneUser { get; }

    public Task<string?> TokenAsync() => Options.TokenAsync();

    // ---------------------------------------------------------------- what each host answers

    /// <summary>The signed-in user's repositories (none without a token).</summary>
    public abstract Task<IReadOnlyList<RepositoryInfo>> MyRepositoriesAsync(CancellationToken cancellationToken);

    /// <summary>The repositories of an owner (a user, an organization, a GitLab group with its subgroups).</summary>
    public abstract Task<IReadOnlyList<RepositoryInfo>> OwnerRepositoriesAsync(string owner, CancellationToken cancellationToken);

    public abstract Task<RepositoryInfo> RepositoryAsync(string owner, string name, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<RefInfo>> BranchesAsync(string owner, string name, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<RefInfo>> TagsAsync(string owner, string name, CancellationToken cancellationToken);

    /// <summary>The commit of a branch (<paramref name="tag"/> false) or tag.</summary>
    public abstract Task<CommitInfo> CommitAsync(string owner, string name, string reference, bool tag, CancellationToken cancellationToken);

    /// <summary>A commit's whole tree in one go, as far as the host gives it.</summary>
    public abstract Task<TreeListing> TreeAsync(string owner, string name, CommitInfo commit, CancellationToken cancellationToken);

    /// <summary>One folder of a commit's tree (<paramref name="path"/> "" = the root; <paramref name="sha"/> the folder's tree, when known).</summary>
    public abstract Task<IReadOnlyList<TreeItem>> FolderAsync(string owner, string name, CommitInfo commit, string path, string? sha, CancellationToken cancellationToken);

    /// <summary>A file's content as it is stored (a Git LFS file: its pointer).</summary>
    public abstract Task<Stream> OpenFileAsync(string owner, string name, CommitInfo commit, TreeItem file, CancellationToken cancellationToken);

    /// <summary>A file's size when the tree did not say it (GitLab); null when it cannot be learned.</summary>
    public virtual Task<long?> FileSizeAsync(string owner, string name, CommitInfo commit, TreeItem file, CancellationToken cancellationToken) =>
        Task.FromResult(file.Size);

    public abstract Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(string owner, string name, CancellationToken cancellationToken);

    public abstract Task<Stream> OpenAssetAsync(AssetInfo asset, CancellationToken cancellationToken);

    // ---------------------------------------------------------------- requests

    /// <summary>The most pages of a list read (100 entries each).</summary>
    internal const int MaxPages = 30;

    /// <summary>Adds the host's sign-in to a request (GitHub: Bearer; GitLab: PRIVATE-TOKEN).</summary>
    protected abstract void Authorize(HttpRequestMessage request, string token);

    /// <summary>A request to the API (a path below <see cref="RepositoryOptions.BaseUrl"/>, or a full address).</summary>
    protected HttpRequestMessage Request(HttpMethod method, string pathOrUrl, string? accept = null)
    {
        var url = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : ApiBase + pathOrUrl;
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("NextCommander");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept ?? "application/json"));
        return request;
    }

    /// <summary>The address API paths are put after ("https://gitlab.com/api/v4").</summary>
    protected virtual string ApiBase => Options.BaseUrl;

    /// <summary>
    /// Sends a request with the token (when one is set and the address is the host's own) and gives the answer when it
    /// succeeded; otherwise throws what the failure means. <paramref name="what"/> names the thing asked for in a
    /// "not found" message.
    /// </summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        var token = await TokenAsync().ConfigureAwait(false);
        if (token is not null && request.RequestUri is { } uri && IsOwnHost(uri))
        {
            Authorize(request, token);
        }
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, "{0} cannot be reached ({1}): check the network connection and the proxy settings.",
                Title, request.RequestUri?.Host), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, "{0} does not answer ({1}): try again later.", Title, request.RequestUri?.Host), ex);
        }
        finally
        {
            request.Dispose();
        }
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        using (response)
        {
            var body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            throw ErrorOf(response, body, what, token is not null);
        }
    }

    private bool IsOwnHost(Uri uri) =>
        Uri.TryCreate(Options.BaseUrl, UriKind.Absolute, out var own) && string.Equals(own.Host, uri.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The message of a failed answer as the matching exception.</summary>
    internal Exception ErrorOf(HttpResponseMessage response, string body, string what, bool signedIn)
    {
        var status = (int)response.StatusCode;
        var at = RateLimitReset(response);
        if (at is not null || status == 429)
        {
            var time = (at ?? DateTime.Now.AddMinutes(1)).ToString("t", CultureInfo.CurrentCulture);
            return signedIn
                ? new RateLimitException(string.Format(CultureInfo.CurrentCulture, "The {0} API rate limit is reached; it resets at {1}.", Title, time), at)
                : new RateLimitException(string.Format(CultureInfo.CurrentCulture,
                    "The {0} API rate limit is reached; it resets at {1}. An access token raises the limit (Configuration > Plugins > GitHub / GitLab repositories).", Title, time), at);
        }
        var message = MessageOf(body);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new UnauthorizedAccessException(signedIn
                    ? string.Format(CultureInfo.CurrentCulture, "{0} refused the access token: it is wrong or has expired. Change it in Configuration > Plugins > GitHub / GitLab repositories.", Title)
                    : string.Format(CultureInfo.CurrentCulture, "{0} wants a sign-in for this: set an access token in Configuration > Plugins > GitHub / GitLab repositories.", Title));
            case HttpStatusCode.Forbidden:
                return new UnauthorizedAccessException(string.Format(CultureInfo.CurrentCulture, "{0} refused access: {1}", Title, message ?? what));
            case HttpStatusCode.NotFound:
                // a private repository answers "not found" to whoever may not see it
                return new DirectoryNotFoundException(signedIn
                    ? string.Format(CultureInfo.CurrentCulture, "Not found: {0} (or the access token has no access to it).", what)
                    : string.Format(CultureInfo.CurrentCulture, "Not found: {0}. A private repository needs an access token: set one in Configuration > Plugins > GitHub / GitLab repositories.", what));
            default:
                return new IOException(string.Format(CultureInfo.CurrentCulture, "{0} answered with an error (HTTP {1}): {2}", Title, status, message ?? response.ReasonPhrase ?? string.Empty));
        }
    }

    /// <summary>When the rate limit resets (local time), if the answer says the limit is reached; else null.</summary>
    protected abstract DateTime? RateLimitReset(HttpResponseMessage response);

    /// <summary>The first value of a header, or null.</summary>
    protected static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A header with seconds since 1970 (UTC) as local time.</summary>
    protected static DateTime? EpochHeader(HttpResponseMessage response, string name) =>
        long.TryParse(Header(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
            : null;

    /// <summary>A Retry-After header as the local time it means.</summary>
    protected static DateTime? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } retry
            ? retry.Date?.LocalDateTime ?? (retry.Delta is { } delta ? DateTime.Now + delta : null)
            : null;

    /// <summary>The "message" (GitHub, GitLab) or "error" of an error answer.</summary>
    internal static string? MessageOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in (string[])["message", "error_description", "error"])
                {
                    if (json.RootElement.TryGetProperty(name, out var value))
                    {
                        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>An answer's JSON.</summary>
    protected async Task<JsonDocument> GetJsonAsync(string path, string what, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, path), what, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The items of a list over its pages (the "next" address of the Link header, which both hosts give), at most
    /// <paramref name="maxPages"/> pages; <c>Complete</c> false when more were left.
    /// </summary>
    protected async Task<(List<T> Items, bool Complete)> GetPagesAsync<T>(string path, string what, Func<JsonElement, T?> read, CancellationToken cancellationToken,
        int maxPages = MaxPages)
        where T : class
    {
        var items = new List<T>();
        string? next = path;
        for (var page = 0; next is not null; page++)
        {
            if (page == maxPages)
            {
                return (items, false);
            }
            using var response = await SendAsync(Request(HttpMethod.Get, next), what, cancellationToken).ConfigureAwait(false);
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in json.RootElement.EnumerateArray())
                    {
                        if (read(element) is { } item)
                        {
                            items.Add(item);
                        }
                    }
                }
            }
            next = NextPage(response);
        }
        return (items, true);
    }

    /// <summary>The rel="next" address of a Link header.</summary>
    internal static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }
        foreach (var part in values.SelectMany(v => v.Split(',')))
        {
            var pieces = part.Split(';');
            if (pieces.Length > 1 && pieces.Skip(1).Any(p => p.Trim().Replace(" ", string.Empty, StringComparison.Ordinal) == "rel=\"next\""))
            {
                return pieces[0].Trim().TrimStart('<').TrimEnd('>');
            }
        }
        return null;
    }

    /// <summary>A file's content: the answer's stream, which closes the answer with it.</summary>
    protected async Task<Stream> OpenStreamAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- reading JSON

    protected static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    protected static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    protected static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>An ISO 8601 time ("2026-11-20T10:15:30Z") as local time; MinValue when missing.</summary>
    protected static DateTime Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.LocalDateTime : DateTime.MinValue;

    /// <summary>A path's parts escaped for an address, the slashes kept ("feature/a b" → "feature/a%20b").</summary>
    protected static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>The stream of an answer, which closes the answer when it is closed.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>An HttpClient with the program's proxy (Configuration > Operation > Proxy).</summary>
    internal static HttpClient CreateClient() => new(WebProxyConfig.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
}
