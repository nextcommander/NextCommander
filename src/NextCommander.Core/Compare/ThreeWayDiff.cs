namespace NextCommander.Core.Compare;

/// <summary>What happened to a part of the common ancestor in the two versions.</summary>
public enum MergeChunkKind
{
    /// <summary>The same in all three.</summary>
    Unchanged,

    /// <summary>Changed on the left only: the left is taken.</summary>
    LeftChanged,

    /// <summary>Changed on the right only: the right is taken.</summary>
    RightChanged,

    /// <summary>Changed the same way on both sides.</summary>
    BothSame,

    /// <summary>Changed differently on both sides: someone has to choose.</summary>
    Conflict,
}

/// <summary>A part of a 3-way comparison: the lines it covers in the base, the left and the right (0-based).</summary>
public sealed record MergeChunk(MergeChunkKind Kind, int BaseStart, int BaseCount, int LeftStart, int LeftCount, int RightStart, int RightCount);

/// <summary>The result of merging: the lines, and how many conflicts were left marked in them.</summary>
public sealed record MergeResult(IReadOnlyList<string> Lines, int Conflicts);

/// <summary>
/// 3-way comparison (diff3): the changes of the left and of the right against their common base, grouped where they
/// touch the same base lines. Changes on one side only merge by themselves; changes on both sides are a conflict unless
/// they are the same.
/// </summary>
public static class ThreeWayDiff
{
    public static IReadOnlyList<MergeChunk> Compare(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var leftHunks = TextDiff.Hunks(baseLines, left, options);
        var rightHunks = TextDiff.Hunks(baseLines, right, options);
        var all = leftHunks.Select(h => (Hunk: h, Left: true)).Concat(rightHunks.Select(h => (Hunk: h, Left: false)))
            .OrderBy(h => h.Hunk.LeftStart).ThenBy(h => h.Hunk.LeftCount).ToList();

        var chunks = new List<MergeChunk>();
        int basePos = 0, leftDelta = 0, rightDelta = 0;
        for (var i = 0; i < all.Count;)
        {
            // a group: hunks whose base ranges overlap or touch
            var lo = all[i].Hunk.LeftStart;
            var hi = lo + all[i].Hunk.LeftCount;
            var group = new List<(DiffHunk Hunk, bool Left)> { all[i] };
            i++;
            while (i < all.Count && all[i].Hunk.LeftStart <= hi && (all[i].Hunk.LeftStart < hi || group.Any(g => g.Left != all[i].Left)))
            {
                hi = Math.Max(hi, all[i].Hunk.LeftStart + all[i].Hunk.LeftCount);
                group.Add(all[i]);
                i++;
            }
            if (lo > basePos)
            {
                chunks.Add(new MergeChunk(MergeChunkKind.Unchanged, basePos, lo - basePos, basePos + leftDelta, lo - basePos, basePos + rightDelta, lo - basePos));
            }
            var (leftStart, leftEnd) = Range(group.Where(g => g.Left).Select(g => g.Hunk).ToList(), lo, hi, leftDelta);
            var (rightStart, rightEnd) = Range(group.Where(g => !g.Left).Select(g => g.Hunk).ToList(), lo, hi, rightDelta);
            var hasLeft = group.Any(g => g.Left);
            var hasRight = group.Any(g => !g.Left);
            var kind = hasLeft && hasRight
                ? Same(left, leftStart, leftEnd, right, rightStart, rightEnd, options) ? MergeChunkKind.BothSame : MergeChunkKind.Conflict
                : hasLeft ? MergeChunkKind.LeftChanged : MergeChunkKind.RightChanged;
            chunks.Add(new MergeChunk(kind, lo, hi - lo, leftStart, leftEnd - leftStart, rightStart, rightEnd - rightStart));
            leftDelta = leftEnd - hi;
            rightDelta = rightEnd - hi;
            basePos = hi;
        }
        if (basePos < baseLines.Count)
        {
            var count = baseLines.Count - basePos;
            chunks.Add(new MergeChunk(MergeChunkKind.Unchanged, basePos, count, basePos + leftDelta, count, basePos + rightDelta, count));
        }
        return chunks;
    }

    /// <summary>The lines of one side that a group covers: its own hunks, or the base range shifted when it has none.</summary>
    private static (int Start, int End) Range(List<DiffHunk> hunks, int lo, int hi, int delta)
    {
        if (hunks.Count == 0)
        {
            return (lo + delta, hi + delta);
        }
        var first = hunks[0];
        var last = hunks[^1];
        return (first.RightStart - (first.LeftStart - lo), last.RightStart + last.RightCount + (hi - (last.LeftStart + last.LeftCount)));
    }

    private static bool Same(IReadOnlyList<string> a, int aStart, int aEnd, IReadOnlyList<string> b, int bStart, int bEnd, DiffOptions options)
    {
        if (aEnd - aStart != bEnd - bStart)
        {
            return false;
        }
        for (var i = 0; i < aEnd - aStart; i++)
        {
            if (TextDiff.Normalize(a[aStart + i], options) != TextDiff.Normalize(b[bStart + i], options))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The merged lines: changes of one side taken, conflicts written with markers (&lt;&lt;&lt;&lt;&lt;&lt;&lt; left,
    /// ||||||| base, ======= , &gt;&gt;&gt;&gt;&gt;&gt;&gt; right) for the user to settle.
    /// </summary>
    public static MergeResult Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right,
        string leftLabel = "left", string baseLabel = "base", string rightLabel = "right", DiffOptions? options = null)
    {
        var lines = new List<string>();
        var conflicts = 0;
        foreach (var chunk in Compare(baseLines, left, right, options))
        {
            switch (chunk.Kind)
            {
                case MergeChunkKind.Unchanged:
                    lines.AddRange(baseLines.Skip(chunk.BaseStart).Take(chunk.BaseCount));
                    break;
                case MergeChunkKind.LeftChanged or MergeChunkKind.BothSame:
                    lines.AddRange(left.Skip(chunk.LeftStart).Take(chunk.LeftCount));
                    break;
                case MergeChunkKind.RightChanged:
                    lines.AddRange(right.Skip(chunk.RightStart).Take(chunk.RightCount));
                    break;
                default:
                    conflicts++;
                    lines.Add("<<<<<<< " + leftLabel);
                    lines.AddRange(left.Skip(chunk.LeftStart).Take(chunk.LeftCount));
                    lines.Add("||||||| " + baseLabel);
                    lines.AddRange(baseLines.Skip(chunk.BaseStart).Take(chunk.BaseCount));
                    lines.Add("=======");
                    lines.AddRange(right.Skip(chunk.RightStart).Take(chunk.RightCount));
                    lines.Add(">>>>>>> " + rightLabel);
                    break;
            }
        }
        return new MergeResult(lines, conflicts);
    }
}
