using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Compare;

/// <summary>One line of a hunk: ' ' context, '-' removed, '+' added.</summary>
public readonly record struct PatchLine(char Op, string Text);

/// <summary>A hunk of a unified diff (0-based starts; a count of 0: the place after which lines go).</summary>
public sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<PatchLine> Lines)
{
    /// <summary>The last old / new line has no line end ("\ No newline at end of file").</summary>
    public bool OldNoNewline { get; init; }

    public bool NewNoNewline { get; init; }
}

/// <summary>The changes of one file in a patch.</summary>
public sealed record FilePatch(string OldPath, string NewPath, IReadOnlyList<PatchHunk> Hunks);

/// <summary>A patch applied: the lines, the hunks that did not fit (their indices), whether the result ends with a line end.</summary>
public sealed record PatchResult(IReadOnlyList<string> Lines, IReadOnlyList<int> Rejected, bool EndsWithNewline);

/// <summary>Unified diffs (patch files): written from two line lists, read back, and applied to a file's lines.</summary>
public static partial class UnifiedDiff
{
    /// <summary>
    /// A unified diff of two line lists with <paramref name="context"/> lines around each change; empty when they are the
    /// same. <paramref name="oldEndsWithNewline"/> / <paramref name="newEndsWithNewline"/>: whether the last line of each
    /// has a line end.
    /// </summary>
    public static string Create(string oldPath, string newPath, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines,
        int context = 3, DiffOptions? options = null, bool oldEndsWithNewline = true, bool newEndsWithNewline = true)
    {
        var hunks = TextDiff.Hunks(oldLines, newLines, options).ToList();
        // a different last line end alone is a change of the last line
        if (hunks.Count == 0 && oldEndsWithNewline == newEndsWithNewline)
        {
            return string.Empty;
        }
        if (oldEndsWithNewline != newEndsWithNewline && oldLines.Count > 0 && newLines.Count > 0
            && (hunks.Count == 0 || hunks[^1].LeftStart + hunks[^1].LeftCount < oldLines.Count))
        {
            hunks.Add(new DiffHunk(oldLines.Count - 1, 1, newLines.Count - 1, 1));
        }
        else if (hunks.Count > 0 && hunks[^1] is var end && end.LeftStart + end.LeftCount == oldLines.Count)
        {
            // a last line without its end that gets lines after it (or loses those after it) changes: it gains (loses) its end
            if (end.LeftCount == 0 && !oldEndsWithNewline && end.LeftStart > 0)
            {
                hunks[^1] = new DiffHunk(end.LeftStart - 1, 1, end.RightStart - 1, end.RightCount + 1);
            }
            else if (end.RightCount == 0 && !newEndsWithNewline && end.RightStart > 0)
            {
                hunks[^1] = new DiffHunk(end.LeftStart - 1, end.LeftCount + 1, end.RightStart - 1, 1);
            }
        }
        var text = new StringBuilder();
        text.Append("--- ").Append(oldPath).Append('\n');
        text.Append("+++ ").Append(newPath).Append('\n');
        for (var i = 0; i < hunks.Count;)
        {
            // hunks closer than twice the context go together
            var first = i;
            while (i + 1 < hunks.Count && hunks[i + 1].LeftStart - (hunks[i].LeftStart + hunks[i].LeftCount) <= 2 * context)
            {
                i++;
            }
            var last = i;
            i++;
            var oldStart = Math.Max(0, hunks[first].LeftStart - context);
            var oldEnd = Math.Min(oldLines.Count, hunks[last].LeftStart + hunks[last].LeftCount + context);
            var newStart = hunks[first].RightStart - (hunks[first].LeftStart - oldStart);
            var newEnd = hunks[last].RightStart + hunks[last].RightCount + (oldEnd - (hunks[last].LeftStart + hunks[last].LeftCount));
            text.Append("@@ -").Append(Range(oldStart, oldEnd - oldStart)).Append(" +").Append(Range(newStart, newEnd - newStart)).Append(" @@\n");
            var o = oldStart;
            for (var h = first; h <= last; h++)
            {
                for (; o < hunks[h].LeftStart; o++)
                {
                    Line(' ', oldLines[o], o == oldLines.Count - 1 && !oldEndsWithNewline);
                }
                for (var k = 0; k < hunks[h].LeftCount; k++)
                {
                    var index = hunks[h].LeftStart + k;
                    Line('-', oldLines[index], index == oldLines.Count - 1 && !oldEndsWithNewline);
                }
                for (var k = 0; k < hunks[h].RightCount; k++)
                {
                    var index = hunks[h].RightStart + k;
                    Line('+', newLines[index], index == newLines.Count - 1 && !newEndsWithNewline);
                }
                o = hunks[h].LeftStart + hunks[h].LeftCount;
            }
            for (; o < oldEnd; o++)
            {
                Line(' ', oldLines[o], o == oldLines.Count - 1 && !oldEndsWithNewline);
            }
        }
        return text.ToString();

        void Line(char op, string line, bool noNewline)
        {
            text.Append(op).Append(line).Append('\n');
            if (noNewline)
            {
                text.Append("\\ No newline at end of file\n");
            }
        }
    }

    private static string Range(int start, int count) =>
        count == 1 ? (start + 1).ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{(count == 0 ? start : start + 1)},{count}");

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>The files of a patch (unified diffs; git's extended headers are skipped).</summary>
    public static IReadOnlyList<FilePatch> Parse(string patch)
    {
        var files = new List<FilePatch>();
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("--- ", StringComparison.Ordinal) || i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                continue;
            }
            var oldPath = PathOf(lines[i][4..]);
            var newPath = PathOf(lines[i + 1][4..]);
            i += 2;
            var hunks = new List<PatchHunk>();
            while (i < lines.Length && HunkHeader().Match(lines[i]) is { Success: true } header)
            {
                int Number(int group, int fallback) => header.Groups[group].Success ? int.Parse(header.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;
                var oldCount = Number(2, 1);
                var newCount = Number(4, 1);
                var oldStart = oldCount == 0 ? Number(1, 0) : Number(1, 1) - 1;
                var newStart = newCount == 0 ? Number(3, 0) : Number(3, 1) - 1;
                var body = new List<PatchLine>();
                bool oldNoNewline = false, newNoNewline = false;
                int oldSeen = 0, newSeen = 0;
                i++;
                while (i < lines.Length && (oldSeen < oldCount || newSeen < newCount || (i < lines.Length && lines[i].StartsWith('\\'))))
                {
                    var line = lines[i];
                    if (line.StartsWith('\\'))
                    {
                        // belongs to the line before it
                        var previous = body.Count > 0 ? body[^1].Op : ' ';
                        oldNoNewline |= previous is '-' or ' ';
                        newNoNewline |= previous is '+' or ' ';
                        i++;
                        continue;
                    }
                    var op = line.Length == 0 ? ' ' : line[0];
                    if (op is not (' ' or '-' or '+'))
                    {
                        break;
                    }
                    body.Add(new PatchLine(op, line.Length == 0 ? string.Empty : line[1..]));
                    oldSeen += op is ' ' or '-' ? 1 : 0;
                    newSeen += op is ' ' or '+' ? 1 : 0;
                    i++;
                }
                hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, body) { OldNoNewline = oldNoNewline, NewNoNewline = newNoNewline });
            }
            files.Add(new FilePatch(oldPath, newPath, hunks));
            i--;
        }
        return files;
    }

    /// <summary>"a/src/x.cs\t2026-..." → "a/src/x.cs".</summary>
    private static string PathOf(string text)
    {
        var tab = text.IndexOf('\t', StringComparison.Ordinal);
        return (tab < 0 ? text : text[..tab]).Trim();
    }

    /// <summary>
    /// A file's patch applied to its lines: each hunk is looked for where it says, then ever further away (the file may
    /// have moved on); a hunk whose old lines are nowhere is rejected and left out.
    /// </summary>
    public static PatchResult Apply(IReadOnlyList<string> lines, FilePatch patch, bool endsWithNewline = true)
    {
        var result = lines.ToList();
        var rejected = new List<int>();
        var shift = 0;
        var newline = endsWithNewline;
        for (var h = 0; h < patch.Hunks.Count; h++)
        {
            var hunk = patch.Hunks[h];
            var old = hunk.Lines.Where(l => l.Op != '+').Select(l => l.Text).ToList();
            var expected = Math.Clamp(hunk.OldStart + shift, 0, result.Count);
            var at = Find(result, old, expected);
            if (at < 0)
            {
                rejected.Add(h);
                continue;
            }
            var replacement = hunk.Lines.Where(l => l.Op != '-').Select(l => l.Text).ToList();
            result.RemoveRange(at, old.Count);
            result.InsertRange(at, replacement);
            shift += replacement.Count - old.Count + (at - expected);
            if (at + replacement.Count == result.Count && (hunk.OldNoNewline || hunk.NewNoNewline))
            {
                newline = !hunk.NewNoNewline;
            }
        }
        return new PatchResult(result, rejected, newline);
    }

    /// <summary>Where <paramref name="old"/> occurs in the lines, nearest to <paramref name="expected"/> first; -1 when nowhere.</summary>
    private static int Find(List<string> lines, List<string> old, int expected)
    {
        bool At(int start)
        {
            if (start < 0 || start + old.Count > lines.Count)
            {
                return false;
            }
            for (var i = 0; i < old.Count; i++)
            {
                if (lines[start + i] != old[i])
                {
                    return false;
                }
            }
            return true;
        }
        for (var distance = 0; distance <= lines.Count; distance++)
        {
            if (At(expected - distance))
            {
                return expected - distance;
            }
            if (distance > 0 && At(expected + distance))
            {
                return expected + distance;
            }
        }
        return -1;
    }
}
