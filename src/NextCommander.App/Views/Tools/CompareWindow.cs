using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using NextCommander.App.Services;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Compare;
using NextCommander.Core.Config;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// Compare by content (user's design, 15 November 2026): a bar with the file, the counts of changes and the controls
/// (side by side / inline, ignore case and whitespace, previous / next difference with a counter, copy a difference to
/// the other side, undo / redo, save, compare again); the two file names tagged Original and Current; the aligned lines
/// with their numbers, − / + signs, syntax colors, the changed characters marked, the missing side hatched and the
/// current difference framed; an overview of all differences on the right edge (click to go there); a status line.
/// F2 / Shift+F2 (Alt+↓ / Alt+↑) go through the differences, F5 compares again, Esc closes. Binary files get a list of
/// differing offsets. As in the compare and merge plan (25 November 2026, phase 2): both sides are edited in place, a
/// difference is copied to the other side (Alt+→ / Alt+←, the arrows in the margin), undo / redo cover both sides, and
/// saving keeps each file's encoding, byte order mark and line ends (CompareWindow.Sides.cs, CompareWindow.Merge.cs).
/// </summary>
public sealed partial class CompareWindow : Window
{
    private const long MaxTextBytes = 64L * 1024 * 1024;

    private readonly ToolContext _context;
    private Side _l;
    private Side _r;

    // the bar
    private readonly TextBlock _badge = new() { FontWeight = FontWeight.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly Border _badgeBox = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(9) };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly StackPanel _stats = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ToggleButton _split = new() { IsChecked = true };
    private readonly ToggleButton _inline = new();
    private readonly ToggleSwitch _ignoreCase = Switch("Ignore case");
    private readonly ToggleSwitch _ignoreSpace = Switch("Ignore whitespace");
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly TextBlock _counter = new() { MinWidth = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Classes = { "noloc" } };
    private readonly Button _copyLeft = new();
    private readonly Button _copyRight = new();
    private readonly Button _undo = new();
    private readonly Button _redo = new();
    private readonly Button _save = new();
    private readonly Button _more = new();
    private readonly Button _compare = new() { Classes = { "accent" } };
    private readonly List<Border> _groups = [];

    // the panel
    private readonly Border _panel = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly Grid _files = new();
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,*,14") };
    private readonly DiffPane _leftPane;
    private readonly DiffPane _rightPane;
    private readonly DiffPane _inlinePane;
    private readonly OverviewRuler _ruler;
    private readonly ListBox _binaryList = new() { IsVisible = false };
    private readonly TextBlock _empty = new() { IsVisible = false, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 48), TextWrapping = TextWrapping.Wrap };

    // below
    private readonly TextBlock _summary = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly Border _toast = new() { IsVisible = false, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), IsHitTestVisible = false };
    private readonly TextBlock _toastText = new() { FontSize = 13 };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };

    private IReadOnlyList<DiffLine> _rows = [];
    private List<Hunk> _hunks = [];
    private int _currentBlock = -1;
    private bool _syncing;
    private Palette _palette = Palette.Light;

    public CompareWindow(VfsEntry left, VfsEntry right, ToolContext context)
    {
        _l = new Side(left);
        _r = new Side(right);
        _context = context;
        UpdateTitle();
        Width = 1200;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        EncodingDetector.EnsureCodePages();

        // the compare settings (Configuration > Operation/Compare): the font, and the switches as they were left
        var options = context.Settings.CompareOptions;
        var font = new FontFamily(string.IsNullOrWhiteSpace(options.Font) ? context.Settings.ListerFont : options.Font);
        var size = options.FontSize > 0 ? options.FontSize : context.Settings.ListerFontSize;
        _ignoreCase.IsChecked = options.IgnoreCase;
        _ignoreSpace.IsChecked = options.Whitespace != CompareWhitespace.Compare;
        _leftPane = new DiffPane(this, font, size, Highlighting(left), numberColumns: 1, isLeft: true);
        _rightPane = new DiffPane(this, font, size, Highlighting(right), numberColumns: 1, isLeft: false);
        _inlinePane = new DiffPane(this, font, size, Highlighting(right), numberColumns: 2, isLeft: null);
        _ruler = new OverviewRuler(this);
        foreach (var (pane, other) in new[] { (_leftPane, _rightPane), (_rightPane, _leftPane) })
        {
            // the two sides scroll together (their lines are aligned)
            pane.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            {
                if (_syncing)
                {
                    return;
                }
                _syncing = true;
                var offset = pane.Editor.TextArea.TextView.ScrollOffset;
                other.Editor.ScrollToVerticalOffset(offset.Y);
                other.Editor.ScrollToHorizontalOffset(offset.X);
                _syncing = false;
            };
        }

        _split.Click += (_, _) => SetView(inline: false);
        _inline.Click += (_, _) => SetView(inline: true);
        _ignoreCase.IsCheckedChanged += (_, _) =>
        {
            Options.IgnoreCase = _ignoreCase.IsChecked == true;
            _ = CompareAsync();
        };
        _ignoreSpace.IsCheckedChanged += (_, _) =>
        {
            // on: the spaces as the settings ignore them (changes in their number unless all were chosen)
            Options.Whitespace = _ignoreSpace.IsChecked != true ? CompareWhitespace.Compare
                : Options.Whitespace == CompareWhitespace.Compare ? CompareWhitespace.IgnoreChanges : Options.Whitespace;
            _ = CompareAsync();
        };
        _previous.Click += (_, _) => MoveToBlock(-1);
        _next.Click += (_, _) => MoveToBlock(+1);
        _copyLeft.Click += (_, _) => _ = CopyBlockAsync(_currentBlock, toRight: false);
        _copyRight.Click += (_, _) => _ = CopyBlockAsync(_currentBlock, toRight: true);
        _undo.Click += (_, _) => _ = UndoAsync();
        _redo.Click += (_, _) => _ = RedoAsync();
        _save.Click += (_, _) => _ = SaveAllAsync();
        _more.Click += (_, _) => OpenMoreMenu();
        _compare.Click += (_, _) => _ = RecompareAsync();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.IsVisible = false;
        };
        WireEditing();

        Content = BuildLayout();
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        ApplyPalette();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += async (_, _) => await CompareAsync();
        Closed += (_, _) => StopWatching();
    }

    private static IHighlightingDefinition? Highlighting(VfsEntry entry) => HighlightingManager.Instance.GetDefinitionByExtension("." + entry.Extension);

    // ---------------------------------------------------------------- what tests and callers see

    public IReadOnlyList<DiffLine> Rows => _rows;

    /// <summary>The differences: runs of lines that are not equal.</summary>
    public int BlockCount => _hunks.Count;

    /// <summary>The current difference (framed; -1: none).</summary>
    public int CurrentBlock => _currentBlock;

    public bool IsBinary { get; private set; }

    public IReadOnlyList<string> BinaryDifferences => _binaryList.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>The counter ("2 / 3") and the summary below.</summary>
    public string StatusText => $"{_counter.Text} — {_summary.Text}";

    /// <summary>The counter of the differences ("2 / 3").</summary>
    public string CounterText => _counter.Text ?? string.Empty;

    /// <summary>Side by side (false) or one column (true).</summary>
    public bool IsInline => _inline.IsChecked == true;

    /// <summary>The lines of the left / right file as they are now (edits included once compared).</summary>
    public IReadOnlyList<string> LeftLines => _l.Lines;

    public IReadOnlyList<string> RightLines => _r.Lines;

    /// <summary>The left / right file has changes not saved.</summary>
    public bool LeftModified => _l.Dirty;

    public bool RightModified => _r.Dirty;

    /// <summary>The editors of the left and right side (side by side).</summary>
    public TextEditor LeftEditor => _leftPane.Editor;

    public TextEditor RightEditor => _rightPane.Editor;

    /// <summary>The last message of the toast ("Compared · 2 differences").</summary>
    public string LastToast => _toastText.Text ?? string.Empty;

    public Task? Comparing { get; private set; }

    // ---------------------------------------------------------------- layout

    private static ToggleSwitch Switch(string text) => new() { OnContent = text, OffContent = text, MinWidth = 0, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };

    private Border Group(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.AddRange(children);
        var group = new Border { Child = panel, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(3) };
        _groups.Add(group);
        return group;
    }

    private static Control Glyph(string geometry) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(geometry),
        Width = 16,
        Height = 16,
        Stretch = Stretch.None,
        StrokeThickness = 1.5,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel IconText(Control icon, string? text, string? key = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        if (text is not null)
        {
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        if (key is not null)
        {
            row.Children.Add(KeyBox(key));
        }
        return row;
    }

    private static Border KeyBox(string text) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 10.5, Classes = { "noloc" } },
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Tag = "kbd",
    };

    private static void Style(Button button, double width = double.NaN)
    {
        button.Height = 30;
        button.Width = width;
        button.Padding = new Thickness(double.IsNaN(width) ? 10 : 0, 0);
        button.CornerRadius = new CornerRadius(7);
        button.BorderThickness = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private Control BuildLayout()
    {
        // the bar: the file and its counts, the controls
        _badgeBox.Child = _badge;
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.AddRange([_title, _stats]);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.AddRange([_badgeBox, heading]);

        _split.Content = IconText(Glyph("M3.25 2.75h9.5a1.5 1.5 0 0 1 1.5 1.5v7.5a1.5 1.5 0 0 1-1.5 1.5h-9.5a1.5 1.5 0 0 1-1.5-1.5v-7.5a1.5 1.5 0 0 1 1.5-1.5zM8 3v10"), "Split");
        _inline.Content = IconText(Glyph("M3.25 2.75h9.5a1.5 1.5 0 0 1 1.5 1.5v7.5a1.5 1.5 0 0 1-1.5 1.5h-9.5a1.5 1.5 0 0 1-1.5-1.5v-7.5a1.5 1.5 0 0 1 1.5-1.5zM4.5 6.5h7M4.5 9.5h7"), "Inline");
        ToolTip.SetTip(_split, "Side by side");
        ToolTip.SetTip(_inline, "Inline");
        _previous.Content = Glyph("M4 10l4-4 4 4");
        _next.Content = Glyph("M4 6l4 4 4-4");
        ToolTip.SetTip(_previous, "Previous difference (Shift+F2)");
        ToolTip.SetTip(_next, "Next difference (F2)");
        _copyLeft.Content = Glyph("M13 8H3.5M7 4.5L3.5 8 7 11.5");
        _copyRight.Content = Glyph("M3 8h9.5M9 4.5L12.5 8 9 11.5");
        ToolTip.SetTip(_copyLeft, "Copy the difference to the left (Alt+Left)");
        ToolTip.SetTip(_copyRight, "Copy the difference to the right (Alt+Right)");
        _undo.Content = Glyph("M5.5 4L2.5 7l3 3M2.5 7h7a4 4 0 0 1 0 8h-2");
        _redo.Content = Glyph("M10.5 4l3 3-3 3M13.5 7h-7a4 4 0 0 0 0 8h2");
        ToolTip.SetTip(_undo, "Undo (Ctrl+Z)");
        ToolTip.SetTip(_redo, "Redo (Ctrl+Y)");
        _save.Content = IconText(Glyph("M3 2.5h8l2.5 2.5v8.5H3zM5.5 2.5v3h5v-3M5.5 13.5V9.5h5v4"), "Save");
        ToolTip.SetTip(_save, "Save the changed files (Ctrl+S saves the side being edited)");
        _more.Content = Glyph("M4 8h.01M8 8h.01M12 8h.01");
        ToolTip.SetTip(_more, "More");
        _compare.Content = IconText(Glyph("M13.5 8a5.5 5.5 0 1 1-1.6-3.9M12.5 1.5v3h-3"), "Compare", "F5");
        ToolTip.SetTip(_compare, "Compare again (F5)");
        foreach (var button in new Button[] { _split, _inline, _save, _compare })
        {
            Style(button);
        }
        foreach (var button in new[] { _previous, _next, _copyLeft, _copyRight, _undo, _redo, _more })
        {
            Style(button, 30);
        }
        var controls = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.AddRange([
            Group(_split, _inline),
            Group(_ignoreCase, _ignoreSpace),
            Group(_previous, _counter, _next),
            Group(_copyLeft, _copyRight),
            Group(_undo, _redo),
            _save,
            _more,
            _compare,
        ]);
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Left);
        controls.HorizontalAlignment = HorizontalAlignment.Right;
        controls.Margin = new Thickness(20, 0, 0, 0);
        bar.Children.AddRange([title, controls]);

        // the panel: the file names, the lines (or the editors, or the binary differences)
        _body.Children.AddRange([_leftPane.Editor, _rightPane.Editor, _inlinePane.Editor, _ruler, _empty]);
        Grid.SetColumn(_rightPane.Editor, 2);
        Grid.SetColumnSpan(_inlinePane.Editor, 3);
        Grid.SetColumn(_ruler, 3);
        Grid.SetColumnSpan(_empty, 3);
        var divider = new Border { Width = 1, Tag = "line" };
        Grid.SetColumn(divider, 1);
        _body.Children.Add(divider);
        _inlinePane.Editor.IsVisible = false;
        _binaryList.FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        var content = new Panel();
        content.Children.AddRange([_body, _binaryList, _pictureHost]);
        var panelRoot = new DockPanel();
        DockPanel.SetDock(_files, Dock.Top);
        panelRoot.Children.AddRange([_files, content]);
        _panel.Child = panelRoot;

        // below: the summary and the keys
        _keys.Children.AddRange([KeyHint("F2", "next"), KeyHint("Alt+→", "copy right"), KeyHint("F5", "compare")]);
        var footer = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
        DockPanel.SetDock(_keys, Dock.Right);
        footer.Children.AddRange([_keys, _summary]);

        var root = new DockPanel { Margin = new Thickness(16, 14) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([bar, footer, _panel]);
        _toast.Child = _toastText;
        var layers = new Panel();
        layers.Children.AddRange([root, _toast]);
        return layers;
    }

    private static StackPanel KeyHint(string keys, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var parts = keys.Split('+');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                row.Children.Add(new TextBlock { Text = "+", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            row.Children.Add(KeyBox(parts[i]));
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>The header of the files: "Original" (left file) and "Current" (right file) with their paths, the name in bold.</summary>
    private void BuildFileHeaders()
    {
        _files.Children.Clear();
        var inline = IsInline;
        _files.ColumnDefinitions = inline ? new ColumnDefinitions("*") : new ColumnDefinitions("*,*");
        _files.RowDefinitions = inline ? new RowDefinitions("Auto,Auto") : new RowDefinitions("Auto");
        var sides = new[] { (_l, "Original", false), (_r, "Current", true) };
        for (var i = 0; i < sides.Length; i++)
        {
            var (side, tag, isRight) = sides[i];
            var entry = side.Entry;
            var label = new Border
            {
                Child = new TextBlock { Text = tag, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(isRight ? _palette.AddFg : _palette.DelFg) },
                Background = new SolidColorBrush(isRight ? _palette.AddBg : _palette.DelBg),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var display = entry.Path.ToDisplayString();
            var folder = display.Length > entry.Name.Length ? display[..^entry.Name.Length] : string.Empty;
            var path = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 13,
                Foreground = new SolidColorBrush(_palette.Muted),
                TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "noloc" },
            };
            path.Inlines!.Add(new Run(folder));
            path.Inlines.Add(new Run(entry.Name) { FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(_palette.Text) });
            if (side.Dirty)
            {
                path.Inlines.Add(new Run(" ●") { Foreground = new SolidColorBrush(_palette.Mod) });
            }
            ToolTip.SetTip(path, display);
            var row = new DockPanel();
            DockPanel.SetDock(label, Dock.Left);
            label.Margin = new Thickness(0, 0, 10, 0);
            // the side's own: read-only, its encoding and line ends (a menu), another file
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
            if (side.ReadOnly)
            {
                tools.Children.Add(new Border
                {
                    Child = new TextBlock { Text = "Read-only", FontSize = 10.5, Foreground = new SolidColorBrush(_palette.Muted) },
                    Background = new SolidColorBrush(_palette.Surface), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var format = new Button
            {
                Content = new TextBlock { Text = side.FormatText, FontSize = 11.5, Foreground = new SolidColorBrush(_palette.Muted), Classes = { "noloc" } },
                Padding = new Thickness(6, 1), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !side.ReadOnly && !IsBinary,
                IsVisible = Mode == CompareMode.Text, // hex, documents, pictures are not written here
            };
            ToolTip.SetTip(format, "Encoding and line ends of the saved file");
            var formatSide = side;
            format.Click += (_, _) => OpenFormatMenu(format, formatSide);
            var browse = new Button { Content = Glyph("M2 4.5h4l1.5 1.5H14v6.5H2z"), Padding = new Thickness(4), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            SetStroke(browse, _palette.Muted);
            ToolTip.SetTip(browse, isRight ? "Choose the right file..." : "Choose the left file...");
            browse.Click += (_, _) => _ = BrowseAsync(isRight);
            tools.Children.AddRange([format, browse]);
            DockPanel.SetDock(tools, Dock.Right);
            row.Children.AddRange([label, tools, path]);
            var cell = new Border
            {
                Child = row,
                Padding = new Thickness(14, 9),
                BorderBrush = new SolidColorBrush(_palette.Line),
                BorderThickness = i == 0 ? new Thickness(0) : inline ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0),
            };
            Grid.SetColumn(cell, inline ? 0 : i);
            Grid.SetRow(cell, inline ? i : 0);
            _files.Children.Add(cell);
        }
    }

    // ---------------------------------------------------------------- colors

    /// <summary>The colors of the design, light and dark.</summary>
    private sealed record Palette(
        Color Bg, Color Surface, Color Surface2, Color Line, Color LineStrong, Color Text, Color Muted, Color Faint,
        Color Accent, Color AccentSoft, Color OnAccent, Color AddBg, Color AddHl, Color AddFg, Color DelBg, Color DelHl, Color DelFg,
        Color Mod, Color Fill, Color Current)
    {
        public static Palette Light { get; } = new(
            Hex("#f4f6f9"), Hex("#ffffff"), Hex("#eef1f6"), Hex("#dde3ec"), Hex("#c9d1dd"), Hex("#18202d"), Hex("#5d6879"), Hex("#8c96a6"),
            Hex("#1f6feb"), Hex("#e3edfd"), Hex("#ffffff"), Hex("#e9f7ee"), Hex("#b4e6c3"), Hex("#1a7f37"), Hex("#fdedef"), Hex("#f6c0c7"), Hex("#c32a3d"),
            Hex("#b7791f"), Hex("#e6eaf0"), Hex("#1f6feb"));

        public static Palette Dark { get; } = new(
            Hex("#0e1218"), Hex("#141922"), Hex("#1b212c"), Hex("#262e3b"), Hex("#344052"), Hex("#e3e8f0"), Hex("#94a0b3"), Hex("#667186"),
            Hex("#58a6ff"), Hex("#16263d"), Hex("#08111d"), Color.FromArgb(33, 46, 160, 67), Color.FromArgb(102, 63, 185, 80), Hex("#63d27e"),
            Color.FromArgb(31, 248, 81, 73), Color.FromArgb(102, 248, 81, 73), Hex("#ff7b72"), Hex("#e3b341"), Hex("#1a2029"), Hex("#58a6ff"));

        private static Color Hex(string value) => Color.Parse(value);
    }

    private Palette Colors => _palette;

    /// <summary>The compare settings (the window's switches change them too).</summary>
    private CompareSettings Options => _context.Settings.CompareOptions;

    /// <summary>The compare settings this window follows (tests).</summary>
    public CompareSettings Settings => Options;

    private void ApplyPalette()
    {
        _palette = ActualThemeVariant == ThemeVariant.Dark ? Palette.Dark : Palette.Light;
        var p = _palette;
        // the buttons of the design: flat, a light fill on hover, a pressed toggle in the soft accent (not Fluent's full accent)
        var transparent = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
        var hover = new SolidColorBrush(p.Surface2);
        var soft = new SolidColorBrush(p.AccentSoft);
        var accent = new SolidColorBrush(p.Accent);
        var textBrush = new SolidColorBrush(p.Text);
        foreach (var (key, brush) in new (string, IBrush)[]
        {
            ("ButtonBackground", transparent), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", hover), ("ButtonBackgroundDisabled", transparent),
            ("ButtonBorderBrush", transparent), ("ButtonBorderBrushPointerOver", transparent), ("ButtonBorderBrushPressed", transparent),
            ("ButtonForeground", textBrush), ("ButtonForegroundPointerOver", textBrush), ("ButtonForegroundPressed", textBrush),
            ("ToggleButtonBackground", transparent), ("ToggleButtonBackgroundPointerOver", hover), ("ToggleButtonBackgroundPressed", hover),
            ("ToggleButtonBackgroundChecked", soft), ("ToggleButtonBackgroundCheckedPointerOver", soft), ("ToggleButtonBackgroundCheckedPressed", soft),
            ("ToggleButtonBorderBrush", transparent), ("ToggleButtonBorderBrushPointerOver", transparent), ("ToggleButtonBorderBrushPressed", transparent),
            ("ToggleButtonBorderBrushChecked", transparent), ("ToggleButtonBorderBrushCheckedPointerOver", transparent), ("ToggleButtonBorderBrushCheckedPressed", transparent),
            ("ToggleButtonForeground", textBrush), ("ToggleButtonForegroundPointerOver", textBrush), ("ToggleButtonForegroundPressed", textBrush),
            ("ToggleButtonForegroundChecked", accent), ("ToggleButtonForegroundCheckedPointerOver", accent), ("ToggleButtonForegroundCheckedPressed", accent),
            ("ToggleSwitchFillOn", accent), ("ToggleSwitchFillOnPointerOver", accent), ("ToggleSwitchStrokeOn", accent),
            ("AccentButtonBackground", accent), ("AccentButtonBackgroundPointerOver", accent), ("AccentButtonForeground", new SolidColorBrush(p.OnAccent)),
        })
        {
            Resources[key] = brush;
        }
        Background = new SolidColorBrush(p.Bg);
        Foreground = new SolidColorBrush(p.Text);
        _badgeBox.Background = new SolidColorBrush(p.AccentSoft);
        _badge.Foreground = new SolidColorBrush(p.Accent);
        _panel.Background = new SolidColorBrush(p.Surface);
        _panel.BorderBrush = new SolidColorBrush(p.Line);
        _panel.BoxShadow = BoxShadows.Parse(ActualThemeVariant == ThemeVariant.Dark ? "0 6 20 0 #40000000" : "0 4 16 0 #0F141E32");
        _files.Background = new SolidColorBrush(p.Surface2);
        foreach (var group in _groups)
        {
            group.Background = new SolidColorBrush(p.Surface);
            group.BorderBrush = new SolidColorBrush(p.Line);
        }
        foreach (var line in _body.Children.OfType<Border>().Where(b => Equals(b.Tag, "line")))
        {
            line.Background = new SolidColorBrush(p.Line);
        }
        _summary.Foreground = new SolidColorBrush(p.Muted);
        _counter.Foreground = new SolidColorBrush(p.Muted);
        foreach (var key in this.GetLogicalDescendantsOfKeys())
        {
            key.BorderBrush = new SolidColorBrush(p.Line);
            if (key.Child is TextBlock text)
            {
                text.Foreground = new SolidColorBrush(p.Faint);
            }
        }
        foreach (var block in _keys.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>()))
        {
            block.Foreground = new SolidColorBrush(p.Muted);
        }
        _toast.Background = new SolidColorBrush(p.Text);
        _toastText.Foreground = new SolidColorBrush(p.Bg);
        _empty.Foreground = new SolidColorBrush(p.Muted);
        foreach (var pane in new[] { _leftPane, _rightPane, _inlinePane })
        {
            pane.ApplyColors();
        }
        _ruler.Background = new SolidColorBrush(p.Surface);
        _ruler.InvalidateVisual();
        _compare.Foreground = new SolidColorBrush(p.OnAccent);
        RecolorChips();
        foreach (var language in _keys.Children.OfType<TextBlock>())
        {
            language.Foreground = new SolidColorBrush(p.Muted);
        }
        UpdateToggles();
        BuildFileHeaders();
    }

    /// <summary>The count chips in the colors of the theme: the number of changes, the added and the removed lines.</summary>
    private void RecolorChips()
    {
        var p = _palette;
        var colors = new[] { (p.Muted, p.Surface2), (p.AddFg, p.AddBg), (p.DelFg, p.DelBg) };
        var chips = _stats.Children.OfType<Border>().ToList();
        for (var i = 0; i < chips.Count; i++)
        {
            var (foreground, background) = colors[Math.Min(i, colors.Length - 1)];
            chips[i].Background = new SolidColorBrush(background);
            if (chips[i].Child is TextBlock text)
            {
                text.Foreground = new SolidColorBrush(foreground);
            }
        }
    }

    /// <summary>The pressed state of the toggle buttons in the design's colors.</summary>
    private void UpdateToggles()
    {
        var p = _palette;
        foreach (var toggle in new[] { _split, _inline })
        {
            var on = toggle.IsChecked == true;
            SetStroke(toggle, on ? p.Accent : p.Text);
        }
        foreach (var button in new[] { _previous, _next, _copyLeft, _copyRight, _undo, _redo, _save, _more })
        {
            SetStroke(button, button.IsEnabled ? p.Text : p.Faint);
        }
        SetStroke(_compare, p.OnAccent);
    }

    private static void SetStroke(ContentControl control, Color color)
    {
        foreach (var path in control.GetLogicalDescendantsOfType<Avalonia.Controls.Shapes.Path>())
        {
            path.Stroke = new SolidColorBrush(color);
        }
    }

    // ---------------------------------------------------------------- comparing

    public Task CompareAsync()
    {
        Comparing = CompareCoreAsync();
        return Comparing;
    }

    /// <summary>
    /// F5 / Compare: the files read again when neither side has unsaved changes, otherwise the texts as they are now
    /// compared again.
    /// </summary>
    private async Task RecompareAsync()
    {
        CommitEdits();
        if (!_l.Dirty && !_r.Dirty)
        {
            _l.Loaded = _r.Loaded = false; // read again
        }
        await CompareAsync();
        ShowToast(_hunks.Count == 0 ? "Compared · files match"
            : _hunks.Count == 1 ? "Compared · 1 difference" : $"Compared · {_hunks.Count} differences");
    }

    private async Task CompareCoreAsync()
    {
        _title.Text = _r.Entry.Name;
        _badge.Text = Badge(_r.Entry.Extension);
        if (!_l.Loaded || !_r.Loaded)
        {
            // what the files are decides how they are compared (CompareWindow.Modes.cs)
            if (await LoadAsync())
            {
                return;
            }
        }
        else if (Mode == CompareMode.Hex)
        {
            ShowHex(); // the options do not change bytes
            return;
        }
        else if (Mode == CompareMode.Picture)
        {
            return;
        }
        var options = Options.ToOptions();
        var leftLines = _l.Lines.ToList();
        var rightLines = _r.Lines.ToList();
        // line ends that count: each line compared with its file's line end, shown without it
        var keepEnds = !options.IgnoreLineEndings;
        var leftInput = keepEnds ? leftLines.Select(l => l + _l.NewLine).ToList() : leftLines;
        var rightInput = keepEnds ? rightLines.Select(l => l + _r.NewLine).ToList() : rightLines;
        var version = _editVersion;
        var rows = await Task.Run(() =>
        {
            var compared = TextDiff.Compare(leftInput, rightInput, options);
            return keepEnds
                ? compared.Select(r => r with
                {
                    Left = r.LeftNumber is { } ln ? leftLines[ln - 1] : null,
                    Right = r.RightNumber is { } rn ? rightLines[rn - 1] : null,
                }).ToList()
                : compared;
        });
        if (version != _editVersion)
        {
            return; // typed meanwhile: the next compare shows it
        }
        var caret = SaveCaret();
        _rows = rows;
        _hunks = Hunk.Of(_rows);
        BuildPanes();
        RestoreCaret(caret);
        UpdateTitle();
        UpdateButtons();

        var added = _rows.Count(r => r.Kind is DiffKind.Added or DiffKind.Changed && !r.Ignored);
        var removed = _rows.Count(r => r.Kind is DiffKind.Removed or DiffKind.Changed && !r.Ignored);
        _stats.Children.Clear();
        _stats.Children.AddRange([
            Chip(_hunks.Count == 1 ? "1 change" : $"{_hunks.Count} changes", _palette.Muted, _palette.Surface2),
            Chip($"+{added}", _palette.AddFg, _palette.AddBg),
            Chip($"−{removed}", _palette.DelFg, _palette.DelBg),
        ]);
        _summary.Text = Mode == CompareMode.Document
            ? $"The text of the documents (read-only) · {leftLines.Count} lines → {rightLines.Count} lines · {added} added, {removed} removed"
            : $"{leftLines.Count} lines → {rightLines.Count} lines · {added} added, {removed} removed";
        UpdateLanguage();
        _empty.IsVisible = _rows.Count == 0;
        _empty.Text = "Both files are empty.";
        var keep = _keepBlock;
        _keepBlock = null;
        _currentBlock = _hunks.Count == 0 ? -1 : keep is { } k ? Math.Clamp(k, 0, _hunks.Count - 1) : caret is null ? 0 : Math.Clamp(_currentBlock, 0, _hunks.Count - 1);
        Highlight(scroll: keep is not null || caret is null);
    }

    private static Border Chip(string text, Color foreground, Color background) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(foreground) },
        Background = new SolidColorBrush(background),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(8, 2),
    };

    /// <summary>The short name of the file type in the badge ("C#", "MD").</summary>
    private static string Badge(string extension) => extension.ToLowerInvariant() switch
    {
        "cs" => "C#",
        "fs" => "F#",
        "" => "TXT",
        var e => e.Length <= 4 ? e.ToUpperInvariant() : e[..4].ToUpperInvariant(),
    };

    /// <summary>The language of the file for the status line ("C#", "Markdown").</summary>
    private static string Language(string extension) => extension.ToLowerInvariant() switch
    {
        "cs" => "C#",
        "fs" => "F#",
        "js" or "mjs" => "JavaScript",
        "ts" => "TypeScript",
        "md" or "markdown" => "Markdown",
        "py" => "Python",
        "json" => "JSON",
        "xml" or "csproj" or "props" or "axaml" or "xaml" => "XML",
        "html" or "htm" => "HTML",
        "css" => "CSS",
        "ps1" => "PowerShell",
        "sh" => "Shell",
        "sql" => "SQL",
        "java" => "Java",
        "cpp" or "cc" or "h" or "hpp" or "c" => "C/C++",
        "" or "txt" or "log" => "Text",
        var e => e.ToUpperInvariant(),
    };

    private readonly TextBlock _position = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };

    private void UpdateLanguage()
    {
        while (_keys.Children.Count > 3)
        {
            _keys.Children.RemoveAt(3);
        }
        _position.Foreground = new SolidColorBrush(_palette.Muted);
        _keys.Children.Add(_position);
        _keys.Children.Add(new TextBlock { Text = $"{Language(_r.Entry.Extension)} · {_r.FormatText}", FontSize = 12, Foreground = new SolidColorBrush(_palette.Muted), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
    }

    /// <summary>The caret's line and column in the file of the side being edited ("Ln 12, Col 4").</summary>
    private void UpdatePosition()
    {
        var pane = FocusedPane();
        if (pane is null)
        {
            _position.Text = string.Empty;
            return;
        }
        var caret = pane.Editor.TextArea.Caret;
        var fileLine = pane.FileLineOf(caret.Line - 1);
        _position.Text = fileLine < 0 ? string.Empty : $"Ln {fileLine + 1}, Col {caret.Column}";
    }

    private async Task<byte[]> ReadAsync(VfsEntry entry)
    {
        if (entry.Size > MaxTextBytes)
        {
            throw new IOException($"{entry.Name} is too large to compare ({entry.Size:N0} bytes).");
        }
        await using var stream = await _context.Registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }


    /// <summary>The text view again (after a binary file was replaced by a text file).</summary>
    private void ShowText()
    {
        _binaryList.IsVisible = false;
        _pictureHost.IsVisible = false;
        _body.IsVisible = true;
        foreach (var control in new Control[] { _split, _inline, _ignoreCase, _ignoreSpace })
        {
            control.IsEnabled = true;
        }
    }

    private void ShowBinary(byte[] a, byte[] b)
    {
        var diffs = new List<string>();
        var count = 0L;
        var common = Math.Min(a.Length, b.Length);
        for (var i = 0; i < common; i++)
        {
            if (a[i] != b[i])
            {
                count++;
                if (diffs.Count < 5000)
                {
                    diffs.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:X8}   {1:X2} ≠ {2:X2}", i, a[i], b[i]));
                }
            }
        }
        if (a.Length != b.Length)
        {
            diffs.Add($"Sizes differ: {a.Length:N0} / {b.Length:N0} bytes");
        }
        // the list of the differing bytes is kept for the tests; the hex dumps are shown (ShowHex)
        _binaryList.ItemsSource = diffs;
        _stats.Children.Clear();
        _stats.Children.Add(Chip($"{count:N0} different bytes", _palette.Muted, _palette.Surface2));
        // whole sentences, each translated (a text put together from pieces is not)
        var summary = count == 0 && a.Length == b.Length ? "The binary files are identical."
            : a.Length != b.Length ? $"Binary comparison: {count:N0} different bytes, sizes differ."
            : $"Binary comparison: {count:N0} different bytes.";
        _summary.Text = Math.Max(a.Length, b.Length) > HexCompare.MaxShownBytes
            ? Core.Localization.Localizer.T(summary) + " " + Core.Localization.Localizer.T($"The first {HexCompare.MaxShownBytes / 1024 / 1024} MB are shown.")
            : summary;
    }

    /// <summary>The panes of the current rows: left and right side by side, and the one-column view.</summary>
    private void BuildPanes()
    {
        var left = new List<PaneLine>();
        var right = new List<PaneLine>();
        var inline = new List<PaneLine>();
        var dels = new List<PaneLine>();
        var adds = new List<PaneLine>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var hunk = Hunk.IndexOf(_hunks, i);
            IReadOnlyList<InlineSpan>? leftSpans = null, rightSpans = null;
            if (row.Kind == DiffKind.Changed && row.Left is not null && row.Right is not null)
            {
                (leftSpans, rightSpans) = TextDiff.InlineDiff(row.Left, row.Right, Options.WordLevel ? InlineLevel.Word : InlineLevel.Character);
            }
            // a moved line has ↕ for its sign (it is added or removed elsewhere); an ignored difference is faint
            var leftSign = row.Kind == DiffKind.Equal ? ' ' : row.MovedTo is not null ? '↕' : '−';
            var rightSign = row.Kind == DiffKind.Equal ? ' ' : row.MovedTo is not null ? '↕' : '+';
            var leftLook = row.Kind == DiffKind.Equal ? Look.Equal : row.Ignored ? Look.Ignored : Look.Del;
            var rightLook = row.Kind == DiffKind.Equal ? Look.Equal : row.Ignored ? Look.Ignored : Look.Add;
            left.Add(row.Left is null
                ? PaneLine.Filler(hunk)
                : new PaneLine(row.Left, row.LeftNumber, null, leftSign, leftLook, hunk, leftSpans));
            right.Add(row.Right is null
                ? PaneLine.Filler(hunk)
                : new PaneLine(row.Right, null, row.RightNumber, rightSign, rightLook, hunk, rightSpans));
            if (row.Kind == DiffKind.Equal)
            {
                inline.Add(new PaneLine(row.Left ?? string.Empty, row.LeftNumber, row.RightNumber, ' ', Look.Equal, -1, null));
                continue;
            }
            if (row.Ignored)
            {
                // not a difference that is visited: its lines in place, faint
                if (row.Left is not null)
                {
                    inline.Add(new PaneLine(row.Left, row.LeftNumber, null, ' ', Look.Ignored, -1, null));
                }
                if (row.Right is not null)
                {
                    inline.Add(new PaneLine(row.Right, null, row.RightNumber, ' ', Look.Ignored, -1, null));
                }
                continue;
            }
            // one column: the removed lines of a difference first, then the added ones
            if (row.Left is not null)
            {
                dels.Add(new PaneLine(row.Left, row.LeftNumber, null, leftSign, Look.Del, hunk, leftSpans));
            }
            if (row.Right is not null)
            {
                adds.Add(new PaneLine(row.Right, null, row.RightNumber, rightSign, Look.Add, hunk, rightSpans));
            }
            if (i == _hunks[hunk].End)
            {
                inline.AddRange(dels);
                inline.AddRange(adds);
                dels.Clear();
                adds.Clear();
            }
        }
        _leftPane.SetLines(left);
        _rightPane.SetLines(right);
        _inlinePane.SetLines(inline);
        _leftPane.SetEditable(!_l.ReadOnly);
        _rightPane.SetEditable(!_r.ReadOnly);
        _ruler.InvalidateVisual();
    }

    // ---------------------------------------------------------------- views and moving

    private void SetView(bool inline)
    {
        _split.IsChecked = !inline;
        _inline.IsChecked = inline;
        _leftPane.Editor.IsVisible = !inline;
        _rightPane.Editor.IsVisible = !inline;
        _body.Children.OfType<Border>().First(b => Equals(b.Tag, "line")).IsVisible = !inline;
        _inlinePane.Editor.IsVisible = inline;
        UpdateToggles();
        BuildFileHeaders();
        Highlight(scroll: true);
    }

    /// <summary>Goes to the next (+1) or previous (-1) difference, around from the last to the first.</summary>
    public void MoveToBlock(int direction)
    {
        if (_hunks.Count == 0)
        {
            return;
        }
        _currentBlock = ((_currentBlock + direction) % _hunks.Count + _hunks.Count) % _hunks.Count;
        Highlight(scroll: true);
    }

    /// <summary>Marks the current difference (frame, counter, ruler) and brings it to the middle of the view.</summary>
    private void Highlight(bool scroll)
    {
        _counter.Text = _hunks.Count > 0 ? $"{_currentBlock + 1} / {_hunks.Count}" : "0 / 0";
        _previous.IsEnabled = _next.IsEnabled = _hunks.Count > 0 && !IsBinary;
        UpdateButtons();
        foreach (var pane in new[] { _leftPane, _rightPane, _inlinePane })
        {
            pane.Redraw();
        }
        _ruler.InvalidateVisual();
        if (!scroll || _currentBlock < 0)
        {
            return;
        }
        foreach (var pane in IsInline ? new[] { _inlinePane } : new[] { _leftPane, _rightPane })
        {
            pane.ScrollToHunk(_currentBlock);
        }
    }

    /// <summary>A click on a line of a difference makes it the current one.</summary>
    private void Select(int hunk, bool scroll)
    {
        if (hunk < 0 || hunk >= _hunks.Count)
        {
            return;
        }
        _currentBlock = hunk;
        Highlight(scroll);
    }

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- the model of the lines

    private enum Look
    {
        Equal,
        Del,
        Add,
        Fill,

        /// <summary>A difference the options ignore (blank lines, the line filters): shown faintly.</summary>
        Ignored,
    }

    /// <summary>One line of a pane: its text, the numbers of the files, the sign, its look, its difference and marked characters.</summary>
    private sealed record PaneLine(string Text, int? LeftNumber, int? RightNumber, char Sign, Look Look, int Hunk, IReadOnlyList<InlineSpan>? Spans)
    {
        public static PaneLine Filler(int hunk) => new(string.Empty, null, null, ' ', Look.Fill, hunk, null);
    }

    /// <summary>A difference: the rows from <see cref="Start"/> to <see cref="End"/> and what they hold (for the ruler's color).</summary>
    private sealed record Hunk(int Start, int End, bool HasAdded, bool HasRemoved, bool HasChanged)
    {
        public static List<Hunk> Of(IReadOnlyList<DiffLine> rows)
        {
            var hunks = new List<Hunk>();
            var start = -1;
            for (var i = 0; i <= rows.Count; i++)
            {
                var different = i < rows.Count && rows[i].Kind != DiffKind.Equal && !rows[i].Ignored; // ignored ones are not visited
                if (different && start < 0)
                {
                    start = i;
                }
                else if (!different && start >= 0)
                {
                    var part = rows.Skip(start).Take(i - start).ToList();
                    hunks.Add(new Hunk(start, i - 1, part.Exists(r => r.Kind == DiffKind.Added), part.Exists(r => r.Kind == DiffKind.Removed), part.Exists(r => r.Kind == DiffKind.Changed)));
                    start = -1;
                }
            }
            return hunks;
        }

        public static int IndexOf(List<Hunk> hunks, int row)
        {
            var index = hunks.FindIndex(h => h.Start <= row && row <= h.End);
            return index;
        }
    }

    /// <summary>
    /// One view of lines (left, right or one column): a read-only editor with the numbers and signs in a margin, the
    /// colors of the lines and the marked characters behind the text, the current difference framed.
    /// </summary>
    private sealed class DiffPane
    {
        private readonly CompareWindow _owner;
        private readonly LineMargin _margin;
        private List<PaneLine> _lines = [];
        private readonly List<TextAnchor> _fillers = [];
        private HashSet<int>? _fillerOffsets;
        private bool _settingLines;

        public DiffPane(CompareWindow owner, FontFamily font, double size, IHighlightingDefinition? highlighting, int numberColumns, bool? isLeft)
        {
            _owner = owner;
            IsLeft = isLeft;
            Editor = new TextEditor
            {
                IsReadOnly = true,
                ShowLineNumbers = false,
                FontFamily = font,
                FontSize = size,
                SyntaxHighlighting = highlighting,
                Padding = new Thickness(0, 6),
                BorderThickness = new Thickness(0),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            Editor.Options.EnableHyperlinks = false;
            Editor.Options.HighlightCurrentLine = false;
            ThemeAwareColors.Attach(Editor);
            _margin = new LineMargin(this, numberColumns);
            Editor.TextArea.LeftMargins.Insert(0, _margin);
            Editor.TextArea.TextView.BackgroundRenderers.Add(new LineColors(this));
            Editor.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                // a click on a line of a difference makes it the current one
                if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } position && position.Line - 1 < _lines.Count && _lines[position.Line - 1].Hunk >= 0)
                {
                    _owner.Select(_lines[position.Line - 1].Hunk, scroll: false);
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            if (isLeft is not null)
            {
                // a side of its own: edited in place, the missing lines kept as they are
                _guard = new FillerGuard(this);
                Search = AvaloniaEdit.Search.SearchPanel.Install(Editor);
                Editor.TextArea.Caret.PositionChanged += (_, _) => _owner.UpdatePosition();
                Editor.GotFocus += (_, _) => _owner.UpdatePosition();
            }
        }

        public TextEditor Editor { get; }

        private readonly FillerGuard? _guard;

        /// <summary>
        /// A side that can be written is edited in place (the guard set after IsReadOnly, which puts AvaloniaEdit's own
        /// provider in its place).
        /// </summary>
        public void SetEditable(bool writable)
        {
            var editable = writable && _guard is not null;
            Editor.IsReadOnly = !editable;
            if (editable)
            {
                Editor.TextArea.ReadOnlySectionProvider = _guard!;
            }
        }

        /// <summary>The left (true) or right (false) side; null: the one-column view (read-only).</summary>
        public bool? IsLeft { get; }

        public AvaloniaEdit.Search.SearchPanel? Search { get; }

        public IReadOnlyList<PaneLine> Lines => _lines;

        public Palette Colors => _owner.Colors;

        public CompareWindow Owner => _owner;

        public int CurrentHunk => _owner._currentBlock;

        /// <summary>Whether the target side of this pane's arrows can be written (the arrows are drawn then).</summary>
        public bool CanCopyFromHere => IsLeft is { } left && !(left ? _owner._r : _owner._l).ReadOnly && !_owner.IsBinary;

        public void SetLines(List<PaneLine> lines)
        {
            _lines = lines;
            _settingLines = true;
            var document = new TextDocument(string.Join('\n', lines.Select(l => l.Text)));
            _fillers.Clear();
            _fillerOffsets = null;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Look == Look.Fill)
                {
                    _fillers.Add(document.CreateAnchor(document.GetLineByNumber(i + 1).Offset));
                }
            }
            document.Changed += (_, _) =>
            {
                _fillerOffsets = null;
                if (!_settingLines)
                {
                    _owner.OnPaneEdited(this);
                }
            };
            Editor.Document = document;
            _settingLines = false;
            _margin.InvalidateMeasure();
            Redraw();
        }

        /// <summary>A line of the pane that stands for lines the other side has (hatched, not part of the file).</summary>
        public bool IsFiller(DocumentLine line)
        {
            _fillerOffsets ??= [.. _fillers.Where(a => !a.IsDeleted).Select(a => a.Offset)];
            return line.Length == 0 && _fillerOffsets.Contains(line.Offset);
        }

        /// <summary>The lines of the file this pane shows (its text without the filler lines).</summary>
        public List<string> FileLines()
        {
            var document = Editor.Document;
            var lines = new List<string>(document.LineCount);
            foreach (var line in document.Lines)
            {
                if (!IsFiller(line))
                {
                    lines.Add(document.GetText(line));
                }
            }
            return lines;
        }

        /// <summary>The file line (0-based) of a line of the pane; -1 for a filler line.</summary>
        public int FileLineOf(int paneLine)
        {
            var document = Editor.Document;
            if (paneLine < 0 || paneLine >= document.LineCount || IsFiller(document.GetLineByNumber(paneLine + 1)))
            {
                return -1;
            }
            var count = 0;
            for (var i = 1; i <= paneLine; i++)
            {
                count += IsFiller(document.GetLineByNumber(i)) ? 0 : 1;
            }
            return count;
        }

        /// <summary>The line of the pane (0-based) that shows a file line; -1 when there is none.</summary>
        public int PaneLineOf(int fileLine) =>
            _lines.FindIndex(l => l.Look != Look.Fill && (IsLeft == false ? l.RightNumber : l.LeftNumber ?? l.RightNumber) == fileLine + 1);

        public void ApplyColors()
        {
            Editor.Background = new SolidColorBrush(Colors.Surface);
            Editor.Foreground = new SolidColorBrush(Colors.Text);
            Redraw();
        }

        public void Redraw()
        {
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            _margin.InvalidateVisual();
        }

        /// <summary>Brings the lines of a difference to the middle of the view.</summary>
        public void ScrollToHunk(int hunk)
        {
            var first = _lines.FindIndex(l => l.Hunk == hunk);
            if (first < 0 || !Editor.IsVisible)
            {
                return;
            }
            var last = _lines.FindLastIndex(l => l.Hunk == hunk);
            var view = Editor.TextArea.TextView;
            var lineHeight = view.DefaultLineHeight;
            var top = first * lineHeight;
            var height = (last - first + 1) * lineHeight;
            var visible = view.Bounds.Height > 0 ? view.Bounds.Height : Editor.Bounds.Height;
            Editor.ScrollToVerticalOffset(Math.Max(0, top - Math.Max(0, (visible - height) / 2)));
            Editor.TextArea.Caret.Line = first + 1;
        }
    }

    /// <summary>
    /// Keeps the filler lines as they are while a side is edited: nothing is typed on them, and no deletion takes their
    /// line or joins a line of the file with one (deleting whole lines above one is fine).
    /// </summary>
    private sealed class FillerGuard(DiffPane pane) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => !pane.IsFiller(pane.Editor.Document.GetLineByOffset(offset));

        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
        {
            var document = pane.Editor.Document;
            int start = segment.Offset, end = segment.EndOffset;
            var startsAtLine = document.GetLineByOffset(start).Offset == start;
            for (var line = document.GetLineByOffset(start); line is not null && line.Offset <= end; line = line.NextLine)
            {
                if (!pane.IsFiller(line))
                {
                    continue;
                }
                if (end == line.Offset && startsAtLine)
                {
                    continue; // whole lines above it go; the filler line stays a line of its own
                }
                return [];
            }
            return [segment];
        }
    }

    /// <summary>The colors of the lines, the hatched missing lines, the marked characters and the frame of the current difference.</summary>
    private sealed class LineColors(DiffPane pane) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var lines = pane.Lines;
            if (lines.Count == 0 || !textView.VisualLinesValid)
            {
                return;
            }
            var p = pane.Colors;
            var width = textView.Bounds.Width;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= lines.Count)
                {
                    continue;
                }
                var line = lines[index];
                var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, width, visual.Height);
                Paint(drawingContext, rect, line.Look, p);
                if (line.Spans is { } spans)
                {
                    var marked = new SolidColorBrush(line.Look == Look.Del ? p.DelHl : p.AddHl);
                    foreach (var span in spans.Where(s => s.Different && s.Length > 0))
                    {
                        var segment = new TextSegment { StartOffset = visual.FirstDocumentLine.Offset + span.Start, Length = span.Length };
                        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        {
                            drawingContext.FillRectangle(marked, r, 3);
                        }
                    }
                }
                Frame(drawingContext, lines, index, rect, pane.CurrentHunk, p);
            }
        }
    }

    /// <summary>The background of a line by its look; missing lines hatched.</summary>
    private static void Paint(DrawingContext context, Rect rect, Look look, Palette p)
    {
        switch (look)
        {
            case Look.Del:
                context.FillRectangle(new SolidColorBrush(p.DelBg), rect);
                break;
            case Look.Add:
                context.FillRectangle(new SolidColorBrush(p.AddBg), rect);
                break;
            case Look.Ignored:
                context.FillRectangle(new SolidColorBrush(p.Surface2), rect);
                break;
            case Look.Fill:
                context.FillRectangle(new SolidColorBrush(p.Fill), rect);
                using (context.PushClip(rect))
                {
                    var pen = new Pen(new SolidColorBrush(p.Line), 1);
                    // diagonal lines every 7 px (135°)
                    for (var x = rect.Left - rect.Height; x < rect.Right; x += 7)
                    {
                        context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                    }
                }
                break;
        }
    }

    /// <summary>The frame of the current difference: lines above its first and below its last line.</summary>
    private static void Frame(DrawingContext context, IReadOnlyList<PaneLine> lines, int index, Rect rect, int current, Palette p)
    {
        if (current < 0 || lines[index].Hunk != current)
        {
            return;
        }
        var pen = new Pen(new SolidColorBrush(p.Current), 1);
        if (index == 0 || lines[index - 1].Hunk != current)
        {
            context.DrawLine(pen, new Point(rect.Left, rect.Top + 0.5), new Point(rect.Right, rect.Top + 0.5));
        }
        if (index == lines.Count - 1 || lines[index + 1].Hunk != current)
        {
            context.DrawLine(pen, new Point(rect.Left, rect.Bottom - 0.5), new Point(rect.Right, rect.Bottom - 0.5));
        }
    }

    /// <summary>The numbers of the lines in the files (one or two columns) and the − / + sign, on the line's color; the accent bar of the current difference.</summary>
    private sealed class LineMargin(DiffPane pane, int columns) : AbstractMargin
    {
        private const double SignWidth = 22;

        /// <summary>The column of the copy arrows on the first line of each difference (the two sides only).</summary>
        private double ArrowWidth => pane.IsLeft is null ? 0 : 18;

        private double NumberWidth => Math.Max(3, pane.Lines.Count.ToString(CultureInfo.InvariantCulture).Length) * CharWidth + 16;

        private double CharWidth => (TextView?.WideSpaceWidth ?? 8) * 0.95;

        protected override Size MeasureOverride(Size availableSize) => new(ArrowWidth + NumberWidth * columns + SignWidth, 0);

        private bool IsFirstOfHunk(int index) =>
            index < pane.Lines.Count && pane.Lines[index].Hunk >= 0 && (index == 0 || pane.Lines[index - 1].Hunk != pane.Lines[index].Hunk);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var view = TextView;
            var point = e.GetPosition(this);
            if (view is null || pane.IsLeft is not { } left || !pane.CanCopyFromHere || point.X > ArrowWidth)
            {
                return;
            }
            var visual = view.GetVisualLineFromVisualTop(point.Y + view.ScrollOffset.Y);
            var index = visual is null ? -1 : visual.FirstDocumentLine.LineNumber - 1;
            if (index >= 0 && IsFirstOfHunk(index))
            {
                _ = pane.Owner.CopyBlockAsync(pane.Lines[index].Hunk, toRight: left);
                e.Handled = true;
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            }
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            var lines = pane.Lines;
            var p = pane.Colors;
            context.FillRectangle(new SolidColorBrush(p.Surface), new Rect(Bounds.Size));
            if (view is null || !view.VisualLinesValid || lines.Count == 0)
            {
                return;
            }
            var typeface = new Typeface(pane.Editor.FontFamily);
            var size = pane.Editor.FontSize * 0.92;
            foreach (var visual in view.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= lines.Count)
                {
                    continue;
                }
                var line = lines[index];
                var rect = new Rect(0, visual.VisualTop - view.ScrollOffset.Y, Bounds.Width, visual.Height);
                Paint(context, rect, line.Look, p);
                var faint = new SolidColorBrush(p.Faint);
                var numbers = columns == 2 ? new[] { line.LeftNumber, line.RightNumber } : new[] { line.LeftNumber ?? line.RightNumber };
                for (var c = 0; c < numbers.Length; c++)
                {
                    if (numbers[c] is { } n)
                    {
                        var text = new FormattedText(n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, faint);
                        context.DrawText(text, new Point(ArrowWidth + (c + 1) * NumberWidth - 10 - text.Width, rect.Top + (rect.Height - text.Height) / 2));
                    }
                }
                if (line.Sign != ' ')
                {
                    var sign = new FormattedText(line.Sign.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, pane.Editor.FontSize,
                        new SolidColorBrush(line.Look == Look.Del ? p.DelFg : p.AddFg));
                    context.DrawText(sign, new Point(ArrowWidth + columns * NumberWidth + (SignWidth - sign.Width) / 2, rect.Top + (rect.Height - sign.Height) / 2));
                }
                if (pane.IsLeft is { } isLeft && pane.CanCopyFromHere && IsFirstOfHunk(index))
                {
                    // the copy arrow: this side's lines of the difference go to the other side
                    var arrow = new FormattedText(isLeft ? "→" : "←", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, pane.Editor.FontSize,
                        new SolidColorBrush(p.Accent));
                    context.DrawText(arrow, new Point((ArrowWidth - arrow.Width) / 2 + 2, rect.Top + (rect.Height - arrow.Height) / 2));
                }
                Frame(context, lines, index, rect, pane.CurrentHunk, p);
                if (pane.CurrentHunk >= 0 && line.Hunk == pane.CurrentHunk)
                {
                    context.FillRectangle(new SolidColorBrush(p.Current), new Rect(0, rect.Top, 3, rect.Height));
                }
            }
        }
    }

    /// <summary>All differences on the right edge, in their place in the file; a click goes to one.</summary>
    private sealed class OverviewRuler : Control
    {
        private readonly CompareWindow _owner;

        public OverviewRuler(CompareWindow owner)
        {
            _owner = owner;
            Width = 14;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "Difference overview");
        }

        public IBrush? Background { get; set; }

        private IEnumerable<(int Index, Rect Rect, Color Color)> Markers()
        {
            var total = Math.Max(1, _owner._rows.Count);
            var p = _owner.Colors;
            for (var i = 0; i < _owner._hunks.Count; i++)
            {
                var h = _owner._hunks[i];
                var color = h.HasChanged || (h.HasAdded && h.HasRemoved) ? p.Mod : h.HasAdded ? p.AddFg : p.DelFg;
                var top = h.Start / (double)total * Bounds.Height;
                var height = Math.Max(4, (h.End - h.Start + 1) / (double)total * Bounds.Height);
                yield return (i, new Rect(3, top, Bounds.Width - 6, height), color);
            }
        }

        public override void Render(DrawingContext context)
        {
            var p = _owner.Colors;
            context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
            context.DrawLine(new Pen(new SolidColorBrush(p.Line), 1), new Point(0.5, 0), new Point(0.5, Bounds.Height));
            foreach (var (index, rect, color) in Markers())
            {
                var current = index == _owner._currentBlock;
                context.FillRectangle(new SolidColorBrush(color, current ? 1 : 0.75), rect, 2);
                if (current)
                {
                    context.DrawRectangle(new Pen(new SolidColorBrush(p.Current), 2), rect.Inflate(2), 3);
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetPosition(this);
            foreach (var (index, rect, _) in Markers())
            {
                if (rect.Inflate(new Thickness(3, 2)).Contains(point))
                {
                    _owner.Select(index, scroll: true);
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}

/// <summary>The key boxes of the compare window (recolored with the theme).</summary>
internal static class CompareWindowKeys
{
    public static IEnumerable<Border> GetLogicalDescendantsOfKeys(this Control control) =>
        control.GetLogicalDescendantsOfType<Border>().Where(b => Equals(b.Tag, "kbd"));

    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control control) where T : class =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(control).OfType<T>();
}
