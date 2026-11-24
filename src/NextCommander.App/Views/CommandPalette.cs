using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views;

/// <summary>
/// The command palette (Ctrl+Shift+P, nc_CommandPalette; the user's request, 23 November 2026; its look the user's template
/// of 24 November 2026): a box at the top of the dimmed window. What is typed finds commands, macros, folders and saved
/// connections, in groups; the scopes (Tab, or a first &gt; / @) narrow it. A path typed goes there, a server address
/// connects, anything else can be searched as a file name. ↑ / ↓ choose, Enter runs; Ctrl+Enter / Shift+Enter open a folder
/// in a new tab / the other panel, Ctrl+E edits a connection, Ctrl+C copies a command's name, Ctrl+K changes its shortcut;
/// Esc clears the text, then closes.
/// </summary>
public sealed class CommandPalette
{
    private readonly MainWindowViewModel _vm;
    private readonly Func<IReadOnlyDictionary<string, string>> _categories;
    private readonly Panel _layer;
    private readonly Grid _root;
    private readonly Border _card;
    private readonly TextBox _box;
    private readonly Border _prefix;
    private readonly Button _clear;
    private readonly StackPanel _scopes;
    private readonly TextBlock _count;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _footer;
    private IReadOnlyList<PaletteItem> _items = [];
    private IReadOnlyList<PaletteRow> _rows = [];
    private readonly List<(PaletteMatch Match, Border Row)> _entries = [];
    private PaletteScope _scope;
    private int _selected;
    private string? _note;

    public CommandPalette(Panel layer, MainWindowViewModel vm, Func<IReadOnlyDictionary<string, string>> categories)
    {
        _vm = vm;
        _layer = layer;
        _categories = categories;

        _box = new TextBox { Classes = { "palettebox" }, VerticalAlignment = VerticalAlignment.Stretch };
        _prefix = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _prefix[!Border.BackgroundProperty] = new DynamicResourceExtension("NcAccentSoftBrush");
        _clear = new Button { Classes = { "flat" }, Width = 26, Height = 26, Padding = new Thickness(0), IsVisible = false, Focusable = false, Content = Glyph("MiX", 12, null), Background = Brushes.Transparent };
        ToolTip.SetTip(_clear, "Clear");
        _clear.Click += (_, _) =>
        {
            _box.Text = string.Empty;
            _box.Focus();
        };
        var search = Glyph("MiSearch", 18, null);
        search.Margin = new Thickness(0, 0, 10, 0);
        search.Opacity = 0.6;
        var input = new DockPanel { Height = 52, Margin = new Thickness(14, 0) };
        DockPanel.SetDock(search, Dock.Left);
        DockPanel.SetDock(_prefix, Dock.Left);
        DockPanel.SetDock(_clear, Dock.Right);
        input.Children.AddRange([search, _prefix, _clear, _box]);

        _count = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Classes = { "noloc" } };
        _count[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
        _scopes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var scopes = new DockPanel { Margin = new Thickness(12, 8) };
        DockPanel.SetDock(_count, Dock.Right);
        scopes.Children.AddRange([_count, _scopes]);

        _list = new StackPanel { Margin = new Thickness(6, 4, 6, 8) };
        _scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(14, 8) };
        var footer = new Border { Child = _footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");

        var inputLine = Line(input);
        var scopeLine = Line(scopes);
        DockPanel.SetDock(inputLine, Dock.Top);
        DockPanel.SetDock(scopeLine, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        _card = new Border
        {
            Width = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 56, 16, 40),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new DockPanel { Children = { inputLine, scopeLine, footer, _scroll } },
            Classes = { "palettecard" },
        };
        _card[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");
        _card[!Border.BoxShadowProperty] = new DynamicResourceExtension("NcMwPopupShadow");
        var scrim = new Border { Background = Brushes.Transparent };
        scrim.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        _root = new Grid { IsVisible = false, Children = { scrim, _card } };
        _root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                _selected = 0;
                _note = null;
                Render();
                _scroll.Offset = default;
            }
        };
        layer.Children.Add(_root);
    }

    private static Border Line(Control child)
    {
        var line = new Border { Child = child, BorderThickness = new Thickness(0, 0, 0, 1) };
        line[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return line;
    }

    /// <summary>The palette closed (the window gives the focus back to the panel).</summary>
    public event EventHandler? Closed;

    public bool IsOpen => _root.IsVisible;

    /// <summary>The text box (tests).</summary>
    public TextBox Box => _box;

    /// <summary>The scope searched now (tests).</summary>
    public PaletteScope Scope => CommandPaletteSource.Parse(_box.Text, _scope).Scope;

    /// <summary>The entries shown now, in order (tests).</summary>
    public IReadOnlyList<PaletteItem> Shown => [.. _entries.Select(e => e.Match.Item)];

    /// <summary>The group titles shown now (tests).</summary>
    public IReadOnlyList<string> Groups => [.. _rows.Where(r => r.Group is not null).Select(r => r.Group!)];

    /// <summary>The entry chosen now (tests).</summary>
    public PaletteItem? Chosen => _selected >= 0 && _selected < _entries.Count ? _entries[_selected].Match.Item : null;

    /// <summary>The footer's texts (tests).</summary>
    public string FooterText => string.Join(" ", _footer.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Select(t => t.Text));

    public void Open(string? text = null)
    {
        FitLayer();
        _items = CommandPaletteSource.Items(_vm, _categories());
        _scope = PaletteScope.All;
        _selected = 0;
        _note = null;
        _box.Text = text ?? string.Empty;
        Render();
        _root.IsVisible = true;
        ApplyScrim();
        Dispatcher.UIThread.Post(() =>
        {
            _box.Focus();
            _box.CaretIndex = _box.Text?.Length ?? 0;
        });
    }

    public void Close()
    {
        if (!_root.IsVisible)
        {
            return;
        }
        _root.IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDark => _layer.ActualThemeVariant == ThemeVariant.Dark;

    private Visual? _watched;

    /// <summary>The palette's layer as large as the window's overlay (the dimmed background everywhere), also after resizing.</summary>
    private void FitLayer()
    {
        if (Avalonia.VisualTree.VisualExtensions.GetVisualParent(_layer) is not { } parent)
        {
            return;
        }
        _layer.Width = parent.Bounds.Width;
        _layer.Height = parent.Bounds.Height;
        _card.MaxHeight = Math.Max(240, parent.Bounds.Height - 96);
        if (!ReferenceEquals(_watched, parent))
        {
            _watched = parent;
            parent.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty && IsOpen)
                {
                    FitLayer();
                }
            };
        }
    }

    private void ApplyScrim()
    {
        if (_root.Children[0] is Border scrim)
        {
            scrim.Background = new SolidColorBrush(IsDark ? Color.FromArgb(0x73, 0, 0, 0) : Color.FromArgb(0x2E, 0, 0, 0));
        }
    }

    /// <summary>The color of a kind of entry (its icon tile), as in the template, for the light or the dark theme.</summary>
    private Color KindColor(PaletteKind kind) => (kind, IsDark) switch
    {
        (PaletteKind.Command, false) => Color.Parse("#005fb8"),
        (PaletteKind.Command, true) => Color.Parse("#60cdff"),
        (PaletteKind.Macro, false) => Color.Parse("#8a3ffc"),
        (PaletteKind.Macro, true) => Color.Parse("#c4a2ff"),
        (PaletteKind.Folder, false) => Color.Parse("#b46b00"),
        (PaletteKind.Folder, true) => Color.Parse("#ffc35a"),
        (PaletteKind.Connection or PaletteKind.Address, false) => Color.Parse("#0f7b6c"),
        (PaletteKind.Connection or PaletteKind.Address, true) => Color.Parse("#4fd6c0"),
        (_, false) => Color.Parse("#5c5c5c"),
        _ => Color.Parse("#c8c8c8"),
    };

    // ---------------------------------------------------------------- the special entries of what is typed

    private PaletteItem GoTo(string text) =>
        new("path:" + text, PaletteKind.Path, $"Go to {text}", "Open this location in the active panel", null, null, null, async () =>
        {
            var expanded = text.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..])
                : Environment.ExpandEnvironmentVariables(text);
            if (VfsPath.TryParse(expanded, out var path) && path is not null)
            {
                await _vm.ActivePanel.NavigateAsync(path);
            }
        });

    private PaletteItem Address(string text, bool save) => save
        ? new("save:" + text, PaletteKind.Address, "Save as a new connection…", "Opens the Connection manager with this address", null, null, null,
            () => _vm.SaveAddressAsConnectionAsync(text)) { IconKey = "save" }
        : new("addr:" + text, PaletteKind.Address, $"Connect to {text}", "Quick connect · not saved", null, null, null,
            () => _vm.OpenAddressAsync(text));

    private PaletteItem Find(string text) =>
        new("find:" + text, PaletteKind.Find, $"Find files named “{text}”", "Search the current folder and subfolders (Alt+F7)", null, null, null,
            () => _vm.FindFilesNamedAsync(text));

    // ---------------------------------------------------------------- drawing

    private void Render()
    {
        var (scope, _, prefixed) = CommandPaletteSource.Parse(_box.Text, _scope);
        _rows = CommandPaletteSource.Rows(_box.Text, _scope, _items, _vm.Settings.CommandPaletteRecent, GoTo, Address, Find);
        _list.Children.Clear();
        _entries.Clear();
        foreach (var row in _rows)
        {
            if (row.Match is { } match)
            {
                var index = _entries.Count;
                var line = EntryRow(match, index);
                _entries.Add((match, line));
                _list.Children.Add(line);
            }
            else
            {
                _list.Children.Add(GroupHeader(row));
            }
        }
        if (_entries.Count == 0)
        {
            var empty = new TextBlock { Text = "No matches", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 28) };
            empty[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
            _list.Children.Add(empty);
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _entries.Count - 1));
        MarkSelected();

        // the scope buttons and the count of results
        _scopes.Children.Clear();
        foreach (var (each, key) in new[] { (PaletteScope.All, ""), (PaletteScope.Commands, ">"), (PaletteScope.Folders, "/"), (PaletteScope.Connections, "@") })
        {
            var button = new Button { Classes = { "flat", "palettescope" }, Focusable = false, Padding = new Thickness(10, 4), Tag = each, Background = Brushes.Transparent, CornerRadius = new CornerRadius(5) };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock { Text = CommandPaletteSource.ScopeName(each), Classes = { "noloc" }, FontSize = 12.5, FontWeight = each == scope ? FontWeight.SemiBold : FontWeight.Normal });
            if (key.Length > 0)
            {
                var hint = new TextBlock { Text = key, FontSize = 10.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
                hint[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("NcMonoFont");
                content.Children.Add(hint);
            }
            button.Content = content;
            if (each == scope)
            {
                button[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("NcAccentSoftBrush");
                button[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
            }
            button.Click += (_, _) => SetScope(each);
            _scopes.Children.Add(button);
        }
        var results = _entries.Count(e => e.Match.Item.Kind is not (PaletteKind.Find or PaletteKind.Path or PaletteKind.Address));
        _count.Text = results > 0 ? Core.Localization.Localizer.T(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} results", results)) : string.Empty;

        // the scope a first character chose, the clear button
        _prefix.IsVisible = prefixed;
        if (prefixed)
        {
            var name = new TextBlock { Text = CommandPaletteSource.ScopeName(scope), FontSize = 12, Classes = { "noloc" } };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
            name[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("NcMonoFont");
            _prefix.Child = name;
        }
        _clear.IsVisible = !string.IsNullOrEmpty(_box.Text);
        _box.PlaceholderText = scope switch
        {
            PaletteScope.Commands => "Search commands, names or shortcuts (e.g. f5, ctrl+p)",
            PaletteScope.Folders => "Search folders, or type a path",
            PaletteScope.Connections => "Search connections, or type an address like sftp://host",
            _ => "Search commands, folders and connections",
        };
        RenderFooter();
    }

    private Control GroupHeader(PaletteRow row)
    {
        var line = new DockPanel { Margin = new Thickness(10, 10, 10, 4) };
        if (row.More is { } more)
        {
            var all = new Button
            {
                Classes = { "flat" },
                Padding = new Thickness(0),
                Focusable = false,
                Background = Brushes.Transparent,
                Content = new TextBlock { Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Show all {0}", row.Count), FontSize = 11.5 },
                Tag = more,
            };
            all[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
            all.Click += (_, _) => SetScope(more);
            DockPanel.SetDock(all, Dock.Right);
            line.Children.Add(all);
        }
        var title = new TextBlock { Text = row.Group, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
        line.Children.Add(title);
        return line;
    }

    private Border EntryRow(PaletteMatch match, int index)
    {
        var item = match.Item;
        var color = KindColor(item.Kind);

        // the icon on a tile of its kind's color
        var tile = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)),
            Child = IconOf(item, color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        // title (the letters found highlighted) and the line below it
        var title = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        Highlight(title, item.Title, match.TitleHits);
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, ClipToBounds = true };
        switch (item.Kind)
        {
            case PaletteKind.Command or PaletteKind.Macro:
                sub.Children.Add(Tag(item.Kind == PaletteKind.Macro ? Core.Localization.Localizer.T("Macro") : item.Category));
                if (item.Steps is { Count: > 0 } steps)
                {
                    sub.Children.Add(Faint(string.Join(" → ", steps)));
                }
                sub.Children.Add(Mono(item.Detail, match.DetailHits));
                break;
            case PaletteKind.Folder:
                sub.Children.Add(Mono(item.Detail, match.DetailHits));
                sub.Children.Add(Faint("· " + item.Category));
                break;
            case PaletteKind.Connection:
                sub.Children.Add(Tag(item.Category));
                sub.Children.Add(Mono(item.Detail, match.DetailHits));
                break;
            default:
                sub.Children.Add(Faint(item.Detail));
                break;
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, sub } };

        // on the right: the shortcut; on the chosen row what Enter does
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (item.Gesture is { Length: > 0 } gesture)
        {
            foreach (var key in gesture.Split('+'))
            {
                right.Children.Add(KeyCap(key, 22, 11.5));
            }
        }
        var go = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Tag = "go" };
        var goText = new TextBlock { Text = EnterText(item), FontSize = 12, Classes = { "noloc" } };
        goText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        var enter = Glyph("MiArrowLeft", 12, null);
        enter[!PathIcon.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        go.Children.AddRange([goText, enter]);

        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12), HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Tag = "bar" };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        var content = new DockPanel { Margin = new Thickness(10, 7) };
        DockPanel.SetDock(tile, Dock.Left);
        DockPanel.SetDock(go, Dock.Right);
        DockPanel.SetDock(right, Dock.Right);
        content.Children.AddRange([tile, go, right, texts]);
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            MinHeight = 44,
            Background = Brushes.Transparent,
            Child = new Panel { Children = { content, bar } },
            Tag = index,
        };
        row.PointerMoved += (_, _) =>
        {
            if (_selected != index)
            {
                _selected = index;
                MarkSelected();
                RenderFooter();
            }
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _selected = index;
                _ = RunAsync(item);
            }
        };
        return row;
    }

    private Control IconOf(PaletteItem item, Color color)
    {
        Control icon = item.Kind switch
        {
            PaletteKind.Connection when item.Protocol is { } protocol => Services.Icons.BrandIcons.Create(protocol),
            PaletteKind.Folder => Glyph("MiFolderOpen", 16, color),
            PaletteKind.Path => Glyph("MiArrowRight", 16, color),
            PaletteKind.Find => Glyph("MiSearch", 16, color),
            PaletteKind.Address => Glyph(item.IconKey == "save" ? "MiPlus" : "MiNetwork", 16, color),
            PaletteKind.Macro => MenuIcons.Create("nc_RecordMacro") ?? Glyph("MiHistory", 16, color),
            _ => (item.IconKey is { } key ? MenuIcons.Create(key) : null) ?? Glyph("MiKeyboard", 16, color),
        };
        icon.Width = 16;
        icon.Height = 16;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private static PathIcon Glyph(string key, double size, Color? color)
    {
        var icon = new PathIcon { Width = size, Height = size };
        if (Application.Current?.TryFindResource(key, out var data) == true && data is Geometry geometry)
        {
            icon.Data = geometry;
        }
        if (color is { } c)
        {
            icon.Foreground = new SolidColorBrush(c);
        }
        return icon;
    }

    private static void Highlight(TextBlock block, string text, IReadOnlySet<int> hits)
    {
        if (hits.Count == 0)
        {
            block.Text = text;
            return;
        }
        var inlines = new InlineCollection();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var hit = i < text.Length && hits.Contains(i);
            var previousHit = i > 0 && hits.Contains(i - 1);
            if (i == text.Length || (i > start && hit != previousHit))
            {
                var run = new Run(text[start..i]);
                if (previousHit)
                {
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
                    run.FontWeight = FontWeight.SemiBold;
                }
                inlines.Add(run);
                start = i;
            }
        }
        block.Inlines = inlines;
    }

    private static Border Tag(string? text)
    {
        var tag = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text ?? string.Empty, FontSize = 10.5, Classes = { "noloc" } },
            IsVisible = !string.IsNullOrEmpty(text),
        };
        tag[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwHoverBrush");
        tag[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        tag.Child[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        return tag;
    }

    private static TextBlock Faint(string? text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
        return block;
    }

    private static TextBlock Mono(string text, IReadOnlySet<int> hits)
    {
        var block = Faint(null);
        block.FontSize = 11.5;
        block[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("NcMonoFont");
        Highlight(block, text, hits);
        return block;
    }

    private static Border KeyCap(string key, double height, double fontSize)
    {
        var cap = new Border
        {
            MinWidth = height,
            Height = height,
            Padding = new Thickness(5, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Child = new TextBlock { Text = key, FontSize = fontSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
        };
        cap[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrongStrokeBrush");
        cap[!Border.BackgroundProperty] = new DynamicResourceExtension("NcFieldBrush");
        cap.Child[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        return cap;
    }

    private static string EnterText(PaletteItem item) => Core.Localization.Localizer.T(item.Kind switch
    {
        PaletteKind.Folder => "Open",
        PaletteKind.Connection => "Connect",
        PaletteKind.Path => "Go",
        PaletteKind.Find => "Search",
        PaletteKind.Address => item.IconKey == "save" ? "Open" : "Connect",
        _ => "Run",
    });

    private void MarkSelected()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var (_, row) = _entries[i];
            var on = i == _selected;
            if (on)
            {
                row[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwAccentSoftBrush");
            }
            else
            {
                row.Background = Brushes.Transparent;
            }
            if (row.Child is Panel panel)
            {
                foreach (var child in panel.Children.OfType<Border>().Where(b => Equals(b.Tag, "bar")))
                {
                    child.IsVisible = on;
                }
                if (panel.Children[0] is DockPanel content)
                {
                    foreach (var go in content.Children.OfType<StackPanel>().Where(s => Equals(s.Tag, "go")))
                    {
                        go.IsVisible = on;
                    }
                }
            }
            if (on)
            {
                row.BringIntoView();
            }
        }
    }

    /// <summary>The footer: the keys of the chosen entry, then Tab and Esc.</summary>
    private void RenderFooter()
    {
        _footer.Children.Clear();
        void Hint(string keys, string text)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var key in keys.Split('+'))
            {
                hint.Children.Add(KeyCap(key, 19, 10.5));
            }
            var label = new TextBlock { Text = Core.Localization.Localizer.T(text), FontSize = 12, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
            hint.Children.Add(label);
            _footer.Children.Add(hint);
        }
        switch (Chosen)
        {
            case { Kind: PaletteKind.Folder } folder:
                Hint("Enter", "Open");
                if (folder.RunInNewTab is not null)
                {
                    Hint("Ctrl+Enter", "New tab");
                    Hint("Shift+Enter", "Other panel");
                }
                break;
            case { Kind: PaletteKind.Connection }:
                Hint("Enter", "Connect");
                Hint("Ctrl+E", "Edit");
                break;
            case { IsCommand: true }:
                Hint("Enter", "Run");
                Hint("Ctrl+C", "Copy ID");
                Hint("Ctrl+K", "Change shortcut");
                break;
            case { } other:
                Hint("Enter", EnterText(other));
                break;
        }
        if (_note is { } note)
        {
            var noted = new TextBlock { Text = note, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            noted[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
            _footer.Children.Add(new StackPanel { Children = { noted } });
        }
        Hint("Tab", "Scope");
        Hint("Esc", string.IsNullOrEmpty(_box.Text) ? "Close" : "Clear");
    }

    // ---------------------------------------------------------------- keys and actions

    /// <summary>A scope chosen (its button, Tab, Show all): the first character's scope goes, the words stay.</summary>
    public void SetScope(PaletteScope scope)
    {
        var (_, text, prefixed) = CommandPaletteSource.Parse(_box.Text, _scope);
        _scope = scope;
        _selected = 0;
        if (prefixed)
        {
            _box.Text = text; // renders
        }
        else
        {
            Render();
        }
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _entries.Count;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Down when count > 0:
                Move((_selected + 1) % count);
                break;
            case Key.Up when count > 0:
                Move((_selected - 1 + count) % count);
                break;
            case Key.PageDown when count > 0:
                Move(Math.Min(count - 1, _selected + 8));
                break;
            case Key.PageUp when count > 0:
                Move(Math.Max(0, _selected - 8));
                break;
            case Key.Enter:
                if (Chosen is { } chosen)
                {
                    _ = RunAsync(chosen, ctrl && chosen.RunInNewTab is not null ? chosen.RunInNewTab : shift && chosen.RunInOtherPanel is not null ? chosen.RunInOtherPanel : null);
                }
                break;
            case Key.Escape:
                if (!string.IsNullOrEmpty(_box.Text))
                {
                    _box.Text = string.Empty;
                }
                else
                {
                    Close();
                }
                break;
            case Key.Tab:
                var order = new[] { PaletteScope.All, PaletteScope.Commands, PaletteScope.Folders, PaletteScope.Connections };
                var current = Array.IndexOf(order, Scope);
                SetScope(order[(current + (shift ? order.Length - 1 : 1)) % order.Length]);
                break;
            case Key.P when ctrl && shift:
                Close(); // the palette's own key closes it again
                break;
            case Key.E when ctrl && Chosen is { Edit: { } edit } connection:
                _ = RunAsync(connection, edit);
                break;
            case Key.C when ctrl && _box.SelectionStart == _box.SelectionEnd && Chosen is { CommandName: { } name }:
                _ = CopyAsync(name);
                break;
            case Key.K when ctrl && Chosen is { CommandName: { } command } chosenCommand:
                _ = RunAsync(chosenCommand, () => _vm.ShowCommandBrowserAsync(command));
                break;
            default:
                return; // typing goes to the box
        }
        e.Handled = true;
    }

    private void Move(int index)
    {
        _selected = index;
        MarkSelected();
        RenderFooter();
    }

    private async Task CopyAsync(string name)
    {
        if (TopLevel.GetTopLevel(_layer)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(name);
        }
        _note = Core.Localization.Localizer.T(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Copied {0}", name));
        RenderFooter();
    }

    /// <summary>Closes the palette, then runs the entry (or one of its other actions); remembered as recently used.</summary>
    public async Task RunAsync(PaletteItem item, Func<Task>? action = null)
    {
        if (item.Kind is PaletteKind.Command or PaletteKind.Macro or PaletteKind.Folder or PaletteKind.Connection)
        {
            CommandPaletteSource.Remember(_vm.Settings.CommandPaletteRecent, item.Id);
        }
        Close();
        await Task.Yield(); // the focus back in the panel first: the command works on the active panel
        await (action ?? item.Run)();
    }
}
