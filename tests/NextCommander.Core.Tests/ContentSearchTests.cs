using System.Text;
using NextCommander.Core.Search;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Find files content search: many files read at once, the line of the first match, binary files skipped.</summary>
public sealed class ContentSearchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private async Task<(List<string> Found, FileSearcher Searcher)> SearchAsync(SearchOptions options)
    {
        options.SearchIn = _dir.Root;
        var searcher = new FileSearcher(_registry, options);
        var found = new List<string>();
        await searcher.RunAsync(e => found.Add(e.Name), CancellationToken.None); // a plain list: calls never overlap
        return (found.Order(StringComparer.Ordinal).ToList(), searcher);
    }

    private ContentHit HitOf(FileSearcher searcher, string name) => searcher.Hits[VfsPath.Local(Path.Combine(_dir.Root, name))];

    [Fact]
    public async Task Finds_every_file_and_the_line_of_the_first_match()
    {
        EncodingDetector.EnsureCodePages();
        for (var i = 0; i < 60; i++)
        {
            _dir.Write($"many/f{i:D2}.txt", i % 3 == 0 ? $"line one\nline two\n  the needle {i} is here\nlast" : "nothing");
        }
        File.WriteAllText(Path.Combine(_dir.Root, "utf16.txt"), "first\r\nsecond NEEDLE line\r\n", Encoding.Unicode); // with a BOM
        File.WriteAllBytes(Path.Combine(_dir.Root, "ansi.txt"), Encoding.GetEncoding(1254).GetBytes("başlık\nçağrı needle şurada\n"));
        _dir.Write("long.txt", new string('x', 500) + " needle " + new string('y', 500));

        var (found, searcher) = await SearchAsync(new SearchOptions { ContainingText = "needle" });
        Assert.Equal(20 + 3, found.Count);
        Assert.Equal(20 + 3, searcher.Progress.Found);
        Assert.Equal("3: the needle 0 is here", HitOf(searcher, "many/f00.txt".Replace('/', Path.DirectorySeparatorChar)).Display);
        Assert.Equal("2: second NEEDLE line", HitOf(searcher, "utf16.txt").Display);
        Assert.Equal("2: çağrı needle şurada", HitOf(searcher, "ansi.txt").Display);
        var longLine = HitOf(searcher, "long.txt");
        Assert.Equal(1, longLine.Line);
        Assert.Contains("needle", longLine.Text, StringComparison.Ordinal);
        Assert.True(longLine.Text.Length <= StreamTextScanner.PreviewLength + 2);
        Assert.StartsWith("…", longLine.Text, StringComparison.Ordinal);

        // "not containing" lists the other files, without lines
        var (others, notSearcher) = await SearchAsync(new SearchOptions { ContainingText = "needle", NotContaining = true, NameMask = "f*.txt" });
        Assert.Equal(40, others.Count);
        Assert.Empty(notSearcher.Hits);
    }

    [Fact]
    public async Task Binary_files_can_be_skipped()
    {
        File.WriteAllBytes(Path.Combine(_dir.Root, "program.bin"), [0x4D, 0x5A, 0, 0, .. "needle"u8, 0, 1, 2]);
        File.WriteAllText(Path.Combine(_dir.Root, "text.txt"), "a needle");
        File.WriteAllBytes(Path.Combine(_dir.Root, "utf16-no-bom.txt"), Encoding.Unicode.GetBytes("a plain needle in UTF-16 text"));

        Assert.Equal(["program.bin", "text.txt", "utf16-no-bom.txt"], (await SearchAsync(new SearchOptions { ContainingText = "needle" })).Found);
        Assert.Equal(["text.txt", "utf16-no-bom.txt"], (await SearchAsync(new SearchOptions { ContainingText = "needle", SkipBinary = true })).Found);
        // a skipped file is not listed as "not containing" either
        Assert.Empty((await SearchAsync(new SearchOptions { ContainingText = "other", NotContaining = true, SkipBinary = true, NameMask = "*.bin" })).Found);
    }

    [Fact]
    public async Task Stopping_ends_the_search()
    {
        for (var i = 0; i < 200; i++)
        {
            _dir.Write($"f{i}.txt", "needle");
        }
        using var cts = new CancellationTokenSource();
        var searcher = new FileSearcher(_registry, new SearchOptions { SearchIn = _dir.Root, ContainingText = "needle" });
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searcher.RunAsync(_ =>
        {
            if (++count == 5)
            {
                cts.Cancel();
            }
        }, cts.Token));
        Assert.InRange(count, 5, 200);
    }

    [Theory]
    [InlineData("short line", "", "short line")]
    [InlineData("\t  indented ", "match", "indented match")]
    public void Lines_are_tidied_for_the_list(string before, string from, string expected) =>
        Assert.Equal(expected, StreamTextScanner.Shorten(before, from));

    [Fact]
    public void Office_text_hits_have_lines_too()
    {
        var text = "Title\nIntro paragraph\nThe needle is in the third one";
        var hit = ContentHit.InText(text, text.IndexOf("needle", StringComparison.Ordinal));
        Assert.Equal("3: The needle is in the third one", hit.Display);
        Assert.Equal("1: needle first", ContentHit.InText("needle first", 0).Display);
    }
}
