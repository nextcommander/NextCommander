using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Columns;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The column sets (view modes; Configuration > Column sets, nc_EditColumnSets). On the left the sets with their icon,
/// columns and folders, New / Duplicate / Delete; on the right the chosen set: its name, the columns (header, field,
/// width, order), a preview drawn to scale, the folders that switch to it, background and tab colors, icon, and the
/// commands run on entering and leaving. Changes are made on a copy: Apply / OK write them back, Cancel drops them.
/// </summary>
public sealed class ColumnSetDialog : Window
{
    private static readonly IBrush s_danger = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly FontFamily s_mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    private static readonly JsonSerializerOptions s_json = new();

    /// <summary>The icons a set can have: stored name, shown name, the file kind drawn.</summary>
    private static readonly (string? Key, string Text, FileKind Kind)[] s_icons =
    [
        (null, "File", FileKind.File),
        ("image", "Picture", FileKind.Image),
        ("audio", "Music", FileKind.Audio),
        ("video", "Video", FileKind.Video),
        ("code", "Code", FileKind.Code),
        ("document", "Document", FileKind.Document),
    ];

    /// <summary>Values of the preview (the window only shows them).</summary>
    private static readonly Dictionary<string, string[]> s_samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file.created"] = ["10/30/2026 9:12", "10/29/2026 18:40", "10/28/2026 7:05"],
        ["file.accessed"] = ["11/18/2026 8:01", "11/17/2026 22:13", "11/12/2026 10:44"],
        ["file.type"] = ["JPEG image", "Text", "PDF document"],
        ["file.folder"] = ["Pictures", "Pictures", "Documents"],
        ["image.dimensions"] = ["4032×3024", "3024×4032", "1920×1080"],
        ["image.width"] = ["4032", "3024", "1920"],
        ["image.height"] = ["3024", "4032", "1080"],
        ["image.taken"] = ["7/14/2026 09:12", "7/14/2026 11:02", "7/15/2026 08:22"],
        ["image.camera"] = ["iPhone 15 Pro", "Canon EOS R6", "Pixel 9"],
        ["image.exposure"] = ["1/250 s", "1/60 s", "1/1000 s"],
        ["media.title"] = ["Intro", "Blue Hour", "Night Drive"],
        ["media.artist"] = ["Deniz Ekinci", "Kuzey Trio", "Ayaz"],
        ["media.album"] = ["Sahil Yolu", "Gece Treni", "İlk Kar"],
        ["media.duration"] = ["3:41", "4:12", "5:03"],
        ["media.bitrate"] = ["320", "256", "192"],
        ["media.year"] = ["2006", "2002", "2004"],
        ["text.lines"] = ["412", "88", "1204"],
        ["text.encoding"] = ["UTF-8", "UTF-8 BOM", "ASCII"],
        ["comment.text"] = ["Final version", "From the client", "—"],
    };

    private static readonly Dictionary<FileKind, string[]> s_names = new()
    {
        [FileKind.Image] = ["IMG_0412.jpg", "IMG_0418.jpg", "DSC_2290.jpg"],
        [FileKind.Audio] = ["01 Intro.mp3", "02 Blue Hour.flac", "03 Night Drive.mp3"],
        [FileKind.Video] = ["Holiday.mp4", "Interview.mkv", "Clip.mov"],
        [FileKind.Code] = ["Program.cs", "Gallery.cs", "index.html"],
        [FileKind.File] = ["Report.pdf", "notes.txt", "Contract.docx"],
    };

    private readonly List<ColumnSet> _sets;
    private readonly IReadOnlyList<ColumnField> _fields;
    private readonly Action? _applied;
    private readonly List<ColumnSet> _work;
    private readonly StackPanel _setList = new() { Spacing = 2, Margin = new Thickness(8, 0) };
    private readonly StackPanel _setTools = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _page = new() { Spacing = 22, MaxWidth = 900, Margin = new Thickness(32, 22, 32, 32) };
    private readonly TextBlock _info = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _apply = new() { Content = "Apply", Classes = { "tb" } };
    private readonly Border _preview = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly TextBox _enter = new() { PlaceholderText = "None", FontFamily = s_mono, MinWidth = 240, Height = 32, MinHeight = 32, Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBox _exit = new() { PlaceholderText = "None", FontFamily = s_mono, MinWidth = 240, Height = 32, MinHeight = 32, Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private string _saved;
    private ColumnSet? _current;
    private bool _confirmDelete;
    private bool _loading;

    public ColumnSetDialog(List<ColumnSet> sets, IReadOnlyList<ColumnField> fields, Action? applied = null)
    {
        _sets = sets;
        _fields = fields;
        _applied = applied;
        _work = [.. sets.Select(s => s.Clone())];
        _saved = Snapshot();
        _current = _work.FirstOrDefault();
        Title = "Column sets";
        Width = 1080;
        Height = 800;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _dot[!Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        _enter.TextChanged += (_, _) => Store(s => s.EnterCommand = Empty(_enter.Text));
        _exit.TextChanged += (_, _) => Store(s => s.ExitCommand = Empty(_exit.Text));
        Content = BuildLayout();
        Render();
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>The sets being edited (a copy until Apply / OK).</summary>
    public IReadOnlyList<ColumnSet> WorkingSets => _work;

    /// <summary>The set shown on the right.</summary>
    public ColumnSet? Current => _current;

    /// <summary>Changes not applied yet.</summary>
    public bool IsDirty => Snapshot() != _saved;

    /// <summary>The headers of the preview, left to right (tests).</summary>
    public IReadOnlyList<string> PreviewHeaders { get; private set; } = [];

    public void SelectSet(int index)
    {
        _current = index >= 0 && index < _work.Count ? _work[index] : null;
        _confirmDelete = false;
        Render();
    }

    // ---------------------------------------------------------------- layout

    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is { } screen && Height > screen.WorkingArea.Height / screen.Scaling - 16)
        {
            Height = screen.WorkingArea.Height / screen.Scaling - 16;
            Position = new PixelPoint(Position.X, screen.WorkingArea.Y);
        }
    }

    private Control BuildLayout()
    {
        // left: the sets
        var title = new TextBlock { Text = "Column sets", FontSize = 12, Opacity = 0.75, Margin = new Thickness(14, 16, 14, 8) };
        var setScroll = new ScrollViewer { Content = _setList };
        var tools = new Border { Child = _setTools, Padding = new Thickness(12, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
        tools[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var left = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(tools, Dock.Bottom);
        left.Children.AddRange([title, tools, setScroll]);
        var leftBorder = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        leftBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        // right: the editor
        var editor = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        editor[!BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        Grid.SetColumn(editor, 1);
        body.Children.AddRange([leftBorder, editor]);

        // below: what changed; Cancel, Apply, OK
        var cancel = new Button { Content = "Cancel", Classes = { "tb" } };
        cancel.Click += (_, _) => Close();
        _apply.Click += (_, _) => Apply();
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "tb", "primary" }, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            Apply();
            Close();
        };
        var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        info.Children.AddRange([_dot, _info]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, _apply, ok]);
        var foot = new DockPanel { Margin = new Thickness(18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, info]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([footBorder, body]);
        return root;
    }

    private static Border Card(params Control[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = new Border { Child = rows[i], BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            stack.Children.Add(row);
        }
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    /// <summary>A setting row: its name and description on the left, the control on the right.</summary>
    private static Control Row(string label, string description, Control control)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.AddRange([
            new TextBlock { Text = label, FontSize = 13.5 },
            new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
        ]);
        control.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { MinHeight = 58, Margin = new Thickness(16, 10) };
        DockPanel.SetDock(control, Dock.Right);
        control.Margin = new Thickness(16, 0, 0, 0);
        row.Children.AddRange([control, texts]);
        return row;
    }

    private static Control Section(string title, Control content, Control? badge = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 0, 0, 8) };
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (badge is not null)
        {
            head.Children.Add(badge);
        }
        var section = new StackPanel();
        section.Children.AddRange([head, content]);
        return section;
    }

    private static Button IconButton(string glyph, string tip, bool danger = false, double size = 32)
    {
        var button = new Button
        {
            Content = DialogLook.Glyph(glyph, 14, danger: danger),
            Classes = { "tb", "icon", "ghost" },
            Width = size,
            Height = size,
            MinHeight = size,
            MinWidth = size,
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private static Control KindIcon(FileKind kind, double size)
    {
        var box = new Viewbox { Width = size, Height = size };
        if (FileKinds.GeometryFor(kind) is { } geometry)
        {
            box.Child = IconPacks.Draw(geometry, FileKinds.BrushFor(kind));
        }
        return box;
    }

    private static FileKind KindOf(ColumnSet set) => s_icons.FirstOrDefault(i => i.Key == set.Icon).Kind is var kind && set.Icon is not null ? kind : FileKind.File;

    private static Color? ParseColor(string? text) => Color.TryParse(text, out var color) ? color : null;

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private string Snapshot() => JsonSerializer.Serialize(_work, s_json);

    // ---------------------------------------------------------------- the sets

    private void RenderSets()
    {
        _setList.Children.Clear();
        foreach (var set in _work)
        {
            var on = ReferenceEquals(set, _current);
            var iconBox = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = KindIcon(KindOf(set), 16) };
            if (ParseColor(set.TabColor) is { } tab)
            {
                iconBox.Background = new SolidColorBrush(tab, 0.14);
                iconBox.BorderBrush = new SolidColorBrush(tab, 0.4);
            }
            else
            {
                iconBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            }
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(set.Name) ? "Untitled" : set.Name,
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontStyle = string.IsNullOrWhiteSpace(set.Name) ? FontStyle.Italic : FontStyle.Normal,
            };
            if (!string.IsNullOrWhiteSpace(set.Name))
            {
                name.Classes.Add("noloc");
            }
            var details = set.Columns.Count == 1 ? "1 column" : string.Format(CultureInfo.CurrentCulture, "{0} columns", set.Columns.Count);
            var detail = new StackPanel { Orientation = Orientation.Horizontal };
            detail.Children.Add(new TextBlock { Text = details, FontSize = 11.5, Opacity = 0.6 });
            if (!string.IsNullOrWhiteSpace(set.AutoPathMask))
            {
                detail.Children.Add(new TextBlock { Text = " · " + set.AutoPathMask.Replace(";", " ", StringComparison.Ordinal), FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
            }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            texts.Children.AddRange([name, detail]);
            var row = new DockPanel();
            DockPanel.SetDock(iconBox, Dock.Left);
            row.Children.AddRange([iconBox, texts]);
            var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(-10, 4, 7, 4), IsVisible = on };
            marker[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Insert(0, marker);
            var item = new Border { Child = row, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
            if (on)
            {
                item[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
            }
            else
            {
                item.Background = Brushes.Transparent;
            }
            var which = set;
            item.PointerPressed += (_, e) =>
            {
                SelectSet(_work.IndexOf(which));
                e.Handled = true;
            };
            item.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    SelectSet(_work.IndexOf(which));
                    e.Handled = true;
                }
            };
            _setList.Children.Add(item);
        }
        RenderSetTools();
    }

    private void RenderSetTools()
    {
        _setTools.Children.Clear();
        if (_confirmDelete && _current is not null)
        {
            var question = new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "Delete “{0}”?", _current.Name), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 110 };
            var yes = new Button { Content = "Delete", Classes = { "tb", "danger" } };
            yes.Click += (_, _) => DeleteSet();
            var no = new Button { Content = "Keep", Classes = { "tb" } };
            no.Click += (_, _) =>
            {
                _confirmDelete = false;
                RenderSetTools();
            };
            _setTools.Children.AddRange([question, yes, no]);
            return;
        }
        var add = new Button { Content = WithGlyph("MiPlus", "New set"), Classes = { "tb" } };
        add.Click += (_, _) => NewSet();
        var copy = IconButton("MiCopy", "Duplicate set");
        copy.Classes.Remove("ghost");
        copy.IsEnabled = _current is not null;
        copy.Click += (_, _) => DuplicateSet();
        var delete = IconButton("MiTrash", "Delete set", danger: true);
        delete.Classes.Remove("ghost");
        delete.IsEnabled = _current is not null;
        delete.Click += (_, _) =>
        {
            _confirmDelete = true;
            RenderSetTools();
        };
        _setTools.Children.AddRange([add, copy, delete]);
    }

    private static StackPanel WithGlyph(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([DialogLook.Glyph(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    public void NewSet()
    {
        var set = new ColumnSet { Name = "New set" };
        _work.Add(set);
        SelectSet(_work.Count - 1);
    }

    public void DuplicateSet()
    {
        if (_current is null)
        {
            return;
        }
        var copy = _current.Clone();
        copy.Name = string.Format(CultureInfo.CurrentCulture, "{0} (copy)", _current.Name);
        copy.AutoPathMask = null; // two sets for the same folders would fight
        _work.Insert(_work.IndexOf(_current) + 1, copy);
        SelectSet(_work.IndexOf(copy));
    }

    public void DeleteSet()
    {
        if (_current is null)
        {
            return;
        }
        var index = _work.IndexOf(_current);
        _work.RemoveAt(index);
        SelectSet(Math.Min(index, _work.Count - 1));
    }

    // ---------------------------------------------------------------- the editor

    /// <summary>Draws the whole window again (a set chosen, columns added / removed / moved, masks ...).</summary>
    private void Render()
    {
        _loading = true;
        _page.Children.Clear();
        var set = _current;
        if (set is null)
        {
            _page.Children.Add(new TextBlock { Text = "No column sets yet. Make one with New set.", Opacity = 0.7, Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        }
        else
        {
            var name = new TextBox { Text = set.Name, PlaceholderText = "Set name", FontSize = 22, FontWeight = FontWeight.SemiBold, MinHeight = 42, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Margin = new Thickness(-8, 0, 0, 0), Classes = { "noloc" } };
            Avalonia.Automation.AutomationProperties.SetName(name, "Set name");
            name.TextChanged += (_, _) => Store(s => s.Name = name.Text ?? string.Empty);
            _page.Children.AddRange([name, ColumnsSection(set), PreviewSection(), AutoSection(set), LookSection(set)]);
        }
        _enter.Text = set?.EnterCommand;
        _exit.Text = set?.ExitCommand;
        _enter.IsEnabled = _exit.IsEnabled = set is not null;
        if (_enter.Parent is Panel oldEnter)
        {
            oldEnter.Children.Remove(_enter);
        }
        if (_exit.Parent is Panel oldExit)
        {
            oldExit.Children.Remove(_exit);
        }
        var commands = Section("Commands", Card(
            Row("On entering", "Runs when a folder switches to this set, e.g. thumbnails view.", WithPicker(_enter)),
            Row("On leaving", "Runs when the folder switches to another set.", WithPicker(_exit))));
        commands.IsVisible = set is not null;
        _page.Children.Add(commands);
        _loading = false;
        RenderSets();
        RenderPreview();
        UpdateFoot();
    }

    private static Control WithPicker(TextBox box)
    {
        var pick = CommandPickerDialog.ButtonFor(box);
        pick.Classes.Add("tb");
        pick.Bind(IsEnabledProperty, new Avalonia.Data.Binding(nameof(IsEnabled)) { Source = box });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.AddRange([box, pick]);
        return line;
    }

    /// <summary>A change typed into a field: stored at once, then the list, preview and footer follow (the fields stay).</summary>
    private void Store(Action<ColumnSet> change)
    {
        if (_loading || _current is null)
        {
            return;
        }
        change(_current);
        Touch();
    }

    private void Touch()
    {
        RenderSets();
        RenderPreview();
        UpdateFoot();
    }

    private void UpdateFoot()
    {
        var dirty = IsDirty;
        _apply.IsEnabled = dirty;
        _dot.IsVisible = dirty;
        _info.Text = dirty ? "Unsaved changes"
            : _work.Count == 1 ? "1 column set" : string.Format(CultureInfo.CurrentCulture, "{0} column sets", _work.Count);
    }

    private string GroupOf(ColumnField field) => field.Provider switch
    {
        "file" => "File",
        "image" => "Image",
        "media" => "Audio and video",
        "text" => "Text",
        "comment" => "Comment",
        var other => other,
    };

    private ColumnField? Field(string id) => _fields.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private Control ColumnsSection(ColumnSet set)
    {
        var count = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        count[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundBaseLowBrush");
        count[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        count.Child = new TextBlock { Text = set.Columns.Count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.8, Classes = { "noloc" } };

        const string grid = "28,1.2*,*,200,90";
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(grid), ColumnSpacing = 10, Margin = new Thickness(12, 8) };
        string[] titles = [string.Empty, "Header", "Field", "Width", string.Empty];
        for (var i = 0; i < titles.Length; i++)
        {
            var title = new TextBlock { Text = titles[i], FontSize = 12, Opacity = 0.7 };
            Grid.SetColumn(title, i);
            head.Children.Add(title);
        }
        var headBorder = new Border { Child = head };
        headBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");

        var rows = new StackPanel();
        for (var i = 0; i < set.Columns.Count; i++)
        {
            var row = ColumnRow(set, i, grid);
            if (i > 0)
            {
                row.BorderThickness = new Thickness(0, 1, 0, 0);
                row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            }
            rows.Children.Add(row);
        }
        if (set.Columns.Count == 0)
        {
            rows.Children.Add(new TextBlock { Text = "No columns yet. Add one below.", Opacity = 0.7, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26) });
        }

        // add a column: the fields not used yet
        var free = _fields.Where(f => !set.Columns.Any(c => string.Equals(c.Field, f.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => GroupOf(f), StringComparer.CurrentCulture).ToList();
        var combo = new ComboBox { ItemsSource = free, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, SelectedIndex = free.Count > 0 ? 0 : -1 };
        Avalonia.Automation.AutomationProperties.SetName(combo, "Field to add");
        combo.ItemTemplate = new FuncDataTemplate<ColumnField>((f, _) =>
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (f is not null)
            {
                line.Children.AddRange([
                    new TextBlock { Text = GroupOf(f), Opacity = 0.6 },
                    new TextBlock { Text = "·", Opacity = 0.6, Classes = { "noloc" } },
                    new TextBlock { Text = f.DisplayName },
                    new TextBlock { Text = f.Id, FontFamily = s_mono, FontSize = 11.5, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
                ]);
            }
            return line;
        });
        var add = new Button { Content = WithGlyph("MiPlus", "Add column"), Classes = { "tb", "primary" }, IsEnabled = free.Count > 0 };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is ColumnField field)
            {
                AddColumn(field.Id);
            }
        };
        var addRow = new DockPanel { Margin = new Thickness(12, 10) };
        DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(8, 0, 0, 0);
        addRow.Children.AddRange([add, combo]);
        var addBorder = new Border { Child = addRow };
        addBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");

        var standard = new CheckBox { IsChecked = set.KeepStandardColumns, Classes = { "switch" } };
        Avalonia.Automation.AutomationProperties.SetName(standard, "Also show standard columns");
        standard.IsCheckedChanged += (_, _) => Store(s => s.KeepStandardColumns = standard.IsChecked == true);
        var standardRow = Row("Also show size, date and attribute columns", "The standard columns stay before the columns above.", standard);

        return Section("Columns", Card(headBorder, rows, addBorder, standardRow), count);
    }

    private Border ColumnRow(ColumnSet set, int index, string grid)
    {
        var column = set.Columns[index];
        var field = Field(column.Field);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions(grid), ColumnSpacing = 10, Margin = new Thickness(12, 8) };

        var number = new TextBlock { Text = (index + 1).ToString(CultureInfo.CurrentCulture), Opacity = 0.5, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "noloc" } };
        var header = new TextBox { Text = column.Header, PlaceholderText = field?.DisplayName ?? column.Field, MinHeight = 32, Height = 32, Padding = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(header, "Header");
        header.TextChanged += (_, _) => Store(_ => column.Header = string.IsNullOrWhiteSpace(header.Text) ? null : header.Text);
        var fieldText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        fieldText.Children.AddRange([
            new TextBlock { Text = column.Field, FontFamily = s_mono, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } },
            new TextBlock { Text = field is null ? "Not available" : GroupOf(field), FontSize = 11, Opacity = 0.55 },
        ]);
        ToolTip.SetTip(fieldText, column.Field);

        var slider = new Slider { Minimum = 40, Maximum = 400, Value = Math.Clamp(column.Width, 40, 400), SmallChange = 5, LargeChange = 20, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(slider, "Width");
        var pixels = new TextBox { Text = column.Width.ToString("0", CultureInfo.CurrentCulture), Width = 58, MinHeight = 32, Height = 32, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(pixels, "Width in pixels");
        var syncing = false;
        slider.ValueChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }
            syncing = true;
            pixels.Text = slider.Value.ToString("0", CultureInfo.CurrentCulture);
            syncing = false;
            Store(_ => column.Width = slider.Value);
        };
        pixels.TextChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }
            if (double.TryParse(pixels.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var w) && w is >= 20 and <= 1000)
            {
                pixels.Classes.Remove("jsoninvalid");
                syncing = true;
                slider.Value = Math.Clamp(w, 40, 400);
                syncing = false;
                Store(_ => column.Width = w);
            }
            else
            {
                pixels.Classes.Add("jsoninvalid");
            }
        };
        var px = new TextBlock { Text = "px", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var width = new DockPanel();
        DockPanel.SetDock(px, Dock.Right);
        DockPanel.SetDock(pixels, Dock.Right);
        pixels.Margin = new Thickness(8, 0, 6, 0);
        width.Children.AddRange([px, pixels, slider]);

        var up = IconButton("MiArrowUp", "Move up", size: 26);
        up.IsEnabled = index > 0;
        up.Click += (_, _) => MoveColumn(index, -1);
        var down = IconButton("MiArrowDown", "Move down", size: 26);
        down.IsEnabled = index < set.Columns.Count - 1;
        down.Click += (_, _) => MoveColumn(index, 1);
        var remove = IconButton("MiX", "Remove column", danger: true, size: 26);
        remove.Click += (_, _) => RemoveColumn(index);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.AddRange([up, down, remove]);

        Grid.SetColumn(header, 1);
        Grid.SetColumn(fieldText, 2);
        Grid.SetColumn(width, 3);
        Grid.SetColumn(actions, 4);
        line.Children.AddRange([number, header, fieldText, width, actions]);
        return new Border { Child = line };
    }

    public void AddColumn(string fieldId)
    {
        if (_current is null)
        {
            return;
        }
        var field = Field(fieldId);
        _current.Columns.Add(new ColumnSpec { Field = fieldId, Width = field?.Kind is ColumnKind.Number ? 80 : 120 });
        Render();
    }

    public void RemoveColumn(int index)
    {
        if (_current is not null && index >= 0 && index < _current.Columns.Count)
        {
            _current.Columns.RemoveAt(index);
            Render();
        }
    }

    public void MoveColumn(int index, int delta)
    {
        if (_current is null || index < 0 || index >= _current.Columns.Count)
        {
            return;
        }
        var target = Math.Clamp(index + delta, 0, _current.Columns.Count - 1);
        var column = _current.Columns[index];
        _current.Columns.RemoveAt(index);
        _current.Columns.Insert(target, column);
        Render();
    }

    // ---------------------------------------------------------------- preview

    private Control PreviewSection()
    {
        _preview[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        if (_preview.Parent is Panel old)
        {
            old.Children.Remove(_preview);
        }
        var note = new TextBlock { Text = "Use the arrows to reorder the columns. Widths are shown to scale.", FontSize = 12, Opacity = 0.6, Margin = new Thickness(2, 8, 0, 0) };
        var stack = new StackPanel();
        stack.Children.AddRange([_preview, note]);
        return Section("Preview", stack);
    }

    /// <summary>A panel with the set: its tab (in the tab color), the name, standard and custom columns at their widths, three files.</summary>
    private void RenderPreview()
    {
        if (_current is not { } set)
        {
            PreviewHeaders = [];
            return;
        }
        var kind = KindOf(set);
        var names = s_names.GetValueOrDefault(kind) ?? s_names[FileKind.File];
        var columns = new List<(string Header, double Width, string[] Values, bool Custom, bool Translate)> { ("Name", 170, names, false, true) };
        if (set.KeepStandardColumns)
        {
            columns.Add(("Size", 70, ["2,310 KB", "1,987 KB", "2,501 KB"], false, true));
            columns.Add(("Date", 120, ["10/30/2026 9:12", "10/29/2026 18:40", "10/28/2026 7:05"], false, true));
        }
        foreach (var c in set.Columns)
        {
            var field = Field(c.Field);
            var values = s_samples.GetValueOrDefault(c.Field) ?? (field?.Kind switch
            {
                ColumnKind.Number => ["42", "7", "128"],
                ColumnKind.Date => ["7/14/2026 09:12", "7/14/2026 11:02", "7/15/2026 08:22"],
                ColumnKind.Size => ["12 KB", "3 MB", "640 KB"],
                _ => ["—", "—", "—"],
            });
            columns.Add((c.Header ?? field?.DisplayName ?? c.Field, Math.Max(20, c.Width), values, true, c.Header is null));
        }
        PreviewHeaders = [.. columns.Select(c => c.Header)];

        var background = ParseColor(set.Background);
        IBrush? text = background is { } bg ? (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255 > 0.4 ? new SolidColorBrush(Color.Parse("#1A1A1A")) : Brushes.White : null;

        var table = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in columns)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition(c.Width, GridUnitType.Pixel));
        }
        for (var r = 0; r < 4; r++)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var head = new TextBlock { Text = c.Header, FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            if (!c.Translate)
            {
                head.Classes.Add("noloc");
            }
            if (c.Custom)
            {
                head[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            }
            else
            {
                head.Opacity = 0.75;
                if (text is not null)
                {
                    head.Foreground = text;
                }
            }
            var headCell = new Border { Child = head, Padding = new Thickness(8, 6), BorderThickness = new Thickness(0, 0, 1, 1) };
            headCell[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            Grid.SetColumn(headCell, i);
            table.Children.Add(headCell);
            for (var r = 0; r < 3; r++)
            {
                var value = new TextBlock { Text = c.Values[r], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
                if (text is not null)
                {
                    value.Foreground = text;
                }
                var cell = new Border { Child = value, Padding = new Thickness(8, 4), BorderThickness = new Thickness(0, 0, 1, 0) };
                cell[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
                if (r == 1)
                {
                    cell[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
                }
                Grid.SetColumn(cell, i);
                Grid.SetRow(cell, r + 1);
                table.Children.Add(cell);
            }
        }
        var tableBox = new Border { Child = table };
        if (background is { } back)
        {
            tableBox.Background = new SolidColorBrush(back);
        }
        else
        {
            tableBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        }
        var scroll = new ScrollViewer { Content = tableBox, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // the tabs: this set (its color above) and another folder
        var setTab = new Border { Padding = new Thickness(12, 5), CornerRadius = new CornerRadius(6, 6, 0, 0), BorderThickness = new Thickness(0, 2, 0, 0) };
        setTab.Child = new TextBlock { Text = string.IsNullOrWhiteSpace(set.Name) ? "Set" : set.Name, FontSize = 12, Classes = { "noloc" } };
        if (background is { } tabBack)
        {
            setTab.Background = new SolidColorBrush(tabBack);
            ((TextBlock)setTab.Child).Foreground = text;
        }
        else
        {
            setTab[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        }
        setTab.BorderBrush = ParseColor(set.TabColor) is { } tabColor ? new SolidColorBrush(tabColor) : Brushes.Transparent;
        var otherTab = new Border { Padding = new Thickness(12, 5), Child = new TextBlock { Text = "Downloads", FontSize = 12, Opacity = 0.7 } };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 6, 8, 0) };
        tabs.Children.AddRange([setTab, otherTab]);
        var tabStrip = new Border { Child = tabs };
        tabStrip[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");

        var stack = new StackPanel();
        stack.Children.AddRange([tabStrip, scroll]);
        _preview.Child = stack;
    }

    // ---------------------------------------------------------------- switch automatically

    private Control AutoSection(ColumnSet set)
    {
        var masks = (set.AutoPathMask ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var tokens = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var purple = Color.Parse("#8A5CF5");
        for (var i = 0; i < masks.Count; i++)
        {
            var mask = masks[i];
            var remove = new Button { Content = DialogLook.Glyph("MiX", 10), Width = 18, Height = 18, MinHeight = 18, MinWidth = 18, Padding = new Thickness(0), Classes = { "tb", "ghost" }, CornerRadius = new CornerRadius(3), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Remove");
            remove.Click += (_, _) => RemoveMask(mask);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.AddRange([
                new TextBlock { Text = mask, FontFamily = s_mono, FontSize = 12, Foreground = new SolidColorBrush(purple), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
                remove,
            ]);
            tokens.Children.Add(new Border { Child = content, Height = 24, CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 0, 4, 0), Background = new SolidColorBrush(purple, 0.15) });
        }
        var input = new TextBox
        {
            PlaceholderText = masks.Count > 0 ? "Add a mask" : "*Pictures*",
            FontFamily = s_mono,
            FontSize = 12.5,
            MinWidth = 160,
            MinHeight = 24,
            Padding = new Thickness(4, 2),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Classes = { "noloc" },
        };
        Avalonia.Automation.AutomationProperties.SetName(input, "Add a mask");
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(input.Text))
            {
                AddMask(input.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Back && string.IsNullOrEmpty(input.Text) && masks.Count > 0)
            {
                RemoveMask(masks[^1]);
                e.Handled = true;
            }
        };
        input.TextChanged += (_, _) =>
        {
            if (input.Text?.Contains(';', StringComparison.Ordinal) == true)
            {
                AddMask(input.Text);
            }
        };
        input.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text) && IsVisible)
            {
                AddMask(input.Text);
            }
        };
        tokens.Children.Add(input);
        var box = new Border { Child = tokens, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(5), MinHeight = 36, Cursor = new Cursor(StandardCursorType.Ibeam) };
        box[!Border.BackgroundProperty] = new DynamicResourceExtension("TextControlBackground");
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        box.PointerPressed += (_, _) => input.Focus();

        var texts = new StackPanel();
        texts.Children.AddRange([
            new TextBlock { Text = "Folders that open with this set", FontSize = 13.5 },
            new TextBlock { Text = "Path masks; * matches any text. Press Enter or ; to add one.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
        ]);
        var block = new StackPanel { Spacing = 10, Margin = new Thickness(16, 12) };
        block.Children.AddRange([texts, box]);
        return Section("Switch automatically", Card(block));
    }

    public void AddMask(string text)
    {
        if (_current is null)
        {
            return;
        }
        var masks = (_current.AutoPathMask ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var mask in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!masks.Contains(mask, StringComparer.OrdinalIgnoreCase))
            {
                masks.Add(mask);
            }
        }
        _current.AutoPathMask = masks.Count == 0 ? null : string.Join(";", masks);
        Render();
        FocusMaskInput();
    }

    public void RemoveMask(string mask)
    {
        if (_current is null)
        {
            return;
        }
        var masks = (_current.AutoPathMask ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => !string.Equals(m, mask, StringComparison.Ordinal)).ToList();
        _current.AutoPathMask = masks.Count == 0 ? null : string.Join(";", masks);
        Render();
        FocusMaskInput();
    }

    private void FocusMaskInput() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_page).OfType<TextBox>()
                .FirstOrDefault(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Add a mask")?.Focus());

    // ---------------------------------------------------------------- look

    private Control LookSection(ColumnSet set)
    {
        var icon = new ComboBox { ItemsSource = s_icons.Select(i => i.Text).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(s_icons, i => i.Key == set.Icon)), MinWidth = 160 };
        Avalonia.Automation.AutomationProperties.SetName(icon, "Icon");
        icon.SelectionChanged += (_, _) => Store(s => s.Icon = s_icons[Math.Max(0, icon.SelectedIndex)].Key);
        return Section("Look", Card(
            Row("List background", "Empty = the theme’s background.", ColorControl(set.Background, v => set.Background = v)),
            Row("Tab color", "Marks tabs that show this set.", ColorControl(set.TabColor, v => set.TabColor = v)),
            Row("Icon", "Shown in this list.", icon)));
    }

    /// <summary>A color: the swatch (opens a picker), its #RRGGBB text, and "use the default".</summary>
    private Control ColorControl(string? value, Action<string?> set)
    {
        var picker = new ColorPicker { Width = 44, Height = 32, IsAlphaEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        if (ParseColor(value) is { } color)
        {
            picker.Color = color;
        }
        Avalonia.Automation.AutomationProperties.SetName(picker, "Pick color");
        var hex = new TextBox { Text = value, PlaceholderText = "Default", Width = 110, MaxLength = 9, FontFamily = s_mono, FontSize = 12.5, MinHeight = 32, Height = 32, Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var clear = IconButton("MiX", "Use default");
        clear.IsEnabled = !string.IsNullOrEmpty(value);
        var syncing = false;
        picker.ColorChanged += (_, e) =>
        {
            if (syncing || _loading)
            {
                return;
            }
            var text = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            syncing = true;
            hex.Text = text;
            syncing = false;
            clear.IsEnabled = true;
            Store(_ => set(text));
        };
        hex.TextChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }
            var text = hex.Text?.Trim() ?? string.Empty;
            if (text.Length > 0 && text[0] != '#' && text.All(Uri.IsHexDigit))
            {
                text = "#" + text;
            }
            if (text.Length == 0)
            {
                hex.Classes.Remove("jsoninvalid");
                clear.IsEnabled = false;
                Store(_ => set(null));
            }
            else if (text.Length == 7 && ParseColor(text) is { } parsed)
            {
                hex.Classes.Remove("jsoninvalid");
                syncing = true;
                picker.Color = parsed;
                syncing = false;
                clear.IsEnabled = true;
                Store(_ => set(text.ToUpperInvariant()));
            }
            else
            {
                hex.Classes.Set("jsoninvalid", text.Length >= 7);
            }
        };
        clear.Click += (_, _) =>
        {
            set(null);
            Render();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([picker, hex, clear]);
        return row;
    }

    // ---------------------------------------------------------------- apply

    /// <summary>Writes the sets back (Apply, OK): the caller's list gets copies of them.</summary>
    public void Apply()
    {
        _sets.Clear();
        _sets.AddRange(_work.Select(s => s.Clone()));
        _saved = Snapshot();
        UpdateFoot();
        _applied?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
