using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NextCommander.App.Services;

/// <summary>One entry of the completion list: what it shows, a description beside it, and the box's text when it is taken.</summary>
public sealed record Suggestion(string Text, string? Detail, string Result);

/// <summary>
/// Completion of a text box while typing (the command line; the user's template, 22 November 2026): the entries for the
/// text are listed above the box: the last 20 command lines (those that start with the text, then those that hold it)
/// or, for a command name being typed (nc_ ...), the commands with their description. ↑ / ↓ choose, Tab takes the
/// choice into the box, Enter runs the chosen one (the box's own Enter runs whatever the box then holds), Esc closes the
/// list. A last word with a path separator is left to the path completion.
/// </summary>
public sealed class HistoryAutoComplete
{
    /// <summary>How many history entries are shown at most.</summary>
    internal const int MaxShown = 8;

    /// <summary>How many commands are shown at most.</summary>
    internal const int MaxCommands = 12;

    private readonly TextBox _box;
    private readonly Func<string, IReadOnlyList<Suggestion>> _source;
    private readonly Func<bool> _blocked;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _applying;

    private HistoryAutoComplete(TextBox box, Func<string, IReadOnlyList<Suggestion>> source, Func<bool>? blocked)
    {
        _box = box;
        _source = source;
        _blocked = blocked ?? (() => false);
        _list = new ListBox { MaxHeight = 320, MinWidth = 260, Classes = { "noloc", "drivelist" } };
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
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: Suggestion item })
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

    /// <summary>History completion of <paramref name="box"/> from <paramref name="items"/>; <paramref name="blocked"/>: another list is open (path completion).</summary>
    public static HistoryAutoComplete Attach(TextBox box, Func<IReadOnlyList<string>> items, Func<bool>? blocked = null) =>
        Attach(box, text => [.. Matches(text, items()).Take(MaxShown).Select(i => new Suggestion(i, null, i))], blocked);

    /// <summary>Completion of <paramref name="box"/> from <paramref name="source"/> (the entries for the box's text).</summary>
    public static HistoryAutoComplete Attach(TextBox box, Func<string, IReadOnlyList<Suggestion>> source, Func<bool>? blocked = null)
    {
        var completion = new HistoryAutoComplete(box, source, blocked);
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

    /// <summary>The entries shown now (tests): their texts.</summary>
    public IReadOnlyList<string> Suggestions => [.. Shown.Select(s => s.Text)];

    /// <summary>The entries shown now with their descriptions (tests).</summary>
    public IReadOnlyList<Suggestion> Shown => _list.ItemsSource as IReadOnlyList<Suggestion> ?? [];

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

    /// <summary>
    /// The commands for the command name being typed: the last part of a chain (after a comma), before any parameter,
    /// starting with nc_ / uc_ (or cm_ / em_ of earlier versions). Names that start with it come first, then names and
    /// descriptions (in the language of the interface) that hold the rest of it; the box keeps the chain before it.
    /// </summary>
    public static IReadOnlyList<Suggestion> CommandMatches(string text, IEnumerable<(string Name, string Description)> commands)
    {
        var start = text.LastIndexOf(',') + 1;
        var part = text[start..].TrimStart();
        var before = text[..(text.Length - part.Length)];
        if (part.Length < 3 || part.Contains(' ', StringComparison.Ordinal)
            || !(part.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || part.StartsWith("uc_", StringComparison.OrdinalIgnoreCase)
                 || part.StartsWith("cm_", StringComparison.OrdinalIgnoreCase) || part.StartsWith("em_", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }
        var rest = part[3..];
        var list = commands.Where(c => !string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)).ToList();
        var prefix = list.Where(c => c.Name.StartsWith(part, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var holding = list.Where(c => rest.Length > 0 && !c.Name.StartsWith(part, StringComparison.OrdinalIgnoreCase)
                && (c.Name.Contains(rest, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(rest, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        return [.. prefix.Concat(holding).Take(MaxCommands).Select(c => new Suggestion(c.Name, c.Description, before + c.Name))];
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
        var matches = _source(text);
        if (matches.Count == 0)
        {
            Close();
            return;
        }
        _list.ItemsSource = matches;
        _list.ItemTemplate = s_template;
        _list.SelectedIndex = -1; // nothing chosen until ↑ / ↓
        _popup.IsOpen = true;
    }

    /// <summary>An entry: its text and, for a command, its description beside it.</summary>
    private static readonly Avalonia.Controls.Templates.FuncDataTemplate<Suggestion> s_template = new((item, _) =>
    {
        var row = new DockPanel();
        if (item?.Detail is { Length: > 0 } detail)
        {
            var description = new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Margin = new Avalonia.Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340,
                [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcFaintBrush"),
            };
            DockPanel.SetDock(description, Dock.Right);
            row.Children.Add(description);
        }
        row.Children.Add(new TextBlock
        {
            Text = item?.Text,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.FontFamilyProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcMonoFont"),
        });
        return row;
    }, supportsRecycling: false);

    private void OnKeyDown(KeyEventArgs e)
    {
        if (!_popup.IsOpen || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return;
        }
        var count = _list.ItemCount;
        switch (e.Key)
        {
            // the list is above the box: ↑ goes into it from below, ↓ back towards the box
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                _list.SelectedIndex = _list.SelectedIndex < 0 ? count - 1 : Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
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
                Take(_list.SelectedItem as Suggestion ?? (Shown.Count > 0 ? Shown[0] : null));
                e.Handled = true;
                break;
            case Key.Enter:
                // the chosen entry goes into the box; the box's Enter then runs it
                if (_list.SelectedItem is Suggestion chosen)
                {
                    Set(chosen.Result);
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
    private void Take(Suggestion? entry)
    {
        if (entry is not null)
        {
            Set(entry.Result);
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
