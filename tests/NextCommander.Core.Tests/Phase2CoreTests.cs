using System.Text;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class Phase2CoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static ParameterContext Ctx(params string[] selected) => new()
    {
        SourceDirectory = "/src/dir",
        TargetDirectory = "/dst",
        CursorName = "my file.txt",
        TargetCursorName = "other.log",
        Selected = selected,
        ClipboardText = "clip",
        Now = new DateTime(2026, 11, 5, 10, 20, 30),
        Separator = '/',
    };

    [Theory]
    [InlineData("%P%N", "\"/src/dir/my file.txt\"")]
    [InlineData("\"%P%N\"", "\"/src/dir/my file.txt\"")]
    [InlineData("%Q%P%N", "/src/dir/my file.txt")]
    [InlineData("%T%M", "/dst/other.log")]
    [InlineData("%O.bak", "\"my file.bak\"")]
    [InlineData("x.%E", "x.txt")]
    [InlineData("%N:~0,2", "my")]
    [InlineData("%N:~-3", "txt")]
    [InlineData("%B", "dir")]
    [InlineData("%B+1", "src")]
    [InlineData("%$CLIPBOARD%", "clip")]
    [InlineData("%$DATE%", "2026-11-05")]
    [InlineData("%$DATE+1D%", "2026-11-06")]
    [InlineData("%$DATE-1M%", "2026-10-05")]
    [InlineData("%$TIME%", "10-20-30")]
    [InlineData("-a %S", "-a \"my file.txt\"")]
    public void Expands_SingleValues(string template, string expected) =>
        Assert.Equal(expected, ParameterExpander.Expand(template, Ctx()));

    [Fact]
    public void Expands_SelectionsAndRanges()
    {
        var c = Ctx("a.txt", "b c.txt", "d.txt");
        Assert.Equal("a.txt \"b c.txt\" d.txt", ParameterExpander.Expand("%S", c));
        Assert.Equal("a.txt", ParameterExpander.Expand("%S1", c));
        Assert.Equal("\"b c.txt\" d.txt", ParameterExpander.Expand("%S2-3", c));
        Assert.Equal("", ParameterExpander.Expand("%Z%R", c with { TargetCursorName = null }));
        Assert.Equal("/src/dir/a.txt \"/src/dir/b c.txt\"", ParameterExpander.Expand("%C1 %C2", c));
    }

    [Fact]
    public void Expands_ListFiles()
    {
        IReadOnlyList<string>? written = null;
        Encoding? encoding = null;
        var result = ParameterExpander.Expand("%L", Ctx("a.txt", "b.txt"), (lines, enc) =>
        {
            written = lines;
            encoding = enc;
            return "/tmp/list.lst";
        });
        Assert.Equal("/tmp/list.lst", result);
        Assert.Equal(["/src/dir/a.txt", "/src/dir/b.txt"], written);

        ParameterExpander.Expand("%WF", Ctx(), (lines, enc) =>
        {
            written = lines;
            encoding = enc;
            return "x";
        });
        Assert.Equal(["my file.txt"], written); // nothing marked → cursor
        Assert.Equal(Encoding.Unicode, encoding);
    }

    [Fact]
    public void Prompt_Marker()
    {
        Assert.True(ParameterExpander.NeedsPrompt("?%N", out var rest));
        Assert.Equal("%N", rest);
        Assert.False(ParameterExpander.NeedsPrompt("%N", out _));
    }

    [Fact]
    public void EnvironmentExpander_Substrings()
    {
        string? Lookup(string n) => n == "v" ? "abcdef" : null;
        Assert.Equal("cd", EnvironmentExpander.Expand("%v:~2,2%", Lookup));
        Assert.Equal("ef", EnvironmentExpander.Expand("%v:~-2%", Lookup));
        Assert.Equal("abcdef/x", EnvironmentExpander.Expand("%v%/x", Lookup));
        Assert.Equal("%unknown%", EnvironmentExpander.Expand("%unknown%", Lookup));
    }

    [Theory]
    [InlineData("rea", QuickMatchMode.Prefix, "readme.md", true)]
    [InlineData("rea", QuickMatchMode.Prefix, "Thread.cs", false)]
    [InlineData("rea", QuickMatchMode.Anywhere, "Thread.cs", true)]
    [InlineData("fmgr", QuickMatchMode.Fuzzy, "FileManager.cs", true)]
    [InlineData("fmx", QuickMatchMode.Fuzzy, "NextCommander.cs", false)]
    [InlineData("*.cs", QuickMatchMode.Prefix, "a.cs", true)]
    [InlineData("a*b", QuickMatchMode.Prefix, "a-x-b-y", true)]
    [InlineData("a*b", QuickMatchMode.Prefix, "xa-b", false)]
    [InlineData("a*b", QuickMatchMode.Anywhere, "xa-b", true)]
    public void QuickMatcher_Modes(string text, QuickMatchMode mode, string name, bool expected) =>
        Assert.Equal(expected, new QuickMatcher(text, mode).IsMatch(name));

    [Fact]
    public void QuickMatcher_FindNext_Wraps()
    {
        var names = new[] { "..", "alpha", "beta", "apple" };
        var m = new QuickMatcher("a", QuickMatchMode.Prefix);
        Assert.Equal(1, m.FindNext(names, 0));
        Assert.Equal(3, m.FindNext(names, 2));
        Assert.Equal(1, m.FindNext(names, 4)); // wraps
        Assert.Equal(1, m.FindNext(names, 2, -1));
        Assert.Equal(-1, new QuickMatcher("zz", QuickMatchMode.Prefix).FindNext(names, 0));
    }

    [Fact]
    public void FrequentDirectories_DecayAndRank()
    {
        var store = new List<FrequentDirectory>();
        var f = new FrequentDirectories(store);
        var t0 = new DateTime(2026, 1, 1);
        for (var i = 0; i < 4; i++)
        {
            f.Record("/old", t0);
        }
        f.Record("/new", t0.AddDays(28));
        f.Record("/new", t0.AddDays(28));
        var top = f.Top(10, t0.AddDays(28));
        Assert.Equal("/new", top[0].Path); // 2 points vs 4 points halved twice = 1
        Assert.Equal(1, top[1].Score, 3);
        f.Remove("/new");
        Assert.Single(f.Top(10, t0.AddDays(28)));
    }

    [Fact]
    public async Task BranchLister_ListsFilesRecursively_AndCopyKeepsRelativePaths()
    {
        _tmp.Write("src/a.txt", "A");
        _tmp.Write("src/x/b.txt", "B");
        _tmp.Write("src/x/y/c.txt", "C");
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local]);
        var root = (await local.GetEntryAsync(_tmp.VPath("src"), CancellationToken.None))!;

        var files = await BranchLister.ListAsync(registry, [root]);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], files.Select(f => f.Name).Order().ToArray());

        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var job = new CopyMoveJob(registry, files.Where(f => f.Name != "a.txt").ToList(), _tmp.VPath("dst"), move: false,
            new CopyOptions { RelativeBase = _tmp.VPath("src") });
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("B", _tmp.Read("dst/x/b.txt"));
        Assert.Equal("C", _tmp.Read("dst/x/y/c.txt"));
    }
}
