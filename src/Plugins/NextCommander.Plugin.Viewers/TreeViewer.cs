using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.Core.Plugins;
using YamlDotNet.RepresentationModel;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// JSON, XML and YAML as a tree that folds: objects, arrays, elements and mappings are branches (with how many items
/// they hold), values are leaves with their type's color. The first two levels are open. At most <see cref="MaxNodes"/>
/// nodes are made; a file that cannot be read shows where it went wrong.
/// </summary>
public sealed class TreeViewer : IViewerPlugin
{
    internal const int MaxNodes = 200_000;

    private static readonly string[] s_json = [".json", ".geojson", ".jsonc", ".har", ".webmanifest"];

    // data files only: .svg stays an image, project and code files (.csproj, .xaml, .config ...) stay text
    private static readonly string[] s_xml = [".xml", ".plist", ".gpx", ".kml", ".resx"];
    private static readonly string[] s_yaml = [".yaml", ".yml"];

    public string Name => "Tree";

    public bool CanView(string fileName, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);
        return s_json.Contains(extension, StringComparer.OrdinalIgnoreCase) || s_xml.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || s_yaml.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A node: its name (key, index, element), its value for a leaf, its kind, its children.</summary>
    internal sealed class Node(string name, string? value, NodeKind kind)
    {
        public string Name { get; } = name;

        public string? Value { get; } = value;

        public NodeKind Kind { get; } = kind;

        public List<Node> Children { get; } = [];
    }

    internal enum NodeKind
    {
        Branch,
        Text,
        Number,
        Boolean,
        Null,
        Attribute,
        Comment,
    }

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 100L * 1024 * 1024, cancellationToken);
        var extension = Path.GetExtension(fileName);
        Node root;
        int count;
        try
        {
            (root, count) = await Task.Run(() =>
            {
                var budget = new Budget();
                var node = s_xml.Contains(extension, StringComparer.OrdinalIgnoreCase) ? FromXml(bytes, budget)
                    : s_yaml.Contains(extension, StringComparer.OrdinalIgnoreCase) ? FromYaml(ViewerHtml.Text(bytes), budget)
                    : FromJson(bytes, budget);
                return (node, budget.Used);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            // where it went wrong, then the text itself
            return new ViewerContent
            {
                Text = ViewerHtml.T($"The file cannot be read as a tree: {ex.Message}") + "\n\n" + ViewerHtml.Text(bytes),
                Status = ViewerHtml.T("Not readable"),
            };
        }
        var tree = new TreeView
        {
            ItemsSource = root.Children,
            ItemTemplate = new FuncTreeDataTemplate<Node>((node, _) => Line(node, dark), node => node.Children),
        };
        tree.Classes.Add("noloc");
        // the first two levels open
        tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem item && item.DataContext is Node node && node.Children.Count > 0 && Depth(item) < 2)
            {
                item.IsExpanded = true;
            }
        };
        var status = ViewerHtml.T($"Nodes: {count.ToString("N0", CultureInfo.CurrentCulture)}")
            + (count >= MaxNodes ? " " + ViewerHtml.T("(the first ones)") : string.Empty);
        return new ViewerContent { Control = tree, Status = status };
    }

    private static int Depth(TreeViewItem item)
    {
        var depth = 0;
        for (var parent = item.Parent; parent is TreeViewItem p; parent = p.Parent)
        {
            depth++;
        }
        return depth;
    }

    /// <summary>"name: value" with the value in its type's color; a branch with its number of items.</summary>
    private static Control Line(Node node, bool dark)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = new TextBlock { Text = node.Name, FontWeight = node.Kind == NodeKind.Branch ? FontWeight.SemiBold : FontWeight.Normal };
        line.Children.Add(name);
        if (node.Kind == NodeKind.Branch)
        {
            line.Children.Add(new TextBlock { Text = $"({node.Children.Count})", Opacity = 0.55 });
            if (node.Value is { Length: > 0 } inline)
            {
                line.Children.Add(new TextBlock { Text = inline, Foreground = Color(NodeKind.Text, dark), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 });
            }
            return line;
        }
        line.Children.Add(new TextBlock { Text = node.Value ?? string.Empty, Foreground = Color(node.Kind, dark), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 });
        return line;
    }

    private static IBrush Color(NodeKind kind, bool dark) => new SolidColorBrush(Avalonia.Media.Color.Parse(kind switch
    {
        NodeKind.Text => dark ? "#CE9178" : "#A31515",
        NodeKind.Number => dark ? "#B5CEA8" : "#098658",
        NodeKind.Boolean or NodeKind.Null => dark ? "#569CD6" : "#0000FF",
        NodeKind.Attribute => dark ? "#9CDCFE" : "#E50000",
        NodeKind.Comment => dark ? "#6A9955" : "#008000",
        _ => dark ? "#D4D4D4" : "#1F1F1F",
    }));

    /// <summary>How many nodes may still be made.</summary>
    private sealed class Budget
    {
        public int Used { get; private set; }

        public bool Take()
        {
            if (Used >= MaxNodes)
            {
                return false;
            }
            Used++;
            return true;
        }
    }

    // ---------------------------------------------------------------- JSON

    internal static Node FromJson(byte[] bytes) => FromJson(bytes, new Budget());

    private static Node FromJson(byte[] bytes, Budget budget)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 512 });
        var root = new Node(string.Empty, null, NodeKind.Branch);
        Add(root, "$", document.RootElement, budget);
        return root;
    }

    private static void Add(Node parent, string name, JsonElement element, Budget budget)
    {
        if (!budget.Take())
        {
            return;
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Node(name, null, NodeKind.Branch);
                parent.Children.Add(obj);
                foreach (var property in element.EnumerateObject())
                {
                    Add(obj, property.Name, property.Value, budget);
                }
                break;
            case JsonValueKind.Array:
                var array = new Node(name, null, NodeKind.Branch);
                parent.Children.Add(array);
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Add(array, $"[{index++}]", item, budget);
                }
                break;
            case JsonValueKind.String:
                parent.Children.Add(new Node(name, "\"" + element.GetString() + "\"", NodeKind.Text));
                break;
            case JsonValueKind.Number:
                parent.Children.Add(new Node(name, element.GetRawText(), NodeKind.Number));
                break;
            case JsonValueKind.True or JsonValueKind.False:
                parent.Children.Add(new Node(name, element.GetRawText(), NodeKind.Boolean));
                break;
            default:
                parent.Children.Add(new Node(name, "null", NodeKind.Null));
                break;
        }
    }

    // ---------------------------------------------------------------- XML

    internal static Node FromXml(byte[] bytes) => FromXml(bytes, new Budget());

    private static Node FromXml(byte[] bytes, Budget budget)
    {
        // no DTD, no outside entities
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = new Node(string.Empty, null, NodeKind.Branch);
        if (document.Root is { } element)
        {
            Add(root, element, budget);
        }
        return root;
    }

    private static void Add(Node parent, XElement element, Budget budget)
    {
        if (!budget.Take())
        {
            return;
        }
        var elements = element.Elements().ToList();
        var text = elements.Count == 0 ? element.Value.Trim() : null;
        var node = new Node(element.Name.LocalName, null, NodeKind.Branch);
        foreach (var attribute in element.Attributes())
        {
            if (budget.Take())
            {
                node.Children.Add(new Node("@" + attribute.Name.LocalName, attribute.Value, NodeKind.Attribute));
            }
        }
        if (elements.Count == 0 && node.Children.Count == 0)
        {
            // <a>text</a>: a leaf
            parent.Children.Add(new Node(element.Name.LocalName, text, NodeKind.Text));
            return;
        }
        if (!string.IsNullOrEmpty(text))
        {
            node.Children.Add(new Node("#text", text, NodeKind.Text));
        }
        parent.Children.Add(node);
        foreach (var child in elements)
        {
            Add(node, child, budget);
        }
    }

    // ---------------------------------------------------------------- YAML

    internal static Node FromYaml(string text) => FromYaml(text, new Budget());

    private static Node FromYaml(string text, Budget budget)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var root = new Node(string.Empty, null, NodeKind.Branch);
        for (var i = 0; i < stream.Documents.Count; i++)
        {
            Add(root, stream.Documents.Count == 1 ? "$" : $"--- {i + 1}", stream.Documents[i].RootNode, budget);
        }
        return root;
    }

    private static void Add(Node parent, string name, YamlNode yaml, Budget budget)
    {
        if (!budget.Take())
        {
            return;
        }
        switch (yaml)
        {
            case YamlMappingNode mapping:
                var map = new Node(name, null, NodeKind.Branch);
                parent.Children.Add(map);
                foreach (var (key, value) in mapping.Children)
                {
                    Add(map, (key as YamlScalarNode)?.Value ?? key.ToString(), value, budget);
                }
                break;
            case YamlSequenceNode sequence:
                var list = new Node(name, null, NodeKind.Branch);
                parent.Children.Add(list);
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    Add(list, $"[{index++}]", item, budget);
                }
                break;
            case YamlScalarNode scalar:
                var text = scalar.Value;
                var kind = scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted ? NodeKind.Text
                    : text is null or "~" or "null" or "Null" or "NULL" ? NodeKind.Null
                    : text is "true" or "false" or "True" or "False" or "TRUE" or "FALSE" ? NodeKind.Boolean
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? NodeKind.Number
                    : NodeKind.Text;
                parent.Children.Add(new Node(name, text ?? "null", kind));
                break;
            default:
                parent.Children.Add(new Node(name, yaml.ToString(), NodeKind.Text));
                break;
        }
    }
}
