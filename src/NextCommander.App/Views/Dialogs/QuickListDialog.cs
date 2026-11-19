using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using Avalonia.Platform.Storage;
using NextCommander.Core.Matching;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Searchable list: type to filter (anywhere, * and ? allowed), Enter to choose, extra action buttons.</summary>
public sealed class QuickListDialog : DialogWindow
{
    private readonly IReadOnlyList<QuickListItem> _all;
    private readonly TextBox _filter = new() { PlaceholderText = "Type to search..." };
    private readonly ListBox _list = new() { Height = 380 };

    public QuickListDialog(string title, IReadOnlyList<QuickListItem> items, IReadOnlyList<string>? actions)
        : base(title, 720)
    {
        _all = items;
        _list.ItemTemplate = new FuncDataTemplate<QuickListItem>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(new TextBlock { Text = item?.Text, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(item?.Detail))
            {
                panel.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            }
            return panel;
        });
        Body.Children.Add(_filter);
        Body.Children.Add(_list);
        Body.Children.Add(new TextBlock { Text = $"{items.Count} items", FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right });

        AddButton("Select", () => Finish(-1), isDefault: true);
        for (var i = 0; i < (actions?.Count ?? 0); i++)
        {
            var index = i;
            AddButton(actions![i], () => Finish(index));
        }
        AddButton("Cancel", Close, isCancel: true);

        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, _) => Finish(-1);
        ApplyFilter();
        Opened += (_, _) => _filter.Focus();
    }

    public QuickListResult? Result { get; private set; }

    private void ApplyFilter()
    {
        var text = _filter.Text ?? string.Empty;
        var matcher = new QuickMatcher(text, QuickMatchMode.Anywhere);
        var filtered = text.Length == 0 ? _all : _all.Where(i => matcher.IsMatch(i.Text) || (i.Detail is not null && matcher.IsMatch(i.Detail))).ToList();
        _list.ItemsSource = filtered;
        _list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.PageDown when count > 0:
                _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 10);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.PageUp when count > 0:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 10);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                Finish(-1);
                e.Handled = true;
                break;
        }
    }

    private void Finish(int action)
    {
        if (_list.SelectedItem is QuickListItem item)
        {
            Result = new QuickListResult(item, action);
            Close();
        }
    }
}

/// <summary>Waits for a key combination (hotkey assignment).</summary>
public sealed class HotkeyCaptureDialog : DialogWindow
{
    private readonly TextBlock _shown = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Text = "…" };

    public HotkeyCaptureDialog(string commandName)
        : base("Assign shortcut", 420)
    {
        Body.Children.Add(Label($"Press a key combination for \"{commandName}\" (Esc: cancel):"));
        Body.Children.Add(_shown);
        AddButton("OK", () =>
        {
            if (Result is not null)
            {
                Close();
            }
        });
        AddButton("Cancel", () =>
        {
            Result = null;
            Close();
        });
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    public string? Result { get; private set; }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Result = null;
            Close();
            return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Result is not null)
        {
            Close();
            return;
        }
        if (HotkeyMap.Describe(e.Key, e.KeyModifiers) is { } text)
        {
            Result = text;
            _shown.Text = text;
        }
    }
}

/// <summary>Edits a button bar button / user command: the fields of the button bar editor in a dialog of their own.</summary>
public sealed class ButtonEditDialog : Window
{
    private readonly ButtonFieldsEditor _fields;

    public ButtonEditDialog(Core.Config.ButtonDefinition? existing, Func<Task<string?>> pickFile,
        Func<Core.Config.ButtonDefinition, int, Control?>? icon = null)
    {
        Title = existing is null ? "New button" : "Edit button";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _fields = new ButtonFieldsEditor(_ => pickFile(), icon) { Margin = new Thickness(20, 16) };
        _fields.Load(existing);

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        save.Click += (_, _) => Finish();
        cancel.Click += (_, _) => Close();
        var header = DialogLook.Header("MiHammer", Title, "What the button runs and how it looks");
        var footer = DialogLook.Footer("nc_ / uc_ commands, programs and folders; parameters: %P %N %T %S %L ...", cancel, save);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([header, footer, _fields]);
        Content = root;
        Opened += (_, _) => _fields.Command.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    public Core.Config.ButtonDefinition? Result { get; private set; }

    /// <summary>The fields (tests).</summary>
    public ButtonFieldsEditor Fields => _fields;

    public void Finish()
    {
        if (string.IsNullOrWhiteSpace(_fields.Command.Text))
        {
            return;
        }
        var button = new Core.Config.ButtonDefinition();
        _fields.Store(button);
        Result = button;
        Close();
    }
}
