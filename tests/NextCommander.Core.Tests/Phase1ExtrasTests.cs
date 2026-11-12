using NextCommander.Core.Config;
using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class Phase1ExtrasTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly LocalFileSystemProvider _local = new();

    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("a", new[] { "a" })]
    [InlineData("a|b| c ", new[] { "a", "b", "c" })]
    [InlineData("\"x|y\"|z", new[] { "x|y", "z" })]
    [InlineData("dir<1-3>", new[] { "dir1", "dir2", "dir3" })]
    [InlineData("<08-10>", new[] { "08", "09", "10" })]
    [InlineData("<3-1>", new[] { "3", "2", "1" })]
    [InlineData("r<1-2>c<1-2>", new[] { "r1c1", "r1c2", "r2c1", "r2c2" })]
    [InlineData("  ", new string[0])]
    public void NameExpander_Expands(string input, string[] expected) =>
        Assert.Equal(expected, NameExpander.Expand(input));

    [Fact]
    public void NameExpander_RejectsHugeRanges() =>
        Assert.Throws<ArgumentException>(() => NameExpander.Expand("<1-100000>"));

    private static VfsEntry E(string name, bool dir = false, DateTime? time = null, UnixFileMode? mode = null) => new()
    {
        Name = name,
        Path = VfsPath.Local(Path.Combine(Path.GetTempPath(), name)),
        IsDirectory = dir,
        LastWriteTime = time ?? new DateTime(2020, 1, 1),
        UnixMode = mode,
    };

    [Fact]
    public void ColorRules_FirstMatchWins_AndRespectDirectoriesAgeAndExec()
    {
        var rules = new ColorRuleSet(
        [
            new ColorRule { Mask = "*", MaxAgeMinutes = 60 },
            new ColorRule { Mask = "*.zip" },
            new ColorRule { Mask = "build\\" },
            new ColorRule { Mask = "*", ExecutableOnly = true },
        ]);
        var now = new DateTime(2024, 1, 1, 12, 0, 0);
        Assert.Equal(0, rules.Match(E("new.txt", time: now.AddMinutes(-5)), now));
        Assert.Equal(1, rules.Match(E("a.zip"), now));
        Assert.Equal(-1, rules.Match(E("a.txt"), now));
        Assert.Equal(2, rules.Match(E("build", dir: true), now));
        Assert.Equal(-1, rules.Match(E("src", dir: true), now));
        Assert.Equal(-1, rules.Match(E("build"), now)); // a *file* named build
        Assert.Equal(3, rules.Match(E("run", mode: UnixFileMode.UserRead | UnixFileMode.UserExecute), now));
    }

    [Fact]
    public void ColorRules_DefaultsParse() =>
        Assert.Equal(1, new ColorRuleSet(ColorRule.Defaults()).Match(E("x.exe"), DateTime.Now));

    [Fact]
    public async Task Copy_PreservesAttributesOrPermissions()
    {
        var src = _tmp.Write("src/ro.txt", "R");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(src, FileAttributes.ReadOnly | FileAttributes.Hidden);
        }
        else
        {
            File.SetUnixFileMode(src, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var job = new CopyMoveJob(new ProviderRegistry([_local]),
            [(await _local.GetEntryAsync(_tmp.VPath("src", "ro.txt"), CancellationToken.None))!], _tmp.VPath("dst"), move: false);
        await job.RunAsync();

        Assert.Equal(JobState.Completed, job.State);
        var dst = Path.Combine(_tmp.Root, "dst", "ro.txt");
        if (OperatingSystem.IsWindows())
        {
            var a = File.GetAttributes(dst);
            Assert.True(a.HasFlag(FileAttributes.ReadOnly));
            Assert.True(a.HasFlag(FileAttributes.Hidden));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(dst));
        }
    }

    [Fact]
    public async Task Copy_FileLinkAsLink_WhenRequested()
    {
        _tmp.Write("target.txt", "T");
        var link = Path.Combine(_tmp.Root, "src", "link.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_tmp.Root, "target.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // symlinks need developer mode/admin on Windows
        }
        var entry = (await _local.GetEntryAsync(VfsPath.Local(link), CancellationToken.None))!;
        Assert.True(entry.IsLink);

        var job = new CopyMoveJob(new ProviderRegistry([_local]), [entry], _tmp.VPath("dst"), false, new CopyOptions { CopyFileLinksAsLinks = true });
        await job.RunAsync();
        Assert.NotNull(new FileInfo(Path.Combine(_tmp.Root, "dst", "link.txt")).LinkTarget);

        var job2 = new CopyMoveJob(new ProviderRegistry([_local]), [entry], _tmp.VPath("dst"), false,
            new CopyOptions { ConflictPolicy = ConflictPolicy.Overwrite, SingleTargetName = "content.txt" });
        await job2.RunAsync();
        var copy = new FileInfo(Path.Combine(_tmp.Root, "dst", "content.txt"));
        Assert.Null(copy.LinkTarget);
        Assert.Equal("T", File.ReadAllText(copy.FullName));
    }
}
