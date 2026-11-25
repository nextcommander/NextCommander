namespace NextCommander.Core.Compare;

/// <summary>
/// Copying differences from one side to the other (Copy to Right / Left): the lines a difference covers on
/// the target side are replaced by the source's. Blocks are given from the last to the first so the earlier ones keep
/// their places; the caller compares again afterwards.
/// </summary>
public static class DiffMerge
{
    /// <summary>
    /// <paramref name="target"/> with the lines of the given differences taken from <paramref name="source"/>.
    /// <paramref name="toRight"/>: the source is the left side (the blocks' left lines go to their right places).
    /// </summary>
    public static List<string> Copy(IReadOnlyList<string> source, IReadOnlyList<string> target, IEnumerable<DiffBlock> blocks, bool toRight)
    {
        var result = target.ToList();
        foreach (var block in blocks.OrderByDescending(b => toRight ? b.RightStart : b.LeftStart))
        {
            var (fromStart, fromCount, toStart, toCount) = toRight
                ? (block.LeftStart, block.LeftCount, block.RightStart, block.RightCount)
                : (block.RightStart, block.RightCount, block.LeftStart, block.LeftCount);
            result.RemoveRange(toStart, toCount);
            result.InsertRange(toStart, source.Skip(fromStart).Take(fromCount));
        }
        return result;
    }

    /// <summary>Every difference copied: the target becomes the source, apart from what the options ignore.</summary>
    public static List<string> CopyAll(IReadOnlyList<string> source, IReadOnlyList<string> target, IReadOnlyList<DiffBlock> blocks, bool toRight) =>
        Copy(source, target, blocks.Where(b => !b.Ignored), toRight);
}
