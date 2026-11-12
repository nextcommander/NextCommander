namespace NextCommander.Core.Matching;

/// <summary>Case-insensitive "natural" order: file2 &lt; file10.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (si < x.Length && x[si] == '0')
                {
                    si++;
                }
                while (sj < y.Length && y[sj] == '0')
                {
                    sj++;
                }
                var ei = si;
                var ej = sj;
                while (ei < x.Length && char.IsAsciiDigit(x[ei]))
                {
                    ei++;
                }
                while (ej < y.Length && char.IsAsciiDigit(y[ej]))
                {
                    ej++;
                }
                var lenDiff = (ei - si) - (ej - sj);
                if (lenDiff != 0)
                {
                    return lenDiff;
                }
                var cmp = string.CompareOrdinal(x, si, y, sj, ei - si);
                if (cmp != 0)
                {
                    return cmp;
                }
                i = ei;
                j = ej;
                continue;
            }

            var cx = x[i];
            var cy = y[j];
            if (cx != cy)
            {
                int c;
                if (char.IsAscii(cx) && char.IsAscii(cy))
                {
                    // Fast path: ASCII case-insensitive (culture comparison per char is ~50x slower).
                    c = char.ToLowerInvariant(cx).CompareTo(char.ToLowerInvariant(cy));
                }
                else
                {
                    c = string.Compare(x, i, y, j, 1, StringComparison.CurrentCultureIgnoreCase);
                }
                if (c != 0)
                {
                    return c;
                }
            }
            i++;
            j++;
        }
        var rest = (x.Length - i) - (y.Length - j);
        return rest != 0 ? rest : string.Compare(x, y, StringComparison.Ordinal);
    }
}
