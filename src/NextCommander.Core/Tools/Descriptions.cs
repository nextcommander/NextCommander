using System.Text;
using NextCommander.Core.Text;

namespace NextCommander.Core.Tools;

/// <summary>
/// File comments in <c>descript.ion</c> (4DOS format): one line per file, <c>name comment</c> or
/// <c>"name with spaces" comment</c>. Multi-line comments store "\n" literally and end with the bytes 0x04 0xC2.
/// The file is written as UTF-8 with BOM when it contains non-ASCII text and kept hidden.
/// </summary>
public static class Descriptions
{
    public const string FileName = "descript.ion";
    private const string MultiLineMarker = "\u0004Â";

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    /// <summary>Character set used when writing (Configuration > Operation).</summary>
    public static Config.DescriptionEncoding Encoding { get; set; } = Config.DescriptionEncoding.Auto;

    /// <summary>Copies (or with <paramref name="move"/> moves) the comments of <paramref name="names"/> to another folder.</summary>
    public static void Carry(string fromDirectory, string toDirectory, IEnumerable<(string From, string To)> names, bool move)
    {
        var source = Read(fromDirectory);
        if (source.Count == 0)
        {
            return;
        }
        var sameFolder = string.Equals(Path.GetFullPath(fromDirectory), Path.GetFullPath(toDirectory), StringComparison.OrdinalIgnoreCase);
        var target = sameFolder ? source : Read(toDirectory);
        var changedSource = false;
        var changedTarget = false;
        foreach (var (from, to) in names)
        {
            if (!source.TryGetValue(from, out var comment))
            {
                continue;
            }
            if (move)
            {
                source.Remove(from);
                changedSource = true;
            }
            target[to] = comment;
            changedTarget = true;
        }
        if (changedTarget)
        {
            Save(toDirectory, target);
        }
        if (changedSource && !sameFolder)
        {
            Save(fromDirectory, source);
        }
    }

    /// <summary>Comments of a folder (file name → comment), names compared case-insensitively.</summary>
    public static Dictionary<string, string> Read(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = PathIn(directory);
        if (!File.Exists(file))
        {
            return result;
        }
        var bytes = File.ReadAllBytes(file);
        var encoding = EncodingDetector.Detect(bytes, out var bom, complete: true);
        foreach (var raw in encoding.GetString(bytes, bom, bytes.Length - bom).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            string name, comment;
            if (line.StartsWith('"'))
            {
                var end = line.IndexOf('"', 1);
                if (end < 0)
                {
                    continue;
                }
                name = line[1..end];
                comment = line[(end + 1)..].TrimStart();
            }
            else
            {
                var space = line.IndexOfAny([' ', '\t']);
                if (space < 0)
                {
                    continue;
                }
                name = line[..space];
                comment = line[(space + 1)..].TrimStart();
            }
            // The marker may arrive decoded as ANSI (\u0004Â) or as UTF-8 bytes read as ANSI.
            var marker = comment.IndexOf('\u0004', StringComparison.Ordinal);
            if (marker >= 0)
            {
                comment = comment[..marker].Replace("\\n", "\n", StringComparison.Ordinal);
            }
            result[name] = comment;
        }
        return result;
    }

    /// <summary>Sets (or with an empty comment removes) the comment of <paramref name="name"/>; deletes an empty descript.ion.</summary>
    public static void Write(string directory, string name, string? comment)
    {
        var comments = Read(directory);
        if (string.IsNullOrWhiteSpace(comment))
        {
            comments.Remove(name);
        }
        else
        {
            comments[name] = comment.TrimEnd();
        }
        Save(directory, comments);
    }

    /// <summary>Moves comments along with renamed / moved files (same folder or another one).</summary>
    public static void Rename(string fromDirectory, string fromName, string toDirectory, string toName)
    {
        var source = Read(fromDirectory);
        if (!source.Remove(fromName, out var comment))
        {
            return;
        }
        Save(fromDirectory, source);
        Write(toDirectory, toName, comment);
    }

    public static void Save(string directory, IReadOnlyDictionary<string, string> comments)
    {
        var file = PathIn(directory);
        if (comments.Count == 0)
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            return;
        }
        var sb = new StringBuilder();
        foreach (var (name, comment) in comments.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(name.Contains(' ', StringComparison.Ordinal) ? $"\"{name}\"" : name).Append(' ');
            var text = comment.Replace("\r\n", "\n", StringComparison.Ordinal);
            sb.Append(text.Contains('\n', StringComparison.Ordinal) ? text.Replace("\n", "\\n", StringComparison.Ordinal) + MultiLineMarker : text);
            sb.Append("\r\n");
        }
        var content = sb.ToString();
        var ascii = content.All(c => c < 0x80 || c == 'Â');
        byte[] bytes = Encoding switch
        {
            Config.DescriptionEncoding.Ansi => EncodingDetector.Ansi.GetBytes(content),
            Config.DescriptionEncoding.Oem => EncodingDetector.Oem.GetBytes(content),
            Config.DescriptionEncoding.Utf8 => [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(content)],
            Config.DescriptionEncoding.Utf16 => [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(content)],
            _ => ascii ? System.Text.Encoding.Latin1.GetBytes(content)
                : [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(content)],
        };
        if (File.Exists(file))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        File.WriteAllBytes(file, bytes);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.Archive);
        }
    }
}
