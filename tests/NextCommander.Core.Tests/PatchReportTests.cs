using System.Text;
using NextCommander.Core.Compare;

namespace NextCommander.Core.Tests;

/// <summary>Text files kept as they are, the HTML report, new and deleted files in patches (the compare and merge plan, phase 5).</summary>
public sealed class PatchReportTests
{
    [Fact]
    public void A_text_file_is_written_back_as_it_was()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("çay\r\nşeker")).ToArray();
        var text = TextContent.Read(bytes);
        Assert.Equal(["çay", "şeker"], text.Lines);
        Assert.True(text.Bom);
        Assert.Equal("\r\n", text.NewLine);
        Assert.False(text.EndsWithNewline);
        Assert.Equal(bytes, text.Bytes());
        Assert.Empty(TextContent.Read([]).Lines);
    }

    [Fact]
    public void The_report_marks_changes_and_leaves_out_far_equal_lines()
    {
        var left = Enumerable.Range(1, 30).Select(i => "line " + i).ToList();
        var right = left.ToList();
        right[14] = "line <15>";
        var rows = TextDiff.Compare(left, right);
        var html = DiffReport.Html(rows, "a.txt", "b.txt", new DateTime(2026, 11, 25), context: 2);
        Assert.Contains("<title>a.txt ↔ b.txt</title>", html, StringComparison.Ordinal);
        Assert.Contains("1 difference", html, StringComparison.Ordinal);
        Assert.Contains("<ins>&lt;</ins>", html, StringComparison.Ordinal); // marked and escaped
        Assert.Contains("class=\"gap\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">line 1<", html, StringComparison.Ordinal);
        Assert.Contains(">line 1<", DiffReport.Html(rows, "a", "b", DateTime.Now), StringComparison.Ordinal);
    }

    [Fact]
    public void New_and_deleted_files_go_through_a_patch()
    {
        var created = UnifiedDiff.Create("/dev/null", "b/docs/new.md", [], ["# Title", "text"]);
        var deleted = UnifiedDiff.Create("a/old.txt", "/dev/null", ["gone"], []);
        var files = UnifiedDiff.Parse(created + deleted);
        Assert.Equal(2, files.Count);
        Assert.True(UnifiedDiff.IsNewFile(files[0]));
        Assert.Equal("docs/new.md", UnifiedDiff.Candidates(files[0])[0]);
        Assert.Equal(["# Title", "text"], UnifiedDiff.Apply([], files[0]).Lines);
        Assert.True(UnifiedDiff.IsDeletedFile(files[1]));
        Assert.Equal("old.txt", UnifiedDiff.Candidates(files[1])[0]);
        Assert.Empty(UnifiedDiff.Apply(["gone"], files[1]).Lines);

        // a patch made one folder higher: the inner path is tried too
        Assert.Equal(["project/src/x.cs", "src/x.cs", "x.cs"], UnifiedDiff.Candidates(new FilePatch("x", "project/src/x.cs", [])));
    }
}
