using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tests;

public class VfsPathTests
{
    [Fact]
    public void Local_ParentAndName()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var p = VfsPath.Local(Path.Combine(root, "a", "b"));
        Assert.Equal("b", p.Name);
        Assert.Equal(VfsPath.Local(Path.Combine(root, "a")), p.Parent);
        Assert.True(VfsPath.Local(root).IsRoot);
    }

    [Fact]
    public void Local_TrimsTrailingSeparator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "x");
        Assert.Equal(VfsPath.Local(dir), VfsPath.Local(dir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Remote_ParseCombineParent()
    {
        var p = VfsPath.Parse("sftp://user@host/home/user");
        Assert.Equal("sftp", p.Scheme);
        Assert.Equal("user@host", p.Authority);
        Assert.Equal("/home/user", p.Path);
        Assert.Equal("/home/user/docs", p.Combine("docs").Path);
        Assert.Equal("/home", p.Parent!.Path);
        Assert.Null(VfsPath.Parse("sftp://host/").Parent);
        Assert.Equal("sftp://user@host/home/user", p.ToString());
    }

    [Fact]
    public void Archive_RoundTrip()
    {
        var container = Path.Combine(Path.GetTempPath(), "my archive.zip");
        var p = VfsPath.Create("archive", container, "/dir/file.txt");
        var parsed = VfsPath.Parse(p.ToString());
        Assert.Equal(p, parsed);
        Assert.Equal(container, parsed.Authority);
        Assert.Equal("file.txt", parsed.Name);
    }

    [Fact]
    public void RelativePaths()
    {
        var baseDir = VfsPath.Local(Path.Combine(Path.GetTempPath(), "base"));
        var child = baseDir.Combine("sub").Combine("f.txt");
        Assert.Equal("sub/f.txt", child.GetRelativeTo(baseDir));
        Assert.True(child.IsSameOrUnder(baseDir));
        Assert.False(baseDir.IsSameOrUnder(child));
        Assert.Null(VfsPath.Local(Path.Combine(Path.GetTempPath(), "base2")).GetRelativeTo(baseDir));
    }
}
