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
public sealed record DiffLine(DiffKind Kind, int? LeftNumber, string? Left, int? RightNumber, string? Right);

/// <summary>A run of equal or different characters inside a changed line.</summary>
public readonly record struct InlineSpan(int Start, int Length, bool Different);

public sealed class DiffOptions
{
    public bool IgnoreCase { get; init; }

    /// <summary>Ignore leading/trailing whitespace and treat runs of spaces as one.</summary>
    public bool IgnoreWhitespace { get; init; }
}

/// <summary>Line based diff (Myers O(ND)) producing side-by-side rows, plus character level differences of changed lines.</summary>
public static class TextDiff
{
    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
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
        var a = left.Select(Id).ToArray();
        var b = right.Select(Id).ToArray();

        // Common prefix / suffix are cheap and frequent.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }
        var edits = Myers(a.AsSpan(prefix, a.Length - prefix - suffix).ToArray(), b.AsSpan(prefix, b.Length - prefix - suffix).ToArray());

        var rows = new List<DiffLine>(Math.Max(a.Length, b.Length));
        for (var i = 0; i < prefix; i++)
        {
            rows.Add(new DiffLine(DiffKind.Equal, i + 1, left[i], i + 1, right[i]));
        }
        int li = prefix, ri = prefix;
        var removed = new List<int>();
        var added = new List<int>();
        void FlushBlock()
        {
            // Pair removed/added lines of one block as "changed" rows, the rest stays removed/added.
            var pairs = Math.Min(removed.Count, added.Count);
            for (var k = 0; k < pairs; k++)
            {
                rows.Add(new DiffLine(DiffKind.Changed, removed[k] + 1, left[removed[k]], added[k] + 1, right[added[k]]));
            }
            for (var k = pairs; k < removed.Count; k++)
            {
                rows.Add(new DiffLine(DiffKind.Removed, removed[k] + 1, left[removed[k]], null, null));
            }
            for (var k = pairs; k < added.Count; k++)
            {
                rows.Add(new DiffLine(DiffKind.Added, null, null, added[k] + 1, right[added[k]]));
            }
            removed.Clear();
            added.Clear();
        }
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case 0:
                    FlushBlock();
                    rows.Add(new DiffLine(DiffKind.Equal, li + 1, left[li], ri + 1, right[ri]));
                    li++;
                    ri++;
                    break;
                case -1:
                    removed.Add(li++);
                    break;
                default:
                    added.Add(ri++);
                    break;
            }
        }
        FlushBlock();
        for (var k = 0; k < suffix; k++)
        {
            rows.Add(new DiffLine(DiffKind.Equal, li + k + 1, left[li + k], ri + k + 1, right[ri + k]));
        }
        return rows;
    }

    private static string Normalize(string line, DiffOptions options)
    {
        if (options.IgnoreWhitespace)
        {
            line = string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        return options.IgnoreCase ? line.ToUpperInvariant() : line;
    }

    /// <summary>Edit script: 0 = keep, -1 = delete from a, +1 = insert from b.</summary>
    private static List<int> Myers(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var script = new List<int>(max);
        if (n == 0 || m == 0)
        {
            script.AddRange(Enumerable.Repeat(-1, n));
            script.AddRange(Enumerable.Repeat(1, m));
            return script;
        }
        // Very different large inputs: give up on a minimal script (memory grows with D²) and show one block.
        const int maxEdits = 10_000;
        var v = new int[(2 * max) + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= Math.Min(max, maxEdits) && !found; d++)
        {
            var snapshot = new int[(2 * d) + 3];
            var from = max - d - 1;
            var skip = from < 0 ? -from : 0;
            Array.Copy(v, from + skip, snapshot, skip, Math.Min(snapshot.Length - skip, v.Length - (from + skip)));
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[max + k - 1] < v[max + k + 1]))
                {
                    x = v[max + k + 1];
                }
                else
                {
                    x = v[max + k - 1] + 1;
                }
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }
        if (!found)
        {
            script.AddRange(Enumerable.Repeat(-1, n));
            script.AddRange(Enumerable.Repeat(1, m));
            return script;
        }
        // Backtrack. trace[d] holds v before step d for k in [-d-1, d+1] (offset d+1).
        int px = n, py = m;
        var reversed = new List<int>(max);
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var vd = trace[d];
            int V(int k) => vd[k + d + 1];
            var k = px - py;
            var prevK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var prevX = V(prevK);
            var prevY = prevX - prevK;
            while (px > prevX && py > prevY)
            {
                reversed.Add(0);
                px--;
                py--;
            }
            reversed.Add(px == prevX ? 1 : -1);
            px = prevX;
            py = prevY;
        }
        while (px > 0 && py > 0)
        {
            reversed.Add(0);
            px--;
            py--;
        }
        reversed.Reverse();
        return reversed;
    }

    /// <summary>Character level differences of two changed lines (common prefix/suffix + middle, recursively by LCS of words).</summary>
    public static (IReadOnlyList<InlineSpan> Left, IReadOnlyList<InlineSpan> Right) InlineDiff(string left, string right)
    {
        var a = left.Select(c => (int)c).ToArray();
        var b = right.Select(c => (int)c).ToArray();
        var script = a.Length * (long)b.Length > 4_000_000 ? null : Myers(a, b);
        if (script is null)
        {
            return ([new InlineSpan(0, left.Length, true)], [new InlineSpan(0, right.Length, true)]);
        }
        var l = new List<InlineSpan>();
        var r = new List<InlineSpan>();
        int li = 0, ri = 0;
        foreach (var op in script)
        {
            switch (op)
            {
                case 0:
                    Append(l, li++, false);
                    Append(r, ri++, false);
                    break;
                case -1:
                    Append(l, li++, true);
                    break;
                default:
                    Append(r, ri++, true);
                    break;
            }
        }
        return (l, r);
    }

    private static void Append(List<InlineSpan> spans, int index, bool different)
    {
        if (spans.Count > 0 && spans[^1].Different == different && spans[^1].Start + spans[^1].Length == index)
        {
            spans[^1] = spans[^1] with { Length = spans[^1].Length + 1 };
        }
        else
        {
            spans.Add(new InlineSpan(index, 1, different));
        }
    }
}
