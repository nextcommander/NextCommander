using System.Text.RegularExpressions;

namespace NextCommander.Core.Compare;

public enum DiffKind
{
    Equal,
    /// <summary>Only on the left (deleted).</summary>
    Removed,
    /// <summary>Only on the right (inserted).</summary>
    Added,
    /// <summary>Changed line: shown side by side with in-line differences.</summary>
    Changed,
}

/// <summary>One aligned row of the side-by-side view (a missing side is null).</summary>
public sealed record DiffLine(DiffKind Kind, int? LeftNumber, string? Left, int? RightNumber, string? Right)
{
    /// <summary>
    /// The row belongs to a difference the options say to ignore (only blank lines, or only lines of the ignore filters):
    /// shown, but not counted or visited.
    /// </summary>
    public bool Ignored { get; init; }

    /// <summary>A removed line that is added elsewhere (or the other way round): the index of that row.</summary>
    public int? MovedTo { get; init; }
}

/// <summary>A run of equal or different characters inside a changed line.</summary>
public readonly record struct InlineSpan(int Start, int Length, bool Different);

/// <summary>How finely a changed line's differences are shown.</summary>
public enum InlineLevel
{
    /// <summary>Character by character.</summary>
    Character,

    /// <summary>Whole words (letters / digits), runs of spaces and single other characters.</summary>
    Word,
}

/// <summary>A filter that changes each line before it is compared (substitution filters).</summary>
public sealed record DiffSubstitution(Regex Pattern, string Replacement);

public sealed class DiffOptions
{
    public bool IgnoreCase { get; init; }

    /// <summary>Ignore leading/trailing whitespace and treat runs of spaces as one.</summary>
    public bool IgnoreWhitespace { get; init; }

    /// <summary>Ignore every space and tab ("a b" = "ab").</summary>
    public bool IgnoreAllWhitespace { get; init; }

    /// <summary>A difference made of blank lines only is ignored.</summary>
    public bool IgnoreBlankLines { get; init; }

    /// <summary>
    /// A line's end ("\r" left at its end by a caller that keeps the line ends) does not count. On by default; a caller
    /// that keeps "\r" and turns it off sees CRLF against LF as a difference.
    /// </summary>
    public bool IgnoreLineEndings { get; init; } = true;

    /// <summary>A difference whose lines all match one of these is ignored (line filters).</summary>
    public IReadOnlyList<Regex> IgnoreLines { get; init; } = [];

    /// <summary>Replacements made in each line before it is compared (the lines shown stay as they are).</summary>
    public IReadOnlyList<DiffSubstitution> Substitutions { get; init; } = [];

    /// <summary>Removed lines added elsewhere are linked (<see cref="DiffLine.MovedTo"/>).</summary>
    public bool DetectMoved { get; init; }

    /// <summary>The removed and added lines of a difference are paired by how alike they are, not by their order.</summary>
    public bool MatchSimilarLines { get; init; } = true;
}

/// <summary>
/// One difference: its rows in the side-by-side view and the lines it covers on each side (a count of 0: the place where
/// lines would go). <see cref="Ignored"/>: a difference the options ignore.
/// </summary>
public sealed record DiffBlock(int FirstRow, int RowCount, int LeftStart, int LeftCount, int RightStart, int RightCount, bool Ignored);

/// <summary>
/// One difference of two line lists as the edit script gives it: <see cref="LeftCount"/> lines from
/// <see cref="LeftStart"/> became <see cref="RightCount"/> lines from <see cref="RightStart"/> (0-based).
/// </summary>
public readonly record struct DiffHunk(int LeftStart, int LeftCount, int RightStart, int RightCount);

/// <summary>
/// Line based diff (Myers O(ND) in linear space, the middle snake divided and conquered, with GNU diff's heuristic when a
/// part gets too expensive) producing side-by-side rows, plus character or word level differences of changed lines.
/// </summary>
public static class TextDiff
{
    /// <summary>Beyond this many steps of one middle-snake search the best split found so far is taken.</summary>
    private const int TooExpensive = 4096;

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var (a, b) = Ids(left, right, options);
        var hunks = Hunks(a, b);

        var rows = new List<DiffLine>(Math.Max(a.Length, b.Length) + 8);
        int li = 0, ri = 0;
        foreach (var hunk in hunks)
        {
            while (li < hunk.LeftStart)
            {
                rows.Add(new DiffLine(DiffKind.Equal, li + 1, left[li], ri + 1, right[ri]));
                li++;
                ri++;
            }
            var firstRow = rows.Count;
            AddBlock(rows, left, right, hunk, options);
            if (IsIgnored(left, right, hunk, options))
            {
                for (var r = firstRow; r < rows.Count; r++)
                {
                    rows[r] = rows[r] with { Ignored = true };
                }
            }
            li = hunk.LeftStart + hunk.LeftCount;
            ri = hunk.RightStart + hunk.RightCount;
        }
        while (li < left.Count)
        {
            rows.Add(new DiffLine(DiffKind.Equal, li + 1, left[li], ri + 1, right[ri]));
            li++;
            ri++;
        }
        if (options.DetectMoved)
        {
            MarkMoved(rows, options);
        }
        return rows;
    }

    /// <summary>The differences of two line lists as hunks of the edit script (no rows; 3-way merges and patches).</summary>
    public static IReadOnlyList<DiffHunk> Hunks(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        var (a, b) = Ids(left, right, options ?? new DiffOptions());
        return Hunks(a, b);
    }

    /// <summary>The differences of the rows (consecutive rows that are not equal), with the lines each covers.</summary>
    public static IReadOnlyList<DiffBlock> Blocks(IReadOnlyList<DiffLine> rows)
    {
        var blocks = new List<DiffBlock>();
        int left = 0, right = 0;
        for (var i = 0; i < rows.Count;)
        {
            if (rows[i].Kind == DiffKind.Equal)
            {
                left++;
                right++;
                i++;
                continue;
            }
            var first = i;
            int leftStart = left, rightStart = right;
            var ignored = true;
            while (i < rows.Count && rows[i].Kind != DiffKind.Equal)
            {
                left += rows[i].Left is null ? 0 : 1;
                right += rows[i].Right is null ? 0 : 1;
                ignored &= rows[i].Ignored;
                i++;
            }
            blocks.Add(new DiffBlock(first, i - first, leftStart, left - leftStart, rightStart, right - rightStart, ignored));
        }
        return blocks;
    }

    private static (int[] A, int[] B) Ids(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions options)
    {
        // Lines become integers: equal lines share an id (fast comparison, options applied once).
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line)
        {
            var key = Normalize(line, options);
            if (!ids.TryGetValue(key, out var id))
            {
                id = ids.Count;
                ids[key] = id;
            }
            return id;
        }
        return ([.. left.Select(Id)], [.. right.Select(Id)]);
    }

    /// <summary>A line as it is compared: the options applied.</summary>
    public static string Normalize(string line, DiffOptions options)
    {
        if (options.IgnoreLineEndings && line.EndsWith('\r'))
        {
            line = line[..^1];
        }
        foreach (var substitution in options.Substitutions)
        {
            line = substitution.Pattern.Replace(line, substitution.Replacement);
        }
        if (options.IgnoreAllWhitespace)
        {
            line = string.Concat(line.Where(c => c is not (' ' or '\t')));
        }
        else if (options.IgnoreWhitespace)
        {
            line = string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        return options.IgnoreCase ? line.ToUpperInvariant() : line;
    }

    /// <summary>Whether the options ignore a difference: only blank lines, or only lines the ignore filters match.</summary>
    private static bool IsIgnored(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffHunk hunk, DiffOptions options)
    {
        if (!options.IgnoreBlankLines && options.IgnoreLines.Count == 0)
        {
            return false;
        }
        bool Skippable(string line) =>
            (options.IgnoreBlankLines && string.IsNullOrWhiteSpace(line)) || options.IgnoreLines.Any(r => r.IsMatch(line));
        for (var i = 0; i < hunk.LeftCount; i++)
        {
            if (!Skippable(left[hunk.LeftStart + i]))
            {
                return false;
            }
        }
        for (var i = 0; i < hunk.RightCount; i++)
        {
            if (!Skippable(right[hunk.RightStart + i]))
            {
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- rows of one difference

    /// <summary>
    /// The rows of one difference: removed and added lines that are alike sit side by side as changed rows (an alignment
    /// of the most alike pairs, in order); between those, the rest are paired by position as a block is shown.
    /// </summary>
    private static void AddBlock(List<DiffLine> rows, IReadOnlyList<string> left, IReadOnlyList<string> right, DiffHunk hunk, DiffOptions options)
    {
        var anchors = options.MatchSimilarLines && hunk.LeftCount > 0 && hunk.RightCount > 0 && (long)hunk.LeftCount * hunk.RightCount <= 40_000
            ? AlignSimilar(left, right, hunk, options)
            : [];
        int l = hunk.LeftStart, r = hunk.RightStart;
        int lEnd = hunk.LeftStart + hunk.LeftCount, rEnd = hunk.RightStart + hunk.RightCount;
        void Gap(int lTo, int rTo)
        {
            while (l < lTo && r < rTo)
            {
                rows.Add(new DiffLine(DiffKind.Changed, l + 1, left[l], r + 1, right[r]));
                l++;
                r++;
            }
            while (l < lTo)
            {
                rows.Add(new DiffLine(DiffKind.Removed, l + 1, left[l], null, null));
                l++;
            }
            while (r < rTo)
            {
                rows.Add(new DiffLine(DiffKind.Added, null, null, r + 1, right[r]));
                r++;
            }
        }
        foreach (var (pl, pr) in anchors)
        {
            Gap(pl, pr); // the lines before a pair: by position, the rest alone, so the pair stays on one row
            rows.Add(new DiffLine(DiffKind.Changed, pl + 1, left[pl], pr + 1, right[pr]));
            l = pl + 1;
            r = pr + 1;
        }
        Gap(lEnd, rEnd);
    }

    /// <summary>The pairs of most alike lines of a difference, in order (a sequence alignment on their likeness).</summary>
    private static List<(int Left, int Right)> AlignSimilar(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffHunk hunk, DiffOptions options)
    {
        int n = hunk.LeftCount, m = hunk.RightCount;
        var leftGrams = new HashSet<int>[n];
        var rightGrams = new HashSet<int>[m];
        for (var i = 0; i < n; i++)
        {
            leftGrams[i] = Bigrams(Normalize(left[hunk.LeftStart + i], options));
        }
        for (var j = 0; j < m; j++)
        {
            rightGrams[j] = Bigrams(Normalize(right[hunk.RightStart + j], options));
        }
        const double threshold = 0.4;
        var score = new double[n + 1, m + 1];
        var take = new double[n, m];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var like = Dice(leftGrams[i - 1], rightGrams[j - 1]);
                take[i - 1, j - 1] = like;
                var best = Math.Max(score[i - 1, j], score[i, j - 1]);
                if (like >= threshold)
                {
                    best = Math.Max(best, score[i - 1, j - 1] + like);
                }
                score[i, j] = best;
            }
        }
        var pairs = new List<(int, int)>();
        for (int i = n, j = m; i > 0 && j > 0;)
        {
            if (take[i - 1, j - 1] >= threshold && Math.Abs(score[i, j] - (score[i - 1, j - 1] + take[i - 1, j - 1])) < 1e-9)
            {
                pairs.Add((hunk.LeftStart + i - 1, hunk.RightStart + j - 1));
                i--;
                j--;
            }
            else if (Math.Abs(score[i, j] - score[i - 1, j]) < 1e-9)
            {
                i--;
            }
            else
            {
                j--;
            }
        }
        pairs.Reverse();
        return pairs;
    }

    private static HashSet<int> Bigrams(string text)
    {
        var grams = new HashSet<int>();
        var trimmed = text.Trim();
        for (var i = 0; i + 1 < trimmed.Length; i++)
        {
            grams.Add((trimmed[i] << 16) | trimmed[i + 1]);
        }
        if (trimmed.Length == 1)
        {
            grams.Add(trimmed[0]);
        }
        return grams;
    }

    /// <summary>How alike two lines are (Dice coefficient of their character pairs, 0..1).</summary>
    private static double Dice(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var common = small.Count(large.Contains);
        return 2.0 * common / (a.Count + b.Count);
    }

    /// <summary>
    /// Removed lines added elsewhere (and the other way round) are linked: a line counts when it is not blank, not a
    /// lone brace or the like, and occurs once among the removed and once among the added lines.
    /// </summary>
    private static void MarkMoved(List<DiffLine> rows, DiffOptions options)
    {
        var removed = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var added = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Ignored)
            {
                continue;
            }
            if (row.Kind == DiffKind.Removed)
            {
                Add(removed, row.Left!, i);
            }
            else if (row.Kind == DiffKind.Added)
            {
                Add(added, row.Right!, i);
            }
        }
        foreach (var (key, from) in removed)
        {
            if (from.Count == 1 && added.TryGetValue(key, out var to) && to.Count == 1)
            {
                rows[from[0]] = rows[from[0]] with { MovedTo = to[0] };
                rows[to[0]] = rows[to[0]] with { MovedTo = from[0] };
            }
        }

        void Add(Dictionary<string, List<int>> map, string line, int row)
        {
            var key = Normalize(line, options).Trim();
            if (key.Count(char.IsLetterOrDigit) < 2)
            {
                return; // blank lines, braces: everywhere, not a move
            }
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }
            list.Add(row);
        }
    }

    // ---------------------------------------------------------------- the edit script

    /// <summary>The differences of two id sequences (see <see cref="Script"/>).</summary>
    private static List<DiffHunk> Hunks(int[] a, int[] b)
    {
        var (removed, added) = Script(a, b);
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && !removed[i] && !added[j])
            {
                i++;
                j++;
                continue;
            }
            int si = i, sj = j;
            while (i < a.Length && removed[i])
            {
                i++;
            }
            while (j < b.Length && added[j])
            {
                j++;
            }
            hunks.Add(new DiffHunk(si, i - si, sj, j - sj));
        }
        return hunks;
    }

    /// <summary>
    /// Which items of <paramref name="a"/> are removed and which of <paramref name="b"/> are added (Myers' middle snake,
    /// divided and conquered as GNU diff does: memory linear in the input).
    /// </summary>
    private static (bool[] Removed, bool[] Added) Script(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        var removed = new bool[n];
        var added = new bool[m];
        var offset = m + 1;
        var fd = new int[n + m + 3];
        var bd = new int[n + m + 3];
        var work = new Stack<(int XOff, int XLim, int YOff, int YLim)>();
        work.Push((0, n, 0, m));
        while (work.Count > 0)
        {
            var (xoff, xlim, yoff, ylim) = work.Pop();
            while (xoff < xlim && yoff < ylim && a[xoff] == b[yoff])
            {
                xoff++;
                yoff++;
            }
            while (xlim > xoff && ylim > yoff && a[xlim - 1] == b[ylim - 1])
            {
                xlim--;
                ylim--;
            }
            if (xoff == xlim)
            {
                for (var y = yoff; y < ylim; y++)
                {
                    added[y] = true;
                }
                continue;
            }
            if (yoff == ylim)
            {
                for (var x = xoff; x < xlim; x++)
                {
                    removed[x] = true;
                }
                continue;
            }
            var (xmid, ymid) = Split(a, b, xoff, xlim, yoff, ylim, fd, bd, offset);
            work.Push((xmid, xlim, ymid, ylim));
            work.Push((xoff, xmid, yoff, ymid));
        }
        return (removed, added);
    }

    /// <summary>A point on an optimal path through the box (the middle snake), or a good one when that gets too expensive.</summary>
    private static (int X, int Y) Split(int[] a, int[] b, int xoff, int xlim, int yoff, int ylim, int[] fd, int[] bd, int o)
    {
        int dmin = xoff - ylim, dmax = xlim - yoff;
        int fmid = xoff - yoff, bmid = xlim - ylim;
        int fmin = fmid, fmax = fmid, bmin = bmid, bmax = bmid;
        var odd = ((fmid - bmid) & 1) != 0;
        fd[o + fmid] = xoff;
        bd[o + bmid] = xlim;
        for (var c = 1; ; c++)
        {
            // forward: one more step on each diagonal
            if (fmin > dmin)
            {
                fd[o + --fmin - 1] = -1;
            }
            else
            {
                ++fmin;
            }
            if (fmax < dmax)
            {
                fd[o + ++fmax + 1] = -1;
            }
            else
            {
                --fmax;
            }
            for (var d = fmax; d >= fmin; d -= 2)
            {
                int tlo = fd[o + d - 1], thi = fd[o + d + 1];
                var x = tlo >= thi ? tlo + 1 : thi;
                var y = x - d;
                while (x < xlim && y < ylim && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                fd[o + d] = x;
                if (odd && bmin <= d && d <= bmax && bd[o + d] <= x)
                {
                    return (x, y);
                }
            }
            // backward
            if (bmin > dmin)
            {
                bd[o + --bmin - 1] = int.MaxValue;
            }
            else
            {
                ++bmin;
            }
            if (bmax < dmax)
            {
                bd[o + ++bmax + 1] = int.MaxValue;
            }
            else
            {
                --bmax;
            }
            for (var d = bmax; d >= bmin; d -= 2)
            {
                int tlo = bd[o + d - 1], thi = bd[o + d + 1];
                var x = tlo < thi ? tlo : thi - 1;
                var y = x - d;
                while (x > xoff && y > yoff && a[x - 1] == b[y - 1])
                {
                    x--;
                    y--;
                }
                bd[o + d] = x;
                if (!odd && fmin <= d && d <= fmax && x <= fd[o + d])
                {
                    return (x, y);
                }
            }
            if (c < TooExpensive)
            {
                continue;
            }
            // too expensive: the furthest point reached forward or backward, whichever went further
            int fxybest = -1, fxbest = xoff;
            for (var d = fmax; d >= fmin; d -= 2)
            {
                var x = Math.Min(fd[o + d], xlim);
                var y = x - d;
                if (ylim < y)
                {
                    x = ylim + d;
                    y = ylim;
                }
                if (fxybest < x + y)
                {
                    fxybest = x + y;
                    fxbest = x;
                }
            }
            int bxybest = int.MaxValue, bxbest = xlim;
            for (var d = bmax; d >= bmin; d -= 2)
            {
                var x = Math.Max(xoff, bd[o + d]);
                var y = x - d;
                if (y < yoff)
                {
                    x = yoff + d;
                    y = yoff;
                }
                if (x + y < bxybest)
                {
                    bxybest = x + y;
                    bxbest = x;
                }
            }
            return (xlim + ylim) - bxybest < fxybest - (xoff + yoff)
                ? (fxbest, fxybest - fxbest)
                : (bxbest, bxybest - bxbest);
        }
    }

    // ---------------------------------------------------------------- in-line differences

    /// <summary>Character (or word) level differences of two changed lines.</summary>
    public static (IReadOnlyList<InlineSpan> Left, IReadOnlyList<InlineSpan> Right) InlineDiff(string left, string right, InlineLevel level = InlineLevel.Character)
    {
        var (leftTokens, rightTokens) = level == InlineLevel.Word ? (Words(left), Words(right)) : (Characters(left), Characters(right));
        if (leftTokens.Count * (long)rightTokens.Count > 25_000_000)
        {
            return ([new InlineSpan(0, left.Length, true)], [new InlineSpan(0, right.Length, true)]);
        }
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id((int Start, int Length) token, string text)
        {
            var key = text.Substring(token.Start, token.Length);
            if (!ids.TryGetValue(key, out var id))
            {
                ids[key] = id = ids.Count;
            }
            return id;
        }
        var a = leftTokens.Select(t => Id(t, left)).ToArray();
        var b = rightTokens.Select(t => Id(t, right)).ToArray();
        var (removed, added) = Script(a, b);
        var l = new List<InlineSpan>();
        var r = new List<InlineSpan>();
        for (var i = 0; i < leftTokens.Count; i++)
        {
            Append(l, leftTokens[i], removed[i]);
        }
        for (var j = 0; j < rightTokens.Count; j++)
        {
            Append(r, rightTokens[j], added[j]);
        }
        return (l, r);
    }

    private static List<(int Start, int Length)> Characters(string text) => [.. Enumerable.Range(0, text.Length).Select(i => (i, 1))];

    /// <summary>Words (letters, digits, _), runs of spaces, and every other character alone.</summary>
    private static List<(int Start, int Length)> Words(string text)
    {
        var tokens = new List<(int, int)>();
        for (var i = 0; i < text.Length;)
        {
            var start = i;
            if (char.IsLetterOrDigit(text[i]) || text[i] == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }
            else if (text[i] is ' ' or '\t')
            {
                while (i < text.Length && text[i] is ' ' or '\t')
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
            tokens.Add((start, i - start));
        }
        return tokens;
    }

    private static void Append(List<InlineSpan> spans, (int Start, int Length) token, bool different)
    {
        if (spans.Count > 0 && spans[^1].Different == different && spans[^1].Start + spans[^1].Length == token.Start)
        {
            spans[^1] = spans[^1] with { Length = spans[^1].Length + token.Length };
        }
        else
        {
            spans.Add(new InlineSpan(token.Start, token.Length, different));
        }
    }
}
