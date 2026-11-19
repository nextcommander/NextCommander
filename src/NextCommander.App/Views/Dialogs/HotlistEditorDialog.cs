using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The directory hotlist (Configuration > Edit Hotlist, nc_ConfigDirHotlist): on the left the entries as a tree
/// (folders, commands, submenus, separators) with Add, move up / down / out of a submenu, delete (with Undo), a filter and
/// dragging; on the right the chosen entry (type, name with its menu key, folder and the other panel's folder, or the
/// command and its parameters) and a preview of the menu it is in. Works on a copy: OK writes it back, Cancel drops it.
/// </summary>
public sealed class HotlistEditorDialog : Window
{
    private enum Kind
    {
        Folder,
        Command,
        Menu,
        Separator,
    }

    private sealed class Node
    {
        public Kind Type { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Target { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public string Params { get; set; } = string.Empty;

        public List<Node> Children { get; } = [];
    }

    private static readonly FontFamily s_mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    private static readonly IBrush s_folder = new SolidColorBrush(Color.Parse("#D6A33A"));
    private static readonly IBrush s_command = new SolidColorBrush(Color.Parse("#9470D0"));
    private static readonly IBrush s_warn = new SolidColorBrush(Color.Parse("#C98A00"));

    private readonly List<HotlistEntry> _entries;
    private readonly string? _activeFolder;
    private readonly string? _otherFolder;
    private readonly bool _showMenuIcons;
    private readonly Node _root = new() { Type = Kind.Menu };
    private readonly HashSet<Node> _expanded = [];
    private readonly StackPanel _tree = new() { Margin = new Thickness(8) };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter", Width = 190, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _up = IconButton("MiArrowUp", "Move up (Alt+↑)");
    private readonly Button _down = IconButton("MiArrowDown", "Move down (Alt+↓)");
    private readonly Button _out = IconButton("MiArrowLeft", "Move out of the submenu");
    private readonly Button _delete = IconButton("MiTrash", "Delete (Del)", danger: true);
    private readonly StackPanel _editor = new() { Margin = new Thickness(18) };
    private readonly TextBlock _info = new() { FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _toast = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 76), Padding = new Thickness(14, 6, 6, 6), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 12 36 0 #40000000") };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly List<(Node Node, Border Row)> _rows = [];
    private TextBox? _nameBox;
    private TextBlock? _nameNote;
    private Border? _preview;
    private string _saved;
    private Node? _current;
    private (Node Node, Node Parent, int Index)? _deleted;
    private Node? _dragNode;
    private Point _pressed;
    private bool _dragging;
    private (Node Node, string Zone)? _dropAt;

    public HotlistEditorDialog(List<HotlistEntry> entries, string? activeFolder = null, string? otherFolder = null, bool showMenuIcons = true)
    {
        _entries = entries;
        _activeFolder = activeFolder;
        _otherFolder = otherFolder;
        _showMenuIcons = showMenuIcons;
        Load(entries);
        _saved = Snapshot();
        _current = _root.Children.FirstOrDefault();
        Title = "Directory hotlist";
        Width = 1000;
        Height = 680;
        MinWidth = 860;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _dot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        _toast[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        _toast[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.IsVisible = false;
        };
        _filter.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 14), Margin = new Thickness(10, 0, 2, 0), Opacity = 0.6 };
        Avalonia.Automation.AutomationProperties.SetName(_filter, "Filter entries");
        _filter.TextChanged += (_, _) => RenderTree();
        _up.Click += (_, _) => Move(-1);
        _down.Click += (_, _) => Move(1);
        _out.Click += (_, _) => MoveOut();
        _delete.Click += (_, _) => Delete();
        Content = BuildLayout();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Render();
        Closed += (_, _) => _toastTimer.Stop();
    }

    /// <summary>OK wrote the entries back.</summary>
    public bool Saved { get; private set; }

    /// <summary>The entries as they would be saved now (tests).</summary>
    public IReadOnlyList<HotlistEntry> Entries => ToEntries();

    /// <summary>The names of the rows shown, indented by two spaces per submenu; a separator is "-" (tests).</summary>
    public IReadOnlyList<string> Rows => [.. _rows.Select(r => new string(' ', 2 * Depth(r.Node)) + (r.Node.Type == Kind.Separator ? "-" : r.Node.Name))];

    /// <summary>The chosen entry's name.</summary>
    public string? CurrentName => _current?.Name;

    /// <summary>The note below the name (its warning when names or menu keys clash).</summary>
    public string NameNote => _nameNote?.Text ?? string.Empty;

    public string FilterText
    {
        get => _filter.Text ?? string.Empty;
        set => _filter.Text = value;
    }

    // ---------------------------------------------------------------- the entries

    private void Load(IEnumerable<HotlistEntry> entries)
    {
        var menus = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var parent = _root;
            var key = string.Empty;
            foreach (var group in entry.Groups())
            {
                key += "/" + group;
                if (!menus.TryGetValue(key, out var menu))
                {
                    menu = new Node { Type = Kind.Menu, Name = group };
                    parent.Children.Add(menu);
                    menus[key] = menu;
                    _expanded.Add(menu);
                }
                parent = menu;
            }
            if (entry.IsMenuOnly())
            {
                continue;
            }
            if (entry.IsSeparator())
            {
                parent.Children.Add(new Node { Type = Kind.Separator });
                continue;
            }
            var node = new Node { Name = entry.Leaf() };
            if (entry.IsCommand())
            {
                var line = entry.Path.Trim();
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                node.Type = Kind.Command;
                node.Command = space < 0 ? line : line[..space];
                node.Params = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            }
            else
            {
                node.Type = Kind.Folder;
                node.Path = entry.Path;
                node.Target = entry.Target ?? string.Empty;
            }
            parent.Children.Add(node);
        }
    }

    /// <summary>A name as one part of the stored name: no "/" (it would make a submenu), never empty or "-".</summary>
    private static string Part(string name)
    {
        var text = name.Trim().Replace('/', '∕');
        return text.Length == 0 ? "Untitled" : text == "-" ? "–" : text;
    }

    private List<HotlistEntry> ToEntries()
    {
        var list = new List<HotlistEntry>();
        void Walk(Node menu, string prefix)
        {
            foreach (var c in menu.Children)
            {
                switch (c.Type)
                {
                    case Kind.Menu when c.Children.Count == 0:
                        list.Add(new HotlistEntry { Name = prefix + Part(c.Name) + "/" });
                        break;
                    case Kind.Menu:
                        Walk(c, prefix + Part(c.Name) + "/");
                        break;
                    case Kind.Separator:
                        list.Add(new HotlistEntry { Name = prefix + "-" });
                        break;
                    case Kind.Command:
                        list.Add(new HotlistEntry { Name = prefix + Part(c.Name), Path = (c.Command.Trim() + " " + c.Params.Trim()).Trim() });
                        break;
                    default:
                        list.Add(new HotlistEntry { Name = prefix + Part(c.Name), Path = c.Path.Trim(), Target = string.IsNullOrWhiteSpace(c.Target) ? null : c.Target.Trim() });
                        break;
                }
            }
        }
        Walk(_root, string.Empty);
        return list;
    }

    private string Snapshot() => string.Join("\n", ToEntries().Select(e => e.Name + "\t" + e.Path + "\t" + e.Target));

    private (Node Node, Node Parent)? Find(Node node, Node? within = null)
    {
        within ??= _root;
        foreach (var c in within.Children)
        {
            if (ReferenceEquals(c, node))
            {
                return (c, within);
            }
            if (c.Type == Kind.Menu && Find(node, c) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private int Depth(Node node)
    {
        var depth = 0;
        for (var parent = Find(node)?.Parent; parent is not null && !ReferenceEquals(parent, _root); parent = Find(parent)?.Parent)
        {
            depth++;
        }
        return depth;
    }

    private static int Count(Node menu) => menu.Children.Sum(c => c.Type == Kind.Menu ? Count(c) : c.Type == Kind.Separator ? 0 : 1);

    // ---------------------------------------------------------------- actions

    /// <summary>Adds an entry ("folder", "command", "menu", "separator") after the chosen one, or into the chosen open submenu.</summary>
    public void Add(string type)
    {
        var kind = type switch { "command" => Kind.Command, "menu" => Kind.Menu, "separator" => Kind.Separator, _ => Kind.Folder };
        var parent = _root;
        var index = _root.Children.Count;
        if (_current is { } current && Find(current) is { } found)
        {
            if (current.Type == Kind.Menu && _expanded.Contains(current))
            {
                parent = current;
                index = current.Children.Count;
            }
            else
            {
                parent = found.Parent;
                index = parent.Children.IndexOf(current) + 1;
            }
        }
        var node = new Node
        {
            Type = kind,
            Name = kind switch { Kind.Command => "New command", Kind.Menu => "New submenu", Kind.Separator => string.Empty, _ => "New folder" },
        };
        if (kind == Kind.Menu)
        {
            _expanded.Add(node);
        }
        parent.Children.Insert(index, node);
        _current = node;
        Render();
        if (_nameBox is { } box)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    public void Select(string name)
    {
        _current = _rows.Select(r => r.Node).FirstOrDefault(n => HotlistEntry.Plain(n.Name) == name || n.Name == name) ?? _current;
        Render();
    }

    public void Move(int delta)
    {
        if (_current is not { } node || Find(node) is not { } found)
        {
            return;
        }
        var list = found.Parent.Children;
        var index = list.IndexOf(node);
        var target = index + delta;
        if (target < 0 || target >= list.Count)
        {
            return;
        }
        (list[index], list[target]) = (list[target], list[index]);
        Render();
    }

    /// <summary>Takes the chosen entry out of its submenu: right after the submenu.</summary>
    public void MoveOut()
    {
        if (_current is not { } node || Find(node) is not { } found || ReferenceEquals(found.Parent, _root) || Find(found.Parent) is not { } menu)
        {
            return;
        }
        found.Parent.Children.Remove(node);
        menu.Parent.Children.Insert(menu.Parent.Children.IndexOf(found.Parent) + 1, node);
        Render();
    }

    public void Delete()
    {
        if (_current is not { } node || Find(node) is not { } found)
        {
            return;
        }
        var index = found.Parent.Children.IndexOf(node);
        found.Parent.Children.RemoveAt(index);
        _deleted = (node, found.Parent, index);
        var siblings = found.Parent.Children;
        _current = index < siblings.Count ? siblings[index] : index > 0 ? siblings[index - 1] : !ReferenceEquals(found.Parent, _root) ? found.Parent : _root.Children.FirstOrDefault();
        Render();
        var name = node.Type == Kind.Separator ? Core.Localization.Localizer.T("Separator") : HotlistEntry.Plain(node.Name);
        Toast(Core.Localization.Localizer.T("“{0}” deleted").Replace("{0}", name, StringComparison.Ordinal), undo: true);
    }

    public void Undo()
    {
        if (_deleted is not { } deleted)
        {
            return;
        }
        deleted.Parent.Children.Insert(Math.Min(deleted.Index, deleted.Parent.Children.Count), deleted.Node);
        _current = deleted.Node;
        _deleted = null;
        _toast.IsVisible = false;
        Render();
    }

    /// <summary>Drops an entry before ("t") or after ("b") another, or into a submenu ("in").</summary>
    public void DropOn(string sourceName, string targetName, string zone)
    {
        var source = _rows.Select(r => r.Node).First(n => n.Name == sourceName);
        var target = _rows.Select(r => r.Node).First(n => n.Name == targetName);
        Drop(source, target, zone);
    }

    private void Drop(Node source, Node target, string zone)
    {
        if (ReferenceEquals(source, target) || Find(source) is not { } from)
        {
            return;
        }
        if (source.Type == Kind.Menu && (ReferenceEquals(source, target) || Find(target, source) is not null))
        {
            Toast(Core.Localization.Localizer.T("A submenu can’t go inside itself"));
            return;
        }
        from.Parent.Children.Remove(source);
        if (zone == "in" && target.Type == Kind.Menu)
        {
            target.Children.Add(source);
            _expanded.Add(target);
        }
        else if (Find(target) is { } to)
        {
            var list = to.Parent.Children;
            list.Insert(list.IndexOf(target) + (zone == "b" ? 1 : 0), source);
        }
        _current = source;
        Render();
    }

    /// <summary>OK: the entries go back into the settings' list.</summary>
    public void Save()
    {
        var entries = ToEntries();
        _entries.Clear();
        _entries.AddRange(entries);
        Saved = true;
        Close();
    }

    // ---------------------------------------------------------------- layout

    private static Button IconButton(string glyph, string tip, bool danger = false)
    {
        var button = new Button { Content = DialogLook.Glyph(glyph, 14, danger: danger), Classes = { "tb", "ghost", "icon" } };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private static Control KindIcon(Kind kind, double size = 16)
    {
        switch (kind)
        {
            case Kind.Folder:
                return new PathIcon { Width = size, Height = size, Data = (Geometry?)Application.Current?.FindResource("FmFolderIcon"), Foreground = s_folder, VerticalAlignment = VerticalAlignment.Center };
            case Kind.Command when IconPacks.Glyph("MiTerminal", IconPacks.Current) is { } terminal:
                return new Viewbox { Width = size, Height = size, Child = IconPacks.Draw(terminal, s_command), VerticalAlignment = VerticalAlignment.Center };
            case Kind.Menu:
                return DialogLook.Glyph("MiStacked", size, accent: true);
            default:
                return DialogLook.Glyph("MiMinusCircle", size);
        }
    }

    private Control BuildLayout()
    {
        // left: Add ▾, move, delete, the filter; the tree; a hint
        var addMenu = new MenuFlyout();
        void AddItem(string text, string hint, string type, string icon)
        {
            var header = new StackPanel();
            header.Children.AddRange([new TextBlock { Text = text }, new TextBlock { Text = hint, FontSize = 11.5, Opacity = 0.6 }]);
            var item = new MenuItem { Header = header, Tag = icon };
            item.Click += (_, _) => Add(type);
            addMenu.Items.Add(item);
        }
        AddItem("Folder", "Opens a folder in the panel", "folder", "hotlist.folder");
        AddItem("Command", "Runs a command instead", "command", "hotlist.command");
        AddItem("Submenu", "Groups entries", "menu", "hotlist.submenu");
        AddItem("Separator", "A line between groups", "separator", "hotlist.separator");
        void Icons()
        {
            foreach (var item in addMenu.Items.OfType<MenuItem>())
            {
                item.Icon = _showMenuIcons && item.Tag is string key ? MenuIcons.Create(key) : null;
            }
        }
        addMenu.Opening += (_, _) => Icons();
        addMenu.Opened += (_, _) => Icons();
        var addContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addContent.Children.AddRange([new TextBlock { Text = "+", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }, new TextBlock { Text = "Add", VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "▾", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
        var add = new Button { Content = addContent, Flyout = addMenu, Classes = { "tb", "primary" } };
        var tools = new DockPanel { Margin = new Thickness(14, 12) };
        DockPanel.SetDock(_filter, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        left.Children.AddRange([add, _up, _down, _out, _delete]);
        tools.Children.AddRange([_filter, left]);
        var toolsBorder = new Border { Child = tools, BorderThickness = new Thickness(0, 0, 0, 1) };
        toolsBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var hint = new TextBlock { Text = "Drag entries to reorder; drop onto a submenu to move inside it. Ctrl+D opens this menu in the panel.", FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8) };
        var hintBorder = new Border { Child = hint, BorderThickness = new Thickness(0, 1, 0, 0) };
        hintBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var treeScroll = new ScrollViewer { Content = _tree };
        var leftPanel = new DockPanel();
        DockPanel.SetDock(toolsBorder, Dock.Top);
        DockPanel.SetDock(hintBorder, Dock.Bottom);
        leftPanel.Children.AddRange([toolsBorder, hintBorder, treeScroll]);
        var leftBorder = new Border { Child = leftPanel, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        leftBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush");

        // right: the entry and the preview
        var right = new Border { Child = new ScrollViewer { Content = _editor } };
        right[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380") };
        Grid.SetColumn(right, 1);
        body.Children.AddRange([leftBorder, right]);

        // below: changes; Cancel, OK
        var cancel = new Button { Content = "Cancel", Classes = { "tb" } };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Content = "OK", Classes = { "tb", "primary" }, MinWidth = 80 };
        ok.Click += (_, _) => Save();
        var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        info.Children.AddRange([_dot, _info]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, ok]);
        var foot = new DockPanel { Margin = new Thickness(18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, info]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([footBorder, body]);
        var layers = new Panel();
        layers.Children.AddRange([root, _toast]);
        return layers;
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        RenderTree();
        RenderEditor();
        UpdateInfo();
    }

    /// <summary>A name with its menu key underlined, or with the filter's text marked.</summary>
    private static TextBlock NameText(string name, string query, double size = 13.5)
    {
        var block = new TextBlock { FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        var inlines = new InlineCollection();
        if (string.IsNullOrWhiteSpace(name))
        {
            block.Text = Core.Localization.Localizer.T("Untitled");
            block.FontStyle = FontStyle.Italic;
            block.Opacity = 0.6;
            return block;
        }
        if (query.Length > 0)
        {
            var plain = HotlistEntry.Plain(name);
            var at = plain.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0)
            {
                inlines.Add(new Run(plain));
            }
            else
            {
                inlines.Add(new Run(plain[..at]));
                var mark = new Run(plain.Substring(at, query.Length)) { FontWeight = FontWeight.SemiBold };
                mark[!TextElement.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                inlines.Add(mark);
                inlines.Add(new Run(plain[(at + query.Length)..]));
            }
        }
        else
        {
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] == '&' && i + 1 < name.Length && name[i + 1] == '&')
                {
                    text.Append('&');
                    i++;
                }
                else if (name[i] == '&' && i + 1 < name.Length)
                {
                    inlines.Add(new Run(text.ToString()));
                    text.Clear();
                    inlines.Add(new Underline { Inlines = { new Run(name[i + 1].ToString()) } });
                    i++;
                }
                else
                {
                    text.Append(name[i]);
                }
            }
            inlines.Add(new Run(text.ToString()));
        }
        block.Inlines = inlines;
        return block;
    }

    private void RenderTree()
    {
        _tree.Children.Clear();
        _rows.Clear();
        var query = (_filter.Text ?? string.Empty).Trim();
        bool Match(Node n) => query.Length == 0
            || (HotlistEntry.Plain(n.Name) + " " + n.Path + " " + n.Target + " " + n.Command).Contains(query, StringComparison.CurrentCultureIgnoreCase);
        bool Visible(Node n) => n.Type switch
        {
            Kind.Menu => Match(n) || n.Children.Any(Visible),
            Kind.Separator => query.Length == 0,
            _ => Match(n),
        };
        void Walk(Node menu, int depth)
        {
            foreach (var c in menu.Children.Where(Visible))
            {
                var row = RowView(c, depth, query);
                _rows.Add((c, row));
                _tree.Children.Add(row);
                if (c.Type == Kind.Menu && (_expanded.Contains(c) || query.Length > 0))
                {
                    Walk(c, depth + 1);
                }
            }
        }
        Walk(_root, 0);
        if (_tree.Children.Count == 0)
        {
            var empty = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 50) };
            if (query.Length > 0)
            {
                empty.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "Nothing matches “{0}”.", query), Opacity = 0.7 });
            }
            else
            {
                empty.Children.AddRange([
                    new Border { Child = DialogLook.Glyph("MiStar", 34), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Your hotlist is empty.", HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 },
                    new TextBlock { Text = "Add the folders you open often; Ctrl+D shows them in the panel.", FontSize = 12.5, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center },
                ]);
            }
            _tree.Children.Add(empty);
        }
        var found = _current is { } current ? Find(current) : null;
        _up.IsEnabled = found is { } a && a.Parent.Children.IndexOf(a.Node) > 0;
        _down.IsEnabled = found is { } b && b.Parent.Children.IndexOf(b.Node) < b.Parent.Children.Count - 1;
        _out.IsEnabled = found is { } o && !ReferenceEquals(o.Parent, _root);
        _delete.IsEnabled = found is not null;
    }

    private Border RowView(Node node, int depth, string query)
    {
        var on = ReferenceEquals(node, _current);
        var row = new Border
        {
            Height = node.Type == Kind.Separator ? 22 : 40,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10 + depth * 22, 0, 10, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
            Tag = node,
        };
        if (on)
        {
            row[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        else
        {
            row.Background = Brushes.Transparent;
        }
        var line = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (node.Type == Kind.Separator)
        {
            var rule = new Border { Height = 1, Margin = new Thickness(26, 0, 0, 0) };
            rule[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            line.Children.Add(rule);
        }
        else
        {
            var twisty = new TextBlock { Width = 18, FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            if (node.Type == Kind.Menu)
            {
                twisty.Text = _expanded.Contains(node) || query.Length > 0 ? "▾" : "▸";
                twisty.PointerPressed += (_, e) =>
                {
                    Toggle(node);
                    e.Handled = true;
                };
            }
            var icon = KindIcon(node.Type);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(twisty, Dock.Left);
            DockPanel.SetDock(icon, Dock.Left);
            var name = NameText(node.Name, query);
            name.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(name, Dock.Left);
            line.Children.AddRange([twisty, icon, name]);
            Control detail = node.Type switch
            {
                Kind.Folder when node.Path.Length == 0 => new TextBlock { Text = Core.Localization.Localizer.T("No folder set"), Foreground = s_warn },
                Kind.Folder => new TextBlock { Text = node.Path, Opacity = 0.55 },
                Kind.Command => new TextBlock { Text = (node.Command + " " + node.Params).Trim(), Opacity = 0.55 },
                _ => CountPill(Count(node)),
            };
            if (detail is TextBlock text)
            {
                text.FontFamily = s_mono;
                text.FontSize = 12;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.VerticalAlignment = VerticalAlignment.Center;
                text.Classes.Add("noloc");
            }
            else
            {
                detail.HorizontalAlignment = HorizontalAlignment.Left;
            }
            line.Children.Add(detail);
        }
        if (on)
        {
            var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(-10 - depth * 22, 11, 0, 11), HorizontalAlignment = HorizontalAlignment.Left };
            marker[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            var layered = new Panel();
            layered.Children.AddRange([line, marker]);
            row.Child = layered;
        }
        else
        {
            row.Child = line;
        }
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                return;
            }
            _dragNode = node;
            _dragging = false;
            _pressed = e.GetPosition(_tree);
            if (e.ClickCount == 2 && node.Type == Kind.Menu)
            {
                Toggle(node);
            }
            else if (!ReferenceEquals(_current, node))
            {
                _current = node;
                Render();
            }
            e.Handled = true;
        };
        row.PointerMoved += (_, e) => OnDragMove(e);
        row.PointerReleased += (_, e) => OnDragEnd(e);
        return row;
    }

    private static Border CountPill(int count)
    {
        var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");
        pill[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        pill.Child = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.7, Classes = { "noloc" } };
        return pill;
    }

    private void Toggle(Node menu)
    {
        if (!_expanded.Remove(menu))
        {
            _expanded.Add(menu);
        }
        RenderTree();
    }

    // ---------------------------------------------------------------- dragging

    private void OnDragMove(PointerEventArgs e)
    {
        if (_dragNode is null || !e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var position = e.GetPosition(_tree);
        if (!_dragging && Math.Abs(position.Y - _pressed.Y) < 5)
        {
            return;
        }
        _dragging = true;
        _dropAt = null;
        foreach (var (node, row) in _rows)
        {
            row.BorderThickness = new Thickness(0);
            if (!ReferenceEquals(node, _current) && node.Type == Kind.Menu)
            {
                row.Background = Brushes.Transparent;
            }
            var top = row.TranslatePoint(default, _tree) ?? default;
            if (position.Y < top.Y || position.Y >= top.Y + row.Bounds.Height || ReferenceEquals(node, _dragNode))
            {
                continue;
            }
            var y = (position.Y - top.Y) / row.Bounds.Height;
            var zone = node.Type == Kind.Menu && y is > 0.25 and < 0.75 ? "in" : y < 0.5 ? "t" : "b";
            _dropAt = (node, zone);
            row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            row.BorderThickness = zone switch { "t" => new Thickness(0, 2, 0, 0), "b" => new Thickness(0, 0, 0, 2), _ => new Thickness(1) };
        }
        e.Handled = true;
    }

    private void OnDragEnd(PointerReleasedEventArgs e)
    {
        var source = _dragNode;
        _dragNode = null;
        if (!_dragging || source is null)
        {
            return;
        }
        _dragging = false;
        if (_dropAt is { } at)
        {
            Drop(source, at.Node, at.Zone);
        }
        else
        {
            RenderTree();
        }
        _dropAt = null;
        e.Handled = true;
    }

    // ---------------------------------------------------------------- the entry

    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) };

    private static TextBlock FieldLabel(string text) => new() { Text = text, FontSize = 12, Opacity = 0.75, Margin = new Thickness(1, 0, 0, 5) };

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };

    private static TextBox Field(string? text, string placeholder, bool mono = false)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        if (mono)
        {
            box.FontFamily = s_mono;
            box.FontSize = 12.5;
        }
        return box;
    }

    private static Border Card(Control content)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 18) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    /// <summary>A text box with buttons after it.</summary>
    private static DockPanel WithButtons(TextBox box, params Control[] buttons)
    {
        var line = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 0, 0) };
        tools.Children.AddRange(buttons);
        DockPanel.SetDock(tools, Dock.Right);
        line.Children.AddRange([tools, box]);
        return line;
    }

    private void RenderEditor()
    {
        _editor.Children.Clear();
        _nameBox = null;
        _nameNote = null;
        var found = _current is { } current ? Find(current) : null;
        if (found is not { } entry)
        {
            _editor.Children.Add(PreviewSection(_root));
            return;
        }
        var node = entry.Node;
        var fields = new StackPanel { Spacing = 12 };

        // the type: a non-empty submenu stays a submenu (its entries would be lost)
        var types = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        (Kind Kind, string Text, string Glyph)[] kinds = [(Kind.Folder, "Folder", "MiFolderOpen"), (Kind.Command, "Command", "MiTerminal"), (Kind.Menu, "Submenu", "MiStacked"), (Kind.Separator, "Separator", "MiMinusCircle")];
        for (var i = 0; i < kinds.Length; i++)
        {
            var (kind, text, glyph) = kinds[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.AddRange([DialogLook.Glyph(glyph, 14), new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }]);
            var button = new ToggleButton
            {
                Content = content,
                IsChecked = node.Type == kind,
                Height = 32,
                Padding = new Thickness(10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                IsEnabled = node.Type == kind || node.Type != Kind.Menu || node.Children.Count == 0,
            };
            button.Click += (_, _) => SetType(node, kind);
            types.Children.Add(button);
        }
        var typeField = new StackPanel();
        typeField.Children.AddRange([FieldLabel("Type"), types]);
        fields.Children.Add(typeField);

        if (node.Type != Kind.Separator)
        {
            var name = Field(node.Name, "Shown in the menu");
            Avalonia.Automation.AutomationProperties.SetName(name, "Name");
            _nameBox = name;
            _nameNote = Note(string.Empty);
            name.TextChanged += (_, _) =>
            {
                node.Name = name.Text ?? string.Empty;
                Edited();
            };
            var nameField = new StackPanel();
            nameField.Children.AddRange([FieldLabel("Name"), name, _nameNote]);
            fields.Children.Add(nameField);
            UpdateNameNote();
        }

        switch (node.Type)
        {
            case Kind.Folder:
                var path = Field(node.Path, "D:\\Movie", mono: true);
                Avalonia.Automation.AutomationProperties.SetName(path, "Folder");
                path.TextChanged += (_, _) =>
                {
                    node.Path = path.Text ?? string.Empty;
                    Edited();
                };
                var browse = IconButton("MiFolderOpen", "Browse");
                browse.Classes.Remove("ghost");
                browse.Click += async (_, _) =>
                {
                    var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder" });
                    if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
                    {
                        path.Text = local;
                    }
                };
                var here = IconButton("MiHome", "Use the current folder");
                here.Classes.Remove("ghost");
                here.IsEnabled = _activeFolder is not null;
                here.Click += (_, _) => path.Text = _activeFolder;
                var pathField = new StackPanel();
                pathField.Children.AddRange([FieldLabel("Folder"), WithButtons(path, browse, here), Note("Environment variables work, e.g. %USERPROFILE%.")]);

                var target = Field(node.Target, "Leave empty to keep it", mono: true);
                Avalonia.Automation.AutomationProperties.SetName(target, "Other panel");
                target.TextChanged += (_, _) =>
                {
                    node.Target = target.Text ?? string.Empty;
                    Edited();
                };
                var other = IconButton("MiSplitView", "Use the other panel’s folder");
                other.Classes.Remove("ghost");
                other.IsEnabled = _otherFolder is not null;
                other.Click += (_, _) => target.Text = _otherFolder;
                var targetField = new StackPanel();
                targetField.Children.AddRange([FieldLabel("Other panel (optional)"), WithButtons(target, other), Note("Opens this folder in the other panel at the same time.")]);
                fields.Children.AddRange([pathField, targetField]);
                break;
            case Kind.Command:
                var command = Field(node.Command, "nc_ExecuteTerminal", mono: true);
                Avalonia.Automation.AutomationProperties.SetName(command, "Command");
                var commandNote = Note("A command starts with nc_ or uc_.");
                void CheckCommand()
                {
                    var bad = node.Command.Trim().Length > 0 && !node.Command.TrimStart().StartsWith("nc_", StringComparison.OrdinalIgnoreCase)
                        && !node.Command.TrimStart().StartsWith("uc_", StringComparison.OrdinalIgnoreCase);
                    SetWarning(commandNote, bad);
                }
                command.TextChanged += (_, _) =>
                {
                    // the picker writes "nc_X param": the parameter goes into its own field
                    var text = command.Text ?? string.Empty;
                    node.Command = text;
                    CheckCommand();
                    Edited();
                };
                CheckCommand();
                var pick = CommandPickerDialog.ButtonFor(command);
                pick.Classes.Add("tb");
                var commandField = new StackPanel();
                commandField.Children.AddRange([FieldLabel("Command"), WithButtons(command, pick), commandNote]);
                var parameters = Field(node.Params, "%P", mono: true);
                Avalonia.Automation.AutomationProperties.SetName(parameters, "Parameters");
                parameters.TextChanged += (_, _) =>
                {
                    node.Params = parameters.Text ?? string.Empty;
                    Edited();
                };
                var paramField = new StackPanel();
                paramField.Children.AddRange([FieldLabel("Parameters (optional)"), parameters]);
                fields.Children.AddRange([commandField, paramField]);
                break;
            case Kind.Menu:
                var count = Count(node);
                fields.Children.Add(Note(count == 1
                    ? Core.Localization.Localizer.T("1 entry inside. Drag entries onto it, or use “Move out” to take them back out.")
                    : Core.Localization.Localizer.T("{0} entries inside. Drag entries onto it, or use “Move out” to take them back out.").Replace("{0}", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)));
                break;
            case Kind.Separator:
                fields.Children.Add(Note("Draws a line between groups in the menu."));
                break;
        }
        _editor.Children.AddRange([SectionTitle("Entry"), Card(fields), PreviewSection(entry.Parent)]);
    }

    private void SetType(Node node, Kind kind)
    {
        if (node.Type == kind || (node.Type == Kind.Menu && node.Children.Count > 0))
        {
            RenderEditor();
            return;
        }
        if (kind == Kind.Menu)
        {
            _expanded.Add(node);
        }
        if (kind != Kind.Separator && string.IsNullOrWhiteSpace(node.Name))
        {
            node.Name = kind switch { Kind.Command => "New command", Kind.Menu => "New submenu", _ => "New folder" };
        }
        node.Type = kind;
        Render();
    }

    /// <summary>A field typed into: the tree, the preview and the footer follow; the fields stay.</summary>
    private void Edited()
    {
        RenderTree();
        if (_current is { } current && Find(current) is { } found)
        {
            ReplacePreview(found.Parent);
        }
        UpdateNameNote();
        UpdateInfo();
    }

    /// <summary>The note below the name: the menu key hint, or a warning when the name or its menu key is used twice in the menu.</summary>
    private void UpdateNameNote()
    {
        if (_nameNote is null || _nameBox is null || _current is not { } node || Find(node) is not { } found)
        {
            return;
        }
        var siblings = found.Parent.Children.Where(c => !ReferenceEquals(c, node) && c.Type != Kind.Separator).ToList();
        var plain = HotlistEntry.Plain(node.Name).Trim();
        var duplicate = plain.Length > 0 && siblings.Any(c => string.Equals(HotlistEntry.Plain(c.Name).Trim(), plain, StringComparison.CurrentCultureIgnoreCase));
        var key = HotlistEntry.MenuKey(node.Name);
        var clash = key is { } k && siblings.Any(c => HotlistEntry.MenuKey(c.Name) == k);
        _nameNote.Text = duplicate ? Core.Localization.Localizer.T("Another entry in this menu has the same name.")
            : clash ? Core.Localization.Localizer.T("The menu key “{0}” is used twice in this menu.").Replace("{0}", char.ToUpperInvariant(key!.Value).ToString(), StringComparison.Ordinal)
            : Core.Localization.Localizer.T("Put & before a letter to make it the menu key, e.g. &Movie.");
        _nameNote.Classes.Set("noloc", true);
        SetWarning(_nameNote, duplicate || clash);
        _nameBox.Classes.Set("jsoninvalid", duplicate || clash);
    }

    /// <summary>A note in the warning color, or back to a quiet note.</summary>
    private static void SetWarning(TextBlock note, bool warning)
    {
        if (warning)
        {
            note.Foreground = s_warn;
            note.Opacity = 1;
        }
        else
        {
            note.ClearValue(TextBlock.ForegroundProperty);
            note.Opacity = 0.6;
        }
    }

    // ---------------------------------------------------------------- preview

    private StackPanel PreviewSection(Node menu)
    {
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var where = new TextBlock { Text = ReferenceEquals(menu, _root) ? Core.Localization.Localizer.T("Hotlist menu") : HotlistEntry.Plain(menu.Name), FontSize = 13, Opacity = 0.55, Classes = { "noloc" } };
        DockPanel.SetDock(where, Dock.Right);
        var label = SectionTitle("Preview");
        label.Margin = new Thickness(0);
        title.Children.AddRange([where, label]);
        _preview = new Border { Child = PreviewItems(menu), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), BoxShadow = BoxShadows.Parse("0 6 18 0 #26000000") };
        _preview[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        _preview[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var section = new StackPanel();
        section.Children.AddRange([title, _preview]);
        return section;
    }

    private void ReplacePreview(Node menu)
    {
        if (_preview is not null)
        {
            _preview.Child = PreviewItems(menu);
        }
    }

    /// <summary>The menu as Ctrl+D shows it; the hotlist's own menu ends with its three commands.</summary>
    private StackPanel PreviewItems(Node menu)
    {
        var items = new StackPanel();
        Border Line(Control icon, Control name, Control? right, bool on)
        {
            var row = new DockPanel();
            icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (right is not null)
            {
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
            }
            row.Children.Add(name);
            var border = new Border { Child = row, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(5) };
            if (on)
            {
                border[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
            }
            return border;
        }
        Border Rule()
        {
            var rule = new Border { Height = 1, Margin = new Thickness(6, 4) };
            rule[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            return rule;
        }
        foreach (var c in menu.Children)
        {
            if (c.Type == Kind.Separator)
            {
                items.Children.Add(Rule());
                continue;
            }
            Control right = c.Type == Kind.Menu
                ? new TextBlock { Text = "›", Opacity = 0.6, Classes = { "noloc" } }
                : new TextBlock { Text = c.Type == Kind.Folder ? c.Path : c.Command, FontFamily = s_mono, FontSize = 11, Opacity = 0.55, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            right.Margin = new Thickness(10, 0, 0, 0);
            items.Children.Add(Line(KindIcon(c.Type, 15), NameText(c.Name, string.Empty, 13), right, ReferenceEquals(c, _current)));
        }
        if (menu.Children.Count == 0)
        {
            items.Children.Add(new TextBlock { Text = "Empty", Opacity = 0.6, Margin = new Thickness(10, 6) });
        }
        if (ReferenceEquals(menu, _root))
        {
            items.Children.Add(Rule());
            foreach (var text in new[] { "+ Add this folder...", "Most frequently used...", "Edit..." })
            {
                items.Children.Add(Line(new Panel { Width = 15 }, new TextBlock { Text = text, FontSize = 13, Opacity = 0.75 }, null, false));
            }
        }
        return items;
    }

    // ---------------------------------------------------------------- footer, toast, keys

    private void UpdateInfo()
    {
        var dirty = Snapshot() != _saved;
        var count = Count(_root);
        var entries = count == 1 ? Core.Localization.Localizer.T("1 entry") : Core.Localization.Localizer.T("{0} entries").Replace("{0}", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        _info.Text = dirty ? Core.Localization.Localizer.T("Unsaved changes") + " · " + entries : entries;
        _info.Classes.Set("noloc", true);
        _dot.IsVisible = dirty;
    }

    private void Toast(string text, bool undo = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
        if (undo)
        {
            var button = new Button { Content = "Undo", Classes = { "tb", "ghost" }, Height = 28, MinHeight = 28 };
            button.Click += (_, _) => Undo();
            row.Children.Add(button);
        }
        _toast.Child = row;
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(undo ? 5 : 2);
        _toastTimer.Start();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox box)
        {
            if (e.Key == Key.Escape && ReferenceEquals(box, _filter) && !string.IsNullOrEmpty(_filter.Text))
            {
                _filter.Text = string.Empty;
                e.Handled = true;
            }
            return;
        }
        var visible = _rows.Select(r => r.Node).ToList();
        var index = _current is null ? -1 : visible.IndexOf(_current);
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        switch (e.Key)
        {
            case Key.Up when alt:
                Move(-1);
                break;
            case Key.Down when alt:
                Move(1);
                break;
            case Key.Down when index + 1 < visible.Count:
                _current = visible[index + 1];
                Render();
                break;
            case Key.Up when index > 0:
                _current = visible[index - 1];
                Render();
                break;
            case Key.Right when _current is { Type: Kind.Menu } menu:
                _expanded.Add(menu);
                RenderTree();
                break;
            case Key.Left when _current is { } node:
                if (node.Type == Kind.Menu && _expanded.Remove(node))
                {
                    RenderTree();
                }
                else if (Find(node) is { } found && !ReferenceEquals(found.Parent, _root))
                {
                    _current = found.Parent;
                    Render();
                }
                break;
            case Key.Delete:
                Delete();
                break;
            case Key.N when e.KeyModifiers == KeyModifiers.Control:
                Add("folder");
                break;
            case Key.F2:
                _nameBox?.Focus();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Enter:
                Save();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
