using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>
/// Downloads http(s) URLs into a folder (paste URLs with Ctrl+V). The file name comes from Content-Disposition or
/// the URL; existing names get " (2)". Data goes to "name.part" first; a local .part left by an interrupted download is
/// resumed with a Range request when the server supports it.
/// </summary>
public sealed partial class HttpDownloadJob : FileJob
{
    private static HttpClient? s_client;
    private static int s_clientProxyVersion;

    /// <summary>The download client; made again when the proxy setting changed.</summary>
    private static HttpClient SharedClient()
    {
        var version = Net.WebProxyConfig.Version;
        if (s_client is null || s_clientProxyVersion != version)
        {
            var handler = Net.WebProxyConfig.CreateHandler();
            handler.AutomaticDecompression = DecompressionMethods.All;
            handler.AllowAutoRedirect = true;
            handler.MaxAutomaticRedirections = 10;
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NextCommander/1.0");
            s_client = client; // a download still running keeps the previous one
            s_clientProxyVersion = version;
        }
        return s_client;
    }

    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<Uri> _urls;
    private readonly VfsPath _target;
    private readonly HttpClient _client;

    public HttpDownloadJob(ProviderRegistry registry, IReadOnlyList<Uri> urls, VfsPath targetDirectory, HttpClient? client = null)
        : base("Download")
    {
        _registry = registry;
        _urls = urls;
        _target = targetDirectory;
        _client = client ?? SharedClient();
    }

    /// <summary>Give downloads the "from the internet" mark (Zone.Identifier / quarantine).</summary>
    public bool MarkDownloads { get; init; } = true;

    /// <summary>Downloaded files (full target paths).</summary>
    public IReadOnlyList<VfsPath> Downloaded => _downloaded;

    private readonly List<VfsPath> _downloaded = [];

    /// <summary>http/https URLs in a text (one per line or separated by spaces); empty if the text holds anything else.</summary>
    public static IReadOnlyList<Uri> ParseUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var result = new List<Uri>();
        foreach (var token in text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim('"', '<', '>');
            if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return []; // not a pure URL list: treat as normal text
            }
            result.Add(uri);
        }
        return result;
    }

    /// <summary>File name for a download: Content-Disposition, else the last URL segment, else the host.</summary>
    public static string FileNameFor(Uri url, ContentDispositionHeaderValue? disposition)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        }
        name = name.Trim().Trim('"');
        name = InvalidChars().Replace(name, "_").Trim(' ', '.');
        return name.Length == 0 ? url.Host + ".html" : name;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")]
    private static partial Regex InvalidChars();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var provider = _registry.Resolve(_target);
        Progress.SetTotals(0, _urls.Count);
        Progress.Start();
        foreach (var url in _urls)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (StopAfterCurrentFile)
            {
                break;
            }
            await WithErrorHandlingAsync(url.ToString(), () => DownloadAsync(provider, url, ct), ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadAsync(IFileSystemProvider provider, Uri url, CancellationToken ct)
    {
        try
        {
            await DownloadCoreAsync(provider, url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"{url}: {ex.Message}", ex);
        }
    }

    private async Task DownloadCoreAsync(IFileSystemProvider provider, Uri url, CancellationToken ct)
    {
        // Name first (HEAD may be refused: then the GET response decides).
        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Ensure(response, url);
        var name = FileNameFor(response.RequestMessage?.RequestUri ?? url, response.Content.Headers.ContentDisposition);
        async Task<bool> Exists(string n) => await provider.GetEntryAsync(_target.Combine(n), ct).ConfigureAwait(false) is not null;
        var finalName = await Exists(name).ConfigureAwait(false)
            ? await ConflictNaming.NextFreeNameAsync(name, false, Exists).ConfigureAwait(false)
            : name;
        var final = _target.Combine(finalName);
        var part = _target.Combine(finalName + ".part");

        // Resume a local .part when the server accepts ranges.
        long offset = 0;
        HttpResponseMessage? ranged = null;
        if (part.IsLocal && File.Exists(part.Path) && response.Headers.AcceptRanges.Contains("bytes"))
        {
            offset = new FileInfo(part.Path).Length;
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(offset, null);
            ranged = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (ranged.StatusCode != HttpStatusCode.PartialContent)
            {
                ranged.Dispose();
                ranged = null;
                offset = 0;
            }
        }
        using (ranged)
        {
            var active = ranged ?? response;
            var length = active.Content.Headers.ContentLength is { } l ? l + offset : -1;
            Progress.AddCounted(Math.Max(0, length), 0);
            Progress.BeginItem(finalName, Math.Max(0, length));
            Progress.AddBytes(offset);
            await using (var input = await active.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = offset > 0
                ? new FileStream(part.Path, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true)
                : await provider.OpenWriteAsync(part, overwrite: true, ct).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 17];
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    Progress.AddBytes(n);
                    await ThrottleAsync(n, ct).ConfigureAwait(false);
                }
            }
            if (length >= 0 && part.IsLocal && new FileInfo(part.Path).Length != length)
            {
                throw new IOException($"{finalName}: connection lost ({new FileInfo(part.Path).Length:N0} / {length:N0} bytes). Retrying resumes where it left off.");
            }
        }
        await provider.MoveAsync(part, final, overwrite: false, ct).ConfigureAwait(false);
        if (final.IsLocal && MarkDownloads)
        {
            Tools.MarkOfTheWeb.MarkDownloaded(final.Path, url);
        }
        if (response.Content.Headers.LastModified is { } modified)
        {
            try
            {
                await provider.SetTimesAsync(final, null, modified.LocalDateTime, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // keep the download time
            }
        }
        lock (_downloaded)
        {
            _downloaded.Add(final);
        }
        Progress.CompleteFile();
    }

    private static void Ensure(HttpResponseMessage response, Uri url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"{url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }
}
