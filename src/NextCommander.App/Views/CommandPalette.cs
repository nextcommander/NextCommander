using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>
/// The command palette (Ctrl+Shift+P, nc_CommandPalette; the user's request, 23 November 2026): a box at the top of the
/// window; what is typed finds commands, folders, Hotlist entries and saved connections (<see cref="CommandPaletteSource"/>).
/// ↑ / ↓ choose, Enter (or a click) runs the chosen one, Esc closes.
/// </summary>
public sealed class CommandPalette
{
    private readonly MainWindowViewModel _vm;
    private readonly Popup _popup;
    private readonly TextBox _box;
    private readonly ListBox _list;
    private IReadOnlyList<PaletteItem> _items = [];

    public CommandPalette(Panel host, MainWindowViewModel vm)
    {
        _vm = vm;
        _box = new TextBox
        {
            PlaceholderText = "Type a command, a folder or a connection",
            Classes = { "palettebox" },
            Margin = new Thickness(8, 8, 8, 6),
        };
        _list = new ListBox
        {
            MaxHeight = 420,
            Classes = { "noloc", "drivelist", "palettelist" },
            ItemTemplate = s_template,
            Margin = new Thickness(4, 0, 4, 6),
        };
        var hint = new TextBlock
        {
            Text = "↑ ↓ choose · Enter run · Esc close",
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 8),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush"),
        };
        DockPanel.SetDock(_box, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        _popup = new Popup
        {
            Child = new Border
            {
                Classes = { "mwpopup" },
                Width = 640,
                Child = new DockPanel { Children = { _box, hint, _list } },
            },
            PlacementTarget = host,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.Top,
            PlacementGravity = PopupGravity.Bottom,
            VerticalOffset = 64,
            IsLightDismissEnabled = true,
        };
        DockPanel.SetDock(_popup, Dock.Top);
        host.Children.Insert(0, _popup);
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Filter();
            }
        };
        _box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PaletteItem item })
            {
                e.Handled = true;
                _ = RunAsync(item);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _popup.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The palette closed (the window gives the focus back to the panel).</summary>
    public event EventHandler? Closed;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The text box (tests).</summary>
    public TextBox Box => _box;

    /// <summary>The entries shown now (tests).</summary>
    public IReadOnlyList<PaletteItem> Shown => _list.ItemsSource as IReadOnlyList<PaletteItem> ?? [];

    /// <summary>The entry chosen now (tests).</summary>
    public PaletteItem? Chosen => _list.SelectedItem as PaletteItem;

    public void Open(string? text = null)
    {
        _items = CommandPaletteSource.Items(_vm);
        _box.Text = text ?? string.Empty;
        Filter();
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            _box.Focus();
            _box.CaretIndex = _box.Text?.Length ?? 0;
        });
    }

    public void Close() => _popup.IsOpen = false;

    private void Filter()
    {
        var found = CommandPaletteSource.Find(_box.Text, _items, _vm.Settings.CommandPaletteRecent);
        _list.ItemsSource = found;
        _list.SelectedIndex = found.Count > 0 ? 0 : -1;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _list.SelectedIndex = (_list.SelectedIndex + 1) % count;
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _list.SelectedIndex = _list.SelectedIndex <= 0 ? count - 1 : _list.SelectedIndex - 1;
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
                e.Handled = true;
                if (_list.SelectedItem is PaletteItem chosen)
                {
                    _ = RunAsync(chosen);
                }
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    /// <summary>Closes the palette, then runs the entry (remembered as recently used).</summary>
    public async Task RunAsync(PaletteItem item)
    {
        CommandPaletteSource.Remember(_vm.Settings.CommandPaletteRecent, item.Id);
        Close();
        await Task.Yield(); // the focus back in the panel first: the command works on the active panel
        await item.Run();
    }

    /// <summary>A row: icon, title, and on the right the shortcut or what the entry is.</summary>
    private static readonly FuncDataTemplate<PaletteItem> s_template = new((item, _) =>
    {
        var row = new DockPanel { Margin = new Thickness(2, 1) };
        if (item is null)
        {
            return row;
        }
        Control icon = item.Protocol is { } protocol ? Services.Icons.BrandIcons.Create(protocol)
            : (item.IconKey is { } key ? MenuIcons.Create(key) : null) ?? new Panel();
        icon.Width = 16;
        icon.Height = 16;
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (item.Gesture is { Length: > 0 } gesture)
        {
            right.Children.Add(new Border
            {
                Classes = { "palettekey" },
                Padding = new Thickness(5, 0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                [!Border.BorderBrushProperty] = new DynamicResourceExtension("NcMwStrokeBrush"),
                Child = new TextBlock { Text = gesture, FontSize = 11, Classes = { "noloc" } },
            });
        }
        var kind = new TextBlock
        {
            Text = Core.Localization.Localizer.T(item.Kind switch
            {
                PaletteKind.Folder => "Folder",
                PaletteKind.Hotlist => "Hotlist",
                PaletteKind.Connection => "Connection",
                _ => "Command",
            }),
            Classes = { "noloc" }, // (translated here: the list's texts are names)
            FontSize = 11,
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush"),
        };
        right.Children.Add(kind);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
        texts.Children.Add(new TextBlock
        {
            Text = item.Detail,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "noloc" },
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("NcFaintBrush"),
        });
        row.Children.Add(texts);
        return row;
    }, supportsRecycling: false);
}
