using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NextCommander.App.Services;

/// <summary>
/// Completion of a text box from earlier entries (the command line's last 20 command lines; the user's template, 22
/// November 2026): while typing, the entries that start with the text (then those that hold it) are listed above the
/// box. ↑ / ↓ choose, Tab takes the choice into the box, Enter runs the chosen one (the box's own Enter runs whatever
/// the box then holds), Esc closes the list. A last word with a path separator is left to the path completion.
/// </summary>
public sealed class HistoryAutoComplete
{
    /// <summary>How many entries are shown at most.</summary>
    internal const int MaxShown = 8;

    private readonly TextBox _box;
    private readonly Func<IReadOnlyList<string>> _items;
    private readonly Func<bool> _blocked;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _applying;

    private HistoryAutoComplete(TextBox box, Func<IReadOnlyList<string>> items, Func<bool>? blocked)
    {
        _box = box;
        _items = items;
        _blocked = blocked ?? (() => false);
        _list = new ListBox { MaxHeight = 280, MinWidth = 260, Classes = { "noloc", "drivelist" } };
        _popup = new Popup
        {
            Child = new Border { Child = _list, Classes = { "mwpopup" }, Margin = new Avalonia.Thickness(0) },
            PlacementTarget = box,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
        };
        // a click takes the entry into the box, without leaving it
        _list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string item })
            {
                Take(item);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTextChanged();
            }
        };
        box.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_box.IsKeyboardFocusWithin)
            {
                Close();
            }
        });
    }

    /// <summary>Adds the completion to <paramref name="box"/>; <paramref name="blocked"/>: another list is open (path completion).</summary>
    public static HistoryAutoComplete Attach(TextBox box, Func<IReadOnlyList<string>> items, Func<bool>? blocked = null)
    {
        var completion = new HistoryAutoComplete(box, items, blocked);
        void Place(Panel panel)
        {
            panel.Children.Add(completion._popup);
            // the keys before the box's own (Enter, ↑ / ↓ of the command line)
            panel.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (ReferenceEquals(e.Source, box) || (e.Source as Avalonia.Visual)?.FindAncestorOfType<TextBox>() == box)
                {
                    completion.OnKeyDown(e);
                }
            }, RoutingStrategies.Tunnel);
        }
        if (box.Parent is Panel parent)
        {
            Place(parent);
        }
        else
        {
            box.AttachedToVisualTree += (_, _) =>
            {
                if (completion._popup.Parent is null && box.Parent is Panel p)
                {
                    Place(p);
                }
            };
        }
        return completion;
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The entries shown now (tests).</summary>
    public IReadOnlyList<string> Suggestions => _list.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>The entries for a text: those that start with it first, then those that hold it; not the text itself.</summary>
    public static IReadOnlyList<string> Matches(string text, IEnumerable<string> items)
    {
        var typed = text.Trim();
        if (typed.Length == 0)
        {
            return [];
        }
        var distinct = items.Where(i => !string.IsNullOrWhiteSpace(i) && !string.Equals(i.Trim(), typed, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return
        [
            .. distinct.Where(i => i.StartsWith(typed, StringComparison.OrdinalIgnoreCase)),
            .. distinct.Where(i => !i.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && i.Contains(typed, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private void OnTextChanged()
    {
        if (_applying)
        {
            return;
        }
        var text = _box.Text ?? string.Empty;
        var lastWord = text[(text.LastIndexOf(' ') + 1)..];
        if (!_box.IsKeyboardFocusWithin || _blocked() || lastWord.IndexOfAny(['\\', '/']) >= 0)
        {
            Close();
            return;
        }
        var matches = Matches(text, _items()).Take(MaxShown).ToList();
        if (matches.Count == 0)
        {
            Close();
            return;
        }
        _list.ItemsSource = matches;
        _list.SelectedIndex = -1; // nothing chosen until ↑ / ↓
        _popup.IsOpen = true;
    }

    private void OnKeyDown(KeyEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            return;
        }
        var count = _list.ItemCount;
        switch (e.Key)
        {
            // the list is above the box: ↑ goes into it from below, ↓ back towards the box
            case Key.Up:
                _list.SelectedIndex = _list.SelectedIndex < 0 ? count - 1 : Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Down:
                if (_list.SelectedIndex >= count - 1)
                {
                    _list.SelectedIndex = -1;
                }
                else
                {
                    _list.SelectedIndex++;
                    _list.ScrollIntoView(_list.SelectedIndex);
                }
                e.Handled = true;
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                Take(_list.SelectedItem as string ?? (Suggestions.Count > 0 ? Suggestions[0] : null));
                e.Handled = true;
                break;
            case Key.Enter:
                // the chosen entry goes into the box; the box's Enter then runs it
                if (_list.SelectedItem is string chosen)
                {
                    Set(chosen);
                }
                Close();
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Puts an entry into the box (caret at its end) and closes the list.</summary>
    private void Take(string? entry)
    {
        if (entry is not null)
        {
            Set(entry);
        }
        Close();
        _box.Focus();
    }

    private void Set(string entry)
    {
        _applying = true;
        _box.Text = entry;
        _applying = false;
        _box.CaretIndex = entry.Length;
        _box.SelectionStart = _box.SelectionEnd = entry.Length;
    }

    public void Close() => _popup.IsOpen = false;
}
