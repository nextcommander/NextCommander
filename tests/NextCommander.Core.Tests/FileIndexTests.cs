using System.Runtime.CompilerServices;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

[Collection("FileIndex")] // FileIndexes.Override is global
public sealed class FileIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose()
    {
        FileIndexes.Override = null;
        _tmp.Dispose();
    }

    /// <summary>Returns fixed paths (like a stale index: may include deleted files and paths outside the root).</summary>
    private sealed class FakeIndex(IReadOnlyList<string> paths, bool fail = false) : IFileIndex
    {
        public List<(string Root, IReadOnlyList<string> Patterns)> Queries { get; } = [];

        public string Name => "Sahte";

        public bool IsAvailable => true;

        public async IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Queries.Add((root, patterns));
            await Task.Yield();
            if (fail)
            {
                throw new IOException("Everything çalışmıyor");
            }
            foreach (var p in paths)
            {
                yield return p;
            }
        }
    }

    [Fact]
    public void Simple_patterns_for_the_index()
    {
        Assert.Equal(["*.txt", "*.cs"], FileIndexes.SimplePatterns("*.txt *.cs | obj\\", false));
        Assert.Equal(["*rapor*"], FileIndexes.SimplePatterns("rapor", false));
        Assert.Empty(FileIndexes.SimplePatterns("*", false)!);
        Assert.Null(FileIndexes.SimplePatterns("sub\\*.txt", false));
        Assert.Null(FileIndexes.SimplePatterns("a.*", true));
    }

    [Fact]
    public async Task Index_candidates_are_checked_against_all_criteria()
    {
        var a = _tmp.Write("a.txt", "merhaba");
        var deep = _tmp.Write(Path.Combine("x", "y", "deep.txt"), "merhaba");
        var excluded = _tmp.Write(Path.Combine("obj", "skip.txt"), "merhaba");
        var other = _tmp.Write("b.log", "merhaba");
        var empty = _tmp.Write("c.txt", "başka");
        var index = new FakeIndex([a, deep, excluded, other, empty, Path.Combine(_tmp.Root, "deleted.txt"), @"Z:\elsewhere\z.txt", "/elsewhere/z.txt"]);
        FileIndexes.Override = index;

        var options = new SearchOptions { SearchIn = _tmp.Root, NameMask = "*.txt | obj\\", ContainingText = "merhaba", UseIndex = true, MaxDepth = 1 };
        var searcher = new FileSearcher(_registry, options);
        var hits = new List<string>();
        await searcher.RunAsync(e => hits.Add(e.Path.Path), CancellationToken.None);

        Assert.Equal([a], hits); // deep: depth 2 > 1; obj excluded; b.log name; c.txt content; others missing/outside
        Assert.Equal("Sahte", searcher.Progress.IndexUsed);
        Assert.Equal(["*.txt"], Assert.Single(index.Queries).Patterns);

        options.MaxDepth = -1;
        hits.Clear();
        await new FileSearcher(_registry, options).RunAsync(e => hits.Add(e.Path.Path), CancellationToken.None);
        Assert.Equal([a, deep], hits);
    }

    [Fact]
    public async Task Failing_index_falls_back_to_walking()
    {
        var a = _tmp.Write("a.txt", "x");
        FileIndexes.Override = new FakeIndex([], fail: true);
        var searcher = new FileSearcher(_registry, new SearchOptions { SearchIn = _tmp.Root, NameMask = "*.txt", UseIndex = true });
        var hits = new List<string>();
        await searcher.RunAsync(e => hits.Add(e.Path.Path), CancellationToken.None);
        Assert.Equal([a], hits);
        Assert.Null(searcher.Progress.IndexUsed);
        Assert.Contains("Everything", searcher.Progress.IndexError, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_folder_size_answer()
    {
        Assert.Equal(1234567L, NextCommander.Core.Search.EverythingIndex.ParseSize("1234567\r\n"));
        Assert.Equal(1234567L, NextCommander.Core.Search.EverythingIndex.ParseSize("1.234.567")); // digit grouping of the system
        Assert.Equal(1234567L, NextCommander.Core.Search.EverythingIndex.ParseSize("1,234,567"));
        Assert.Null(NextCommander.Core.Search.EverythingIndex.ParseSize("Error 8: Everything IPC not found."));
        Assert.Null(NextCommander.Core.Search.EverythingIndex.ParseSize(string.Empty));
    }

    /// <summary>
    /// plocate requires every pattern of one call to match: each pattern is its own call (checked against real plocate
    /// 1.1.19: "*.bin *.TXT" in one call found nothing, in two calls both kinds).
    /// </summary>
    [Fact]
    public void Locate_asks_one_pattern_per_call()
    {
        var locate = new NextCommander.Core.Search.LocateIndex("/usr/bin/plocate", "/tmp/test.db");
        Assert.Equal(["-0", "-i", "-d", "/tmp/test.db", "/home/a/*.txt"], locate.Arguments("/home/a/", "*.txt"));
        Assert.Equal(["-0", "-i", "-d", "/tmp/test.db", "/home/a/*/r?por.txt"], locate.Arguments("/home/a", "r?por.txt"));
        Assert.Equal(["-0", "-i", "/home/a/*"], new NextCommander.Core.Search.LocateIndex("/usr/bin/plocate").Arguments("/home/a", null));
    }
}
