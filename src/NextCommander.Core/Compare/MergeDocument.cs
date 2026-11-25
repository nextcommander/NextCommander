namespace NextCommander.Core.Compare;

/// <summary>What happened to a part of the files in a 3-way merge.</summary>
public enum MergeBlockKind
{
    /// <summary>The same in all three.</summary>
    Same,

    /// <summary>Changed on the left only.</summary>
    Left,

    /// <summary>Changed on the right only.</summary>
    Right,

    /// <summary>Changed the same way on both sides.</summary>
    Both,

    /// <summary>Changed differently on both sides.</summary>
    Conflict,
}

/// <summary>What a block of the result is made of.</summary>
public enum MergeTake
{
    Left,
    Base,
    Right,

    /// <summary>The left lines, then the right ones.</summary>
    Both,
}

/// <summary>A part of a 3-way merge: its lines in the base and on each side, and what the result takes.</summary>
public sealed class MergeBlock
{
    public required MergeBlockKind Kind { get; init; }

    public required IReadOnlyList<string> Base { get; init; }

    public required IReadOnlyList<string> Left { get; init; }

    public required IReadOnlyList<string> Right { get; init; }

    /// <summary>The base is known (a git conflict without the diff3 style has none).</summary>
    public bool HasBase { get; init; } = true;

    /// <summary>What the merge takes by itself (null for a conflict).</summary>
    public MergeTake? Auto => Kind switch
    {
        MergeBlockKind.Left or MergeBlockKind.Both => MergeTake.Left,
        MergeBlockKind.Right => MergeTake.Right,
        MergeBlockKind.Same => MergeTake.Base,
        _ => null,
    };

    /// <summary>What the result takes now (null: an unresolved conflict).</summary>
    public MergeTake? Taken { get; set; }

    public bool IsChange => Kind != MergeBlockKind.Same;

    public bool IsUnresolved => Kind == MergeBlockKind.Conflict && Taken is null;

    /// <summary>An unresolved conflict is written with its base section (the file had git's diff3 markers).</summary>
    public bool MarkerBase { get; init; }

    /// <summary>The labels of the markers of an unresolved conflict (git's own when the file came with them).</summary>
    public string LeftLabel { get; init; } = "Left";

    public string RightLabel { get; init; } = "Right";

    /// <summary>The lines of the result for this block; null while it is an unresolved conflict.</summary>
    public IReadOnlyList<string>? Lines => Taken switch
    {
        MergeTake.Left => Left,
        MergeTake.Right => Right,
        MergeTake.Base => Base,
        MergeTake.Both => [.. Left, .. Right],
        _ => null,
    };
}

/// <summary>
/// A 3-way merge as blocks (the merge window of the user's template, 25 November 2026): made from three files, or from a
/// file with conflict markers; each change has what the result takes, and the result is written from them (unresolved
/// conflicts with their markers).
/// </summary>
public sealed class MergeDocument
{
    private MergeDocument(List<MergeBlock> blocks)
    {
        Blocks = blocks;
        Changes = [.. Enumerable.Range(0, blocks.Count).Where(i => blocks[i].IsChange)];
    }

    public IReadOnlyList<MergeBlock> Blocks { get; }

    /// <summary>The indices of the blocks that are changes, in order.</summary>
    public IReadOnlyList<int> Changes { get; }

    public int ConflictsLeft => Blocks.Count(b => b.IsUnresolved);

    /// <summary>The changes of the left and of the right side since the base.</summary>
    public static MergeDocument FromFiles(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right,
        string leftLabel = "Left", string rightLabel = "Right", DiffOptions? options = null)
    {
        var blocks = new List<MergeBlock>();
        foreach (var chunk in ThreeWayDiff.Compare(baseLines, left, right, options))
        {
            var kind = chunk.Kind switch
            {
                MergeChunkKind.LeftChanged => MergeBlockKind.Left,
                MergeChunkKind.RightChanged => MergeBlockKind.Right,
                MergeChunkKind.BothSame => MergeBlockKind.Both,
                MergeChunkKind.Conflict => MergeBlockKind.Conflict,
                _ => MergeBlockKind.Same,
            };
            var block = new MergeBlock
            {
                Kind = kind,
                Base = [.. baseLines.Skip(chunk.BaseStart).Take(chunk.BaseCount)],
                Left = [.. left.Skip(chunk.LeftStart).Take(chunk.LeftCount)],
                Right = [.. right.Skip(chunk.RightStart).Take(chunk.RightCount)],
                LeftLabel = leftLabel,
                RightLabel = rightLabel,
            };
            block.Taken = block.Auto;
            blocks.Add(block);
        }
        return new MergeDocument(blocks);
    }

    /// <summary>A file with conflict markers: the text around them is the same on all sides, each conflict a block.</summary>
    public static MergeDocument FromMarkers(IReadOnlyList<string> lines)
    {
        var blocks = new List<MergeBlock>();
        var at = 0;
        void Same(int end)
        {
            if (end > at)
            {
                var part = lines.Skip(at).Take(end - at).ToList();
                blocks.Add(new MergeBlock { Kind = MergeBlockKind.Same, Base = part, Left = part, Right = part, Taken = MergeTake.Base });
            }
        }
        foreach (var conflict in ConflictMarkers.Find(lines))
        {
            Same(conflict.Start);
            blocks.Add(new MergeBlock
            {
                Kind = MergeBlockKind.Conflict,
                Base = conflict.Base ?? [],
                HasBase = conflict.Base is not null,
                MarkerBase = conflict.Base is not null,
                Left = conflict.Left,
                Right = conflict.Right,
                LeftLabel = conflict.LeftLabel,
                RightLabel = conflict.RightLabel,
            });
            at = conflict.End;
        }
        Same(lines.Count);
        return new MergeDocument(blocks);
    }

    /// <summary>The result's lines: each block as taken, an unresolved conflict with its markers.</summary>
    public List<string> Result()
    {
        var lines = new List<string>();
        foreach (var block in Blocks)
        {
            if (block.Lines is { } taken)
            {
                lines.AddRange(taken);
                continue;
            }
            lines.Add("<<<<<<< " + block.LeftLabel);
            lines.AddRange(block.Left);
            if (block.MarkerBase)
            {
                lines.Add("||||||| base");
                lines.AddRange(block.Base);
            }
            lines.Add("=======");
            lines.AddRange(block.Right);
            lines.Add(">>>>>>> " + block.RightLabel);
        }
        return lines;
    }
}
