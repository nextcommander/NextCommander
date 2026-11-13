using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Chooses a command (nc_ and the user's uc_ commands) from a list with its icon and description instead of typing its
/// name: the button bar, user commands, the hotlist, view modes, internal associations and the Start menu use it.
/// </summary>
public sealed class CommandPickerDialog : Window
{
    /// <summary>A command of the list.</summary>
    public sealed record Entry(string Name, string Description);

    /// <summary>The commands to choose from (set by the main window's view model).</summary>
    public static Func<IReadOnlyList<Entry>>? Source { get; set; }

    private readonly List<Entry> _all;
    private readonly TextBox _search = new() { PlaceholderText = "Search by name or description" };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _count = new() { Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    public CommandPickerDialog(string? current = null)
    {
        Title = "Choose a command";
        Width = 640;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = [.. (Source?.Invoke() ?? []).OrderBy(e => e.Name.StartsWith("uc_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        _list.ItemTemplate = new FuncDataTemplate<Entry>((entry, _) => Line(entry));
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) =>
        {
            // the arrows move in the list while the search keeps the focus
            if (e.Key is Key.Down or Key.Up && _list.ItemCount > 0)
            {
                _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.ItemCount - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
            }
        };
        _list.DoubleTapped += (_, _) => Choose();

        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "dialog" } };
        ok.Click += (_, _) => Choose();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Classes = { "buttonrow" } };
        buttons.Children.AddRange([ok, cancel]);
        var bottom = new DockPanel { Margin = new Thickness(16, 6, 16, 14) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.AddRange([buttons, _count]);

        var root = new DockPanel();
        _search.Margin = new Thickness(16, 14, 16, 8);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _list.Margin = new Thickness(16, 0);
        root.Children.AddRange([_search, bottom, _list]);
        Content = root;

        Filter();
        // the command already in the field is selected (its name without parameters)
        var name = (current ?? string.Empty).Trim().TrimStart('*').Split(' ', 2)[0];
        if (Shown.ToList().FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index)
        {
            _list.SelectedIndex = index;
            Opened += (_, _) => _list.ScrollIntoView(index);
        }
        Opened += (_, _) => _search.Focus();
    }

    /// <summary>The command chosen; null when cancelled.</summary>
    public string? Result { get; private set; }

    /// <summary>The commands shown with the search (tests).</summary>
    public IReadOnlyList<Entry> Shown => _list.ItemsSource as IReadOnlyList<Entry> ?? [];

    public string SearchText
    {
        get => _search.Text ?? string.Empty;
        set
        {
            _search.Text = value;
            Filter();
        }
    }

    public int SelectedIndex
    {
        get => _list.SelectedIndex;
        set => _list.SelectedIndex = value;
    }

    /// <summary>Opens the list over <paramref name="owner"/> and returns the command chosen (null: cancelled).</summary>
    public static async Task<string?> PickAsync(Window owner, string? current = null)
    {
        var dialog = new CommandPickerDialog(current);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    /// <summary>A "Command..." button that puts the chosen command into <paramref name="box"/>.</summary>
    public static Button ButtonFor(TextBox box)
    {
        var button = new Button { Content = "Command..." };
        ToolTip.SetTip(button, "Choose a command from the list");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is Window owner && await PickAsync(owner, box.Text) is { } command)
            {
                box.Text = command;
            }
        };
        return button;
    }

    private void Filter()
    {
        var text = SearchText.Trim();
        var shown = text.Length == 0 ? _all
            : _all.Where(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || e.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || Localizer.T(e.Description).Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _list.ItemsSource = shown;
        if (shown.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        _count.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} commands", shown.Count);
    }

    private static Control Line(Entry? entry)
    {
        if (entry is null)
        {
            return new Panel();
        }
        var icon = new Border { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        if (Services.MenuIcons.Create(entry.Name) is { } glyph)
        {
            icon.Child = new Viewbox { Child = glyph };
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Description, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = entry.Name, FontSize = 11, Opacity = 0.6, Classes = { "noloc" } });
        var line = new DockPanel { Margin = new Thickness(0, 1) };
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.AddRange([icon, text]);
        return line;
    }

    /// <summary>Takes the selected command (Enter, double click, OK).</summary>
    public void Choose()
    {
        if (_list.SelectedItem is Entry entry)
        {
            Result = entry.Name;
            Close();
        }
    }
}
