using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using NextCommander.App.Services.Design;
using Avalonia.Layout;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Change attributes (laid out after the template): the item with its attributes before → after, the attributes as
/// switches (mixed when the items differ), the modification time, and for folders the scope; the footer counts the
/// changes and Apply works only when there are some.
/// </summary>
public sealed class AttributesDialog : DialogWindow
{
    private static readonly (string Text, string Description, char Letter, EntryAttributes Flag)[] s_flags =
    [
        ("Read-only", "Files can be opened but not changed", 'r', EntryAttributes.ReadOnly),
        ("Hidden", "Not shown unless hidden files are visible", 'h', EntryAttributes.Hidden),
        ("System", "Marked as used by the system", 's', EntryAttributes.System),
        ("Archive", "Changed since the last backup", 'a', EntryAttributes.Archive),
    ];

    private const string Clock = "M13.75,8 A5.75,5.75 0 1 1 2.25,8 A5.75,5.75 0 1 1 13.75,8 Z M8,5 L8,8 L10,9.5";

    private readonly List<(CheckBox Box, EntryAttributes Flag, bool? Initial, Border Key, TextBlock Changed)> _boxes = [];
    private readonly CheckBox _changeTime = new();
    private readonly TextBox _time = new() { Classes = { "nc-mono", "noloc" } };
    private readonly Button _now = Nc.Button("Now");
    private readonly Border _timeKey;
    private readonly TextBlock _timeChanged = ChangedLabel();
    private readonly CheckBox _recursive = new();
    private readonly Border _recursiveNote;
    private readonly TextBlock _error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _summary = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "noloc" } };
    private readonly StackPanel _before = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly StackPanel _after = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly Button _apply = Nc.Button("Apply", "primary", "Enter");
    private readonly string _initialTime;

    public AttributesDialog(IReadOnlyList<VfsEntry> entries)
        : base("Change attributes", 480)
    {
        Nc.Apply(this);
        var single = entries.Count == 1 ? entries[0] : null;
        var folders = entries.Count(e => e.IsDirectory);

        // the item, and its attributes before → after
        var badge = new Border { Classes = { "nc-badge-lg" }, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(single is { IsDirectory: false } ? "M6,2.5 L14.5,2.5 L19.5,7.5 L19.5,21.5 L6,21.5 Z M14.5,2.5 L14.5,7.5 L19.5,7.5" : Nc.Icons.FolderFilled),
            Width = 26, Height = 26, Stretch = Avalonia.Media.Stretch.Uniform,
        };
        if (single is { IsDirectory: false })
        {
            glyph.StrokeThickness = 1.6;
            glyph[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmFileBrush");
        }
        else
        {
            glyph[!Avalonia.Controls.Shapes.Shape.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmFolderBrush");
        }
        badge.Child = glyph;
        var name = new TextBlock
        {
            Text = single?.Name ?? Format("{0} items", entries.Count),
            FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Classes = { "noloc" },
        };
        var kind = new TextBlock
        {
            Text = single is null
                ? Format("{0} files, {1} folders", entries.Count - folders, folders)
                : single.IsDirectory ? Core.Localization.Localizer.T("Folder") : Core.Formatting.SizeFormatter.Human(single.Size),
            Classes = { "nc-meta", "nc-mono", "noloc" },
        };
        var attributes = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _before, new TextBlock { Text = "→", Classes = { "nc-hint", "noloc" }, VerticalAlignment = VerticalAlignment.Center }, _after },
        };
        Avalonia.Automation.AutomationProperties.SetName(attributes, "Attributes before and after");
        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        DockPanel.SetDock(attributes, Dock.Right);
        head.Children.AddRange([badge, attributes, new StackPanel { Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, kind } }]);

        // the attributes
        var rows = new List<Border>();
        foreach (var (text, description, letter, flag) in s_flags)
        {
            var set = entries.Count(e => e.Attributes.HasFlag(flag));
            bool? initial = set == 0 ? false : set == entries.Count ? true : null;
            var box = new CheckBox { IsThreeState = initial is null, IsChecked = initial };
            var key = Key(new TextBlock { Text = char.ToUpperInvariant(letter).ToString(), Classes = { "nc-mono", "noloc" } });
            var changed = ChangedLabel();
            _boxes.Add((box, flag, initial, key, changed));
            rows.Add(Row(key, text, description, changed, box));
            box.IsCheckedChanged += (_, _) => Update();
        }

        // the modification time
        _initialTime = (single ?? (entries.Count > 0 ? entries[0] : null))?.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _time.Text = _initialTime;
        _timeKey = Key(Nc.Icon(Clock, 14, null));
        var timeRow = Row(_timeKey, "Set modification time", Format("Currently {0}", _initialTime), _timeChanged, _changeTime);
        _now.Click += (_, _) => _time.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var timeLine = Nc.Line(Nc.Combo(_time, null), _now);
        timeLine.Margin = new Thickness(48, 2, 10, 10);
        Avalonia.Automation.AutomationProperties.SetName(_time, "New modification time");
        _changeTime.IsCheckedChanged += (_, _) => Update();
        _time.TextChanged += (_, _) => Update();

        // the scope (folders)
        var scopeKey = Key(Nc.Icon(Nc.Icons.Tree, 14, null));
        var inside = single is { IsDirectory: true }
            ? Format("Every file and folder inside {0}", single.Name)
            : Core.Localization.Localizer.T("Every file and folder inside the selected folders");
        var scopeRow = Row(scopeKey, "Also apply to everything in subfolders", inside, null, _recursive);
        _recursiveNote = Note("All their contents are changed too. On program or system folders this can stop programs from updating or starting.");
        _recursive.IsCheckedChanged += (_, _) => Update();

        _error[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcBadBrush");
        var content = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(22, 18, 22, 20),
            Children =
            {
                head,
                Section("Attributes", List([.. rows])),
                Section("Timestamp", List(timeRow, timeLine)),
            },
        };
        if (folders > 0)
        {
            content.Children.Add(Section("Scope", List(scopeRow, _recursiveNote)));
        }
        content.Children.Add(_error);

        var cancel = Nc.Button("Cancel", "", "Esc");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _apply.IsDefault = true;
        _apply.Click += (_, _) => Accept();
        _summary[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcMutedBrush");
        var footer = Nc.Footer([_summary], cancel, _apply);
        footer.Padding = new Thickness(22, 14);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([footer, content]);
        Content = root;
        Update();
    }

    public AttributeChange? Result { get; private set; }

    /// <summary>The footer's line (tests).</summary>
    public string SummaryText => _summary.Text ?? string.Empty;

    /// <summary>The switch of an attribute (tests).</summary>
    public CheckBox SwitchOf(EntryAttributes flag) => _boxes.Single(b => b.Flag == flag).Box;

    public bool CanApply => _apply.IsEnabled;

    private static TextBlock ChangedLabel()
    {
        var label = new TextBlock { Text = "Changed", FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Margin = new Thickness(8, 0) };
        label[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentBrush");
        return label;
    }

    /// <summary>The square on the left of a row: a letter or an icon, in the accent tint while on.</summary>
    private static Border Key(Control content)
    {
        var key = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Child = content, VerticalAlignment = VerticalAlignment.Center };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        SetKey(key, false);
        return key;
    }

    private static void SetKey(Border key, bool on)
    {
        key[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(on ? "NcAccentSoftBrush" : "NcSurface2Brush");
        key[!TextElement.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(on ? "NcAccentBrush" : "NcMutedBrush");
    }

    /// <summary>A row: the key, the name and its description, "Changed", the switch; a click on the row turns the switch.</summary>
    private static Border Row(Border key, string title, string? description, TextBlock? changed, CheckBox check)
    {
        check.Classes.Add("nc-switch");
        Avalonia.Automation.AutomationProperties.SetName(check, title);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { new TextBlock { Text = title } } };
        if (description is not null)
        {
            texts.Children.Add(new TextBlock { Text = description, Classes = { "nc-hint" } });
        }
        var dock = new DockPanel();
        DockPanel.SetDock(key, Dock.Left);
        DockPanel.SetDock(check, Dock.Right);
        dock.Children.AddRange([key, check]);
        if (changed is not null)
        {
            DockPanel.SetDock(changed, Dock.Right);
            dock.Children.Add(changed);
        }
        dock.Children.Add(texts);
        var row = new Border { Classes = { "nc-opt" }, Child = dock, Padding = new Thickness(10, 8) };
        row.PointerReleased += (_, e) =>
        {
            if (check.IsEnabled && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && (e.Source as Visual)?.FindAncestorOfType<CheckBox>() is null)
            {
                // the same order as the check box: off → on (→ mixed, for mixed items) → off
                check.IsChecked = check.IsChecked switch
                {
                    false => true,
                    true => check.IsThreeState ? null : false,
                    _ => false,
                };
                e.Handled = true;
            }
        };
        return row;
    }

    private static Border List(params Control[] rows)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.AddRange(rows);
        return new Border { Classes = { "nc-options" }, Child = stack };
    }

    private static StackPanel Section(string title, Control body) =>
        new() { Spacing = 8, Children = { new TextBlock { Text = title, Classes = { "nc-section" } }, body } };

    /// <summary>A warning under the scope switch.</summary>
    private static Border Note(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12.5 };
        block[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcWarnBrush");
        var note = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 8), Margin = new Thickness(6, 2, 6, 6), Child = block, IsVisible = false };
        note[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcWarnSoftBrush");
        return note;
    }

    /// <summary>The letters r h s a: on, off ('-') or mixed ('?'); a letter that changes is marked.</summary>
    private static void ShowLetters(StackPanel panel, IEnumerable<(char Letter, bool? State, bool Changed)> letters)
    {
        panel.Children.Clear();
        foreach (var (letter, state, changed) in letters)
        {
            var text = new TextBlock
            {
                Text = state switch { true => letter.ToString(), false => "-", _ => "?" },
                FontWeight = Avalonia.Media.FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Classes = { "nc-mono", "noloc" },
            };
            text[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(changed ? "NcAccentBrush" : state == true ? "NcTextBrush" : "NcFaintBrush");
            var cell = new Border { Width = 16, Height = 22, CornerRadius = new CornerRadius(4), Child = text };
            cell[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(changed ? "NcAccentSoftBrush" : "NcSurface2Brush");
            panel.Children.Add(cell);
        }
    }

    /// <summary>The keys, "Changed" labels, the letters, the time field, the note and the footer follow the switches.</summary>
    private void Update()
    {
        var changes = 0;
        foreach (var (box, _, initial, key, changed) in _boxes)
        {
            var dirty = box.IsChecked != initial;
            changed.IsVisible = dirty;
            SetKey(key, box.IsChecked == true);
            changes += dirty ? 1 : 0;
        }
        var timeOn = _changeTime.IsChecked == true;
        _time.IsEnabled = _now.IsEnabled = timeOn;
        var timeDirty = timeOn && (_time.Text ?? string.Empty).Trim() != _initialTime;
        _timeChanged.IsVisible = timeDirty;
        SetKey(_timeKey, timeOn);
        changes += timeDirty ? 1 : 0;
        _recursiveNote.IsVisible = _recursive.IsChecked == true;

        ShowLetters(_before, _boxes.Select(b => (Letter(b.Flag), b.Initial, false)));
        ShowLetters(_after, _boxes.Select(b => (Letter(b.Flag), b.Box.IsChecked, b.Box.IsChecked != b.Initial)));

        string T(string text) => Core.Localization.Localizer.T(text);
        var deep = _recursive.IsChecked == true;
        _summary.Text = changes == 0
            ? T("No changes yet")
            : Format(changes == 1
                ? deep ? "{0} change, also inside the folders" : "{0} change"
                : deep ? "{0} changes, also inside the folders" : "{0} changes", changes);
        _apply.IsEnabled = changes > 0;
    }

#pragma warning disable CA1863 // the pattern is the translation of the running language
    /// <summary>A translated pattern filled in.</summary>
    private static string Format(string pattern, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Core.Localization.Localizer.T(pattern), args);
#pragma warning restore CA1863

    private static char Letter(EntryAttributes flag) => s_flags.First(f => f.Flag == flag).Letter;

    private void Accept()
    {
        if (!_apply.IsEnabled)
        {
            return;
        }
        DateTime? time = null;
        if (_changeTime.IsChecked == true)
        {
            if (!DateTime.TryParse(_time.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                && !DateTime.TryParse(_time.Text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out t))
            {
                _error.Text = "Could not parse date (e.g. 2024-05-01 14:30:00).";
                _error.IsVisible = true;
                return;
            }
            time = t;
        }
        var set = EntryAttributes.None;
        var clear = EntryAttributes.None;
        foreach (var (box, flag, initial, _, _) in _boxes)
        {
            if (box.IsChecked == initial)
            {
                continue; // untouched
            }
            if (box.IsChecked == true)
            {
                set |= flag;
            }
            else if (box.IsChecked == false)
            {
                clear |= flag;
            }
        }
        Result = new AttributeChange(set, clear, time, _recursive.IsChecked == true);
        Close();
    }
}

/// <summary>Create checksums: algorithm, one file for all or one per file.</summary>
public sealed class ChecksumDialog : DialogWindow
{
    private readonly ComboBox _kind = new() { MinWidth = 160 };
    private readonly RadioButton _single = new() { Content = "In one file:", IsChecked = true, GroupName = "mode" };
    private readonly RadioButton _perFile = new() { Content = "Separate file for each file", GroupName = "mode" };
    private readonly TextBox _name = new();
    private string _baseName;

    public ChecksumDialog(string baseName, ChecksumKind kind)
        : base("Create checksums", 460)
    {
        _baseName = baseName;
        _kind.ItemsSource = Checksums.Available.Select(Checksums.DisplayName).ToList();
        _kind.SelectedIndex = Math.Max(0, Checksums.Available.ToList().IndexOf(kind));
        _kind.SelectionChanged += (_, _) => UpdateName();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Algorithm:", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_kind);
        Body.Children.Add(row);
        Body.Children.Add(_single);
        Body.Children.Add(_name);
        Body.Children.Add(_perFile);
        _single.IsCheckedChanged += (_, _) => _name.IsEnabled = _single.IsChecked == true;
        UpdateName();
        AddButton("Create", () =>
        {
            Result = (SelectedKind, _single.IsChecked == true ? (_name.Text ?? string.Empty).Trim() : null);
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
    }

    public (ChecksumKind Kind, string? SingleFileName)? Result { get; private set; }

    private ChecksumKind SelectedKind => Checksums.Available[Math.Max(0, _kind.SelectedIndex)];

    private void UpdateName()
    {
        var text = _name.Text;
        if (!string.IsNullOrEmpty(text) && Checksums.FromFileName(text) is not null)
        {
            _baseName = Path.GetFileNameWithoutExtension(text);
        }
        _name.Text = _baseName + Checksums.Extension(SelectedKind);
    }
}
