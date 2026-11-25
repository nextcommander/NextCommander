using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
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
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Compare;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>The three files of a 3-way merge: the common ancestor and the two versions made from it.</summary>
public sealed record MergeSources(VfsEntry Base, VfsEntry Left, VfsEntry Right);

/// <summary>
/// The 3-way merge after the user's template (25 November 2026): a bar with how many conflicts are left, the changes one
/// by one (Alt+↑ / Alt+↓, F8 the next conflict), what each takes (Left / Base / Right / Both, Ctrl+1 .. Ctrl+4) and
/// Undo; the three files below it aligned block by block (left changes blue, right purple, the same change on both sides
/// green, conflicts orange, the lines a side has not hatched, "Took Left" / "Auto" on each change); the result under them,
/// each block colored by where it comes from, unresolved conflicts with their markers. Given one file with conflict
/// markers (a git merge that stopped), its conflicts are the blocks. Saving writes the result in the output file's
/// encoding and line ends.
/// </summary>
public sealed class MergeWindow : Window
{
    private readonly ToolContext _context;
    private readonly VfsEntry _output;
    private readonly MergeSources? _sources;
    private readonly TextEditor[] _panes;
    private readonly TextEditor _result;
    private readonly List<PaneRow>[] _paneRows = [[], [], []];
    private List<ResultRow> _resultRows = [];
    private readonly Stack<(int Block, MergeTake? Taken)> _history = new();
    private readonly Dictionary<MergeTake, Button> _takes = [];
    private readonly Border _state = new() { CornerRadius = new CornerRadius(999), Height = 34, Padding = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _stateDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stateText = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counter = new() { MinWidth = 104, TextAlignment = TextAlignment.Center, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _nextConflict;
    private readonly Button _undo;
    private readonly Button _save = new() { IsDefault = true, Classes = { "dialog" } };
    private readonly List<Border> _groups = [];
    private readonly Border _toast = new() { IsVisible = false, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 9), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 48), IsHitTestVisible = false };
    private readonly TextBlock _toastText = new() { FontSize = 13 };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private MergeDocument? _merge;
    private TextContent _format = TextContent.Empty();
    private Look _look = Look.Light;
    private int _current;
    private bool _dirty;
    private bool _closingConfirmed;
    private bool _syncing;

    public MergeWindow(VfsEntry output, MergeSources? sources, ToolContext context)
    {
        _output = output;
        _sources = sources;
        _context = context;
        Width = 1280;
        Height = 860;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var options = context.Settings.CompareOptions;
        var font = new FontFamily(string.IsNullOrWhiteSpace(options.Font) ? context.Settings.ListerFont : options.Font);
        var size = options.FontSize > 0 ? options.FontSize : context.Settings.ListerFontSize;
        var highlighting = HighlightingManager.Instance.GetDefinitionByExtension("." + output.Extension);
        TextEditor Editor(Func<int, int?> number)
        {
            var editor = new TextEditor
            {
                FontFamily = font, FontSize = size, IsReadOnly = true, ShowLineNumbers = false, SyntaxHighlighting = highlighting,
                BorderThickness = new Thickness(0), Padding = new Thickness(0, 4), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            editor.Options.EnableHyperlinks = false;
            editor.Options.HighlightCurrentLine = false;
            ThemeAwareColors.Attach(editor);
            editor.TextArea.LeftMargins.Insert(0, new NumberMargin(this, number));
            return editor;
        }
        _panes = [Editor(r => _paneRows[0].ElementAtOrDefault(r)?.Number), Editor(r => _paneRows[1].ElementAtOrDefault(r)?.Number), Editor(r => _paneRows[2].ElementAtOrDefault(r)?.Number)];
        _result = Editor(r => _resultRows.ElementAtOrDefault(r)?.Number);
        for (var i = 0; i < 3; i++)
        {
            var column = i;
            _panes[i].TextArea.TextView.BackgroundRenderers.Add(new PaneColors(this, column));
            _panes[i].TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(column);
            Clickable(_panes[i], row => _paneRows[column].ElementAtOrDefault(row)?.Block);
        }
        _result.TextArea.TextView.BackgroundRenderers.Add(new ResultColors(this));
        _result.TextArea.TextView.LineTransformers.Add(new ResultMarks(this));
        Clickable(_result, row => _resultRows.ElementAtOrDefault(row)?.Block);

        _previous = ToolButton(Glyph("M4 10l4-4 4 4"), null, "Previous change (Alt+Up)", () => Move(-1));
        _next = ToolButton(Glyph("M4 6l4 4 4-4"), null, "Next change (Alt+Down)", () => Move(+1));
        _nextConflict = ToolButton(Glyph("M8 2.5v7M8 12.5v.5"), "Next conflict", "Next conflict (F8)", NextConflict);
        _undo = ToolButton(Glyph("M6 3.5L2.5 7 6 10.5M2.5 7h7a4 4 0 0 1 0 8H8"), null, "Undo (Ctrl+Z)", Undo);
        foreach (var (take, text, tip) in new[]
        {
            (MergeTake.Left, "Left", "Take left (Ctrl+1)"), (MergeTake.Base, "Base", "Take base (Ctrl+2)"),
            (MergeTake.Right, "Right", "Take right (Ctrl+3)"), (MergeTake.Both, "Both", "Take left then right (Ctrl+4)"),
        })
        {
            var chosen = take;
            _takes[take] = ToolButton(null, text, tip, () => Take(chosen));
        }
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.IsVisible = false;
        };
        Content = BuildLayout();
        ApplyLook();
        ActualThemeVariantChanged += (_, _) => ApplyLook();
        EventHandler themeApplied = (_, _) => ApplyLook();
        ColorThemes.Applied += themeApplied;
        Closed += (_, _) => ColorThemes.Applied -= themeApplied;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AskSaveOnClose = async () =>
        {
            var dialog = new ChoiceDialog<SaveChoice>("Unsaved changes", $"{_output.Name} changed. Save the changes?",
                [("Save", SaveChoice.Save), ("Don't save", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)], cancelValue: SaveChoice.Cancel);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        UpdateTitle();
        Opened += (_, _) => Loading = LoadAsync();
    }

    // ---------------------------------------------------------------- what tests and callers see

    public Task? Loading { get; private set; }

    /// <summary>The blocks of the merge (null until loaded).</summary>
    public MergeDocument? Merge => _merge;

    /// <summary>The conflicts not resolved yet.</summary>
    public int ConflictCount => _merge?.ConflictsLeft ?? 0;

    /// <summary>The current change (its index among the changes; -1: none).</summary>
    public int Current => _merge is { Changes.Count: > 0 } ? _current : -1;

    /// <summary>The result as it would be saved now.</summary>
    public string ResultText => string.Join('\n', _merge?.Result() ?? []);

    /// <summary>The counter ("Change 2 of 5") and the state ("1 conflict left").</summary>
    public string CounterText => _counter.Text ?? string.Empty;

    public string StateText => _stateText.Text ?? string.Empty;

    /// <summary>The last message ("Saved ...").</summary>
    public string LastToast => _toastText.Text ?? string.Empty;

    /// <summary>The result was saved with no conflict left (git can stage it).</summary>
    public bool Resolved { get; private set; }

    public bool IsDirty => _dirty;

    public Func<Task<SaveChoice>> AskSaveOnClose { get; set; }

    // ---------------------------------------------------------------- colors

    /// <summary>The colors of the template; the four of the merge come from the color theme (keys Merge.*).</summary>
    private sealed record Look(Color Left, Color Right, Color Both, Color Conflict, Color Accent, Color Surface, Color Surface2, Color Surface3,
        Color Line, Color LineStrong, Color Text, Color Muted, Color Faint, Color Fill, bool Dark)
    {
        public static Look Light { get; } = new(Hex("#0F6CDB"), Hex("#7A4FD6"), Hex("#17804F"), Hex("#B86E00"), Hex("#0F6CDB"), Hex("#FBFBFD"),
            Hex("#F1F3F7"), Hex("#E6E9EF"), Hex("#D9DDE5"), Hex("#BCC3CF"), Hex("#1A1E27"), Hex("#5B6475"), Hex("#8A92A1"), Hex("#F3F5F8"), false);

        public static Look DarkLook { get; } = new(Hex("#4EA1FF"), Hex("#C08BFF"), Hex("#4CC38A"), Hex("#F0A23B"), Hex("#3B8CFF"), Hex("#1C1F26"),
            Hex("#242832"), Hex("#2D323E"), Hex("#323846"), Hex("#454D5E"), Hex("#E8EBF1"), Hex("#9AA3B4"), Hex("#6B7486"), Hex("#1F232B"), true);

        /// <summary>A side's color made light for a background (as the template's 8 % / 12 %).</summary>
        public Color Tint(Color color, bool conflict = false) =>
            Color.FromArgb((byte)(255 * (conflict ? (Dark ? 0.15 : 0.16) : (Dark ? 0.12 : 0.08))), color.R, color.G, color.B);

        private static Color Hex(string value) => Color.Parse(value);
    }

    private void ApplyLook()
    {
        var look = ActualThemeVariant == ThemeVariant.Dark ? Look.DarkLook : Look.Light;
        Color Of(string key, Color fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush ? brush.Color : fallback;
        _look = look with
        {
            Left = Of("FmMergeLeftBrush", look.Left),
            Right = Of("FmMergeRightBrush", look.Right),
            Both = Of("FmMergeBothBrush", look.Both),
            Conflict = Of("FmMergeConflictBrush", look.Conflict),
            Accent = Of("FmCompareCurrentBrush", look.Accent),
        };
        var l = _look;
        Background = new SolidColorBrush(l.Surface);
        Foreground = new SolidColorBrush(l.Text);
        foreach (var group in _groups)
        {
            group.Background = new SolidColorBrush(l.Surface2);
            group.BorderBrush = new SolidColorBrush(l.Line);
        }
        foreach (var editor in _panes.Append(_result))
        {
            editor.Background = new SolidColorBrush(l.Surface);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        _toast.Background = new SolidColorBrush(l.Text);
        _toastText.Foreground = new SolidColorBrush(l.Surface);
        foreach (var border in this.GetLogicalDescendantsOfKeys())
        {
            border.BorderBrush = new SolidColorBrush(l.LineStrong);
        }
        Paint();
    }

    // ---------------------------------------------------------------- layout

    private static Control Glyph(string geometry) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(geometry), Width = 15, Height = 15, Stretch = Stretch.None, StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
    };

    private Button ToolButton(Control? icon, string? text, string tip, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            content.Children.Add(icon);
        }
        if (text is not null)
        {
            if (Enum.TryParse<MergeTake>(text, out var take))
            {
                content.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Tag = take, VerticalAlignment = VerticalAlignment.Center });
            }
            content.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        }
        var button = new Button
        {
            Content = content, Height = 30, MinWidth = 30, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private Border Group(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.AddRange(children);
        var group = new Border { Child = panel, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
        _groups.Add(group);
        return group;
    }

    private static Border KeyBox(string text) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 10.5, Classes = { "noloc" } },
        BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "kbd",
    };

    /// <summary>A path with its name in bold (the folder gives way from the start).</summary>
    private static TextBlock PathText(string path, string? prefix = null)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        var folder = path.Length > name.Length ? path[..^name.Length] : string.Empty;
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12.5, Opacity = 0.75,
            TextTrimming = TextTrimming.LeadingCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" },
        };
        ToolTip.SetTip(text, path);
        text.Inlines!.Add(new Run((prefix ?? string.Empty) + folder));
        text.Inlines.Add(new Run(name) { FontWeight = FontWeight.SemiBold });
        return text;
    }

    private Border SideTag(string text, Func<Look, Color> color, Func<Look, Color> background)
    {
        var block = new TextBlock { Text = Localizer.T(text).ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
        var tag = new Border { Child = block, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center };
        _tags.Add((tag, block, color, background));
        return tag;
    }

    private readonly List<(Border Tag, TextBlock Text, Func<Look, Color> Color, Func<Look, Color> Background)> _tags = [];

    private Control BuildLayout()
    {
        // the bar: the state, the changes, what to take, Undo, Save
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.AddRange([_stateDot, _stateText]);
        _state.Child = stateRow;
        var takeLabel = new TextBlock { Text = "Take", FontSize = 12, Opacity = 0.6, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var saveContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        saveContent.Children.AddRange([new TextBlock { Text = "Save", VerticalAlignment = VerticalAlignment.Center }, KeyBox("Ctrl S")]);
        _save.Content = saveContent;
        _save.Height = 36;
        _save.Click += async (_, _) => await SaveAsync();
        var bar = new DockPanel { Margin = new Thickness(16, 8, 16, 12) };
        DockPanel.SetDock(_save, Dock.Right);
        var tools = new WrapPanel { ItemSpacing = 14, LineSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.AddRange([
            _state,
            Group(_previous, _counter, _next, _nextConflict),
            Group([takeLabel, .. _takes.Values]),
            _undo,
        ]);
        bar.Children.AddRange([_save, tools]);

        // the three files, aligned
        var heads = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var bodies = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var names = _sources is { } s
            ? new[] { ("Left", s.Left.Path.ToDisplayString()), ("Base", s.Base.Path.ToDisplayString()), ("Right", s.Right.Path.ToDisplayString()) }
            : [("Left", Localizer.T("ours (the first side of each conflict)")), ("Base", Localizer.T("the base, where the markers have one")), ("Right", Localizer.T("theirs (the second side)"))];
        for (var i = 0; i < 3; i++)
        {
            var head = new DockPanel { Margin = new Thickness(12, 8) };
            var tag = i switch
            {
                0 => SideTag("Left", l => l.Left, l => l.Tint(l.Left)),
                1 => SideTag("Base", l => l.Muted, l => l.Surface3),
                _ => SideTag("Right", l => l.Right, l => l.Tint(l.Right)),
            };
            tag.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(tag, Dock.Left);
            head.Children.AddRange([tag, PathText(names[i].Item2)]);
            var headCell = Lined(head, left: i > 0, bottom: true);
            headCell[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
            Grid.SetColumn(headCell, i);
            heads.Children.Add(headCell);
            var body = Lined(_panes[i], left: i > 0, bottom: false);
            Grid.SetColumn(body, i);
            bodies.Children.Add(body);
        }
        var panes = new DockPanel();
        DockPanel.SetDock(heads, Dock.Top);
        panes.Children.AddRange([heads, bodies]);

        // the result
        var resultHead = new DockPanel { Margin = new Thickness(16, 8) };
        var resultTag = SideTag("Result", l => l.Both, l => l.Tint(l.Both));
        resultTag.Margin = new Thickness(0, 0, 8, 0);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, color) in new (string, Func<Look, Color>)[] { ("Left", l => l.Left), ("Right", l => l.Right), ("Both", l => l.Both), ("Conflict", l => l.Conflict) })
        {
            var swatch = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            _swatches.Add((swatch, color));
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.AddRange([swatch, new TextBlock { Text = text, FontSize = 12, Opacity = 0.75 }]);
            legend.Children.Add(item);
        }
        DockPanel.SetDock(resultTag, Dock.Left);
        DockPanel.SetDock(legend, Dock.Right);
        resultHead.Children.AddRange([resultTag, legend, PathText(_output.Path.ToDisplayString(), "→ ")]);
        var resultHeadCell = Lined(resultHead, left: false, bottom: true);
        resultHeadCell[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
        var result = new DockPanel();
        DockPanel.SetDock(resultHeadCell, Dock.Top);
        result.Children.AddRange([resultHeadCell, _result]);

        var body2 = new Grid { RowDefinitions = new RowDefinitions("1.15*,Auto,*") };
        var splitter = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        Grid.SetRow(result, 2);
        body2.Children.AddRange([Lined(panes, left: false, bottom: false, top: true), splitter, result]);

        // the line below: what happens, the keys
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        StackPanel Hint(string text, params string[] boxes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var box in boxes)
            {
                row.Children.Add(KeyBox(box));
            }
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        keys.Children.AddRange([Hint("change", "Alt", "↑", "↓"), Hint("conflict", "F8"), Hint("take", "Ctrl", "1–4")]);
        var status = new DockPanel { Margin = new Thickness(16, 8) };
        DockPanel.SetDock(keys, Dock.Right);
        status.Children.AddRange([keys, new TextBlock
        {
            Text = "Changes made on one side are merged automatically. Conflicts are marked in the result until you pick a side.",
            FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        }]);
        var statusCell = Lined(status, left: false, bottom: false, top: true);
        statusCell[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(statusCell, Dock.Bottom);
        root.Children.AddRange([bar, statusCell, body2]);
        _toast.Child = _toastText;
        var layers = new Panel();
        layers.Children.AddRange([root, _toast]);
        return layers;
    }

    private readonly List<(Border Swatch, Func<Look, Color> Color)> _swatches = [];

    private static Border Lined(Control child, bool left, bool bottom, bool top = false)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(left ? 1 : 0, top ? 1 : 0, 0, bottom ? 1 : 0) };
        border[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return border;
    }

    // ---------------------------------------------------------------- loading

    private async Task<TextContent> ReadAsync(VfsEntry entry)
    {
        await using var stream = await _context.Registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return TextContent.Read(memory.ToArray());
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_sources is { } sources)
            {
                var @base = await ReadAsync(sources.Base);
                var left = await ReadAsync(sources.Left);
                var right = await ReadAsync(sources.Right);
                _merge = MergeDocument.FromFiles(@base.Lines, left.Lines, right.Lines, sources.Left.Name, sources.Right.Name, _context.Settings.CompareOptions.ToOptions());
                // the result is written as the output file is (or as the left one when it is new)
                _format = await _context.Registry.Resolve(_output.Path).GetEntryAsync(_output.Path, CancellationToken.None) is { } existing
                    && existing.Path != sources.Left.Path ? await ReadAsync(existing) : left;
                _dirty = true; // a merge of three files is new text
            }
            else
            {
                _format = await ReadAsync(_output);
                _merge = MergeDocument.FromMarkers(_format.Lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message);
            return;
        }
        // the first conflict, else the first change
        var conflict = _merge.Changes.ToList().FindIndex(i => _merge.Blocks[i].IsUnresolved);
        _current = Math.Max(0, conflict);
        Render();
        ScrollToCurrent();
    }

    // ---------------------------------------------------------------- the rows

    /// <summary>A row of one of the three files: its text, its number in the file, its block; a pad stands for lines the others have.</summary>
    private sealed record PaneRow(string Text, int? Number, int Block, bool Pad);

    private enum RowStyle
    {
        Text,
        Marker,
        Placeholder,
    }

    /// <summary>A row of the result: its text, its number in the result, its block and how it is drawn.</summary>
    private sealed record ResultRow(string Text, int? Number, int Block, RowStyle Style);

    /// <summary>Everything drawn again from the blocks (the scroll places kept).</summary>
    private void Render()
    {
        if (_merge is not { } merge)
        {
            return;
        }
        for (var c = 0; c < 3; c++)
        {
            _paneRows[c].Clear();
        }
        var numbers = new int[3];
        for (var b = 0; b < merge.Blocks.Count; b++)
        {
            var block = merge.Blocks[b];
            var sides = new[] { block.Left, block.Base, block.Right };
            var height = block.IsChange ? Math.Max(1, sides.Max(s => s.Count)) : block.Left.Count;
            for (var c = 0; c < 3; c++)
            {
                for (var k = 0; k < height; k++)
                {
                    _paneRows[c].Add(k < sides[c].Count ? new PaneRow(sides[c][k], ++numbers[c], b, false) : new PaneRow(string.Empty, null, b, true));
                }
            }
        }
        var rows = new List<ResultRow>();
        var number = 0;
        for (var b = 0; b < merge.Blocks.Count; b++)
        {
            var block = merge.Blocks[b];
            if (block.Lines is { } lines)
            {
                if (lines.Count == 0 && block.IsChange)
                {
                    rows.Add(new ResultRow(Localizer.T("(lines removed)"), null, b, RowStyle.Placeholder));
                }
                rows.AddRange(lines.Select(line => new ResultRow(line, ++number, b, RowStyle.Text)));
                continue;
            }
            rows.Add(new ResultRow("<<<<<<< " + block.LeftLabel, null, b, RowStyle.Marker));
            rows.AddRange(block.Left.Select(line => new ResultRow(line, ++number, b, RowStyle.Text)));
            if (block.MarkerBase)
            {
                rows.Add(new ResultRow("||||||| base", null, b, RowStyle.Marker));
                rows.AddRange(block.Base.Select(line => new ResultRow(line, ++number, b, RowStyle.Text)));
            }
            rows.Add(new ResultRow("=======", null, b, RowStyle.Marker));
            rows.AddRange(block.Right.Select(line => new ResultRow(line, ++number, b, RowStyle.Text)));
            rows.Add(new ResultRow(">>>>>>> " + block.RightLabel, null, b, RowStyle.Marker));
        }
        _resultRows = rows;

        var scrolls = _panes.Append(_result).Select(e => e.TextArea.TextView.ScrollOffset).ToList();
        for (var c = 0; c < 3; c++)
        {
            _panes[c].Document = new TextDocument(string.Join('\n', _paneRows[c].Select(r => r.Text)));
        }
        _result.Document = new TextDocument(string.Join('\n', rows.Select(r => r.Text)));
        void Restore()
        {
            var editors = _panes.Append(_result).ToList();
            for (var i = 0; i < editors.Count; i++)
            {
                editors[i].ScrollToVerticalOffset(scrolls[i].Y);
                editors[i].ScrollToHorizontalOffset(scrolls[i].X);
            }
        }
        Restore();
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
        Paint();
    }

    /// <summary>The bar and the colors as the blocks are now.</summary>
    private void Paint()
    {
        var l = _look;
        foreach (var (tag, text, color, background) in _tags)
        {
            text.Foreground = new SolidColorBrush(color(l));
            tag.Background = new SolidColorBrush(background(l));
        }
        foreach (var (swatch, color) in _swatches)
        {
            swatch.Background = new SolidColorBrush(color(l));
        }
        foreach (var (take, button) in _takes)
        {
            if (button.Content is StackPanel { Children: [Border dot, ..] })
            {
                dot.Background = new SolidColorBrush(take switch { MergeTake.Left => l.Left, MergeTake.Right => l.Right, MergeTake.Both => l.Both, _ => l.Faint });
            }
        }
        foreach (var editor in _panes.Append(_result))
        {
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            editor.TextArea.TextView.Redraw();
            foreach (var margin in editor.TextArea.LeftMargins.OfType<NumberMargin>())
            {
                margin.InvalidateVisual();
            }
        }
        if (_merge is not { } merge)
        {
            return;
        }
        var left = merge.ConflictsLeft;
        var state = left > 0 ? l.Conflict : l.Both;
        _stateDot.Fill = new SolidColorBrush(state);
        _stateText.Foreground = new SolidColorBrush(state);
        _state.Background = new SolidColorBrush(l.Tint(state, conflict: left > 0));
        _stateText.Text = left == 0 ? "No conflicts left" : left == 1 ? "1 conflict left" : $"{left} conflicts left";
        _counter.Text = merge.Changes.Count == 0 ? "No changes" : $"Change {_current + 1} of {merge.Changes.Count}";
        _counter.Foreground = new SolidColorBrush(l.Muted);
        _previous.IsEnabled = _next.IsEnabled = merge.Changes.Count > 1;
        _nextConflict.IsEnabled = left > 0;
        var current = CurrentBlock;
        foreach (var (take, button) in _takes)
        {
            button.IsEnabled = current is not null && (take != MergeTake.Base || current.HasBase);
            var pressed = current?.Taken == take;
            button.Background = pressed ? new SolidColorBrush(l.Surface) : Brushes.Transparent;
            button.BorderBrush = new SolidColorBrush(l.LineStrong);
            button.BorderThickness = new Thickness(pressed ? 1 : 0);
        }
        _undo.IsEnabled = _history.Count > 0;
        foreach (var button in _takes.Values.Concat([_previous, _next, _nextConflict, _undo]))
        {
            foreach (var path in button.GetLogicalDescendantsOfType<Avalonia.Controls.Shapes.Path>())
            {
                path.Stroke = new SolidColorBrush(button.IsEnabled ? l.Text : l.Faint);
            }
        }
        UpdateTitle();
    }

    private MergeBlock? CurrentBlock => _merge is { Changes.Count: > 0 } merge ? merge.Blocks[merge.Changes[_current]] : null;

    private int CurrentIndex => _merge is { Changes.Count: > 0 } merge ? merge.Changes[_current] : -1;

    private void UpdateTitle() => Title = (_dirty ? "● " : string.Empty) + $"Merge: {_output.Name}";

    // ---------------------------------------------------------------- moving, taking

    /// <summary>The next (+1) or previous (-1) change, around from the last to the first.</summary>
    public void Move(int direction)
    {
        if (_merge is not { Changes.Count: > 0 } merge)
        {
            return;
        }
        _current = ((_current + direction) % merge.Changes.Count + merge.Changes.Count) % merge.Changes.Count;
        Paint();
        ScrollToCurrent();
    }

    /// <summary>F8: the next conflict not resolved yet.</summary>
    public void NextConflict()
    {
        if (_merge is not { Changes.Count: > 0 } merge)
        {
            return;
        }
        for (var k = 1; k <= merge.Changes.Count; k++)
        {
            var j = (_current + k) % merge.Changes.Count;
            if (merge.Blocks[merge.Changes[j]].IsUnresolved)
            {
                _current = j;
                Paint();
                ScrollToCurrent();
                return;
            }
        }
    }

    /// <summary>What the current change takes; a conflict resolved goes on to the next one.</summary>
    public void Take(MergeTake take)
    {
        if (CurrentBlock is not { } block || block.Taken == take || (take == MergeTake.Base && !block.HasBase))
        {
            return;
        }
        _history.Push((CurrentIndex, block.Taken));
        var wasConflict = block.IsUnresolved;
        block.Taken = take;
        _dirty = true;
        Render();
        if (wasConflict && _merge!.ConflictsLeft > 0)
        {
            DispatcherTimer.RunOnce(NextConflict, TimeSpan.FromMilliseconds(250));
        }
    }

    public void Undo()
    {
        if (_merge is not { } merge || !_history.TryPop(out var step))
        {
            return;
        }
        merge.Blocks[step.Block].Taken = step.Taken;
        _current = Math.Max(0, merge.Changes.ToList().IndexOf(step.Block));
        _dirty = true;
        Render();
        ScrollToCurrent();
    }

    /// <summary>A click on a change makes it the current one.</summary>
    private void Select(int block)
    {
        var index = _merge?.Changes.ToList().IndexOf(block) ?? -1;
        if (index < 0)
        {
            return;
        }
        _current = index;
        Paint();
    }

    private void Clickable(TextEditor editor, Func<int, int?> blockOf) =>
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (editor.GetPositionFromPoint(e.GetPosition(editor)) is { } position && blockOf(position.Line - 1) is { } block)
            {
                Select(block);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

    /// <summary>The current change a third from the top in the files and in the result.</summary>
    private void ScrollToCurrent()
    {
        var block = CurrentIndex;
        if (block < 0)
        {
            return;
        }
        void Scroll(TextEditor editor, int row)
        {
            if (row < 0)
            {
                return;
            }
            var height = editor.TextArea.TextView.Bounds.Height > 0 ? editor.TextArea.TextView.Bounds.Height : editor.Bounds.Height;
            editor.ScrollToVerticalOffset(Math.Max(0, (row * editor.TextArea.TextView.DefaultLineHeight) - (height / 3)));
        }
        void Both()
        {
            Scroll(_panes[0], _paneRows[0].FindIndex(r => r.Block == block));
            Scroll(_result, _resultRows.FindIndex(r => r.Block == block));
        }
        Both();
        Dispatcher.UIThread.Post(Both, DispatcherPriority.Background);
    }

    /// <summary>The three files are aligned: they scroll together.</summary>
    private void SyncScroll(int from)
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;
        var offset = _panes[from].TextArea.TextView.ScrollOffset;
        for (var i = 0; i < 3; i++)
        {
            if (i != from)
            {
                _panes[i].ScrollToVerticalOffset(offset.Y);
            }
        }
        _syncing = false;
    }

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Writes the result (unresolved conflicts with their markers); true when saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (_merge is not { } merge)
        {
            return false;
        }
        var content = new TextContent
        {
            Lines = merge.Result(),
            Encoding = _format.Encoding,
            Bom = _format.Bom,
            NewLine = _format.NewLine,
            EndsWithNewline = _format.EndsWithNewline,
        };
        try
        {
            var provider = _context.Registry.Resolve(_output.Path);
            await using (var stream = await provider.OpenWriteAsync(_output.Path, overwrite: true, CancellationToken.None))
            {
                await stream.WriteAsync(content.Bytes());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
        {
            ShowToast(ex.Message);
            return false;
        }
        _dirty = false;
        var left = merge.ConflictsLeft;
        Resolved = left == 0;
        Paint();
        var path = _output.Path.ToDisplayString();
        ShowToast(left == 0 ? $"Saved {path}"
            : left == 1 ? $"Saved {path} with 1 conflict marker still in the file" : $"Saved {path} with {left} conflict markers still in the file");
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || !_dirty)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var choice = await AskSaveOnClose();
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAsync()))
            {
                return;
            }
            _closingConfirmed = true;
            Close();
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrl = mods == KeyModifiers.Control || mods == KeyModifiers.Meta;
        MergeTake? take = e.Key switch
        {
            Key.D1 or Key.NumPad1 => MergeTake.Left,
            Key.D2 or Key.NumPad2 => MergeTake.Base,
            Key.D3 or Key.NumPad3 => MergeTake.Right,
            Key.D4 or Key.NumPad4 => MergeTake.Both,
            _ => null,
        };
        if (take is { } chosen && (ctrl || mods == KeyModifiers.Alt))
        {
            Take(chosen);
        }
        else if (e.Key is Key.Up && mods == KeyModifiers.Alt || e.Key == Key.F2 && mods == KeyModifiers.Shift)
        {
            Move(-1);
        }
        else if (e.Key is Key.Down && mods == KeyModifiers.Alt || e.Key == Key.F2 && mods == KeyModifiers.None)
        {
            Move(+1);
        }
        else if (e.Key == Key.F8 && mods == KeyModifiers.None)
        {
            NextConflict();
        }
        else if (e.Key == Key.Z && ctrl)
        {
            Undo();
        }
        else if (e.Key == Key.S && ctrl)
        {
            _ = SaveAsync();
        }
        else if (e.Key == Key.Escape && mods == KeyModifiers.None)
        {
            Close();
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>The background of a block's row in one of the three files, as the template colors them.</summary>
    private Color? PaneColor(MergeBlock block, int column, bool pad)
    {
        var l = _look;
        if (pad)
        {
            return l.Fill;
        }
        return (block.Kind, column) switch
        {
            (MergeBlockKind.Same, _) => null,
            (_, 1) => l.Surface2,
            (MergeBlockKind.Conflict, _) => l.Tint(l.Conflict, conflict: true),
            (MergeBlockKind.Both, _) => l.Tint(l.Both),
            (MergeBlockKind.Left, 0) => l.Tint(l.Left),
            (MergeBlockKind.Right, 2) => l.Tint(l.Right),
            _ => null,
        };
    }

    private Color KindColor(MergeBlock block) => block.Kind switch
    {
        MergeBlockKind.Left => _look.Left,
        MergeBlockKind.Right => _look.Right,
        MergeBlockKind.Both => _look.Both,
        _ => _look.Conflict,
    };

    /// <summary>"Took Left" when a change takes other than what the merge took by itself, "Auto" when it takes that.</summary>
    private static string? Picked(MergeBlock block) =>
        !block.IsChange || block.Taken is not { } taken ? null
        : block.Kind != MergeBlockKind.Conflict && taken == block.Auto ? Localizer.T("Auto")
        : Localizer.T(taken switch
        {
            MergeTake.Left => "Took Left",
            MergeTake.Right => "Took Right",
            MergeTake.Base => "Took Base",
            _ => "Took Both",
        });

    private sealed class PaneColors(MergeWindow owner, int column) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            var rows = owner._paneRows[column];
            if (!textView.VisualLinesValid || owner._merge is not { } merge || rows.Count == 0)
            {
                return;
            }
            var l = owner._look;
            var current = owner.CurrentIndex;
            var frame = new Pen(new SolidColorBrush(l.Accent), 1);
            var typeface = new Typeface(new FontFamily("Segoe UI,Inter,system-ui"), FontStyle.Normal, FontWeight.SemiBold);
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count)
                {
                    continue;
                }
                var row = rows[index];
                var block = merge.Blocks[row.Block];
                var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height);
                if (owner.PaneColor(block, column, row.Pad) is { } color)
                {
                    context.FillRectangle(new SolidColorBrush(color), rect);
                }
                if (row.Pad)
                {
                    using (context.PushClip(rect))
                    {
                        var pen = new Pen(new SolidColorBrush(l.Line), 1);
                        for (var x = rect.Left - rect.Height; x < rect.Right; x += 7)
                        {
                            context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                        }
                    }
                }
                var first = index == 0 || rows[index - 1].Block != row.Block;
                var last = index == rows.Count - 1 || rows[index + 1].Block != row.Block;
                if (block.IsChange && column == 0)
                {
                    context.FillRectangle(new SolidColorBrush(owner.KindColor(block)), new Rect(0, rect.Top, row.Block == current ? 4 : 3, rect.Height));
                }
                if (row.Block == current)
                {
                    if (first)
                    {
                        context.DrawLine(frame, rect.TopLeft, rect.TopRight);
                    }
                    if (last)
                    {
                        context.DrawLine(frame, rect.BottomLeft, rect.BottomRight);
                    }
                }
                if (first && column == 2 && Picked(block) is { } picked)
                {
                    var text = new FormattedText(picked.ToUpperInvariant(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, new SolidColorBrush(l.Muted));
                    var box = new Rect(rect.Right - text.Width - 20, rect.Top + 3, text.Width + 12, Math.Min(rect.Height - 6, text.Height + 2));
                    context.FillRectangle(new SolidColorBrush(l.Surface3), box, 4);
                    context.DrawText(text, new Point(box.X + 6, box.Y + ((box.Height - text.Height) / 2)));
                }
            }
        }
    }

    private sealed class ResultColors(MergeWindow owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            var rows = owner._resultRows;
            if (!textView.VisualLinesValid || owner._merge is not { } merge || rows.Count == 0)
            {
                return;
            }
            var l = owner._look;
            var current = owner.CurrentIndex;
            var frame = new Pen(new SolidColorBrush(l.Accent), 1);
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count)
                {
                    continue;
                }
                var row = rows[index];
                var block = merge.Blocks[row.Block];
                if (!block.IsChange)
                {
                    continue;
                }
                // where the block comes from: the side taken, both, the base, or an open conflict
                var color = block.IsUnresolved ? l.Conflict
                    : block.Taken == MergeTake.Both || (block.Kind == MergeBlockKind.Both && block.Taken == MergeTake.Left) ? l.Both
                    : block.Taken == MergeTake.Left ? l.Left
                    : block.Taken == MergeTake.Right ? l.Right
                    : l.Faint;
                var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height);
                context.FillRectangle(new SolidColorBrush(block.Taken == MergeTake.Base ? l.Surface2 : l.Tint(color, conflict: block.IsUnresolved)), rect);
                context.FillRectangle(new SolidColorBrush(color), new Rect(0, rect.Top, 3, rect.Height));
                if (row.Block == current)
                {
                    if (index == 0 || rows[index - 1].Block != row.Block)
                    {
                        context.DrawLine(frame, rect.TopLeft, rect.TopRight);
                    }
                    if (index == rows.Count - 1 || rows[index + 1].Block != row.Block)
                    {
                        context.DrawLine(frame, rect.BottomLeft, rect.BottomRight);
                    }
                }
            }
        }
    }

    /// <summary>The markers of a conflict in its color and bold, "(lines removed)" faint and italic.</summary>
    private sealed class ResultMarks(MergeWindow owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var index = line.LineNumber - 1;
            if (index >= owner._resultRows.Count || line.Length == 0)
            {
                return;
            }
            var style = owner._resultRows[index].Style;
            if (style == RowStyle.Text)
            {
                return;
            }
            var l = owner._look;
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(style == RowStyle.Marker ? l.Conflict : l.Faint));
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily,
                    style == RowStyle.Placeholder ? FontStyle.Italic : FontStyle.Normal, style == RowStyle.Marker ? FontWeight.Bold : FontWeight.Normal));
            });
        }
    }

    /// <summary>The numbers of the rows in their file (none for pads and markers).</summary>
    private sealed class NumberMargin(MergeWindow owner, Func<int, int?> numberOf) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new((TextView?.WideSpaceWidth ?? 8) * 4.2, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= Changed;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += Changed;
            }
        }

        private void Changed(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid)
            {
                return;
            }
            var brush = new SolidColorBrush(owner._look.Faint);
            var typeface = new Typeface(view.GetValue(TextBlock.FontFamilyProperty));
            var size = view.GetValue(TextBlock.FontSizeProperty) * 0.9;
            foreach (var visual in view.VisualLines)
            {
                if (numberOf(visual.FirstDocumentLine.LineNumber - 1) is not { } number)
                {
                    continue;
                }
                var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
                var top = visual.VisualTop - view.ScrollOffset.Y;
                context.DrawText(text, new Point(Bounds.Width - text.Width - 10, top + ((visual.Height - text.Height) / 2)));
            }
        }
    }
}
