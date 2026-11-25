namespace NextCommander.Core.Compare;

/// <summary>
/// One conflict written into a file: from the "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" line (<see cref="Start"/>) to the line after
/// "&gt;&gt;&gt;&gt;&gt;&gt;&gt;" (<see cref="End"/>, exclusive); the lines of each side and, in the diff3 style, of the
/// base.
/// </summary>
public sealed record ConflictBlock(int Start, int End, IReadOnlyList<string> Left, IReadOnlyList<string>? Base, IReadOnlyList<string> Right,
    string LeftLabel, string RightLabel);

/// <summary>What a conflict becomes.</summary>
public enum ConflictChoice
{
    Left,
    Right,
    Base,

    /// <summary>The left lines, then the right ones.</summary>
    LeftThenRight,

    /// <summary>The right lines, then the left ones.</summary>
    RightThenLeft,
}

/// <summary>
/// Conflict markers as git and diff3 write them (the compare and merge plan, phase 6; 25 November 2026): finding the conflicts
/// in a file's lines and replacing one by a side.
/// </summary>
public static class ConflictMarkers
{
    private static bool Marker(string line, char c) =>
        line.Length >= 7 && line.AsSpan(0, 7).IndexOfAnyExcept(c) < 0 && (line.Length == 7 || line[7] == ' ');

    /// <summary>The conflicts of a file, in order (an unfinished one at the end is left out).</summary>
    public static IReadOnlyList<ConflictBlock> Find(IReadOnlyList<string> lines)
    {
        var blocks = new List<ConflictBlock>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Marker(lines[i], '<'))
            {
                continue;
            }
            var start = i;
            var leftLabel = lines[i].Length > 8 ? lines[i][8..] : string.Empty;
            var left = new List<string>();
            List<string>? @base = null;
            var right = new List<string>();
            var part = 0; // 0 left, 1 base, 2 right
            var closed = false;
            for (i++; i < lines.Count; i++)
            {
                var line = lines[i];
                if (Marker(line, '<'))
                {
                    break; // a new conflict before this one ended: this one is not a conflict
                }
                if (part == 0 && Marker(line, '|'))
                {
                    @base = [];
                    part = 1;
                }
                else if (part < 2 && Marker(line, '='))
                {
                    part = 2;
                }
                else if (part == 2 && Marker(line, '>'))
                {
                    blocks.Add(new ConflictBlock(start, i + 1, left, @base, right, leftLabel, line.Length > 8 ? line[8..] : string.Empty));
                    closed = true;
                    break;
                }
                else
                {
                    (part == 0 ? left : part == 1 ? @base! : right).Add(line);
                }
            }
            if (!closed)
            {
                i = start; // look again from the next line
            }
        }
        return blocks;
    }

    /// <summary>The lines a choice puts in place of a conflict (Base without a base: the empty list).</summary>
    public static IReadOnlyList<string> LinesOf(ConflictBlock block, ConflictChoice choice) => choice switch
    {
        ConflictChoice.Left => block.Left,
        ConflictChoice.Right => block.Right,
        ConflictChoice.Base => block.Base ?? [],
        ConflictChoice.LeftThenRight => [.. block.Left, .. block.Right],
        _ => [.. block.Right, .. block.Left],
    };

    /// <summary>The lines with one conflict replaced by a choice.</summary>
    public static List<string> Resolve(IReadOnlyList<string> lines, ConflictBlock block, ConflictChoice choice)
    {
        var result = lines.Take(block.Start).ToList();
        result.AddRange(LinesOf(block, choice));
        result.AddRange(lines.Skip(block.End));
        return result;
    }

    /// <summary>
    /// A 3-way merge as lines with markers; each conflict's left label ends in " #n" (its number among the conflicts),
    /// so the merge window finds it again in the three files.
    /// </summary>
    public static (List<string> Lines, IReadOnlyList<MergeChunk> Chunks) Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right,
        string leftLabel, string baseLabel, string rightLabel, DiffOptions? options = null)
    {
        var chunks = ThreeWayDiff.Compare(baseLines, left, right, options);
        var lines = new List<string>();
        var conflict = 0;
        foreach (var chunk in chunks)
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
                    conflict++;
                    lines.Add($"<<<<<<< {leftLabel} #{conflict}");
                    lines.AddRange(left.Skip(chunk.LeftStart).Take(chunk.LeftCount));
                    lines.Add("||||||| " + baseLabel);
                    lines.AddRange(baseLines.Skip(chunk.BaseStart).Take(chunk.BaseCount));
                    lines.Add("=======");
                    lines.AddRange(right.Skip(chunk.RightStart).Take(chunk.RightCount));
                    lines.Add(">>>>>>> " + rightLabel);
                    break;
            }
        }
        return (lines, chunks);
    }

    /// <summary>The number of a conflict from its label (" #3" → 3); null for markers written elsewhere (git).</summary>
    public static int? NumberOf(ConflictBlock block)
    {
        var hash = block.LeftLabel.LastIndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 && int.TryParse(block.LeftLabel.AsSpan(hash + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
