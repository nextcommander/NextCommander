using System.Globalization;
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
using NextCommander.Core.Config;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What the restore window needs: the store, the settings in use, a file picker, opening the backups folder.</summary>
public sealed record RestoreBackupsRequest(SettingsStore Store, AppSettings Current, Func<string, Task<string?>> PickFile, Action OpenFolder);

/// <summary>The backup and the parts of it to restore.</summary>
public sealed record RestoreChoice(string Backup, IReadOnlyList<SettingsPart> Parts);

/// <summary>
/// Configuration > Restore settings from a backup (the user's template of 24 November 2026): the backups by day (search,
/// filters: mine / automatic / pinned; why each was made, a name, pinned, "Same as now", "Duplicate") on the left; on the
/// right what restoring the chosen one would change, part by part (settings, connections, Hotlist, shortcuts, button bars,
/// color theme), each part chosen or not. Back up now, add a file, name (F2), pin, delete (Del, with Undo). The settings
/// in use are always kept as a backup first, so a restore can be undone; NextCommander starts again after it.
/// </summary>
public sealed class RestoreBackupsWindow : Window
{
    private static readonly (SettingsPart Part, string Name, string Hint)[] s_parts =
    [
        (SettingsPart.Settings, "Settings", "Layout, display, copy and other options"),
        (SettingsPart.Connections, "Connections", "Saved FTP, SFTP, SMB, S3… connections"),
        (SettingsPart.Hotlist, "Hotlist", "Directory hotlist entries"),
        (SettingsPart.Shortcuts, "Shortcuts", "Custom keyboard shortcuts"),
        (SettingsPart.ButtonBars, "Button bars", "Buttons and their order"),
        (SettingsPart.ColorTheme, "Color theme", "Active theme"),
    ];

    private readonly RestoreBackupsRequest _request;
    private readonly string _current;
    private readonly TextBox _search = new() { PlaceholderText = "Search by date, label or connection name", Height = 32 };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _list = new() { Margin = new Thickness(6, 0, 6, 8) };
    private readonly ScrollViewer _listScroll;
    private readonly DockPanel _leftFooter = new() { Margin = new Thickness(12, 8) };
    private readonly ContentControl _right = new();
    private readonly Grid _overlay = new() { IsVisible = false };
    private IReadOnlyList<SettingsBackup> _backups = [];
    private string _filter = "all";
    private string? _selected;
    private readonly Dictionary<SettingsPart, bool> _picked = [];
    private readonly HashSet<SettingsPart> _open = [];
    private bool _renaming;
    private (string Path, string Text, SettingsBackupInfo Info, string When)? _deleted;

    public RestoreBackupsWindow(RestoreBackupsRequest request)
    {
        _request = request;
        _current = SettingsDiff.Normalized(request.Current);
        Title = "Restore settings from a backup";
        Width = 980;
        Height = 680;
        MinWidth = 760;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");

        // left: search, filters, the backups by day, how many are kept
        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 14), Margin = new Thickness(10, 0, 4, 0), Opacity = 0.6 };
        _search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                RenderList();
                if (Visible().FirstOrDefault() is { } first && !Visible().Any(b => b.Path == _selected))
                {
                    Select(first.Path);
                }
            }
        };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(12, 12, 12, 8), Children = { _search, _chips } };
        _listScroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var leftFoot = Lined(_leftFooter, top: true);
        var left = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(leftFoot, Dock.Bottom);
        left.Children.AddRange([head, leftFoot, _listScroll]);
        var leftBox = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        leftBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*") };
        Grid.SetColumn(_right, 1);
        main.Children.AddRange([leftBox, _right]);
        Content = new Panel { Children = { main, _overlay } };

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            Reload();
            _search.Focus();
        };
        Closed += (_, _) => _deleted = null;
    }

    /// <summary>The backup and parts chosen (null: closed without restoring).</summary>
    public RestoreChoice? Result { get; private set; }

    /// <summary>The backups shown in the list now (tests).</summary>
    public IReadOnlyList<SettingsBackup> Shown => Visible();

    /// <summary>The backup chosen now (tests).</summary>
    public SettingsBackup? Chosen => _backups.FirstOrDefault(b => b.Path == _selected);

    /// <summary>Whether a part is chosen for the restore (tests).</summary>
    public bool IsPicked(SettingsPart part) => _picked.GetValueOrDefault(part);

    // ---------------------------------------------------------------- the backups

    private void Reload(string? select = null)
    {
        _backups = _request.Store.BackupList();
        _selected = select ?? (_backups.Any(b => b.Path == _selected) ? _selected : _backups.Count > 0 ? _backups[0].Path : null);
        ResetPicks();
        RenderList();
        RenderRight();
    }

    /// <summary>Chooses a backup (the list, ↑ / ↓): every part with changes chosen, the first one opened.</summary>
    public void Select(string path)
    {
        _selected = path;
        _renaming = false;
        ResetPicks();
        RenderList();
        RenderRight();
    }

    private void ResetPicks()
    {
        _picked.Clear();
        _open.Clear();
        if (Chosen?.Settings is not { } settings)
        {
            return;
        }
        foreach (var (part, _, _) in s_parts)
        {
            _picked[part] = SettingsDiff.Changes(part, settings, _request.Current).Count > 0;
        }
        if (s_parts.Select(p => p.Part).FirstOrDefault(p => _picked[p]) is var first && _picked.GetValueOrDefault(first))
        {
            _open.Add(first);
        }
    }

    private static string TriggerText(SettingsBackup backup) => backup.Trigger switch
    {
        "manual" => Localizer.T("Created by you"),
        "file" => Localizer.T("Added from a file"),
        "start" => Localizer.T("Automatic · at start"),
        var t when t.StartsWith("before:", StringComparison.Ordinal) => Localizer.T($"Automatic · before “{Localizer.T(t[7..])}”"),
        _ => backup.Trigger,
    };

    private static string TriggerGlyph(SettingsBackup backup) => backup.Trigger switch
    {
        "manual" => "MiStar",
        "file" => "MiDoc",
        "start" => "MiPower",
        _ => "MiLock",
    };

    private static string DayName(DateTime time)
    {
        var days = (DateTime.Today - time.Date).Days;
        return days == 0 ? Localizer.T("Today") : days == 1 ? Localizer.T("Yesterday") : time.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    }

    private static string Ago(DateTime time)
    {
        var minutes = Math.Max(0, (int)Math.Round((DateTime.Now - time).TotalMinutes));
        return minutes < 60 ? Localizer.T($"{minutes} min ago")
            : minutes < 1440 ? Localizer.T($"{(int)Math.Round(minutes / 60.0)} h ago")
            : Localizer.T($"{(int)Math.Round(minutes / 1440.0)} days ago");
    }

    private bool IsSameAsNow(SettingsBackup backup) => backup.Settings is { } s && SettingsDiff.Normalized(s) == _current;

    /// <summary>The same settings as the next older backup (safe to delete).</summary>
    private bool IsDuplicate(SettingsBackup backup)
    {
        var index = _backups.ToList().IndexOf(backup);
        return index >= 0 && index + 1 < _backups.Count && backup.Settings is { } a && _backups[index + 1].Settings is { } b
            && SettingsDiff.Normalized(a) == SettingsDiff.Normalized(b);
    }

    private List<SettingsBackup> Visible()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        return [.. _backups.Where(b => _filter switch
            {
                "mine" => b.IsOwn,
                "auto" => !b.IsOwn,
                "pinned" => b.Pinned,
                _ => true,
            })
            .Where(b => query.Length == 0 || string.Join(' ',
                [b.Time.ToString("d", CultureInfo.CurrentCulture), b.Time.ToString("t", CultureInfo.CurrentCulture), DayName(b.Time),
                    b.Label ?? string.Empty, TriggerText(b),
                    .. b.Settings?.Connections.Select(c => c.Name) ?? [], .. b.Settings?.Hotlist.Select(h => h.Name) ?? []])
                .Contains(query, StringComparison.CurrentCultureIgnoreCase))];
    }

    // ---------------------------------------------------------------- the left side

    private void RenderList()
    {
        _chips.Children.Clear();
        foreach (var (key, text) in new[] { ("all", "All"), ("mine", "Mine"), ("auto", "Automatic"), ("pinned", "Pinned") })
        {
            var chip = new Button { Content = text, Classes = { "flat" }, Height = 24, MinHeight = 0, Padding = new Thickness(10, 0), FontSize = 12, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Tag = key, Focusable = false };
            chip[!BorderBrushProperty] = new DynamicResourceExtension(key == _filter ? "NcAccentBrush" : "NcMwStrongStrokeBrush");
            chip[!BackgroundProperty] = new DynamicResourceExtension(key == _filter ? "NcAccentSoftBrush" : "NcFieldBrush");
            chip[!ForegroundProperty] = new DynamicResourceExtension(key == _filter ? "NcAccentBrush" : "NcMutedBrush");
            chip.Click += (_, _) =>
            {
                _filter = key;
                RenderList();
                if (Visible().FirstOrDefault() is { } first && !Visible().Any(b => b.Path == _selected))
                {
                    Select(first.Path);
                }
            };
            _chips.Children.Add(chip);
        }

        _list.Children.Clear();
        string? day = null;
        foreach (var backup in Visible())
        {
            var name = DayName(backup.Time);
            if (name != day)
            {
                day = name;
                var header = new TextBlock { Text = name, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 10, 8, 4), Classes = { "noloc" } };
                header[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
                _list.Children.Add(header);
            }
            _list.Children.Add(BackupRow(backup));
        }
        if (_list.Children.Count == 0)
        {
            _list.Children.Add(Faint(_backups.Count == 0 ? "No backup yet: Back up now keeps the settings as they are." : "No backups match.", 13, new Thickness(16, 24)));
        }

        _leftFooter.Children.Clear();
        var folder = Link("Open folder", () => _request.OpenFolder());
        DockPanel.SetDock(folder, Dock.Right);
        _leftFooter.Children.Add(folder);
        if (_deleted is { } deleted)
        {
            var undo = Link("Undo", UndoDelete);
            DockPanel.SetDock(undo, Dock.Right);
            _leftFooter.Children.Add(undo);
            _leftFooter.Children.Add(Faint(Localizer.T($"Deleted backup from {deleted.When}"), 12));
        }
        else
        {
            _leftFooter.Children.Add(Faint(Localizer.T($"{_backups.Count} backups · keeps last {SettingsStore.BackupCount} automatic"), 12));
        }
    }

    private Control BackupRow(SettingsBackup backup)
    {
        var selected = backup.Path == _selected;
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        tile[!Border.BackgroundProperty] = new DynamicResourceExtension(backup.IsOwn ? "NcAccentSoftBrush" : "NcMwHoverBrush");
        tile.Child = DialogLook.Glyph(TriggerGlyph(backup), 15, accent: backup.IsOwn);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = backup.Time.ToString("t", CultureInfo.CurrentCulture), FontWeight = FontWeight.SemiBold, FontSize = 13.5, Classes = { "noloc" } });
        if (backup.Label is { Length: > 0 } label)
        {
            title.Children.Add(new TextBlock { Text = label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
        }
        var texts = new StackPanel { Children = { title, Faint(backup.Damaged ? Localizer.T("Damaged: it cannot be restored") : TriggerText(backup), 12) } };
        var right = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        if (backup.Pinned)
        {
            var pin = DialogLook.Glyph("MiLock", 13, accent: true);
            ToolTip.SetTip(pin, "Pinned — never removed automatically");
            pin.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(pin);
        }
        if (IsSameAsNow(backup))
        {
            right.Children.Add(Pill("Same as now", ok: true));
        }
        else if (IsDuplicate(backup))
        {
            var duplicate = Pill("Duplicate", ok: false);
            ToolTip.SetTip(duplicate, "The same settings as the backup below it — safe to delete");
            right.Children.Add(duplicate);
        }
        var row = new DockPanel { Margin = new Thickness(10, 8) };
        DockPanel.SetDock(tile, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.AddRange([tile, right, texts]);
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12), HorizontalAlignment = HorizontalAlignment.Left, IsVisible = selected };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        var box = new Border { CornerRadius = new CornerRadius(6), Child = new Panel { Children = { row, bar } }, Background = Brushes.Transparent, Tag = backup.Path };
        if (selected)
        {
            box[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwAccentSoftBrush");
            Dispatcher.UIThread.Post(() => box.BringIntoView(), DispatcherPriority.Background);
        }
        box.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
            {
                Select(backup.Path);
            }
        };
        return box;
    }

    // ---------------------------------------------------------------- the right side

    private void RenderRight()
    {
        if (Chosen is not { } backup)
        {
            _right.Content = Footer(null, 0, 0);
            return;
        }
        var body = new StackPanel { Margin = new Thickness(22, 18) };

        // the title (or the name being typed), what it is, the buttons
        Control heading;
        var when = backup.Time.ToString("M", CultureInfo.CurrentCulture) + ", " + backup.Time.ToString("t", CultureInfo.CurrentCulture);
        if (_renaming)
        {
            var name = new TextBox { Text = backup.Label ?? string.Empty, PlaceholderText = "Name this backup", FontSize = 16, Width = 340, HorizontalAlignment = HorizontalAlignment.Left, Tag = "label" };
            name.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    CommitLabel(name.Text);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _renaming = false;
                    RenderRight();
                }
            };
            name.LostFocus += (_, _) =>
            {
                if (_renaming)
                {
                    CommitLabel(name.Text);
                }
            };
            Dispatcher.UIThread.Post(() =>
            {
                name.Focus();
                name.SelectAll();
            });
            heading = name;
        }
        else
        {
            heading = new TextBlock { Text = backup.Label is { Length: > 0 } label ? label : when, FontSize = 18, FontWeight = FontWeight.SemiBold, Classes = { "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        var content = backup.Settings is { } held ? Localizer.T($"{held.Connections.Count} connections, {held.Hotlist.Count} Hotlist entries") : string.Empty;
        var sub = Faint((backup.Label is { Length: > 0 } ? when + " · " : string.Empty) + Ago(backup.Time) + " · " + TriggerText(backup) + (content.Length > 0 ? " · " + content : string.Empty), 12.5);
        sub.TextWrapping = TextWrapping.Wrap;
        var titles = new StackPanel { Spacing = 2, Children = { heading, sub } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(Ghost("MiPencil", "Name this backup (F2)", () =>
        {
            _renaming = true;
            RenderRight();
        }, "rename"));
        actions.Children.Add(Ghost("MiLock", backup.Pinned ? "Unpin" : "Pin — never remove automatically", TogglePin, "pin", backup.Pinned));
        actions.Children.Add(Ghost("MiTrash", "Delete (Del)", Delete, "delete"));
        var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(actions, Dock.Right);
        headRow.Children.AddRange([actions, titles]);
        body.Children.Add(headRow);

        if (backup.Damaged || backup.Settings is not { } settings)
        {
            body.Children.Add(Banner(false, "This backup cannot be read any more", "Choose another one, or delete it."));
            _right.Content = Footer(body, 0, 0);
            return;
        }

        // the changes, part by part
        var changes = s_parts.ToDictionary(p => p.Part, p => SettingsDiff.Changes(p.Part, settings, _request.Current));
        var total = changes.Values.Sum(c => c.Count);
        if (total == 0)
        {
            body.Children.Add(Banner(true, "This backup matches your current settings", "Restoring it wouldn’t change anything."));
        }
        else if (backup.Trigger.StartsWith("before:", StringComparison.Ordinal))
        {
            body.Children.Add(Banner(false, Localizer.T($"Made just before “{Localizer.T(backup.Trigger[7..])}”"), "Restore it to undo that action."));
        }
        var section = new DockPanel { Margin = new Thickness(2, 4, 2, 8) };
        if (total > 0)
        {
            var allChosen = s_parts.Where(p => changes[p.Part].Count > 0).All(p => _picked.GetValueOrDefault(p.Part));
            var all = Link(allChosen ? "Clear all" : "Select all", () =>
            {
                foreach (var (part, _, _) in s_parts)
                {
                    _picked[part] = !allChosen && changes[part].Count > 0;
                }
                RenderRight();
            });
            DockPanel.SetDock(all, Dock.Right);
            section.Children.Add(all);
        }
        var sectionTitle = new TextBlock { Text = "What restoring would change", FontSize = 12, FontWeight = FontWeight.SemiBold };
        sectionTitle[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        section.Children.Add(sectionTitle);
        body.Children.Add(section);

        var card = new StackPanel();
        foreach (var (part, name, hint) in s_parts)
        {
            card.Children.Add(PartRow(part, name, hint, changes[part], card.Children.Count == 0));
        }
        var cardBox = new Border { Child = card, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        cardBox[!Border.BackgroundProperty] = new DynamicResourceExtension("NcMwCardBrush");
        cardBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        body.Children.Add(cardBox);

        var always = new CheckBox { IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 14, 0, 0) };
        always.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Back up current settings first", FontSize = 13 },
                Faint("Always on, so a restore can be undone.", 12),
            },
        };
        body.Children.Add(always);

        var chosenParts = s_parts.Where(p => _picked.GetValueOrDefault(p.Part) && changes[p.Part].Count > 0).ToList();
        _right.Content = Footer(body, total, chosenParts.Sum(p => changes[p.Part].Count), chosenParts.Count);
    }

    private Control PartRow(SettingsPart part, string name, string hint, IReadOnlyList<SettingsChange> changes, bool first)
    {
        var count = changes.Count;
        var check = new CheckBox { IsChecked = count > 0 && _picked.GetValueOrDefault(part), IsEnabled = count > 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Tag = part, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(check, Localizer.T("Restore") + " " + Localizer.T(name));
        check.IsCheckedChanged += (_, _) =>
        {
            _picked[part] = check.IsChecked == true;
            RenderRight();
        };
        var names = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.Medium }, Faint(hint, 12) },
        };
        var counted = new TextBlock
        {
            Text = count > 0 ? Localizer.T(count == 1 ? "1 change" : $"{count} changes") : Localizer.T("No changes"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "noloc" },
        };
        counted[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(count > 0 ? "NcMutedBrush" : "NcFaintBrush");
        var open = _open.Contains(part) && count > 0;
        var chevron = DialogLook.Glyph("MiArrowRight", 12);
        chevron.Margin = new Thickness(10, 0, 0, 0);
        chevron.RenderTransform = open ? new RotateTransform(90) : null;
        chevron.Opacity = count > 0 ? 0.6 : 0;
        var line = new DockPanel { Margin = new Thickness(12, 9) };
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        DockPanel.SetDock(counted, Dock.Right);
        line.Children.AddRange([check, chevron, counted, names]);
        var head = new Border { Child = line, Background = Brushes.Transparent, Tag = "part:" + part };
        head.PointerPressed += (_, e) =>
        {
            if (count == 0 || (e.Source as Visual)?.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
            {
                return;
            }
            if (!_open.Remove(part))
            {
                _open.Add(part);
            }
            RenderRight();
        };
        var row = new StackPanel { Children = { head } };
        if (open)
        {
            var diff = new StackPanel { Spacing = 3, Margin = new Thickness(40, 2, 12, 10) };
            foreach (var change in changes)
            {
                diff.Children.Add(ChangeLine(change));
            }
            row.Children.Add(diff);
        }
        var box = new Border { Child = row, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return box;
    }

    private static Control ChangeLine(SettingsChange change)
    {
        var (text, brush) = change.Op switch
        {
            "add" => ("Add back", "NcOkBrush"),
            "del" => ("Remove", "NcBadBrush"),
            _ => ("Change", "NcWarnBrush"),
        };
        var op = new TextBlock { Text = text, Width = 70, FontSize = 11, FontWeight = FontWeight.SemiBold };
        op[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brush);
        var name = new TextBlock { Text = change.Name, FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0), Classes = { "noloc" } };
        var value = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        value[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("NcMonoFont");
        value[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
        if (change.Op == "chg" && change.From is not null)
        {
            value.Inlines = [new Avalonia.Controls.Documents.Run(change.From) { TextDecorations = TextDecorations.Strikethrough }, new Avalonia.Controls.Documents.Run(" → " + change.To)];
        }
        else
        {
            value.Text = change.To ?? change.From ?? string.Empty;
        }
        var line = new DockPanel();
        DockPanel.SetDock(op, Dock.Left);
        DockPanel.SetDock(name, Dock.Left);
        line.Children.AddRange([op, name, value]);
        return line;
    }

    /// <summary>The body over the bottom row: Open file…, Back up now, what is chosen, Restore…, Cancel.</summary>
    private Control Footer(Control? body, int total, int chosenChanges, int chosenParts = 0)
    {
        var restore = new Button { Classes = { "dialog" }, IsDefault = true, IsEnabled = chosenChanges > 0, Tag = "restore", MinWidth = 100 };
        restore.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { DialogLook.Glyph("MiHistory", 14), new TextBlock { Text = "Restore…" } } };
        restore.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "Cancel", Classes = { "dialog" }, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var summary = Faint(total == 0 ? string.Empty
            : chosenChanges == 0 ? Localizer.T("Nothing selected")
            : Localizer.T($"{chosenChanges} changes · {chosenParts} parts"), 12.5);
        summary.VerticalAlignment = VerticalAlignment.Center;
        summary.Margin = new Thickness(0, 0, 8, 0);
        var row = new DockPanel { Margin = new Thickness(16, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.AddRange([summary, restore, cancel]);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        left.Children.Add(TextButton("MiDoc", "Open file…", () => _ = AddFileAsync(), "file"));
        left.Children.Add(TextButton("MiPlus", "Back up now", BackUpNow, "backupnow"));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.AddRange([buttons, left]);
        var footer = Lined(row, top: true);
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        var page = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        page.Children.Add(footer);
        page.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return page;
    }

    // ---------------------------------------------------------------- actions

    private void CommitLabel(string? label)
    {
        _renaming = false;
        if (_selected is { } path)
        {
            _request.Store.SetLabel(path, label);
        }
        Reload(_selected);
    }

    private void TogglePin()
    {
        if (Chosen is { } backup)
        {
            _request.Store.SetPinned(backup.Path, !backup.Pinned);
            Reload(backup.Path);
        }
    }

    /// <summary>Deletes the chosen backup (Undo in the list's footer brings it back); a pinned one is refused.</summary>
    public void Delete()
    {
        if (Chosen is not { } backup)
        {
            return;
        }
        if (backup.Pinned)
        {
            ShowNote("Unpin it first — pinned backups are protected");
            return;
        }
        var visible = Visible();
        var at = visible.FindIndex(b => b.Path == backup.Path);
        var text = File.ReadAllText(backup.Path);
        var info = _request.Store.InfoOf(backup.Path);
        _request.Store.DeleteBackup(backup.Path);
        _deleted = (backup.Path, text, info, backup.Time.ToString("t", CultureInfo.CurrentCulture));
        var next = visible.Where(b => b.Path != backup.Path).ElementAtOrDefault(Math.Max(0, at == visible.Count - 1 ? at - 1 : at));
        Reload(next?.Path);
    }

    private void UndoDelete()
    {
        if (_deleted is { } deleted)
        {
            _deleted = null;
            _request.Store.PutBack(deleted.Path, deleted.Text, deleted.Info);
            Reload(deleted.Path);
        }
    }

    private void BackUpNow()
    {
        if (_request.Store.BackupNow() is { } made)
        {
            Reload(made);
            _renaming = true; // give it a name
            RenderRight();
        }
    }

    private async Task AddFileAsync()
    {
        if (await _request.PickFile("Add a settings file to the backups") is not { } file)
        {
            return;
        }
        try
        {
            Reload(_request.Store.AddBackupFromFile(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            ShowNote(Localizer.T($"Not a settings file that loads: {ex.Message}"));
        }
    }

    /// <summary>Asks before restoring: the parts replaced, the rest kept, a backup made first, the restart.</summary>
    public void Confirm()
    {
        if (Chosen is not { Settings: { } settings } backup)
        {
            return;
        }
        var parts = s_parts.Where(p => _picked.GetValueOrDefault(p.Part) && SettingsDiff.Changes(p.Part, settings, _request.Current).Count > 0).ToList();
        if (parts.Count == 0)
        {
            return;
        }
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(18, 0, 0, 10) };
        foreach (var (part, name, _) in parts)
        {
            var count = SettingsDiff.Changes(part, settings, _request.Current).Count;
            list.Children.Add(new TextBlock { Text = "• " + Localizer.T(name) + " — " + Localizer.T(count == 1 ? "1 change" : $"{count} changes"), FontSize = 13, Classes = { "noloc" } });
        }
        var title = new TextBlock { Text = Localizer.T($"Restore from {(backup.Label is { Length: > 0 } label ? label : backup.Time.ToString("t", CultureInfo.CurrentCulture))}?"), FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8), Classes = { "noloc" } };
        var content = new StackPanel
        {
            Margin = new Thickness(22, 20),
            Children =
            {
                title,
                Faint("These parts of your settings will be replaced:", 13),
                list,
                Faint("Everything else stays as it is. Your current settings are backed up first, and NextCommander starts again to use them.", 13),
            },
        };
        foreach (var block in content.Children.OfType<TextBlock>())
        {
            block.TextWrapping = TextWrapping.Wrap;
        }
        var yes = new Button { Content = "Restore and restart", Classes = { "dialog" }, IsDefault = true, Tag = "dorestore" };
        yes.Click += (_, _) =>
        {
            Result = new RestoreChoice(backup.Path, [.. parts.Select(p => p.Part)]);
            Close();
        };
        var no = new Button { Content = "Cancel", Classes = { "dialog" } };
        no.Click += (_, _) => _overlay.IsVisible = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 12), Children = { yes, no } };
        var foot = Lined(buttons, top: true);
        foot[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurface2Brush");
        var dialog = new Border
        {
            Width = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new DockPanel { Children = { foot, content } },
        };
        DockPanel.SetDock(foot, Dock.Bottom);
        dialog[!Border.BackgroundProperty] = new DynamicResourceExtension("NcSurfaceBrush");
        dialog[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrongStrokeBrush");
        dialog[!Border.BoxShadowProperty] = new DynamicResourceExtension("NcMwPopupShadow");
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)) });
        _overlay.Children.Add(dialog);
        _overlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => yes.Focus());
    }

    /// <summary>Restores what the confirmation lists (tests: without clicking).</summary>
    public void ConfirmRestore() => _overlay.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Tag, "dorestore"))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Chooses a part or not (tests; the check boxes).</summary>
    public void Pick(SettingsPart part, bool picked)
    {
        _picked[part] = picked;
        RenderRight();
    }

    private void ShowNote(string text)
    {
        _leftFooter.Children.Clear();
        var note = Faint(text, 12);
        note[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcWarnBrush");
        _leftFooter.Children.Add(note);
        DispatcherTimer.RunOnce(RenderList, TimeSpan.FromSeconds(4));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_overlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _overlay.IsVisible = false;
            }
            return;
        }
        var inName = (e.Source as TextBox)?.Tag as string == "label";
        if (inName)
        {
            return;
        }
        var inSearch = ReferenceEquals(e.Source, _search) || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) == _search;
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                var visible = Visible();
                var at = visible.FindIndex(b => b.Path == _selected);
                if (visible.Count > 0)
                {
                    Select(visible[Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, visible.Count - 1)].Path);
                }
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                Confirm();
                e.Handled = true;
                break;
            case Key.F2:
                _renaming = true;
                RenderRight();
                e.Handled = true;
                break;
            case Key.Delete when !inSearch:
                Delete();
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _search.Focus();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- small parts

    private static TextBlock Faint(string text, double size, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, Margin = margin ?? default };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush");
        return block;
    }

    private static Border Pill(string text, bool ok)
    {
        var pill = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), BorderThickness = new Thickness(ok ? 0 : 1), HorizontalAlignment = HorizontalAlignment.Right };
        var label = new TextBlock { Text = text, FontSize = 10.5 };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ok ? "NcOkBrush" : "NcFaintBrush");
        pill.Child = label;
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension(ok ? "NcOkSoftBrush" : "NcMwHoverBrush");
        pill[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return pill;
    }

    private static Control Banner(bool ok, string title, string text)
    {
        var icon = DialogLook.Glyph(ok ? "MiCheck" : "MiInfo", 16);
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var texts = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap } } };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.AddRange([icon, texts]);
        var banner = new Border { Child = row, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 14), Tag = ok ? "ok" : "warn" };
        banner[!Border.BackgroundProperty] = new DynamicResourceExtension(ok ? "NcOkSoftBrush" : "NcWarnSoftBrush");
        return banner;
    }

    private static Button Link(string text, Action click)
    {
        var link = new Button { Content = text, Classes = { "flat" }, Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, Focusable = false, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        link[!ForegroundProperty] = new DynamicResourceExtension("NcAccentBrush");
        link.Click += (_, _) => click();
        return link;
    }

    private static Button Ghost(string glyph, string tip, Action click, string tag, bool on = false)
    {
        var button = new Button { Classes = { "flat" }, Width = 32, Height = 32, Padding = new Thickness(0), Background = Brushes.Transparent, Content = DialogLook.Glyph(glyph, 15, accent: on), Tag = tag, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button TextButton(string glyph, string text, Action click, string tag)
    {
        var button = new Button { Classes = { "flat" }, Background = Brushes.Transparent, Tag = tag, Padding = new Thickness(10, 4) };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { DialogLook.Glyph(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        button.Click += (_, _) => click();
        return button;
    }

    private static Border Lined(Control child, bool top)
    {
        var box = new Border { Child = child, BorderThickness = top ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1) };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush");
        return box;
    }
}
