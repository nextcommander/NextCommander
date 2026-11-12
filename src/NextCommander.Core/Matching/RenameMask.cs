using System.Text;

namespace NextCommander.Core.Matching;

/// <summary>
/// Target name mask used by copy/move (F5/F6), e.g. <c>*.bak</c>, <c>old_*.*</c>, <c>*_v?.txt</c>.
/// '*' in the name part is the source base name, '*' in the extension part the source extension,
/// '?' the source character at the same position.
/// </summary>
public static class RenameMask
{
    public static bool IsMask(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains('*', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal));

    public static string Apply(string sourceName, string mask)
    {
        if (string.IsNullOrEmpty(mask) || mask == "*.*" || mask == "*")
        {
            return sourceName;
        }

        SplitExtension(sourceName, out var srcBase, out var srcExt);
        var maskDot = mask.LastIndexOf('.');
        if (maskDot < 0)
        {
            // No extension part in the mask: it applies to the whole name.
            return Expand(mask, sourceName);
        }

        var nameMask = mask[..maskDot];
        var extMask = mask[(maskDot + 1)..];
        var name = Expand(nameMask, srcBase);
        var ext = Expand(extMask, srcExt);
        return ext.Length == 0 ? name : name + "." + ext;
    }

    private static string Expand(string mask, string source)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < mask.Length; i++)
        {
            switch (mask[i])
            {
                case '*':
                    sb.Append(source);
                    break;
                case '?':
                    if (i < source.Length)
                    {
                        sb.Append(source[i]);
                    }
                    break;
                default:
                    sb.Append(mask[i]);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void SplitExtension(string name, out string baseName, out string ext)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0)
        {
            baseName = name;
            ext = string.Empty;
        }
        else
        {
            baseName = name[..dot];
            ext = name[(dot + 1)..];
        }
    }
}
