using NextCommander.Core.Search;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>NextCommander's own file index: build, queries, folder sizes, saving and catching up, live changes.</summary>
public sealed class LocalFileIndexTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Root => Path.Combine(_dir.Root, "kök");

    private string IndexFile => Path.Combine(_dir.Root, "index", "files.idx");

    private void MakeTree()
    {
        _dir.Write(Path.Combine("kök", "rapor.txt"), new string('r', 100));
        _dir.Write(Path.Combine("kök", "Belgeler", "Rapor 2024.TXT"), new string('b', 1000));
        _dir.Write(Path.Combine("kök", "Belgeler", "ğüşİöç.txt"), "x");
        _dir.Write(Path.Combine("kök", "Belgeler", "Alt", "derin.bin"), new string('d', 5000));
        _dir.Write(Path.Combine("kök", "proje", "node_modules", "paket", "index.js"), "js"); // excluded by name
        _dir.Write(Path.Combine("kök", "proje", "kod.cs"), "cs");
    }

    private LocalFileIndex Create() => new([Root], IndexFile, ["node_modules"]) { WatchDelay = TimeSpan.FromMilliseconds(50) };

    private static List<string> Names(IEnumerable<string> paths) => paths.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()!;

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Builds_queries_and_sums_folder_sizes()
    {
        MakeTree();
        using var index = Create();
        Assert.False(index.IsReady);
        await index.StartAsync();
        Assert.True(index.IsReady);
        Assert.Equal(5, index.Files); // rapor, Rapor 2024, ğüşİöç, derin, kod (node_modules left out)
        Assert.Equal(["Rapor 2024.TXT", "rapor.txt", "ğüşİöç.txt"], Names(index.Query(Root, ["*.txt"])));
        Assert.Equal(["Rapor 2024.TXT", "rapor.txt"], Names(index.Query(Root, ["*rapor*"])));
        Assert.Equal(["derin.bin", "kod.cs"], Names(index.Query(Root, ["*.bin", "*.cs"])));
        Assert.Equal(["ğüşİöç.txt"], Names(index.Query(Root, ["*ĞÜŞ*"])));
        Assert.Equal(["Rapor 2024.TXT", "derin.bin", "ğüşİöç.txt", "Alt"], index.Query(Path.Combine(Root, "Belgeler"), []).Select(Path.GetFileName).OrderBy(n => n!.Contains('.') ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(index.Query(Root, []), p => p.Contains("node_modules", StringComparison.Ordinal));
        Assert.Equal(6000 + 1, index.FolderSize(Path.Combine(Root, "Belgeler")));
        Assert.Equal(6000 + 1 + 100 + 2, index.FolderSize(Root));
        Assert.Throws<IOException>(() => index.Query(_dir.Root, [])); // not under a root: the searcher walks instead
        Assert.Null(index.FolderSize(_dir.Root));
    }

    [Fact]
    public async Task Saved_index_catches_up_with_changes_made_while_closed()
    {
        MakeTree();
        using (var first = Create())
        {
            await first.StartAsync();
        }
        Assert.True(File.Exists(IndexFile));

        // while the program is closed: a new file, a folder removed, a file grown
        _dir.Write(Path.Combine("kök", "Belgeler", "yeni.txt"), "yeni");
        Directory.Delete(Path.Combine(Root, "Belgeler", "Alt"), recursive: true);
        Directory.CreateDirectory(Path.Combine(Root, "Yeni klasör", "iç"));
        _dir.Write(Path.Combine("kök", "Yeni klasör", "iç", "a.txt"), "a");

        using var second = Create();
        await second.StartAsync();
        var all = Names(second.Query(Root, []));
        Assert.Contains("yeni.txt", all);
        Assert.Contains("a.txt", all);
        Assert.DoesNotContain("derin.bin", all);
        Assert.DoesNotContain("Alt", all);
        Assert.Equal(1000 + 1 + 4, second.FolderSize(Path.Combine(Root, "Belgeler")));
    }

    /// <summary>While closed, a file grows but its folder keeps its time (only the contents changed).</summary>
    private void GrowFileKeepingFolderTime()
    {
        var folder = Path.Combine(Root, "Belgeler");
        var time = Directory.GetLastWriteTimeUtc(folder);
        File.AppendAllText(Path.Combine(folder, "Rapor 2024.TXT"), new string('+', 500));
        Directory.SetLastWriteTimeUtc(folder, time);
    }

    [Fact]
    public async Task Folder_times_alone_miss_contents_changed_while_closed()
    {
        MakeTree();
        using (var first = Create())
        {
            await first.StartAsync();
        }
        GrowFileKeepingFolderTime();
        using var second = Create();
        await second.StartAsync();
        Assert.Equal("times", second.StartMethods[Root]);
        Assert.Equal(1000 + 1 + 5000, second.FolderSize(Path.Combine(Root, "Belgeler"))); // the old size
    }

    [Fact]
    public async Task Full_check_reads_every_folder_when_due()
    {
        MakeTree();
        using (var first = Create())
        {
            first.FullCheckInterval = TimeSpan.FromDays(1);
            await first.StartAsync();
            Assert.NotNull(first.LastFullCheck); // the first build reads everything
        }
        GrowFileKeepingFolderTime();
        using (var notDue = Create())
        {
            notDue.FullCheckInterval = TimeSpan.FromDays(1);
            await notDue.StartAsync();
            Assert.Equal("times", notDue.StartMethods[Root]);
        }
        using var due = Create();
        due.FullCheckInterval = TimeSpan.FromMilliseconds(1);
        await due.StartAsync();
        Assert.Equal("full", due.StartMethods[Root]);
        Assert.Equal(1500 + 1 + 5000, due.FolderSize(Path.Combine(Root, "Belgeler")));
    }

    [Fact]
    public async Task Change_journal_finds_contents_changed_while_closed()
    {
        if (!OperatingSystem.IsWindows() || UsnJournal.VolumeOf(Root) is not { } volume || UsnJournal.Current(volume) is null)
        {
            return; // no NTFS journal here
        }
        MakeTree();
        using (var first = Create())
        {
            first.UseChangeJournal = true;
            await first.StartAsync();
        }
        Assert.True(File.Exists(IndexFile + ".state"));
        GrowFileKeepingFolderTime();
        _dir.Write(Path.Combine("kök", "proje", "yeni.cs"), "y");
        Directory.Delete(Path.Combine(Root, "Belgeler", "Alt"), recursive: true);

        using var second = Create();
        second.UseChangeJournal = true;
        await second.StartAsync();
        Assert.Equal("journal", second.StartMethods[Root]);
        Assert.Equal(1500 + 1, second.FolderSize(Path.Combine(Root, "Belgeler")));
        var all = Names(second.Query(Root, []));
        Assert.Contains("yeni.cs", all);
        Assert.DoesNotContain("derin.bin", all);

        // a journal place that is not valid any more (another journal): the folder times are compared
        File.WriteAllText(IndexFile + ".state", """{"Journals":{"X":[1,2]}}""".Replace("X", volume.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
        using var third = Create();
        third.UseChangeJournal = true;
        await third.StartAsync();
        Assert.Equal("times", third.StartMethods[Root]);
    }

    [Fact]
    public async Task Follows_changes_while_running()
    {
        MakeTree();
        using var index = Create();
        await index.StartAsync();

        _dir.Write(Path.Combine("kök", "Belgeler", "canlı.txt"), "canlı");
        await WaitAsync(() => Names(index.Query(Root, ["*canlı*"])).Count == 1);

        Directory.CreateDirectory(Path.Combine(Root, "Belgeler", "Yeni", "Daha"));
        _dir.Write(Path.Combine("kök", "Belgeler", "Yeni", "Daha", "iç.txt"), "i");
        await WaitAsync(() => index.Query(Root, ["iç.txt"]).Count == 1);

        Directory.Move(Path.Combine(Root, "Belgeler", "Yeni"), Path.Combine(Root, "Belgeler", "Taşındı"));
        await WaitAsync(() => index.Query(Root, ["iç.txt"]).SingleOrDefault()?.Contains("Taşındı", StringComparison.Ordinal) == true);

        File.Delete(Path.Combine(Root, "Belgeler", "canlı.txt"));
        await WaitAsync(() => index.Query(Root, ["*canlı*"]).Count == 0);
    }

    [Fact]
    public async Task File_search_uses_it_and_finds_what_a_walk_finds()
    {
        MakeTree();
        using var index = Create();
        await index.StartAsync();
        var registry = new ProviderRegistry([new LocalFileSystemProvider()]);
        FileIndexes.Override = index;
        try
        {
            foreach (var mask in new[] { "*.txt", "rapor", "*.bin *.cs", "*" })
            {
                async Task<List<string>> Run(bool useIndex)
                {
                    var searcher = new FileSearcher(registry, new SearchOptions { SearchIn = Root, NameMask = mask, UseIndex = useIndex, Kind = SearchItemKind.Both });
                    var found = new List<string>();
                    await searcher.RunAsync(e => { lock (found) { found.Add(e.Path.Path); } }, CancellationToken.None);
                    if (useIndex)
                    {
                        Assert.Equal(index.Name, searcher.Progress.IndexUsed);
                    }
                    return found.Where(p => !p.Contains("node_modules", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
                }
                Assert.Equal(await Run(false), await Run(true));
            }
        }
        finally
        {
            FileIndexes.Override = null;
        }
    }

    [Theory]
    [InlineData("*.pdf", "Rapor.PDF", true)]
    [InlineData("*.pdf", "rapor.pdf.bak", false)]
    [InlineData("rapor*", "Rapor 2024.txt", true)]
    [InlineData("*rapor*", "yillik-RAPOR.docx", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXcYb", false)]
    [InlineData("ab*ba", "aba", false)]
    [InlineData("kod.cs", "KOD.CS", true)]
    [InlineData("r?por.txt", "rapor.txt", true)]
    [InlineData("*ğüş*", "ĞÜŞiöç.txt", true)]
    public void Name_matcher_like_the_wildcards(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, LocalFileIndex.NameMatcher(pattern)(name));
        Assert.Equal(expected, LocalFileIndex.PatternRegex(pattern).IsMatch(name)); // the same as the regex it replaces
    }
}
