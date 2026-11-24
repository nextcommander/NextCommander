using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NextCommander.App.Services.Design;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Permissions (Files > Permissions...; user's request, 24 November 2026). Unix places (Linux, macOS, SFTP): the read /
/// write / execute switches of owner, group and others, the special bits, the octal value, owner and group, and for
/// folders "also inside". Windows: the rules of the access list (inherited ones shown, changed at the folder they come
/// from), rules added or removed, and the owner. Switches of items that differ start mixed; only what is touched
/// changes.
/// </summary>
public sealed class PermissionsDialog : DialogWindow
{
    private static readonly (string Who, UnixFileMode Read, UnixFileMode Write, UnixFileMode Execute)[] s_classes =
    [
        ("Owner", UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute),
        ("Group", UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute),
        ("Others", UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute),
    ];

    private static readonly (string Text, UnixFileMode Flag)[] s_special =
    [
        ("Set user ID", UnixFileMode.SetUser),
        ("Set group ID", UnixFileMode.SetGroup),
        ("Sticky (only the owner deletes)", UnixFileMode.StickyBit),
    ];

    private static readonly (AclRights Rights, string Text)[] s_rights =
    [
        (AclRights.FullControl, "Full control"),
        (AclRights.Modify, "Modify"),
        (AclRights.ReadAndExecute, "Read & execute"),
        (AclRights.Read, "Read"),
        (AclRights.Write, "Write"),
    ];

    private readonly PermissionKind _kind;
    private readonly List<(CheckBox Box, UnixFileMode Flag, bool? Initial)> _bits = [];
    private readonly TextBox _octal = new() { Width = 80, Classes = { "nc-mono", "noloc" } };
    private readonly TextBlock _symbolic = new() { Classes = { "nc-mono", "noloc" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _owner = new() { Classes = { "noloc" } };
    private readonly TextBox _group = new() { Classes = { "noloc" } };
    private readonly string? _initialOwner;
    private readonly string? _initialGroup;
    private readonly CheckBox _recursive = new() { Content = "Also everything inside the folders" };
    private readonly ListBox _rules = new() { MaxHeight = 220, Classes = { "noloc" } };
    private readonly List<AclEntry> _shownRules = [];
    private readonly List<AclEntry> _added = [];
    private readonly List<string> _removed = [];
    private readonly HashSet<string> _initialExplicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _identity = new() { PlaceholderText = "User or group (e.g. Users)", Classes = { "noloc" } };
    private readonly ComboBox _rightsBox = new() { MinWidth = 150 };
    private readonly ComboBox _allowBox = new() { MinWidth = 90 };
    private readonly Button _remove = Nc.Button("Remove");
    private readonly TextBlock _error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly Button _apply = Nc.Button("Apply", "primary", "Enter");
    private bool _syncing;

    public PermissionsDialog(IReadOnlyList<VfsEntry> entries, IReadOnlyList<FilePermissions> current)
        : base("Permissions", 520)
    {
        Nc.Apply(this);
        _kind = current.Count > 0 ? current[0].Kind : PermissionKind.None;
        var title = new TextBlock
        {
            Text = entries.Count == 1 ? entries[0].Name : ItemsText(entries.Count),
            FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Classes = { "noloc" },
        };
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(22, 18, 22, 20), Children = { title } };
        _initialOwner = Same(current.Select(p => p.Owner));
        _initialGroup = Same(current.Select(p => p.Group));
        if (_kind == PermissionKind.Unix)
        {
            BuildUnix(content, current, entries.Any(e => e.IsDirectory));
        }
        else
        {
            BuildAcl(content, current, entries.Count);
        }
        _error[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcBadBrush");
        content.Children.Add(_error);

        var cancel = Nc.Button("Cancel", "", "Esc");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _apply.IsDefault = true;
        _apply.Click += (_, _) => Accept();
        var footer = Nc.Footer([], cancel, _apply);
        footer.Padding = new Thickness(22, 14);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([footer, new ScrollViewer { Content = content }]);
        Content = root;
        Update();
    }

#pragma warning disable CA1863 // the pattern is the translation of the running language
    private static string ItemsText(int count) => string.Format(CultureInfo.CurrentCulture, Core.Localization.Localizer.T("{0} items"), count);
#pragma warning restore CA1863

    /// <summary>The change chosen (null: cancelled).</summary>
    public PermissionChange? Result { get; private set; }

    /// <summary>The switch of a bit (tests).</summary>
    public CheckBox BitOf(UnixFileMode flag) => _bits.Single(b => b.Flag == flag).Box;

    public TextBox OctalBox => _octal;

    public TextBox OwnerBox => _owner;

    public TextBox IdentityBox => _identity;

    public IReadOnlyList<AclEntry> ShownRules => _shownRules;

    public bool CanApply => _apply.IsEnabled;

    private static string? Same(IEnumerable<string?> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static StackPanel Section(string title, Control body) =>
        new() { Spacing = 8, Children = { new TextBlock { Text = title, Classes = { "nc-section" } }, body } };

    // ---------------------------------------------------------------- Unix

    private void BuildUnix(StackPanel content, IReadOnlyList<FilePermissions> current, bool folders)
    {
        bool? StateOf(UnixFileMode flag)
        {
            var on = current.Count(p => p.Mode is { } m && m.HasFlag(flag));
            return on == 0 ? false : on == current.Count ? true : null;
        }
        CheckBox Bit(UnixFileMode flag, string name)
        {
            var initial = StateOf(flag);
            var box = new CheckBox { IsThreeState = initial is null, IsChecked = initial, HorizontalAlignment = HorizontalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            _bits.Add((box, flag, initial));
            box.IsCheckedChanged += (_, _) => Update();
            return box;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        var col = 1;
        foreach (var header in new[] { "Read", "Write", "Execute" })
        {
            var text = new TextBlock { Text = header, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "nc-hint" } };
            Grid.SetColumn(text, col++);
            grid.Children.Add(text);
        }
        var row = 1;
        foreach (var (who, read, write, execute) in s_classes)
        {
            var label = new TextBlock { Text = who, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            col = 1;
            foreach (var (flag, name) in new[] { (read, "Read"), (write, "Write"), (execute, "Execute") })
            {
                var box = Bit(flag, who + " " + name);
                Grid.SetRow(box, row);
                Grid.SetColumn(box, col++);
                grid.Children.Add(box);
            }
            row++;
        }
        var special = new StackPanel { Spacing = 2 };
        foreach (var (text, flag) in s_special)
        {
            var box = Bit(flag, text);
            box.HorizontalAlignment = HorizontalAlignment.Left;
            box.Content = text;
            special.Children.Add(box);
        }
        _octal.TextChanged += (_, _) => OctalEdited();
        Avalonia.Automation.AutomationProperties.SetName(_octal, "Octal value");
        var values = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { new TextBlock { Text = "Octal", VerticalAlignment = VerticalAlignment.Center }, _octal, _symbolic },
        };
        content.Children.Add(Section("Permissions", new StackPanel { Spacing = 10, Children = { grid, special, values } }));

        var numeric = current.Any(p => p.NumericOwner);
        _owner.Text = _initialOwner;
        _group.Text = _initialGroup;
        _owner.PlaceholderText = _group.PlaceholderText = _initialOwner is null && current.Count > 1 ? "(differs: empty keeps it)" : null;
        _owner.TextChanged += (_, _) => Update();
        _group.TextChanged += (_, _) => Update();
        var ownership = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Nc.Field("Owner", _owner, numeric ? "A number (the server gives no names)" : "A user name (changing it needs root)"),
                Nc.Field("Group", _group, numeric ? "A number" : "A group name"),
            },
        };
        content.Children.Add(Section("Owner and group", ownership));
        if (folders)
        {
            _recursive.IsCheckedChanged += (_, _) => Update();
            content.Children.Add(_recursive);
        }
    }

    /// <summary>The mode the switches give, or null when one is mixed.</summary>
    private UnixFileMode? CurrentMode()
    {
        var mode = UnixFileMode.None;
        foreach (var (box, flag, _) in _bits)
        {
            if (box.IsChecked is null)
            {
                return null;
            }
            if (box.IsChecked == true)
            {
                mode |= flag;
            }
        }
        return mode;
    }

    private void OctalEdited()
    {
        if (_syncing || PermissionChange.ParseOctal(_octal.Text) is not { } mode)
        {
            return;
        }
        _syncing = true;
        foreach (var (box, flag, _) in _bits)
        {
            box.IsThreeState = false;
            box.IsChecked = mode.HasFlag(flag);
        }
        _syncing = false;
        Update();
    }

    // ---------------------------------------------------------------- Windows

    private void BuildAcl(StackPanel content, IReadOnlyList<FilePermissions> current, int count)
    {
        _shownRules.AddRange(current.Count > 0 ? current[0].Acl : []);
        _initialExplicit.UnionWith(_shownRules.Where(r => !r.Inherited).Select(r => r.Identity));
        _rules.SelectionChanged += (_, _) => Update();
        _remove.Click += (_, _) => RemoveSelected();
        var rules = new StackPanel { Spacing = 8, Children = { _rules, new StackPanel { Orientation = Orientation.Horizontal, Children = { _remove } } } };
        if (count > 1)
        {
            rules.Children.Insert(0, new TextBlock { Text = "The rules of the first item; the changes are made on every selected item.", Classes = { "nc-hint" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
        content.Children.Add(Section("Rules", rules));

        _rightsBox.ItemsSource = s_rights.Select(r => r.Text).ToList();
        _rightsBox.SelectedIndex = 2;
        _allowBox.ItemsSource = new[] { "Allow", "Deny" };
        _allowBox.SelectedIndex = 0;
        var add = Nc.Button("Add");
        add.Click += (_, _) => AddRule();
        Avalonia.Automation.AutomationProperties.SetName(_identity, "User or group");
        var adding = new StackPanel
        {
            Spacing = 8,
            Children = { _identity, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _allowBox, _rightsBox, add } } },
        };
        content.Children.Add(Section("Add a rule", adding));

        _owner.Text = _initialOwner;
        _owner.TextChanged += (_, _) => Update();
        Avalonia.Automation.AutomationProperties.SetName(_owner, "Owner");
        content.Children.Add(Section("Owner", new StackPanel
        {
            Spacing = 4,
            Children = { _owner, new TextBlock { Text = "Changing the owner needs administrator rights", Classes = { "nc-hint" } } },
        }));
        ShowRules();
    }

    /// <summary>Adds the rule of the fields (a folder's rule also goes to what is inside).</summary>
    public void AddRule()
    {
        var identity = (_identity.Text ?? string.Empty).Trim();
        if (identity.Length == 0)
        {
            return;
        }
        var rule = new AclEntry(identity, _allowBox.SelectedIndex != 1, s_rights[Math.Max(0, _rightsBox.SelectedIndex)].Rights);
        _added.Add(rule);
        _shownRules.Insert(_shownRules.FindLastIndex(r => !r.Inherited) + 1, rule);
        _identity.Text = string.Empty;
        ShowRules();
    }

    /// <summary>Selects a rule of the list (tests).</summary>
    public void SelectRule(int index) => _rules.SelectedIndex = index;

    /// <summary>
    /// Removes the selected rule: every explicit rule of its user or group goes (an inherited rule stays: it is changed at
    /// the folder it comes from).
    /// </summary>
    public void RemoveSelected()
    {
        if (_rules.SelectedIndex < 0 || _rules.SelectedIndex >= _shownRules.Count || _shownRules[_rules.SelectedIndex] is not { Inherited: false } rule)
        {
            return;
        }
        _added.RemoveAll(a => string.Equals(a.Identity, rule.Identity, StringComparison.OrdinalIgnoreCase));
        if (_initialExplicit.Contains(rule.Identity) && !_removed.Contains(rule.Identity, StringComparer.OrdinalIgnoreCase))
        {
            _removed.Add(rule.Identity); // the item had rules of this user or group: they go
        }
        _shownRules.RemoveAll(r => !r.Inherited && string.Equals(r.Identity, rule.Identity, StringComparison.OrdinalIgnoreCase));
        ShowRules();
    }

    /// <summary>"Allow · Users · Read &amp; execute (inherited)".</summary>
    internal static string Describe(AclEntry rule)
    {
        string T(string text) => Core.Localization.Localizer.T(text);
        var rights = s_rights.FirstOrDefault(r => r.Rights == rule.Rights).Text ?? "Special";
        return $"{T(rule.Allow ? "Allow" : "Deny")} · {rule.Identity} · {T(rights)}" + (rule.Inherited ? " " + T("(inherited)") : string.Empty);
    }

    private void ShowRules()
    {
        _rules.ItemsSource = _shownRules.Select(Describe).ToList();
        Update();
    }

    // ---------------------------------------------------------------- both

    private PermissionChange Change()
    {
        var set = UnixFileMode.None;
        var clear = UnixFileMode.None;
        foreach (var (box, flag, initial) in _bits)
        {
            if (box.IsChecked == initial || box.IsChecked is null)
            {
                continue;
            }
            if (box.IsChecked == true)
            {
                set |= flag;
            }
            else
            {
                clear |= flag;
            }
        }
        string? Edited(TextBox box, string? initial) =>
            (box.Text ?? string.Empty).Trim() is var text && text.Length > 0 && !string.Equals(text, initial, StringComparison.Ordinal) ? text : null;
        return new PermissionChange
        {
            Set = set,
            Clear = clear,
            Owner = Edited(_owner, _initialOwner),
            Group = _kind == PermissionKind.Unix ? Edited(_group, _initialGroup) : null,
            AddRules = [.. _added],
            RemoveRulesOf = [.. _removed],
            Recursive = _kind == PermissionKind.Unix && _recursive.IsChecked == true,
        };
    }

    private void Update()
    {
        if (_kind == PermissionKind.Unix && !_syncing)
        {
            _syncing = true;
            var mode = CurrentMode();
            var octal = mode is { } m ? PermissionChange.Octal(m) : string.Empty;
            if (!string.Equals(PermissionChange.ParseOctal(_octal.Text) is { } typed ? PermissionChange.Octal(typed) : null, octal, StringComparison.Ordinal))
            {
                _octal.Text = octal;
            }
            _symbolic.Text = mode is { } shown ? PermissionChange.Text(shown) : Core.Localization.Localizer.T("(mixed)");
            _syncing = false;
        }
        _remove.IsEnabled = _rules.SelectedIndex >= 0 && _rules.SelectedIndex < _shownRules.Count && !_shownRules[_rules.SelectedIndex].Inherited;
        _apply.IsEnabled = !Change().IsEmpty;
    }

    /// <summary>Apply (Enter).</summary>
    public void Accept()
    {
        var change = Change();
        if (change.IsEmpty)
        {
            return;
        }
        Result = change;
        Close();
    }
}
