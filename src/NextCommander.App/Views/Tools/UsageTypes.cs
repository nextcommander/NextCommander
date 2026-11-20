using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Tools;

namespace NextCommander.App.Views.Tools;

/// <summary>A group of file types in the folder sizes (video, audio, programs ...) with its color.</summary>
public sealed record UsageCategory(string Name, Color Color, FileKind[] Kinds);

/// <summary>
/// The file types of the folder sizes: every extension belongs to a category (by <see cref="FileKinds"/>), each
/// category has a hue and its extensions shades of it, so the treemap, the bars and the list of types agree.
/// </summary>
public static class UsageTypes
{
    public static readonly UsageCategory[] Categories =
    [
        new("Video", Color.Parse("#E5484D"), [FileKind.Video]),
        new("Audio", Color.Parse("#3E8ED0"), [FileKind.Audio]),
        new("Programs", Color.Parse("#30A46C"), [FileKind.Executable]),
        new("Archives", Color.Parse("#F0A020"), [FileKind.Archive]),
        new("Images", Color.Parse("#8E4EC6"), [FileKind.Image]),
        new("Documents", Color.Parse("#12A594"), [FileKind.Document, FileKind.Pdf]),
        new("Code and data", Color.Parse("#7C8796"), [FileKind.Code]),
        new("Other", Color.Parse("#A0A0A0"), []),
    ];

    /// <summary>The index of the "Other" category.</summary>
    public static int Other => Categories.Length - 1;

    /// <summary>The category of an extension (".mp4" → Video).</summary>
    public static int CategoryOf(string extension)
    {
        var kind = FileKinds.OfExtension(extension);
        for (var i = 0; i < Other; i++)
        {
            if (Array.IndexOf(Categories[i].Kinds, kind) >= 0)
            {
                return i;
            }
        }
        return Other;
    }

    /// <summary>Lighter (k &gt; 0, towards white) or darker (k &lt; 0) of a color.</summary>
    public static Color Shade(Color c, double k)
    {
        byte F(byte v) => (byte)Math.Clamp(Math.Round(k >= 0 ? v + (255 - v) * k : v * (1 + k)), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }

    /// <summary>
    /// The color of every extension under <paramref name="root"/>: its category's hue, the largest extension of a category
    /// a little darker, the smaller ones lighter.
    /// </summary>
    public static Dictionary<string, Color> Colors(UsageNode root)
    {
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var group in DiskUsage.Extensions(root).GroupBy(e => CategoryOf(e.Extension)))
        {
            var list = group.ToList(); // largest first
            for (var i = 0; i < list.Count; i++)
            {
                var k = list.Count > 1 ? Math.Min(1, i / (double)Math.Min(list.Count - 1, 6)) * 0.5 - 0.18 : 0;
                colors[list[i].Extension] = Shade(Categories[group.Key].Color, k);
            }
        }
        return colors;
    }

    /// <summary>The bytes of every category under each folder (for the bars of the folders), from the files up.</summary>
    public static Dictionary<UsageNode, long[]> Mix(UsageNode root)
    {
        var mix = new Dictionary<UsageNode, long[]>();
        long[] Of(UsageNode node)
        {
            var sizes = new long[Categories.Length];
            var stack = new Stack<(UsageNode Node, bool Done)>();
            stack.Push((node, false));
            while (stack.Count > 0)
            {
                var (n, done) = stack.Pop();
                if (!n.IsDirectory)
                {
                    continue;
                }
                if (!done)
                {
                    stack.Push((n, true));
                    foreach (var child in n.Children.Where(c => c.IsDirectory))
                    {
                        stack.Push((child, false));
                    }
                    continue;
                }
                var own = new long[Categories.Length];
                foreach (var child in n.Children)
                {
                    if (child.IsDirectory)
                    {
                        var sub = mix[child];
                        for (var i = 0; i < own.Length; i++)
                        {
                            own[i] += sub[i];
                        }
                    }
                    else
                    {
                        own[CategoryOf(child.Extension)] += child.Size;
                    }
                }
                mix[n] = own;
            }
            return mix.TryGetValue(node, out var s) ? s : sizes;
        }
        Of(root);
        return mix;
    }
}
