using System.Net;
using NextCommander.Core.Tools;

namespace NextCommander.Core.Tests;

public sealed class UpdateCheckerTests
{
    private sealed class FixedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData("v1.2", "1.2.0.0")]
    [InlineData("1.2.3-beta.1", "1.2.3.0")]
    [InlineData("release-2.0.1.7", "2.0.1.7")]
    [InlineData("3", "3.0.0.0")]
    public void Parses_versions(string text, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(text));

    [Fact]
    public void Reads_manifest_and_github_release()
    {
        var manifest = UpdateChecker.Parse("""{ "version": "1.4.0", "url": "https://example.com/dl", "notes": "Yenilikler" }""")!;
        Assert.Equal(new Version(1, 4, 0, 0), manifest.Version);
        Assert.Equal("https://example.com/dl", manifest.DownloadUrl);
        var github = UpdateChecker.Parse("""{ "tag_name": "v2.1.0", "html_url": "https://github.com/x/y/releases/tag/v2.1.0", "body": "notlar" }""")!;
        Assert.Equal(new Version(2, 1, 0, 0), github.Version);
        Assert.Equal("notlar", github.Notes);
        Assert.Null(UpdateChecker.Parse("""{ "name": "x" }"""));
        Assert.Null(UpdateChecker.Parse("not json"));
    }

    [Fact]
    public async Task Reports_only_newer_versions()
    {
        var address = new Uri("https://updates.example/latest.json");
        using var newer = new HttpClient(new FixedResponse(HttpStatusCode.OK, """{ "version": "1.1" }"""));
        Assert.NotNull(await UpdateChecker.CheckAsync(address, new Version(1, 0, 5), newer, CancellationToken.None));
        Assert.Null(await UpdateChecker.CheckAsync(address, new Version(1, 1), newer, CancellationToken.None));
        Assert.Null(await UpdateChecker.CheckAsync(address, new Version(1, 2, 0, 0), newer, CancellationToken.None));

        using var failing = new HttpClient(new FixedResponse(HttpStatusCode.NotFound, ""));
        await Assert.ThrowsAsync<IOException>(() => UpdateChecker.CheckAsync(address, new Version(1, 0), failing, CancellationToken.None));
        using var garbage = new HttpClient(new FixedResponse(HttpStatusCode.OK, "<html>"));
        await Assert.ThrowsAsync<IOException>(() => UpdateChecker.CheckAsync(address, new Version(1, 0), garbage, CancellationToken.None));
    }
}
