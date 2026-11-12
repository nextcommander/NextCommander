using System.Net;
using System.Net.Http.Headers;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class HttpDownloadTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _tmp.Dispose();

    /// <summary>Serves fixed content per path; honours Range requests.</summary>
    private sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ranges.Add(request.Headers.Range);
            var path = request.RequestUri!.AbsolutePath;
            if (!files.TryGetValue(path, out var data))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            response.Headers.AcceptRanges.Add("bytes");
            if (request.Headers.Range?.Ranges.First().From is { } from)
            {
                response.StatusCode = HttpStatusCode.PartialContent;
                data = data[(int)from..];
            }
            response.Content = new ByteArrayContent(data);
            if (path == "/download")
            {
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"report.pdf\"" };
            }
            response.Content.Headers.LastModified = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void Parses_url_lists_only()
    {
        Assert.Equal(2, HttpDownloadJob.ParseUrls("https://a.com/x.zip\r\nhttp://b.org/y").Count);
        Assert.Empty(HttpDownloadJob.ParseUrls("see https://a.com/x.zip"));
        Assert.Empty(HttpDownloadJob.ParseUrls("ftp://a.com/x"));
        Assert.Empty(HttpDownloadJob.ParseUrls(""));
        Assert.Equal("a b.txt", HttpDownloadJob.FileNameFor(new Uri("https://h/dir/a%20b.txt?x=1"), null));
        Assert.Equal("h.html", HttpDownloadJob.FileNameFor(new Uri("https://h/"), null));
        Assert.Equal("x_y.bin", HttpDownloadJob.FileNameFor(new Uri("https://h/x"), new ContentDispositionHeaderValue("attachment") { FileName = "x:y.bin" }));
    }

    [Fact]
    public async Task Downloads_names_and_resumes()
    {
        var big = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var server = new FakeServer(new() { ["/files/data.bin"] = big, ["/download"] = [1, 2, 3] });
        using var client = new HttpClient(server);
        _tmp.Write("data.bin", "existing");
        // An interrupted earlier download of "data (2).bin" left the first 50,000 bytes.
        File.WriteAllBytes(Path.Combine(_tmp.Root, "data (2).bin.part"), big[..50_000]);

        var job = new HttpDownloadJob(_registry, [new Uri("http://x/files/data.bin"), new Uri("http://x/download"), new Uri("http://x/missing")],
            _tmp.VPath(), client);
        await job.RunAsync();

        Assert.True(job.Errors.Count == 1, string.Join(" / ", job.Errors.Select(e => e.Message)));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_tmp.Root, "data (2).bin")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_tmp.Root, "data.bin")));
        Assert.False(File.Exists(Path.Combine(_tmp.Root, "data (2).bin.part")));
        Assert.Contains(server.Ranges, r => r?.Ranges.First().From == 50_000);
        Assert.True(File.Exists(Path.Combine(_tmp.Root, "report.pdf")), string.Join(", ", Directory.GetFiles(_tmp.Root).Select(Path.GetFileName)));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_tmp.Root, "report.pdf")));
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_tmp.Root, "report.pdf")));
        Assert.Equal(2, job.Downloaded.Count);
        if (NextCommander.Core.Tools.MarkOfTheWeb.IsSupported)
        {
            Assert.Contains("ZoneId=3", NextCommander.Core.Tools.MarkOfTheWeb.Read(Path.Combine(_tmp.Root, "report.pdf")), StringComparison.Ordinal);
        }
        Assert.Contains(job.Errors, e => e.Message.Contains("404", StringComparison.Ordinal));
    }
}
