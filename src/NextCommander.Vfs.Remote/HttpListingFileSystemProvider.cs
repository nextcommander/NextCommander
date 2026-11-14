using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Web server folder listings, read only: <c>http://host/path/</c> and <c>https://</c>. The automatic index pages of
/// Apache, nginx, IIS, lighttpd, Caddy and Python's http.server are read for the links to the files and subfolders of a
/// folder, with the size and date where the page shows them. Files are copied out (downloaded); nothing is changed on
/// the server. Proxy of Configuration > Operation > Proxy.
/// </summary>
public sealed partial class HttpListingFileSystemProvider : IFileSystemProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, IReadOnlyList<VfsEntry>> _listings = new(StringComparer.Ordinal);

    public HttpListingFileSystemProvider(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? NextCommander.Core.Net.WebProxyConfig.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NextCommander");
    }

    public string Scheme => "https";

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public bool CanHandle(VfsPath path) => path.Scheme is "http" or "https";

    /// <summary>The address of a path; folders end in "/".</summary>
    public static Uri UrlOf(VfsPath path, bool folder)
    {
        var segments = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var text = "/" + string.Join('/', segments);
        if (folder && !text.EndsWith('/'))
        {
            text += "/";
        }
        return new Uri($"{path.Scheme}://{path.Authority}{text}");
    }

    private static UnauthorizedAccessException ReadOnly() => new("A web server listing is read only: copy files out of it.");

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        var url = UrlOf(directory, folder: true);
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!media.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Not a folder listing: {url}");
        }
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = ParseListing(html, response.RequestMessage?.RequestUri ?? url)
            .Select(i => new VfsEntry
            {
                Name = i.Name,
                Path = directory.Combine(i.Name),
                IsDirectory = i.IsDirectory,
                Size = i.Size ?? 0,
                LastWriteTime = i.Modified ?? DateTime.MinValue,
            })
            .ToList();
        _listings[directory.ToString()] = entries;
        return entries;
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (path.Parent is { } parent && _listings.TryGetValue(parent.ToString(), out var listed)
            && listed.FirstOrDefault(e => e.Name == path.Name) is { } known)
        {
            return known;
        }
        // not listed yet: ask the server (a folder answers its listing at the address with "/")
        using var response = await SendAsync(HttpMethod.Head, UrlOf(path, folder: false), cancellationToken, allowMissing: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        var final = response.RequestMessage?.RequestUri;
        var isFolder = final?.AbsolutePath.EndsWith('/') == true
            || response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true && path.Name.IndexOf('.', StringComparison.Ordinal) < 0;
        return new VfsEntry
        {
            Name = path.Name,
            Path = path,
            IsDirectory = isFolder,
            Size = isFolder ? 0 : response.Content.Headers.ContentLength ?? 0,
            LastWriteTime = response.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue,
        };
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, UrlOf(path, folder: false), cancellationToken, headersOnly: true).ConfigureAwait(false);
        try
        {
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, CancellationToken cancellationToken, bool allowMissing = false, bool headersOnly = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(new HttpRequestMessage(method, url),
                headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"{url.Host}: {ex.Message}", ex);
        }
        if (response.IsSuccessStatusCode || (allowMissing && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }
        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.NotFound => new FileNotFoundException($"Not found: {url}"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new UnauthorizedAccessException($"Access denied: {url}"),
            _ => new IOException($"{url.Host}: HTTP {(int)status} {status}"),
        };
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => throw ReadOnly();

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- the listing page

    /// <summary>
    /// The children of the folder at <paramref name="folder"/> named by the links of its listing page: links to the
    /// folder itself, to its parent, to sort orders ("?C=M;O=A"), to other folders or hosts are left out. The text after a
    /// link gives the date and size when the server prints them (Apache, nginx, IIS formats).
    /// </summary>
    public static IReadOnlyList<(string Name, bool IsDirectory, long? Size, DateTime? Modified)> ParseListing(string html, Uri folder)
    {
        var result = new List<(string, bool, long?, DateTime?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var basePath = folder.AbsolutePath.EndsWith('/') ? folder.AbsolutePath : folder.AbsolutePath + "/";
        var anchors = Anchor().Matches(html);
        for (var i = 0; i < anchors.Count; i++)
        {
            var href = WebUtility.HtmlDecode(anchors[i].Groups["href"].Value).Trim();
            if (href.Length == 0 || href.StartsWith('?') || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!Uri.TryCreate(new Uri(folder, basePath), href, out var target) || target.Host != folder.Host || target.Scheme != folder.Scheme)
            {
                continue;
            }
            var path = target.AbsolutePath;
            if (!path.StartsWith(basePath, StringComparison.Ordinal) || path.Length == basePath.Length)
            {
                continue; // the folder itself, its parent, another folder
            }
            var rest = path[basePath.Length..];
            var isDirectory = rest.EndsWith('/');
            rest = rest.TrimEnd('/');
            if (rest.Length == 0 || rest.Contains('/', StringComparison.Ordinal))
            {
                continue; // deeper than a child
            }
            var name = Uri.UnescapeDataString(rest);
            if (!seen.Add(name))
            {
                continue; // icon and name link to the same file
            }
            // the rest of the link's line: "  05-Jan-2024 10:22    1234" after it (nginx), a table row (Apache),
            // "Monday, January 5, 2024 10:22 AM  1234" before it (IIS)
            var line = WebUtility.HtmlDecode(Tag().Replace(LineAround(html, anchors[i].Index, anchors[i].Index + anchors[i].Length), " "));
            result.Add((name, isDirectory, isDirectory ? null : SizeIn(line), DateIn(line)));
        }
        return result;
    }

    /// <summary>The text of a link's line without the link itself: from the line's start (a line break, &lt;br&gt; or &lt;tr&gt;) to its end.</summary>
    private static string LineAround(string html, int start, int end)
    {
        var from = new[] { html.LastIndexOf('\n', start), LastIndexOf(html, "<br", start), LastIndexOf(html, "<tr", start) }.Max();
        var to = new[] { IndexOf(html, "\n", end), IndexOf(html, "<br", end), IndexOf(html, "</tr", end) }.Min();
        return html[(from < 0 ? 0 : from)..start] + " " + html[end..to];
    }

    private static int LastIndexOf(string html, string value, int before) =>
        before <= 0 ? -1 : html.LastIndexOf(value, before - 1, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(string html, string value, int after)
    {
        var index = html.IndexOf(value, after, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html.Length : index;
    }

    private static readonly string[] s_dateFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd-MMM-yyyy HH:mm:ss", "dd-MMM-yyyy HH:mm", "yyyy-MMM-dd HH:mm:ss", "yyyy-MMM-dd HH:mm",
        "dddd, MMMM d, yyyy h:mm tt", "M/d/yyyy h:mm tt",
    ];

    private static DateTime? DateIn(string text)
    {
        var match = Date().Match(text);
        if (!match.Success)
        {
            return null;
        }
        return DateTime.TryParseExact(Regex.Replace(match.Value.Trim(), @"\s+", " "), s_dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;
    }

    /// <summary>The size after the date: bytes (nginx, IIS), or 1.2K / 3.4M / 5G (Apache, lighttpd).</summary>
    private static long? SizeIn(string text)
    {
        var date = Date().Match(text);
        var tail = date.Success ? text[(date.Index + date.Length)..] : text;
        var match = Size().Match(tail);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }
        var factor = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024d,
            "M" => 1024d * 1024,
            "G" => 1024d * 1024 * 1024,
            "T" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)Math.Round(number * factor);
    }

    [GeneratedRegex("""<a\s[^>]*?href\s*=\s*["']?(?<href>[^"' >]+)["']?[^>]*>.*?</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}(:\d{2})?|\d{2}-[A-Za-z]{3}-\d{4}\s+\d{1,2}:\d{2}(:\d{2})?|\d{4}-[A-Za-z]{3}-\d{2}\s+\d{1,2}:\d{2}(:\d{2})?|[A-Za-z]+,\s+[A-Za-z]+\s+\d{1,2},\s+\d{4}\s+\d{1,2}:\d{2}\s+[AP]M|\d{1,2}/\d{1,2}/\d{4}\s+\d{1,2}:\d{2}\s+[AP]M")]
    private static partial Regex Date();

    [GeneratedRegex(@"(?<![\w.])(\d+(?:\.\d+)?)\s*([KMGT])?(?:i?B)?(?![\w.:/-])", RegexOptions.IgnoreCase)]
    private static partial Regex Size();
}
