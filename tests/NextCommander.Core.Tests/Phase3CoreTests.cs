using System.IO.Compression;
using System.Text;
using NextCommander.Core.Search;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class EncodingDetectorTests
{
    [Fact]
    public void Detects_boms_utf8_utf16_and_ansi()
    {
        EncodingDetector.EnsureCodePages();
        Assert.Equal(65001, EncodingDetector.Detect([0xEF, 0xBB, 0xBF, (byte)'a'], out var bom).CodePage);
        Assert.Equal(3, bom);
        Assert.Equal(1200, EncodingDetector.Detect([0xFF, 0xFE, (byte)'a', 0], out bom).CodePage);
        Assert.Equal(2, bom);
        Assert.Equal(1200, EncodingDetector.Detect(Encoding.Unicode.GetBytes("hello world, plain text"), out bom).CodePage);
        Assert.Equal(0, bom);
        Assert.Equal(1201, EncodingDetector.Detect(Encoding.BigEndianUnicode.GetBytes("hello world, plain text"), out _).CodePage);
        Assert.Equal(65001, EncodingDetector.Detect(Encoding.UTF8.GetBytes("çğışöü"), out _).CodePage);
        var turkish = Encoding.GetEncoding(1254).GetBytes("çğışöü");
        Assert.NotEqual(65001, EncodingDetector.Detect(turkish, out _).CodePage);
    }

    [Fact]
    public void Utf8_sequence_cut_by_sample_end_is_still_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("abcç");
        Assert.True(EncodingDetector.IsValidUtf8(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.False(EncodingDetector.IsValidUtf8(bytes.AsSpan(0, bytes.Length - 1), complete: true));
        Assert.False(EncodingDetector.IsValidUtf8([0xC3, 0x28]));
    }

    [Fact]
    public void Binary_and_newline_detection()
    {
        Assert.True(EncodingDetector.LooksBinary([1, 2, 0, 4, 200, 0, 0, 0, 9, 9]));
        Assert.False(EncodingDetector.LooksBinary(Encoding.Unicode.GetBytes("utf16 text here")));
        Assert.Equal("\r\n", EncodingDetector.DetectNewLine("a\r\nb\nc"));
        Assert.Equal("\n", EncodingDetector.DetectNewLine("a\nb"));
    }
}

public sealed class LineNavigatorTests
{
    private static LineNavigator Nav(string text, Encoding enc, int max = 4096)
    {
        var bytes = enc.GetBytes(text);
        return new LineNavigator(new MemorySource(bytes), enc, 0, max);
    }

    [Fact]
    public void Walks_lines_forward_and_backward()
    {
        var nav = Nav("one\ntwo\r\nthree\n\nfive", Encoding.ASCII);
        var lines = nav.ReadLines(0, 10);
        Assert.Equal(["one", "two", "three", "", "five"], lines.Select(l => l.Text));
        var last = lines[^1].Start;
        Assert.Equal(lines[3].Start, nav.PreviousLineStart(last));
        Assert.Equal(lines[1].Start, nav.LineStartOf(lines[1].Start + 2));
        Assert.Equal(0, nav.PreviousLineStart(lines[1].Start));
        Assert.Equal(lines[2].Start, nav.Move(0, 2));
        Assert.Equal(lines[1].Start, nav.Move(lines[3].Start, -2));
    }

    [Fact]
    public void Utf16_lines_and_long_line_pieces()
    {
        var nav = Nav("ab\ncd\n", Encoding.Unicode);
        Assert.Equal(["ab", "cd"], nav.ReadLines(0, 5).Select(l => l.Text));
        Assert.Equal(6, nav.NextLineStart(0));

        var longNav = Nav(new string('x', 100) + "\nend", Encoding.ASCII, max: 32);
        var pieces = longNav.ReadLines(0, 10);
        Assert.Equal(5, pieces.Count); // 32+32+32+5 ("xxxx\n") + "end"
        Assert.Equal("end", pieces[^1].Text);
        Assert.Equal(64, longNav.LineStartOf(70));
    }
}

public sealed class TextSearchTests
{
    [Fact]
    public void Matcher_options()
    {
        Assert.Equal(6, new TextMatcher("World").Find("hello world").Index);
        Assert.Equal(-1, new TextMatcher("World", caseSensitive: true).Find("hello world").Index);
        Assert.Equal(-1, new TextMatcher("wor", wholeWords: true).Find("hello world").Index);
        Assert.Equal(10, new TextMatcher("wor", wholeWords: true).Find("the world wor").Index);
        Assert.Equal((2, 3), new TextMatcher(@"\d+", regex: true).Find("ab123c"));
        Assert.Equal(9, new TextMatcher("ab").FindLast("ab ab ab ab", 10).Index);
    }

    [Fact]
    public async Task Stream_scanner_finds_matches_across_chunk_borders_with_byte_offsets()
    {
        var prefix = new string('x', StreamTextScanner.ChunkBytes - 3);
        var text = prefix + "çanta needle";
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = new MemoryStream(bytes);
        var hit = await StreamTextScanner.FindAsync(stream, Encoding.UTF8, new TextMatcher("çanta"), CancellationToken.None);
        Assert.Equal(prefix.Length, hit!.Value.Offset);
        Assert.Equal(6, hit.Value.Length);

        stream.Position = 0;
        var none = await StreamTextScanner.FindAsync(stream, Encoding.UTF8, new TextMatcher("absent"), CancellationToken.None);
        Assert.Null(none);
    }

    [Fact]
    public async Task Contains_checks_bom_and_multiple_encodings()
    {
        EncodingDetector.EnsureCodePages();
        var matcher = new TextMatcher("şeker");
        var ansi = new MemoryStream(Encoding.GetEncoding(1254).GetBytes("bir şeker"));
        Assert.True(await StreamTextScanner.ContainsAsync(ansi, matcher, [Encoding.UTF8, Encoding.GetEncoding(1254)], CancellationToken.None));
        var utf16 = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("bir şeker")]);
        Assert.True(await StreamTextScanner.ContainsAsync(utf16, matcher, [Encoding.UTF8], CancellationToken.None));
        var bytes = new MemoryStream([1, 2, 3, 0x4D, 0x5A, 9]);
        Assert.Equal(3, await StreamTextScanner.FindBytesAsync(bytes, [0x4D, 0x5A], CancellationToken.None));
    }
}

public sealed class FileSearcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public FileSearcherTests()
    {
        _dir.Write("a.txt", "hello world");
        _dir.Write("b.log", "nothing here");
        _dir.Write(Path.Combine("sub", "c.txt"), "Hello again");
        _dir.Write(Path.Combine("sub", "deep", "d.txt"), "deep");
        _dir.Write(Path.Combine("obj", "e.txt"), "hello obj");
        File.WriteAllText(Path.Combine(_dir.Root, "big.bin"), new string('z', 5000));
        File.SetLastWriteTime(Path.Combine(_dir.Root, "b.log"), new DateTime(2020, 1, 1));
    }

    public void Dispose() => _dir.Dispose();

    private async Task<List<string>> Run(SearchOptions options, IReadOnlyList<VfsEntry>? within = null)
    {
        options.SearchIn = string.IsNullOrEmpty(options.SearchIn) ? _dir.Root : options.SearchIn;
        var found = new List<VfsEntry>();
        await new FileSearcher(_registry, options).RunAsync(found.Add, CancellationToken.None, within);
        return found.Select(e => e.Path.GetRelativeTo(VfsPath.Local(_dir.Root))!).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Name_mask_with_excluded_directories_and_depth()
    {
        Assert.Equal(["a.txt", "obj/e.txt", "sub/c.txt", "sub/deep/d.txt"], await Run(new SearchOptions { NameMask = "*.txt" }));
        Assert.Equal(["a.txt", "sub/c.txt", "sub/deep/d.txt"], await Run(new SearchOptions { NameMask = "*.txt | obj\\" }));
        Assert.Equal(["a.txt", "obj/e.txt", "sub/c.txt"], await Run(new SearchOptions { NameMask = "*.txt", MaxDepth = 1 }));
        Assert.Equal(["sub"], await Run(new SearchOptions { NameMask = "*u*", Kind = SearchItemKind.Directories }));
        Assert.Equal(["sub/deep"], await Run(new SearchOptions { NameMask = "d*", Kind = SearchItemKind.Directories }));
        Assert.Equal(["obj"], await Run(new SearchOptions { NameMask = "obj\\" }));
        Assert.Equal(["a.txt", "b.log"], await Run(new SearchOptions { NameMask = "^[ab]\\.", NameIsRegex = true }));
    }

    [Fact]
    public async Task Content_size_date_and_not_containing()
    {
        Assert.Equal(["a.txt", "obj/e.txt", "sub/c.txt"], await Run(new SearchOptions { ContainingText = "hello" }));
        Assert.Equal(["a.txt", "obj/e.txt"], await Run(new SearchOptions { ContainingText = "hello", CaseSensitive = true }));
        Assert.Equal(["b.log", "big.bin", "sub/deep/d.txt"], await Run(new SearchOptions { ContainingText = "hello", NotContaining = true }));
        Assert.Equal(["big.bin"], await Run(new SearchOptions { MinSize = 1000, Kind = SearchItemKind.Files }));
        Assert.Equal(["b.log"], await Run(new SearchOptions { ModifiedBefore = new DateTime(2021, 1, 1), Kind = SearchItemKind.Files }));
    }

    [Fact]
    public async Task List_file_and_previous_results()
    {
        var list = _dir.Write("list.lst", Path.Combine(_dir.Root, "sub") + Environment.NewLine + Path.Combine(_dir.Root, "a.txt"));
        Assert.Equal(["a.txt", "sub/c.txt", "sub/deep/d.txt"], await Run(new SearchOptions { SearchIn = "@" + list, NameMask = "*.txt" }));

        var previous = new List<VfsEntry>();
        await new FileSearcher(_registry, new SearchOptions { SearchIn = _dir.Root, NameMask = "*.txt" }).RunAsync(previous.Add, CancellationToken.None);
        Assert.Equal(["a.txt", "obj/e.txt"], await Run(new SearchOptions { ContainingText = "hello", CaseSensitive = true }, previous));
    }

    [Fact]
    public async Task Office_documents_are_searched_as_text()
    {
        var docx = Path.Combine(_dir.Root, "report.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<w:document><w:body><w:p><w:r><w:t>Mer</w:t></w:r><w:r><w:t>haba &amp; dünya</w:t></w:r></w:p></w:body></w:document>");
        }
        Assert.Equal(["report.docx"], await Run(new SearchOptions { ContainingText = "Merhaba & dünya" }));
        Assert.Empty(await Run(new SearchOptions { ContainingText = "Merhaba", OfficeDocuments = false }));
    }

    [Fact]
    public async Task Duplicates_by_content_need_the_whole_file_when_the_samples_agree()
    {
        var folder = Path.Combine(_dir.Root, "big");
        Directory.CreateDirectory(folder);
        var content = new byte[100_000];
        new Random(5).NextBytes(content);
        void Write(string name, int? changed)
        {
            var copy = (byte[])content.Clone();
            if (changed is { } at)
            {
                copy[at] ^= 0xFF;
            }
            File.WriteAllBytes(Path.Combine(folder, name), copy);
        }
        Write("b1.bin", null);
        Write("b2.bin", null);
        Write("b3.bin", 20_000); // outside the start / middle / end samples: only the whole file tells
        Write("b4.bin", 50_000); // in the middle sample
        Write("b5.bin", 99_999); // in the end sample
        var all = new List<VfsEntry>();
        await new FileSearcher(_registry, new SearchOptions { SearchIn = folder, Kind = SearchItemKind.Files }).RunAsync(all.Add, CancellationToken.None);
        long read = 0;

        var groups = await DuplicateFinder.FindAsync(_registry, all, DuplicateMode.Content, CancellationToken.None, n => Interlocked.Add(ref read, n));

        Assert.Equal(["b1.bin", "b2.bin"], Assert.Single(groups).Select(e => e.Name).Order(StringComparer.Ordinal));
        // samples of the five, then only the three whose samples agree read whole
        Assert.Equal(5L * 3 * DuplicateFinder.SampleBytes + 3L * content.Length, read);
    }

    [Fact]
    public async Task Duplicates_by_content_and_name()
    {
        _dir.Write(Path.Combine("x", "same1.dat"), "identical content");
        _dir.Write(Path.Combine("y", "same2.dat"), "identical content");
        _dir.Write(Path.Combine("y", "other.dat"), "different content");
        _dir.Write(Path.Combine("y", "a.txt"), "hello world");
        var all = new List<VfsEntry>();
        await new FileSearcher(_registry, new SearchOptions { SearchIn = _dir.Root, Kind = SearchItemKind.Files }).RunAsync(all.Add, CancellationToken.None);

        var byContent = await DuplicateFinder.FindAsync(_registry, all, DuplicateMode.Content, CancellationToken.None);
        var names = byContent.Select(g => string.Join(",", g.Select(e => e.Name).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["a.txt,a.txt", "same1.dat,same2.dat"], names);

        var byName = await DuplicateFinder.FindAsync(_registry, all, DuplicateMode.Name, CancellationToken.None);
        Assert.Single(byName);
    }
}

public sealed class SearchInputParserTests
{
    [Fact]
    public void Sizes()
    {
        Assert.Null(SearchInputParser.ParseSize(" "));
        Assert.Equal(1500, SearchInputParser.ParseSize("1500"));
        Assert.Equal(10 * 1024, SearchInputParser.ParseSize("10k"));
        Assert.Equal((long)(2.5 * 1024 * 1024), SearchInputParser.ParseSize("2,5 MB"));
        Assert.Equal(1L << 30, SearchInputParser.ParseSize("1G"));
        Assert.Throws<FormatException>(() => SearchInputParser.ParseSize("abc"));
    }

    [Fact]
    public void Dates()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        Assert.Null(SearchInputParser.ParseDate(null, now));
        Assert.Equal(now.AddDays(-7), SearchInputParser.ParseDate("7g", now));
        Assert.Equal(now.AddDays(-7), SearchInputParser.ParseDate("7d", now));
        Assert.Equal(now.AddHours(-12), SearchInputParser.ParseDate("12s", now));
        Assert.Equal(now.AddMinutes(-30), SearchInputParser.ParseDate("30dk", now));
        Assert.Equal(new DateTime(2024, 5, 1), SearchInputParser.ParseDate("2024-05-01", now));
        Assert.Throws<FormatException>(() => SearchInputParser.ParseDate("yesterday-ish", now));
    }
}
