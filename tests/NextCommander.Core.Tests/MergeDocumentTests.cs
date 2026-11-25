using NextCommander.Core.Compare;

namespace NextCommander.Core.Tests;

/// <summary>The blocks of the merge window (the user's template, 25 November 2026).</summary>
public sealed class MergeDocumentTests
{
    [Fact]
    public void Three_files_become_blocks_with_their_own_choices()
    {
        string[] @base = ["deneme1", "[network]", "timeout = 30", "[retry]", "retries = 3", "[logging]", "level = info"];
        string[] left = ["deneme2", "[network]", "timeout = 60", "[retry]", "retries = 3", "[logging]", "level = debug"];
        string[] right = ["deneme2", "[network]", "timeout = 45", "[retry]", "retries = 5", "[logging]", "level = info"];
        var merge = MergeDocument.FromFiles(@base, left, right, "file2.txt", "file3.txt");
        var kinds = merge.Changes.Select(i => merge.Blocks[i].Kind).ToList();
        Assert.Equal([MergeBlockKind.Both, MergeBlockKind.Conflict, MergeBlockKind.Right, MergeBlockKind.Left], kinds);
        Assert.Equal(1, merge.ConflictsLeft);

        // an unresolved conflict is written with markers; once taken, the result is plain
        Assert.Equal(["deneme2", "[network]", "<<<<<<< file2.txt", "timeout = 60", "=======", "timeout = 45", ">>>>>>> file3.txt",
            "[retry]", "retries = 5", "[logging]", "level = debug"], merge.Result());
        var conflict = merge.Blocks[merge.Changes[1]];
        conflict.Taken = MergeTake.Both;
        Assert.Equal(0, merge.ConflictsLeft);
        Assert.Equal(["timeout = 60", "timeout = 45"], conflict.Lines);

        // a change taken from the other side, or the base
        var rightChange = merge.Blocks[merge.Changes[2]];
        Assert.Equal(MergeTake.Right, rightChange.Auto);
        rightChange.Taken = MergeTake.Base;
        Assert.Contains("retries = 3", merge.Result());
    }

    [Fact]
    public void A_file_with_markers_becomes_blocks()
    {
        var merge = MergeDocument.FromMarkers(["top", "<<<<<<< HEAD", "a", "=======", "b", ">>>>>>> topic", "end", "<<<<<<< ours", "x", "||||||| base", "o", "=======", "y", ">>>>>>> theirs"]);
        Assert.Equal(2, merge.ConflictsLeft);
        Assert.False(merge.Blocks[1].HasBase);
        Assert.True(merge.Blocks[3].HasBase);
        // written back as it came while nothing is taken
        Assert.Equal(["top", "<<<<<<< HEAD", "a", "=======", "b", ">>>>>>> topic", "end", "<<<<<<< ours", "x", "||||||| base", "o", "=======", "y", ">>>>>>> theirs"], merge.Result());
        merge.Blocks[1].Taken = MergeTake.Right;
        merge.Blocks[3].Taken = MergeTake.Base;
        Assert.Equal(["top", "b", "end", "o"], merge.Result());
    }
}
