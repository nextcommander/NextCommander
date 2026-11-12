using NextCommander.Core.Matching;

namespace NextCommander.Core.Tests;

public class MatchingTests
{
    [Theory]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("*.txt", "a.doc", false)]
    [InlineData("*.*", "Makefile", true)]
    [InlineData("*.txt *.doc", "x.doc", true)]
    [InlineData("*.txt;*.doc", "x.doc", true)]
    [InlineData("* | *.bak", "x.bak", false)]
    [InlineData("* | *.bak", "x.txt", true)]
    [InlineData("file?.log", "file1.log", true)]
    [InlineData("file?.log", "file10.log", false)]
    [InlineData("\"my file*\"", "my file 1.txt", true)]
    [InlineData("", "anything", true)]
    public void MatchesFiles(string mask, string name, bool expected) =>
        Assert.Equal(expected, WildcardMatcher.Parse(mask).MatchesFile(name));

    [Fact]
    public void DirectoryPatterns_OnlyApplyToDirectories()
    {
        var m = WildcardMatcher.Parse("* | obj\\ bin/");
        Assert.False(m.MatchesDirectory("obj"));
        Assert.False(m.MatchesDirectory("bin"));
        Assert.True(m.MatchesDirectory("src"));
        Assert.True(m.MatchesFile("obj")); // a file named obj is not excluded
    }

    [Fact]
    public void Selection_SelectsDirectoriesOnlyWithDirectoryPattern()
    {
        Assert.False(WildcardMatcher.Parse("*").SelectsDirectory("src"));
        Assert.True(WildcardMatcher.Parse("*.cs src\\").SelectsDirectory("src"));
    }

    [Theory]
    [InlineData("report.txt", "*.bak", "report.bak")]
    [InlineData("report.txt", "*.*", "report.txt")]
    [InlineData("report.txt", "old_*.*", "old_report.txt")]
    [InlineData("report.txt", "new.txt", "new.txt")]
    [InlineData("report.txt", "???.*", "rep.txt")]
    [InlineData("archive.tar.gz", "*.zip", "archive.tar.zip")]
    [InlineData("Makefile", "*.bak", "Makefile.bak")]
    [InlineData("a.txt", "*_copy", "a.txt_copy")]
    public void RenameMask_Apply(string source, string mask, string expected) =>
        Assert.Equal(expected, RenameMask.Apply(source, mask));

    [Fact]
    public void NaturalSort_OrdersNumbersNumerically()
    {
        var names = new[] { "file10", "file2", "File1", "file02b", "file2a" };
        var sorted = names.Order(NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[] { "File1", "file2", "file2a", "file02b", "file10" }, sorted);
    }
}
