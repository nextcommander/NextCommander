using System.Globalization;
using System.Text;

namespace NextCommander.Core.Compare;

/// <summary>
/// Two binary files side by side as hex dumps (the compare and merge plan, phase 7; 25 November 2026): one row per 16 bytes at
/// the same offset on both sides ("00000010  48 65 6C ...  |Hel...|"), a row that differs is a changed row, the bytes
/// past the end of the shorter file are added or removed rows. The rows go through the compare window as text rows.
/// </summary>
public static class HexCompare
{
    public const int Width = 16;

    /// <summary>Files larger than this are shown up to it (the bytes after it are only counted).</summary>
    public const int MaxShownBytes = 8 * 1024 * 1024;

    /// <summary>The rows of the two dumps.</summary>
    public static IReadOnlyList<DiffLine> Rows(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var shownLeft = left[..Math.Min(left.Length, MaxShownBytes)];
        var shownRight = right[..Math.Min(right.Length, MaxShownBytes)];
        var count = (Math.Max(shownLeft.Length, shownRight.Length) + Width - 1) / Width;
        var rows = new List<DiffLine>(count);
        for (var row = 0; row < count; row++)
        {
            var offset = row * Width;
            var l = offset < shownLeft.Length ? shownLeft.Slice(offset, Math.Min(Width, shownLeft.Length - offset)) : [];
            var r = offset < shownRight.Length ? shownRight.Slice(offset, Math.Min(Width, shownRight.Length - offset)) : [];
            var leftText = l.Length > 0 ? Line(offset, l) : null;
            var rightText = r.Length > 0 ? Line(offset, r) : null;
            var kind = leftText is null ? DiffKind.Added
                : rightText is null ? DiffKind.Removed
                : l.SequenceEqual(r) ? DiffKind.Equal : DiffKind.Changed;
            rows.Add(new DiffLine(kind, leftText is null ? null : row + 1, leftText, rightText is null ? null : row + 1, rightText));
        }
        return rows;
    }

    /// <summary>One row: the offset, the bytes in hex (a gap after the eighth), the printable characters.</summary>
    public static string Line(int offset, ReadOnlySpan<byte> bytes)
    {
        var line = new StringBuilder(80);
        line.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < Width; i++)
        {
            line.Append(i < bytes.Length ? bytes[i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(i == 7 ? "  " : " ");
        }
        line.Append(" |");
        foreach (var b in bytes)
        {
            line.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return line.Append('|').ToString();
    }

    /// <summary>How many bytes differ (in the common length) and whether the sizes differ.</summary>
    public static (long Different, bool SizesDiffer) Count(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        long different = 0;
        var common = Math.Min(left.Length, right.Length);
        for (var i = 0; i < common; i++)
        {
            different += left[i] != right[i] ? 1 : 0;
        }
        return (different, left.Length != right.Length);
    }
}
