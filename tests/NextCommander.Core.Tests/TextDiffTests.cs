using System.Text.RegularExpressions;
using NextCommander.Core.Compare;

namespace NextCommander.Core.Tests;

/// <summary>The compare engine after the compare and merge plan, phase 1 (25 November 2026).</summary>
public sealed class TextDiffTests
{
    /// <summary>The left and right lines the rows hold, in order (each must be the whole input).</summary>
    private static (List<string> Left, List<string> Right) Sides(IReadOnlyList<DiffLine> rows) =>
        ([.. rows.Where(r => r.Left is not null).Select(r => r.Left!)], [.. rows.Where(r => r.Right is not null).Select(r => r.Right!)]);

    private static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var t = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
            }
        }
        return t[a.Count, b.Count];
    }

    [Fact]
    public void The_linear_space_script_is_minimal_and_keeps_every_line()
    {
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "l" + random.Next(0, 6)).ToList();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "l" + random.Next(0, 6)).ToList();
            var rows = TextDiff.Compare(a, b, new DiffOptions { MatchSimilarLines = false });
            var (left, right) = Sides(rows);
            Assert.Equal(a, left);
            Assert.Equal(b, right);
            Assert.Equal(Lcs(a, b), rows.Count(r => r.Kind == DiffKind.Equal));
            Assert.All(rows.Where(r => r.Kind == DiffKind.Equal), r => Assert.Equal(r.Left, r.Right));
        }
    }

    [Fact]
    public void Very_different_large_inputs_are_compared_without_giving_up()
    {
        var random = new Random(3);
        var a = Enumerable.Range(0, 30000).Select(_ => "x" + random.Next(0, 50000)).ToList();
        var b = Enumerable.Range(0, 30000).Select(_ => "x" + random.Next(0, 50000)).ToList();
        a[100] = b[200] = "a line both have";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = TextDiff.Compare(a, b, new DiffOptions { MatchSimilarLines = false });
        // generous: the full suite slows it down (alone about 2 s)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
        var (left, right) = Sides(rows);
        Assert.Equal(a, left);
        Assert.Equal(b, right);
    }

    [Fact]
    public void The_options_ignore_blank_lines_filters_spaces_and_line_ends()
    {
        // blank lines only: shown, but ignored
        var rows = TextDiff.Compare(["a", "b"], ["a", "", "b"], new DiffOptions { IgnoreBlankLines = true });
        Assert.True(Assert.Single(rows, r => r.Kind != DiffKind.Equal).Ignored);
        Assert.True(Assert.Single(TextDiff.Blocks(rows)).Ignored);
        Assert.False(TextDiff.Compare(["a", "b"], ["a", "", "b"])[1].Ignored);

        // lines of the ignore filters
        var stamp = new Regex(@"^// generated \d+$");
        rows = TextDiff.Compare(["// generated 1", "x"], ["// generated 2", "x"], new DiffOptions { IgnoreLines = [stamp] });
        Assert.All(rows, r => Assert.True(r.Kind == DiffKind.Equal || r.Ignored));
        rows = TextDiff.Compare(["// generated 1", "x"], ["// generated 2", "y"], new DiffOptions { IgnoreLines = [stamp] });
        Assert.Contains(rows, r => r.Kind != DiffKind.Equal && !r.Ignored);

        // substitutions: version numbers do not count
        var version = new DiffSubstitution(new Regex(@"\d+\.\d+\.\d+"), "#");
        Assert.All(TextDiff.Compare(["v 1.2.3"], ["v 4.5.6"], new DiffOptions { Substitutions = [version] }), r => Assert.Equal(DiffKind.Equal, r.Kind));

        // every space, or only changes of spaces
        Assert.Equal(DiffKind.Equal, TextDiff.Compare(["a b"], ["ab"], new DiffOptions { IgnoreAllWhitespace = true })[0].Kind);
        Assert.Equal(DiffKind.Changed, TextDiff.Compare(["a b"], ["ab"], new DiffOptions { IgnoreWhitespace = true })[0].Kind);

        // line ends kept by the caller: CRLF against LF counts only when asked
        Assert.Equal(DiffKind.Equal, TextDiff.Compare(["a\r"], ["a"])[0].Kind);
        Assert.Equal(DiffKind.Changed, TextDiff.Compare(["a\r"], ["a"], new DiffOptions { IgnoreLineEndings = false })[0].Kind);
    }

    [Fact]
    public void Alike_lines_of_a_difference_sit_side_by_side()
    {
        string[] a = ["int alpha = 1;", "int beta = 2;"];
        string[] b = ["// a new comment", "int alpha = 10;", "int beta = 20;"];
        var rows = TextDiff.Compare(a, b);
        Assert.Equal([DiffKind.Added, DiffKind.Changed, DiffKind.Changed], rows.Select(r => r.Kind));
        Assert.Equal(("int alpha = 1;", "int alpha = 10;"), (rows[1].Left, rows[1].Right));

        // by position (as before) when asked
        rows = TextDiff.Compare(a, b, new DiffOptions { MatchSimilarLines = false });
        Assert.Equal(("int alpha = 1;", "// a new comment"), (rows[0].Left, rows[0].Right));
    }

    [Fact]
    public void Moved_lines_are_linked()
    {
        string[] a = ["first()", "second()", "third()", "end"];
        string[] b = ["second()", "third()", "end", "first()"];
        var rows = TextDiff.Compare(a, b, new DiffOptions { DetectMoved = true }).ToList();
        var removed = rows.Single(r => r.Kind == DiffKind.Removed);
        var added = rows.Single(r => r.Kind == DiffKind.Added);
        Assert.Equal(rows.IndexOf(added), removed.MovedTo);
        Assert.Equal(rows.IndexOf(removed), added.MovedTo);
        Assert.Null(TextDiff.Compare(a, b).Single(r => r.Kind == DiffKind.Removed).MovedTo);
    }

    [Fact]
    public void Words_or_characters_in_a_changed_line()
    {
        var (left, right) = TextDiff.InlineDiff("total = count + 1", "total = amount + 1", InlineLevel.Word);
        Assert.Equal(new InlineSpan(8, 5, true), Assert.Single(left, s => s.Different));
        Assert.Equal(new InlineSpan(8, 6, true), Assert.Single(right, s => s.Different));
        (left, _) = TextDiff.InlineDiff("total = count + 1", "total = amount + 1");
        Assert.Contains(left, s => s.Different);
        Assert.Equal("total = count + 1".Length, left.Sum(s => s.Length));
    }

    [Fact]
    public void Differences_are_copied_to_the_other_side()
    {
        string[] a = ["one", "two", "three", "four"];
        string[] b = ["one", "2", "three", "four", "five"];
        var rows = TextDiff.Compare(a, b);
        var blocks = TextDiff.Blocks(rows);
        Assert.Equal(2, blocks.Count);
        Assert.Equal((1, 1, 1, 1), (blocks[0].LeftStart, blocks[0].LeftCount, blocks[0].RightStart, blocks[0].RightCount));
        Assert.Equal((4, 0, 4, 1), (blocks[1].LeftStart, blocks[1].LeftCount, blocks[1].RightStart, blocks[1].RightCount));

        // the first one to the right, the second (an added line) to the left
        Assert.Equal(["one", "two", "three", "four", "five"], DiffMerge.Copy(a, b, [blocks[0]], toRight: true));
        Assert.Equal(["one", "two", "three", "four", "five"], DiffMerge.Copy(b, a, [blocks[1]], toRight: false));
        Assert.Equal(a, DiffMerge.CopyAll(a, b, blocks, toRight: true));
        Assert.Equal(b, DiffMerge.CopyAll(b, a, blocks, toRight: false));
    }

    [Fact]
    public void Three_way_changes_merge_or_conflict()
    {
        string[] baseLines = ["a", "b", "c", "d", "e"];
        string[] left = ["a", "B", "c", "d", "e"];
        string[] right = ["a", "b", "c", "D", "e", "f"];
        var merged = ThreeWayDiff.Merge(baseLines, left, right);
        Assert.Equal(0, merged.Conflicts);
        Assert.Equal(["a", "B", "c", "D", "e", "f"], merged.Lines);
        var kinds = ThreeWayDiff.Compare(baseLines, left, right).Select(c => c.Kind).ToList();
        Assert.Contains(MergeChunkKind.LeftChanged, kinds);
        Assert.Contains(MergeChunkKind.RightChanged, kinds);

        // the same change on both sides
        Assert.Contains(ThreeWayDiff.Compare(baseLines, left, left), c => c.Kind == MergeChunkKind.BothSame);

        // different changes of the same line
        string[] other = ["a", "bee", "c", "d", "e"];
        merged = ThreeWayDiff.Merge(baseLines, left, other, "mine", "base", "theirs");
        Assert.Equal(1, merged.Conflicts);
        Assert.Equal(["a", "<<<<<<< mine", "B", "||||||| base", "b", "=======", "bee", ">>>>>>> theirs", "c", "d", "e"], merged.Lines);

        // insertions at the same place conflict; at different places they do not
        Assert.Equal(1, ThreeWayDiff.Merge(["x"], ["x", "L"], ["x", "R"]).Conflicts);
        Assert.Equal(["L", "x", "R"], ThreeWayDiff.Merge(["x"], ["L", "x"], ["x", "R"]).Lines);
    }

    [Fact]
    public void A_patch_is_written_read_and_applied()
    {
        var oldLines = Enumerable.Range(1, 30).Select(i => "line " + i).ToList();
        var newLines = oldLines.ToList();
        newLines[2] = "line three";
        newLines.RemoveAt(15);
        newLines.Insert(25, "inserted");
        var patch = UnifiedDiff.Create("a/f.txt", "b/f.txt", oldLines, newLines);
        Assert.StartsWith("--- a/f.txt\n+++ b/f.txt\n@@ -1,6 +1,6 @@\n", patch, StringComparison.Ordinal);
        Assert.Contains("-line 3\n+line three\n", patch, StringComparison.Ordinal);
        Assert.Empty(UnifiedDiff.Create("a", "b", oldLines, oldLines));

        var file = Assert.Single(UnifiedDiff.Parse("diff --git a/f.txt b/f.txt\nindex 1..2\n" + patch));
        Assert.Equal(("a/f.txt", "b/f.txt"), (file.OldPath, file.NewPath));
        var applied = UnifiedDiff.Apply(oldLines, file);
        Assert.Empty(applied.Rejected);
        Assert.Equal(newLines, applied.Lines);

        // the file moved on: lines added at the top, the hunks are found further down
        var moved = new[] { "new top 1", "new top 2" }.Concat(oldLines).ToList();
        applied = UnifiedDiff.Apply(moved, file);
        Assert.Empty(applied.Rejected);
        Assert.Equal(new[] { "new top 1", "new top 2" }.Concat(newLines), applied.Lines);

        // a hunk whose lines are gone is rejected, the others are applied
        var changed = oldLines.ToList();
        changed[2] = "someone else's change";
        applied = UnifiedDiff.Apply(changed, file);
        Assert.Equal([0], applied.Rejected);
        Assert.Contains("inserted", applied.Lines);
    }

    [Fact]
    public void A_missing_last_line_end_is_part_of_the_patch()
    {
        string[] oldLines = ["a", "b"];
        var patch = UnifiedDiff.Create("a", "b", oldLines, oldLines, oldEndsWithNewline: true, newEndsWithNewline: false);
        Assert.Contains("\\ No newline at end of file", patch, StringComparison.Ordinal);
        var applied = UnifiedDiff.Apply(oldLines, UnifiedDiff.Parse(patch)[0]);
        Assert.Equal(oldLines, applied.Lines);
        Assert.False(applied.EndsWithNewline);

        patch = UnifiedDiff.Create("a", "b", ["a"], ["a", "b"], oldEndsWithNewline: false, newEndsWithNewline: false);
        applied = UnifiedDiff.Apply(["a"], UnifiedDiff.Parse(patch)[0], endsWithNewline: false);
        Assert.Equal(["a", "b"], applied.Lines);
        Assert.False(applied.EndsWithNewline);
    }
}
