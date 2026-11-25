using NextCommander.Core.Compare;

namespace NextCommander.Core.Tests;

/// <summary>Conflict markers found and resolved (the compare and merge plan, phase 6; 25 November 2026).</summary>
public sealed class ConflictMarkersTests
{
    [Fact]
    public void Git_and_diff3_conflicts_are_found_and_resolved()
    {
        string[] lines =
        [
            "top",
            "<<<<<<< HEAD",
            "mine",
            "=======",
            "theirs",
            ">>>>>>> feature",
            "middle",
            "<<<<<<< ours",
            "a",
            "||||||| base",
            "b",
            "=======",
            "c",
            "d",
            ">>>>>>> theirs",
            "<<<<<<< not closed",
            "end",
        ];
        var blocks = ConflictMarkers.Find(lines);
        Assert.Equal(2, blocks.Count);
        Assert.Equal((1, 6, "HEAD", "feature"), (blocks[0].Start, blocks[0].End, blocks[0].LeftLabel, blocks[0].RightLabel));
        Assert.Null(blocks[0].Base);
        Assert.Equal(["b"], blocks[1].Base!);
        Assert.Equal(["c", "d"], blocks[1].Right);

        Assert.Equal(["top", "theirs", "middle"], ConflictMarkers.Resolve(lines, blocks[0], ConflictChoice.Right).Take(3));
        Assert.Equal(["mine", "theirs"], ConflictMarkers.LinesOf(blocks[0], ConflictChoice.LeftThenRight));
        Assert.Empty(ConflictMarkers.LinesOf(blocks[0], ConflictChoice.Base));
        var resolved = ConflictMarkers.Resolve(lines, blocks[1], ConflictChoice.Base);
        Assert.Single(ConflictMarkers.Find(resolved));
        Assert.Contains("b", resolved);

        // a line of seven '=' inside text without a conflict is not one
        Assert.Empty(ConflictMarkers.Find(["=======", "text", ">>>>>>>"]));
    }

    [Fact]
    public void A_three_way_merge_numbers_its_conflicts()
    {
        var (lines, chunks) = ConflictMarkers.Merge(["a", "b", "c"], ["a", "B", "c"], ["a", "bee", "c"], "left.txt", "base.txt", "right.txt");
        var block = Assert.Single(ConflictMarkers.Find(lines));
        Assert.Equal(1, ConflictMarkers.NumberOf(block));
        Assert.Equal(["B"], block.Left);
        Assert.Equal(["b"], block.Base!);
        Assert.Contains(chunks, c => c.Kind == MergeChunkKind.Conflict);
        Assert.Null(ConflictMarkers.NumberOf(block with { LeftLabel = "HEAD" }));
    }
}
