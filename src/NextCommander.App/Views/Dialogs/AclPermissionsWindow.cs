using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The permissions of one Windows file or folder (Files > Permissions...; the user's template of 24 November 2026): who
/// has access on the left (the rules of the item, then those inherited from the folder above, each user or group once:
/// the twin rules Windows keeps for what is inside are one), the chosen rule on the right (allow / deny, where it
/// applies, the access level, the basic rights, the advanced rights they include), the effective access of the signed-in
/// user, the owner, inheritance (turned off: the inherited rules kept as own rules or removed), "replace on everything
/// inside". Nothing changes before Apply.
/// </summary>
public sealed class AclPermissionsWindow : Window
{
    /// <summary>One rule as the window shows it: who, allow / deny, the rights, where it applies, inherited or not.</summary>
    public sealed class Rule
    {
        public int Id { get; init; }

        public string Who { get; set; } = string.Empty;

        public bool Allow { get; set; } = true;

        public int Mask { get; set; }

        public AclApplies Applies { get; set; } = AclApplies.ThisFolderSubfoldersFiles;

        public bool Inherited { get; set; }

        public Rule Copy() => (Rule)MemberwiseClone();

        public bool SameAs(Rule other) => Who == other.Who && Allow == other.Allow && Mask == other.Mask && Applies == other.Applies && Inherited == other.Inherited;
    }

    /// <summary>The basic rights (the check boxes) and what each includes.</summary>
    private static readonly (string Key, string Name, string Hint, int Mask)[] s_basic =
    [
        ("full", "Full control", "Everything, including changing permissions", AclMask.FullControl),
        ("modify", "Modify", "Read, change and delete", AclMask.Modify),
        ("rx", "Read & execute", "Open files and run programs", AclMask.ReadAndExecute),
        ("list", "List folder contents", "See what’s inside", AclMask.ReadAndExecute),
        ("read", "Read", "Open files, see attributes", AclMask.Read),
        ("write", "Write", "Create files and folders, change content", AclMask.Write),
    ];

    private static readonly (string Key, string Name)[] s_levels =
    [
        ("full", "Full control"), ("modify", "Modify"), ("rx", "Read & execute"), ("read", "Read"), ("write", "Write"),
    ];

    private static readonly (string Name, int Mask)[] s_advanced =
    [
        ("Traverse folder / run file", AclMask.Traverse),
        ("List folder / read data", AclMask.ListDirectory),
        ("Read attributes", AclMask.ReadAttributes),
        ("Read extended attributes", AclMask.ReadExtendedAttributes),
        ("Create files / write data", AclMask.CreateFiles),
        ("Create folders / append data", AclMask.CreateDirectories),
        ("Write attributes", AclMask.WriteAttributes),
        ("Write extended attributes", AclMask.WriteExtendedAttributes),
        ("Delete subfolders and files", AclMask.DeleteSubdirectoriesAndFiles),
        ("Delete", AclMask.Delete),
        ("Read permissions", AclMask.ReadPermissions),
        ("Change permissions", AclMask.ChangePermissions),
        ("Take ownership", AclMask.TakeOwnership),
    ];

    private static readonly (AclApplies Applies, string Name)[] s_applies =
    [
        (AclApplies.ThisFolderSubfoldersFiles, "This folder, subfolders and files"),
        (AclApplies.ThisFolderOnly, "This folder only"),
        (AclApplies.ThisFolderAndSubfolders, "This folder and subfolders"),
        (AclApplies.ThisFolderAndFiles, "This folder and files"),
        (AclApplies.SubfoldersAndFilesOnly, "Subfolders and files only"),
        (AclApplies.SubfoldersOnly, "Subfolders only"),
        (AclApplies.FilesOnly, "Files only"),
    ];

    private readonly VfsEntry _entry;
    private readonly string _parent;
    private readonly List<Rule> _original;
    private readonly bool _originalInherit;
    private readonly string? _originalOwner;
    private List<Rule> _rules;
    private bool _inherit;
    private string? _owner;
    private int _selected;
    private string? _picker; // the text of the "Add" picker (null: closed)
    private string? _ownerEdit; // the text of the owner picker (null: closed)
    private string? _pickerError;
    private readonly Border _head = new();
    private readonly DockPanel _left = new();
    private readonly ContentControl _right = new();
    private readonly Border _footer = new();
    private readonly Grid _overlay = new() { IsVisible = false };
    private readonly CheckBox _recurse = new() { Content = "Also replace on all subfolders and files", FontSize = 12.5 };

    public AclPermissionsWindow(VfsEntry entry, FilePermissions current)
    {
        _entry = entry;
        _parent = entry.Path.Parent?.ToDisplayString() ?? string.Empty;
        _original = Merge(current.Acl);
        _rules = [.. _original.Select(r => r.Copy())];
        _originalInherit = _inherit = !current.InheritanceProtected;
        _originalOwner = _owner = current.Owner;
        _selected = _rules.Count > 0 ? _rules[0].Id : 0;

        Title = "Permissions";
        Width = 1000;
        Height = 700;
        MinWidth = 820;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");

        var leftBox = new Border { Child = _left, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        leftBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*") };
        Grid.SetColumn(_right, 1);
        main.Children.AddRange([leftBox, _right]);
        var mainBox = Lined(main, top: true);
        _footer.BorderThickness = new Thickness(0, 1, 0, 0);
        _footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        _footer[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        var page = new DockPanel();
        DockPanel.SetDock(_head, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        page.Children.AddRange([_head, _footer, mainBox]);
        Content = new Panel { Children = { page, _overlay } };
        _recurse.IsVisible = entry.IsDirectory;
        _recurse.IsCheckedChanged += (_, _) => RenderFooter();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Render();
    }

    /// <summary>The change chosen (null: closed without applying).</summary>
    public PermissionChange? Result { get; private set; }

    /// <summary>"Open" of an inherited rule: the folder above's permissions are wanted (the window closes).</summary>
    public bool ParentRequested { get; private set; }

    /// <summary>The rules shown, the item's own first (tests).</summary>
    public IReadOnlyList<Rule> Rules => [.. _rules.Where(r => !r.Inherited), .. _rules.Where(r => r.Inherited)];

    /// <summary>The rule chosen (tests).</summary>
    public Rule? Chosen => _rules.FirstOrDefault(r => r.Id == _selected);

    /// <summary>
    /// The rules as Windows lists them, each user or group once per allow / deny and inherited or not: the twin rule Windows
    /// keeps for what is inside (often with "generic" rights) joins the item's own, and where they apply is what both reach.
    /// </summary>
    public static List<Rule> Merge(IReadOnlyList<AclEntry> acl)
    {
        var rules = new List<Rule>();
        var reach = new Dictionary<int, (bool This, bool Sub, bool Files)>();
        foreach (var entry in acl)
        {
            var (self, sub, files) = AclMask.Reach(entry.EffectiveApplies);
            var mask = entry.EffectiveMask & ~AclMask.Synchronize;
            var twin = rules.FirstOrDefault(r => string.Equals(r.Who, entry.Identity, StringComparison.OrdinalIgnoreCase) && r.Allow == entry.Allow && r.Inherited == entry.Inherited);
            if (twin is null)
            {
                twin = new Rule { Id = rules.Count + 1, Who = entry.Identity, Allow = entry.Allow, Inherited = entry.Inherited, Mask = mask };
                rules.Add(twin);
                reach[twin.Id] = (self, sub, files);
                continue;
            }
            var (a, b, c) = reach[twin.Id];
            reach[twin.Id] = (a || self, b || sub, c || files);
            // the rights the item itself gets win; an inherit-only twin with wider rights widens them
            twin.Mask |= mask;
        }
        foreach (var rule in rules)
        {
            var (self, sub, files) = reach[rule.Id];
            rule.Applies = AclMask.AppliesOf(self, sub, files);
        }
        return rules;
    }

    // ---------------------------------------------------------------- what changed

    private List<(string Kind, Rule? Rule)> Changes()
    {
        var changes = new List<(string, Rule?)>();
        foreach (var rule in _rules)
        {
            var before = _original.FirstOrDefault(o => o.Id == rule.Id);
            if (before is null)
            {
                changes.Add(("add", rule));
            }
            else if (!before.SameAs(rule))
            {
                changes.Add(("chg", rule));
            }
        }
        changes.AddRange(_original.Where(o => _rules.All(r => r.Id != o.Id)).Select(o => ("del", (Rule?)o)));
        if (_inherit != _originalInherit)
        {
            changes.Add(("inh", null));
        }
        if (!string.Equals(_owner, _originalOwner, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(("own", null));
        }
        return changes;
    }

    private bool NeedsAdmin(List<(string Kind, Rule? Rule)> changes) => changes.Any(c => c.Kind is "own");

    /// <summary>The level a set of rights has (Full control ... Write; Special: another mix; No access).</summary>
    private static string LevelOf(int mask)
    {
        var keys = KeysOf(mask);
        foreach (var (key, name) in s_levels)
        {
            if (keys.SetEquals(Implied(key)))
            {
                return name;
            }
        }
        return mask == 0 ? "No access" : "Special";
    }

    /// <summary>A basic right with those it includes (Full control: all of them).</summary>
    private static HashSet<string> Implied(string key) => key switch
    {
        "full" => ["full", "modify", "rx", "list", "read", "write"],
        "modify" => ["modify", "rx", "list", "read", "write"],
        "rx" => ["rx", "list", "read"],
        _ => [key],
    };

    private static HashSet<string> KeysOf(int mask) =>
        [.. s_basic.Where(b => AclMask.Has(mask, b.Mask)).Select(b => b.Key)];

    private static int MaskOf(IEnumerable<string> keys) => keys.Aggregate(0, (m, k) => m | s_basic.First(b => b.Key == k).Mask);

    // ---------------------------------------------------------------- drawing

    private void Render()
    {
        RenderHead();
        RenderLeft();
        RenderRight();
        RenderFooter();
    }

    private void RenderHead()
    {
        var icon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xE8, 0xA3, 0x3D)) };
        icon.Child = Glyph(_entry.IsDirectory ? "MiFolderOpen" : "MiDoc", 24, Color.Parse("#d38b1c"));
        var names = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = _entry.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
                Mono(_entry.Path.ToDisplayString(), 12.5),
            },
        };
        var ownerBox = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 6, 6), MinWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        ownerBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        ownerBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwCardBrush");
        if (_ownerEdit is not null)
        {
            var box = PickerBox(_ownerEdit, "User or group", text =>
            {
                _ownerEdit = text;
                RenderHead();
            }, chosen =>
            {
                _owner = chosen;
                _ownerEdit = null;
                Render();
            }, () =>
            {
                _ownerEdit = null;
                RenderHead();
            }, "owner");
            var cancel = SmallButton(null, "Cancel", () =>
            {
                _ownerEdit = null;
                RenderHead();
            });
            var row = new DockPanel();
            DockPanel.SetDock(cancel, Dock.Right);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            row.Children.AddRange([cancel, new StackPanel { Children = { Faint("New owner", 11.5), box } }]);
            ownerBox.Child = row;
        }
        else
        {
            var kind = _owner is null ? AccountKind.User : WindowsAccounts.KindOf(_owner);
            var change = SmallButton("MiLock", "Change", () =>
            {
                _ownerEdit = string.Empty;
                RenderHead();
            });
            change.Tag = "owner";
            ToolTip.SetTip(change, "Needs administrator rights");
            var label = Faint(string.Equals(_owner, _originalOwner, StringComparison.OrdinalIgnoreCase) ? Localizer.T("Owner") : Localizer.T("Owner") + " · " + Localizer.T("changed"), 11.5);
            var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = ShortName(_owner ?? "?"), FontWeight = FontWeight.Medium, Classes = { "noloc" } }, Mono(_owner ?? string.Empty, 11.5) } };
            var row = new DockPanel();
            var avatar = Avatar(kind, 30);
            avatar.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(avatar, Dock.Left);
            DockPanel.SetDock(change, Dock.Right);
            row.Children.AddRange([avatar, change, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, value } }]);
            ownerBox.Child = row;
        }
        var head = new DockPanel { Margin = new Thickness(20, 14, 20, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(ownerBox, Dock.Right);
        head.Children.AddRange([icon, ownerBox, names]);
        _head.Child = head;
    }

    private void RenderLeft()
    {
        _left.Children.Clear();
        var add = SmallButton("MiPlus", "Add", () =>
        {
            _picker = _picker is null ? string.Empty : null;
            _pickerError = null;
            RenderLeft();
        });
        add.Tag = "add";
        var title = new DockPanel { Margin = new Thickness(16, 12, 12, 6) };
        DockPanel.SetDock(add, Dock.Right);
        var heading = new TextBlock { Text = "Who has access", FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        heading[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        title.Children.AddRange([add, heading]);
        DockPanel.SetDock(title, Dock.Top);
        _left.Children.Add(title);
        if (_picker is not null)
        {
            var picker = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
            picker.Children.Add(PickerBox(_picker, "Type a user or group name", text =>
            {
                _picker = text;
                _pickerError = null;
                RenderLeft();
            }, AddRule, () =>
            {
                _picker = null;
                RenderLeft();
            }, "add"));
            if (_pickerError is { } error)
            {
                var note = Faint(error, 11.5);
                note.TextWrapping = TextWrapping.Wrap;
                note[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcBadBrush");
                picker.Children.Add(note);
            }
            DockPanel.SetDock(picker, Dock.Top);
            _left.Children.Add(picker);
        }

        // inheritance at the bottom
        var toggle = new ToggleSwitch { IsChecked = _inherit, OnContent = string.Empty, OffContent = string.Empty, VerticalAlignment = VerticalAlignment.Top, Tag = "inherit", MinWidth = 0 };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == _inherit)
            {
                return;
            }
            if (_inherit)
            {
                StopInheriting();
            }
            else
            {
                _inherit = true;
                _rules.AddRange(_original.Where(o => o.Inherited && _rules.All(r => r.Id != o.Id)).Select(o => o.Copy()));
                Render();
            }
        };
        var inheritText = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Localizer.T($"Inherit from {_parent}"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } },
                Faint(_inherit ? "Rules set on the parent folder also apply here." : "Only the rules above apply.", 11.5),
            },
        };
        var inheritRow = new DockPanel { Margin = new Thickness(14, 10) };
        DockPanel.SetDock(toggle, Dock.Left);
        inheritRow.Children.AddRange([toggle, inheritText]);
        var inheritBox = Lined(inheritRow, top: true);
        inheritBox.IsVisible = _parent.Length > 0;
        DockPanel.SetDock(inheritBox, Dock.Bottom);
        _left.Children.Add(inheritBox);

        var list = new StackPanel { Margin = new Thickness(6, 0, 6, 8) };
        var changed = Changes().Where(c => c.Rule is not null).Select(c => c.Rule!.Id).ToHashSet();
        var own = _rules.Where(r => !r.Inherited).ToList();
        var inherited = _rules.Where(r => r.Inherited).ToList();
        if (own.Count > 0)
        {
            list.Children.Add(GroupTitle(Localizer.T(_entry.IsDirectory ? "Set on this folder" : "Set on this file"), null));
            list.Children.AddRange(own.Select(r => RuleRow(r, changed.Contains(r.Id))));
        }
        if (inherited.Count > 0)
        {
            list.Children.Add(GroupTitle(Localizer.T($"Inherited from {_parent}"), "MiLock"));
            list.Children.AddRange(inherited.Select(r => RuleRow(r, changed.Contains(r.Id))));
        }
        if (_rules.Count == 0)
        {
            var none = Faint("Nobody has access — not even you or Windows.", 13);
            none.TextWrapping = TextWrapping.Wrap;
            none.Margin = new Thickness(14, 20);
            none[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcBadBrush");
            list.Children.Add(none);
        }
        _left.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    private static Control GroupTitle(string text, string? glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 10, 10, 4) };
        if (glyph is not null)
        {
            var icon = DialogLook.Glyph(glyph, 11);
            icon.Opacity = 0.6;
            row.Children.Add(icon);
        }
        var title = Faint(text, 11.5);
        title.Classes.Add("noloc");
        row.Children.Add(title);
        return row;
    }

    private Control RuleRow(Rule rule, bool changed)
    {
        var selected = rule.Id == _selected;
        var avatar = Avatar(WindowsAccounts.KindOf(rule.Who), 30);
        avatar.Margin = new Thickness(0, 0, 10, 0);
        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = ShortName(rule.Who), FontSize = 13.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } }, Mono(rule.Who, 11.5) },
        };
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (_original.All(o => o.Id != rule.Id))
        {
            pills.Children.Add(Pill(Localizer.T("New"), "new"));
        }
        pills.Children.Add(Pill((rule.Allow ? string.Empty : Localizer.T("Deny") + " · ") + Localizer.T(LevelOf(rule.Mask)), rule.Allow ? "level" : "deny"));
        var row = new DockPanel { Margin = new Thickness(10, 8) };
        DockPanel.SetDock(avatar, Dock.Left);
        DockPanel.SetDock(pills, Dock.Right);
        row.Children.AddRange([avatar, pills, texts]);
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12), HorizontalAlignment = HorizontalAlignment.Left, IsVisible = selected };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 8, 0, 0), IsVisible = changed };
        dot[!Border.BackgroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        ToolTip.SetTip(dot, "Changed");
        var box = new Border { CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = new Panel { Children = { row, bar, dot } }, Tag = rule.Id };
        if (selected)
        {
            box[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwAccentSoftBrush");
        }
        box.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
            {
                Select(rule.Id);
            }
        };
        return box;
    }

    private void RenderRight()
    {
        if (Chosen is not { } rule)
        {
            var empty = Faint("Select someone on the left.", 13);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            _right.Content = empty;
            return;
        }
        var locked = rule.Inherited;
        var body = new StackPanel { Margin = new Thickness(22, 16) };

        // who
        var avatar = Avatar(WindowsAccounts.KindOf(rule.Who), 40);
        avatar.Margin = new Thickness(0, 0, 12, 0);
        var description = WindowsAccounts.DescriptionOf(rule.Who);
        var who = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = ShortName(rule.Who), FontSize = 16, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
                Mono(rule.Who + (description.Length > 0 ? " · " + description : string.Empty), 12),
            },
        };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(avatar, Dock.Left);
        if (!locked)
        {
            var remove = SmallButton("MiTrash", "Remove", RemoveRule);
            remove.Tag = "remove";
            remove[!ForegroundProperty] = new DynamicResourceExtension("NcBadBrush");
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
        }
        head.Children.AddRange([avatar, who]);
        body.Children.Add(head);

        if (locked)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            var open = SmallButton("MiFolderOpen", Localizer.T($"Open {_parent}"), () =>
            {
                ParentRequested = true;
                Close();
            });
            open.Tag = "openparent";
            var stop = SmallButton(null, "Stop inheriting…", StopInheriting);
            stop.Tag = "stopinherit";
            actions.Children.AddRange([open, stop]);
            body.Children.Add(Banner(false, "MiLock", Localizer.T($"Inherited from {_parent}"), "To change this rule, edit the parent folder, or stop inheriting here and edit a copy.", actions));
        }

        // allow / deny and where it applies
        var kind = Segmented([("allow", "Allow"), ("deny", "Deny")], rule.Allow ? "allow" : "deny", locked, key =>
        {
            rule.Allow = key == "allow";
            Render();
        }, deny: !rule.Allow);
        var applies = new ComboBox { IsEnabled = !locked && _entry.IsDirectory, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "applies" };
        foreach (var (each, name) in s_applies)
        {
            applies.Items.Add(new ComboBoxItem { Content = name, Tag = each });
        }
        applies.SelectedIndex = Array.FindIndex(s_applies, a => a.Applies == rule.Applies);
        applies.SelectionChanged += (_, _) =>
        {
            if (applies.SelectedItem is ComboBoxItem { Tag: AclApplies chosen } && chosen != rule.Applies)
            {
                rule.Applies = chosen;
                Render();
            }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,12,*"), Margin = new Thickness(0, 0, 0, 14) };
        var ruleField = Field("Rule", kind);
        var appliesField = Field("Applies to", applies);
        Grid.SetColumn(appliesField, 2);
        row.Children.AddRange([ruleField, appliesField]);
        body.Children.Add(row);
        if (!rule.Allow)
        {
            body.Children.Add(Banner(true, "MiInfo", null, "Deny wins over Allow — even for members of other groups that are allowed.", null));
        }

        // the access level and the basic rights
        var level = LevelOf(rule.Mask);
        var levels = s_levels.Select(l => (l.Key, l.Name)).ToList();
        var current = s_levels.FirstOrDefault(l => l.Name == level).Key;
        if (current is null)
        {
            levels.Add(("custom", level));
        }
        body.Children.Add(Field("Access level", Segmented(levels, current ?? "custom", locked, key =>
        {
            if (key != "custom")
            {
                rule.Mask = MaskOf(Implied(key));
                Render();
            }
        }), 10));
        var matrix = new StackPanel();
        var headRow = new DockPanel { Margin = new Thickness(12, 5) };
        var allowHead = Faint(rule.Allow ? "Allow" : "Deny", 11.5);
        allowHead.Width = 64;
        allowHead.TextAlignment = TextAlignment.Center;
        DockPanel.SetDock(allowHead, Dock.Right);
        headRow.Children.AddRange([allowHead, Faint("Permission", 11.5)]);
        var headBox = new Border { Child = headRow };
        headBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwHoverBrush");
        matrix.Children.Add(headBox);
        var keys = KeysOf(rule.Mask);
        foreach (var (key, name, hint, _) in s_basic)
        {
            var check = new CheckBox { IsChecked = keys.Contains(key), IsEnabled = !locked, HorizontalAlignment = HorizontalAlignment.Center, Tag = key, MinWidth = 0 };
            Avalonia.Automation.AutomationProperties.SetName(check, name);
            var k = key;
            check.IsCheckedChanged += (_, _) => SetRight(k, check.IsChecked == true);
            var cell = new Border { Width = 64, Child = check };
            var line = new DockPanel { Margin = new Thickness(12, 7) };
            DockPanel.SetDock(cell, Dock.Right);
            line.Children.AddRange([cell, new StackPanel { Children = { new TextBlock { Text = name, FontSize = 13 }, Faint(hint, 11.5) } }]);
            matrix.Children.Add(Lined(line, top: true));
        }
        var matrixBox = new Border { Child = matrix, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        matrixBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwCardBrush");
        matrixBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        body.Children.Add(matrixBox);

        // the advanced rights they include
        var advanced = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < s_advanced.Length; i++)
        {
            var (name, mask) = s_advanced[i];
            var on = AclMask.Has(rule.Mask, mask);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2), Opacity = on ? 1 : 0.5 };
            var mark = DialogLook.Glyph(on ? "MiCheck" : "MiX", 12);
            item.Children.AddRange([mark, new TextBlock { Text = name, FontSize = 12.5 }]);
            if (on)
            {
                ((TextBlock)item.Children[1])[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcOkBrush");
            }
            if (advanced.RowDefinitions.Count <= i / 2)
            {
                advanced.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(item, i / 2);
            Grid.SetColumn(item, i % 2);
            advanced.Children.Add(item);
        }
        body.Children.Add(new Expander { Header = "Advanced rights included", Content = advanced, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch });

        body.Children.Add(EffectiveAccess());
        _right.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>What the signed-in user gets from the rules shown (allowed minus denied; the Administrators' rules only with administrator rights).</summary>
    private Control EffectiveAccess()
    {
        var groups = WindowsAccounts.CurrentGroups();
        var admins = WindowsAccounts.AdministratorsName;
        var elevated = WindowsAccounts.IsElevated;
        var allowed = 0;
        var denied = 0;
        var via = new List<string>();
        foreach (var rule in _rules)
        {
            if (!groups.Contains(rule.Who) || (!elevated && string.Equals(rule.Who, admins, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!AclMask.Reach(rule.Applies).This && _entry.IsDirectory)
            {
                continue; // for what is inside only
            }
            if (rule.Allow)
            {
                allowed |= rule.Mask;
                via.Add(ShortName(rule.Who));
            }
            else
            {
                denied |= rule.Mask;
            }
        }
        var effective = allowed & ~denied;
        var level = Localizer.T(LevelOf(effective));
        var result = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
        result.Inlines = [new Avalonia.Controls.Documents.Run(level) { FontWeight = FontWeight.SemiBold }];
        if (via.Count > 0)
        {
            result.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + Localizer.T($"via {string.Join(", ", via.Distinct())}")));
        }
        var notes = new List<string>();
        if (!elevated && admins is not null && groups.Contains(admins) && _rules.Any(r => string.Equals(r.Who, admins, StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add(Localizer.T("Gets the Administrators' rights only when running as administrator."));
        }
        if (denied != 0)
        {
            notes.Add(Localizer.T("Some rights are denied."));
        }
        var title = Faint(Localizer.T($"Effective access for {ShortName(WindowsAccounts.CurrentUser)} (you)"), 12.5);
        title.Classes.Add("noloc");
        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 16, 0, 0),
            Tag = "effective",
            Child = new StackPanel { Spacing = 4, Children = { title, result } },
        };
        if (notes.Count > 0)
        {
            var note = Faint(string.Join(" ", notes), 12);
            note.TextWrapping = TextWrapping.Wrap;
            note.Classes.Add("noloc");
            ((StackPanel)box.Child).Children.Add(note);
        }
        box[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwCardBrush");
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return box;
    }

    /// <summary>The effective access line (tests).</summary>
    public string EffectiveText => this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "effective"))?.Child is StackPanel panel
        && panel.Children.OfType<TextBlock>().ElementAtOrDefault(1) is { } line ? string.Concat(line.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []) : string.Empty;

    private void RenderFooter()
    {
        var changes = Changes();
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (changes.Count > 0)
        {
            summary.Children.Add(DialogLook.Glyph("MiInfo", 14));
            summary.Children.Add(Faint(Localizer.T(changes.Count == 1 ? "1 unsaved change" : $"{changes.Count} unsaved changes")
                + (NeedsAdmin(changes) ? " · " + Localizer.T("needs administrator rights") : string.Empty), 12.5));
        }
        else
        {
            summary.Children.Add(Faint("No changes", 12.5));
        }
        var row = new DockPanel { Margin = new Thickness(20, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _recurse.VerticalAlignment = VerticalAlignment.Center;
        _recurse.Margin = new Thickness(0, 0, 8, 0);
        if (_recurse.Parent is Panel old)
        {
            old.Children.Remove(_recurse);
        }
        var apply = new Button { Classes = { "dialog" }, IsDefault = true, IsEnabled = changes.Count > 0 || _recurse.IsChecked == true, Tag = "apply", MinWidth = 96 };
        apply.Content = NeedsAdmin(changes)
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { DialogLook.Glyph("MiLock", 13), new TextBlock { Text = "Apply" } } }
            : "Apply";
        apply.Click += (_, _) => Accept();
        var close = new Button { Content = changes.Count > 0 ? "Cancel" : "Close", Classes = { "dialog" }, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.AddRange([_recurse, apply, close]);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { summary } };
        if (changes.Count > 0)
        {
            var reset = SmallButton("MiHistory", "Reset", Reset);
            reset.Tag = "reset";
            reset.Background = Brushes.Transparent;
            left.Children.Add(reset);
        }
        row.Children.Add(left);
        _footer.Child = row;
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Chooses a rule (the list, ↑ / ↓).</summary>
    public void Select(int id)
    {
        _selected = id;
        _picker = null;
        Render();
    }

    /// <summary>Turns a basic right on (with those it includes) or off (with those that include it).</summary>
    public void SetRight(string key, bool on)
    {
        if (Chosen is not { Inherited: false } rule)
        {
            return;
        }
        var keys = KeysOf(rule.Mask);
        if (on)
        {
            keys.UnionWith(Implied(key));
        }
        else
        {
            // off: and every right that includes it (Read off: Read & execute, List folder contents, Modify, Full control)
            var removed = s_basic.First(b => b.Key == key).Mask;
            keys.RemoveWhere(k => AclMask.Has(s_basic.First(b => b.Key == k).Mask, removed));
        }
        var advancedOnly = rule.Mask & ~MaskOf(KeysOf(rule.Mask)) & ~AclMask.Synchronize; // rights beyond the basic ones stay
        rule.Mask = MaskOf(keys) | (on ? advancedOnly : 0);
        Render();
    }

    /// <summary>Adds a rule for a user or group (Read &amp; execute, everything inside too); an unknown name is said.</summary>
    public void AddRule(string who)
    {
        var name = who.Trim();
        if (name.Length == 0)
        {
            return;
        }
        if (OperatingSystem.IsWindows() && !WindowsAccounts.Exists(name))
        {
            _pickerError = Localizer.T($"No user or group named “{name}” on this computer or domain.");
            RenderLeft();
            return;
        }
        var id = _rules.Concat(_original).Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;
        _rules.Insert(0, new Rule { Id = id, Who = name, Allow = true, Mask = AclMask.ReadAndExecute, Applies = _entry.IsDirectory ? AclApplies.ThisFolderSubfoldersFiles : AclApplies.ThisFolderOnly });
        _selected = id;
        _picker = null;
        _pickerError = null;
        Render();
    }

    /// <summary>Removes the chosen rule (an own one).</summary>
    public void RemoveRule()
    {
        if (Chosen is not { Inherited: false } rule)
        {
            return;
        }
        var list = Rules.ToList();
        var at = list.IndexOf(rule);
        _rules.Remove(rule);
        var rest = Rules;
        _selected = rest.Count > 0 ? rest[Math.Min(at, rest.Count - 1)].Id : 0;
        Render();
    }

    /// <summary>Asks what happens to the inherited rules when inheritance is turned off: kept as own rules, or removed.</summary>
    public void StopInheriting()
    {
        var inherited = _rules.Where(r => r.Inherited).ToList();
        var copy = Choice("MiCheck", "Keep them as rules of this item", "Recommended", "Nobody loses access. You can then edit or remove each one.", false, () => StopInheriting(keep: true));
        copy.Tag = "keep";
        var names = string.Join(", ", inherited.Select(r => ShortName(r.Who)).Distinct());
        var drop = Choice("MiTrash", "Remove them", null,
            Localizer.T($"{names} will lose access") + (_rules.Any(r => !r.Inherited) ? "." : " — " + Localizer.T("no one will have access") + "."), true, () => StopInheriting(keep: false));
        drop.Tag = "drop";
        var cancel = new Button { Content = "Cancel", Classes = { "dialog" } };
        cancel.Click += (_, _) => CloseOverlay();
        ShowOverlay(Localizer.T($"Stop inheriting from {_parent}?"), Localizer.T($"What should happen to the {inherited.Count} inherited rules?"), [copy, drop], cancel);
    }

    /// <summary>Turns inheritance off: the inherited rules become own rules, or go.</summary>
    public void StopInheriting(bool keep)
    {
        _inherit = false;
        if (keep)
        {
            foreach (var rule in _rules.Where(r => r.Inherited))
            {
                rule.Inherited = false;
            }
        }
        else
        {
            _rules.RemoveAll(r => r.Inherited);
        }
        if (Chosen is null)
        {
            _selected = Rules.Count > 0 ? Rules[0].Id : 0;
        }
        CloseOverlay();
        Render();
    }

    private void Reset()
    {
        _rules = [.. _original.Select(r => r.Copy())];
        _inherit = _originalInherit;
        _owner = _originalOwner;
        _selected = _rules.Count > 0 ? _rules[0].Id : 0;
        Render();
    }

    /// <summary>The change to make: the item's own rules as shown, inheritance, the owner, "replace on everything inside".</summary>
    public void Accept()
    {
        var changes = Changes();
        var rulesChanged = changes.Any(c => c.Kind is "add" or "chg" or "del") || _inherit != _originalInherit;
        var change = new PermissionChange
        {
            ReplaceRules = rulesChanged
                ? [.. _rules.Where(r => !r.Inherited).Select(r => new AclEntry(r.Who, r.Allow, AclMask.NameOf(r.Mask)) { Mask = r.Mask, Applies = r.Applies })]
                : null,
            Inherit = _inherit != _originalInherit ? _inherit : null,
            Owner = !string.Equals(_owner, _originalOwner, StringComparison.OrdinalIgnoreCase) ? _owner : null,
            ReplaceChildren = _entry.IsDirectory && _recurse.IsChecked == true,
        };
        if (change.IsEmpty)
        {
            return;
        }
        Result = change;
        Close();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_overlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                CloseOverlay();
                e.Handled = true;
            }
            return;
        }
        if (e.Source is TextBox)
        {
            return; // the pickers take their keys
        }
        var list = Rules;
        var at = list.ToList().FindIndex(r => r.Id == _selected);
        switch (e.Key)
        {
            case Key.Down or Key.Up when list.Count > 0 && e.Source is not ComboBox:
                Select(list[Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, list.Count - 1)].Id);
                e.Handled = true;
                break;
            case Key.Delete:
                RemoveRule();
                e.Handled = true;
                break;
            case Key.Insert:
                _picker = string.Empty;
                RenderLeft();
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Accept();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- the user / group picker

    /// <summary>A text box with the users and groups that match below it; Enter or a click takes one (or what is typed).</summary>
    private Control PickerBox(string text, string placeholder, Action<string> typed, Action<string> chosen, Action cancel, string tag)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder, Height = 30, Tag = "picker-" + tag, Classes = { "noloc" } };
        var taken = tag == "add" ? _rules.Where(r => !r.Inherited).Select(r => r.Who).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var matches = WindowsAccounts.Suggestions().Concat(_rules.Select(r => r.Who)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !taken.Contains(n) && (text.Length == 0 || n.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            .Take(7).ToList();
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var highlighted = 0;
        var moved = false;
        void Mark()
        {
            for (var i = 0; i < list.Children.Count; i++)
            {
                if (list.Children[i] is Border option)
                {
                    if (i == highlighted)
                    {
                        option[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwAccentSoftBrush");
                    }
                    else
                    {
                        option.Background = Brushes.Transparent;
                    }
                }
            }
        }
        foreach (var name in matches)
        {
            var avatar = Avatar(WindowsAccounts.KindOf(name), 24);
            avatar.Margin = new Thickness(0, 0, 8, 0);
            var line = new DockPanel { Margin = new Thickness(8, 5) };
            var id = Mono(name, 11.5);
            DockPanel.SetDock(avatar, Dock.Left);
            DockPanel.SetDock(id, Dock.Right);
            line.Children.AddRange([avatar, id, new TextBlock { Text = ShortName(name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
            var option = new Border { Child = line, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Tag = name };
            option.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                chosen(name);
            };
            list.Children.Add(option);
        }
        Mark();
        box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when matches.Count > 0:
                    highlighted = (highlighted + 1) % matches.Count;
                    moved = true;
                    Mark();
                    e.Handled = true;
                    break;
                case Key.Up when matches.Count > 0:
                    highlighted = (highlighted - 1 + matches.Count) % matches.Count;
                    moved = true;
                    Mark();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    e.Handled = true;
                    // the one chosen with the arrows; else the name typed when it is listed (or the only match), else what is typed
                    var typedName = (box.Text ?? string.Empty).Trim();
                    chosen(moved && matches.Count > 0 ? matches[highlighted]
                        : typedName.Length == 0 && matches.Count > 0 ? matches[0]
                        : matches.FirstOrDefault(m => string.Equals(m, typedName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ShortName(m), typedName, StringComparison.OrdinalIgnoreCase))
                          ?? (matches.Count == 1 ? matches[0] : typedName));
                    break;
                case Key.Escape:
                    e.Handled = true;
                    cancel();
                    break;
            }
        };
        box.TextChanged += (_, _) =>
        {
            if (box.Text != text)
            {
                typed(box.Text ?? string.Empty);
            }
        };
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(box) is not null)
            {
                box.Focus();
                box.CaretIndex = box.Text?.Length ?? 0;
            }
        });
        var suggestions = new Border { Child = list, IsVisible = matches.Count > 0, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4) };
        suggestions[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");
        suggestions[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrongStrokeBrush");
        return new StackPanel { Children = { box, suggestions } };
    }

    // ---------------------------------------------------------------- the small dialog over the window

    private void ShowOverlay(string title, string text, IReadOnlyList<Control> choices, Button cancel)
    {
        var content = new StackPanel { Margin = new Thickness(22, 20), Spacing = 8 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        var line = Faint(text, 13);
        line.TextWrapping = TextWrapping.Wrap;
        line.Classes.Add("noloc");
        content.Children.Add(line);
        content.Children.AddRange(choices);
        var foot = Lined(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 12), Children = { cancel } }, top: true);
        foot[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        var page = new DockPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        page.Children.AddRange([foot, content]);
        var dialog = new Border { Width = 480, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), ClipToBounds = true, Child = page };
        dialog[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");
        dialog[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrongStrokeBrush");
        dialog[!Border.BoxShadowProperty] = new DynamicResourceExtension("NcMwPopupShadow");
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)) });
        _overlay.Children.Add(dialog);
        _overlay.IsVisible = true;
    }

    private void CloseOverlay()
    {
        _overlay.IsVisible = false;
        _overlay.Children.Clear();
        Render(); // the inherit switch back to how it is
    }

    /// <summary>Takes a choice of the dialog over the window (tests: "keep", "drop").</summary>
    public void ChooseInOverlay(string tag) =>
        _overlay.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Tag, tag))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button Choice(string glyph, string title, string? recommended, string text, bool bad, Action click)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold };
        if (bad)
        {
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcBadBrush");
        }
        heading.Children.Add(name);
        if (recommended is not null)
        {
            var tag = new TextBlock { Text = recommended, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            tag[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
            heading.Children.Add(tag);
        }
        var detail = Faint(text, 12.5);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.Classes.Add("noloc");
        var icon = DialogLook.Glyph(glyph, 16);
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.AddRange([icon, new StackPanel { Spacing = 2, Children = { heading, detail } }]);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), CornerRadius = new CornerRadius(8) };
        button[!BackgroundProperty] = new DynamicResourceExtension("NcMwCardBrush");
        button.Click += (_, _) => click();
        return button;
    }

    // ---------------------------------------------------------------- small parts

    /// <summary>"BUILTIN\Users" → "Users".</summary>
    private static string ShortName(string who) => who.Contains('\\', StringComparison.Ordinal) ? who[(who.LastIndexOf('\\') + 1)..] : who;

    private static Control Avatar(AccountKind kind, double size)
    {
        var avatar = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(kind == AccountKind.System ? 8 : size / 2) };
        avatar[!Border.BackgroundProperty] = new DynamicResourceExtension(kind == AccountKind.User ? "NcAccentSoftBrush" : "NcMwHoverBrush");
        var glyph = DialogLook.Glyph(kind switch { AccountKind.User => "MiStar", AccountKind.System => "MiChip", _ => "MiTabs" }, size * 0.5, accent: kind == AccountKind.User);
        avatar.Child = glyph;
        return avatar;
    }

    private static Control Glyph(string key, double size, Color color)
    {
        var icon = new PathIcon { Width = size, Height = size, Foreground = new SolidColorBrush(color) };
        if (Application.Current?.TryFindResource(key, out var data) == true && data is Geometry geometry)
        {
            icon.Data = geometry;
        }
        return icon;
    }

    private static Border Pill(string text, string kind)
    {
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), BorderThickness = new Thickness(kind == "level" ? 1 : 0) };
        var label = new TextBlock { Text = text, FontSize = 11.5, Classes = { "noloc" } };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(kind switch { "deny" => "NcBadBrush", "new" => "NcAccentBrush", _ => "NcMutedBrush" });
        pill.Child = label;
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension(kind switch { "deny" => "NcBadSoftBrush", "new" => "NcAccentSoftBrush", _ => "NcMwHoverBrush" });
        pill[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return pill;
    }

    private static Control Banner(bool warn, string glyph, string? title, string text, Control? actions)
    {
        var icon = DialogLook.Glyph(glyph, 16);
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var texts = new StackPanel();
        if (title is not null)
        {
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        }
        texts.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (actions is not null)
        {
            texts.Children.Add(actions);
        }
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.AddRange([icon, texts]);
        var banner = new Border { Child = row, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(warn ? 0 : 1) };
        banner[!Border.BackgroundProperty] = new DynamicResourceExtension(warn ? "NcWarnSoftBrush" : "NcMwHoverBrush");
        banner[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return banner;
    }

    /// <summary>Buttons in a row, the chosen one raised (Allow / Deny, the access levels).</summary>
    private static Control Segmented(IReadOnlyList<(string Key, string Name)> options, string chosen, bool locked, Action<string> pick, bool deny = false)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var (key, name) in options)
        {
            var on = key == chosen;
            var button = new Button
            {
                Content = name,
                FontSize = 12.5,
                Padding = new Thickness(10, 5),
                Margin = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = !locked || on,
                IsHitTestVisible = !locked && key != "custom",
                FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
                Tag = "seg-" + key,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            if (on)
            {
                button[!BackgroundProperty] = new DynamicResourceExtension("NcFieldBrush");
                if (deny)
                {
                    button[!ForegroundProperty] = new DynamicResourceExtension("NcBadBrush");
                }
            }
            button.Click += (_, _) => pick(key);
            row.Children.Add(button);
        }
        var box = new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(1) };
        box[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwHoverBrush");
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return box;
    }

    private static Control Field(string label, Control control, double bottom = 0)
    {
        var title = Faint(label, 12);
        title.Margin = new Thickness(1, 0, 0, 5);
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        return new StackPanel { Margin = new Thickness(0, 0, 0, bottom), Children = { title, control } };
    }

    private static Button SmallButton(string? glyph, string text, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null)
        {
            content.Children.Add(DialogLook.Glyph(glyph, 13));
        }
        content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(10, 4), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Faint(string text, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
        return block;
    }

    private static TextBlock Mono(string text, double size)
    {
        var block = Faint(text, size);
        block.Classes.Add("noloc");
        block[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("NcMonoFont");
        return block;
    }

    private static Border Lined(Control child, bool top)
    {
        var box = new Border { Child = child, BorderThickness = top ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1) };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return box;
    }
}
