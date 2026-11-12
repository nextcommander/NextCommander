using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.Core.Config;

namespace NextCommander.App.Services;

/// <summary>
/// Path completion for a text box (Configuration > Operation > Auto-complete paths): while a local path is typed, the
/// folders (and, for the command line, files) that start with the typed name are listed below the box. Up / Down choose,
/// Enter / Tab take the choice, Esc closes the list. "Add the suggested name" also writes the first suggestion into the
/// box, selected, so that typing on replaces it.
/// </summary>
public sealed class PathAutoComplete
{
    private readonly TextBox _box;
    private readonly bool _files;
    private readonly Func<string?> _baseFolder;
    private readonly bool _lastWordOnly;
    private readonly Func<AppSettings?> _settings;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private CancellationTokenSource? _pending;
    private bool _applying;
    private string _lastText = string.Empty;

    /// <summary>A suggestion was chosen with ↑ / ↓ (then Enter takes it).</summary>
    private bool _chosen;

    private PathAutoComplete(TextBox box, bool files, bool lastWordOnly, Func<string?>? baseFolder, Func<AppSettings?>? settings)
    {
        _box = box;
        _settings = settings ?? (() => Settings);
        _files = files;
        _lastWordOnly = lastWordOnly;
        _baseFolder = baseFolder ?? (() => null);
        _list = new ListBox { MaxHeight = 260, MinWidth = 240, FontSize = 12, Classes = { "noloc" } };
        _popup = new Popup
        {
            Child = new Border
            {
                Child = _list,
                BorderThickness = new Avalonia.Thickness(1),
                [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
                [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush"),
            },
            PlacementTarget = box,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
        };
        // a click takes the suggestion without moving the focus out of the box (the path line closes when it loses it)
        _list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string item })
            {
                _list.SelectedItem = item;
                Accept();
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
            if (!_list.IsKeyboardFocusWithin && !_box.IsKeyboardFocusWithin)
            {
                Close();
            }
        });
    }

    /// <summary>
    /// Adds completion to <paramref name="box"/>. <paramref name="files"/>: files too, not only folders;
    /// <paramref name="lastWordOnly"/>: only the last word is a path (command line); <paramref name="baseFolder"/>: the
    /// folder relative paths start in.
    /// </summary>
    public static PathAutoComplete Attach(TextBox box, bool files = false, bool lastWordOnly = false, Func<string?>? baseFolder = null,
        Func<AppSettings?>? settings = null)
    {
        var completion = new PathAutoComplete(box, files, lastWordOnly, baseFolder, settings);
        void Place(Panel panel)
        {
            panel.Children.Add(completion._popup); // a popup needs a place in the tree; it takes no room
            // keys on the parent: they come before the box's own handlers (Enter must take a suggestion, not run)
            panel.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (ReferenceEquals(e.Source, box) || (e.Source as Avalonia.Visual)?.FindAncestorOfType<TextBox>() == box)
                {
                    completion.OnKeyDown(box, e);
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

    /// <summary>
    /// The settings in effect when a box has no own source (dialogs); set when the window starts.
    /// </summary>
    public static AppSettings? Settings { get; set; }

    public bool IsOpen => _popup.IsOpen;

    public IReadOnlyList<string> Suggestions => _list.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>The token being completed: (start index, text).</summary>
    private (int Start, string Token) Token(string text)
    {
        if (!_lastWordOnly)
        {
            return (0, text);
        }
        var start = text.LastIndexOf(' ') + 1;
        return (start, text[start..]);
    }

    private void OnTextChanged()
    {
        var text = _box.Text ?? string.Empty;
        var deleting = text.Length < _lastText.Length;
        _lastText = text;
        if (_applying)
        {
            return;
        }
        _pending?.Cancel();
        if (_settings() is not { AutoCompletePaths: true } settings || !_box.IsKeyboardFocusWithin)
        {
            Close();
            return;
        }
        var (start, token) = Token(text);
        var cts = new CancellationTokenSource();
        _pending = cts;
        var folder = _baseFolder();
        _ = Task.Run(() => SuggestionsFor(token, folder, _files), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion || (_box.Text ?? string.Empty) != text)
            {
                return;
            }
            Show(t.Result, start, token, appendInline: settings.AutoCompleteAppend && !deleting);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Folders (and files) of the typed folder whose names start with the typed name; full paths as typed.</summary>
    public static IReadOnlyList<string> SuggestionsFor(string token, string? baseFolder, bool files)
    {
        var unquoted = token.Trim('"');
        var separator = unquoted.LastIndexOfAny(['\\', '/']);
        if (separator < 0 || unquoted.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || unquoted.Contains("://", StringComparison.Ordinal))
        {
            return [];
        }
        var folderPart = unquoted[..(separator + 1)];
        var namePart = unquoted[(separator + 1)..];
        string folder;
        try
        {
            folder = Path.IsPathRooted(folderPart) ? folderPart : baseFolder is null ? string.Empty : Path.GetFullPath(Path.Combine(baseFolder, folderPart));
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                return [];
            }
            var directory = new DirectoryInfo(folder);
            var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.System };
            IEnumerable<FileSystemInfo> items = files
                ? directory.EnumerateFileSystemInfos(namePart + "*", options)
                : directory.EnumerateDirectories(namePart + "*", options);
            return items
                .OrderBy(i => i is FileInfo)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(200)
                .Select(i => folderPart + i.Name + (i is DirectoryInfo ? folderPart[^1].ToString() : string.Empty))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private void Show(IReadOnlyList<string> suggestions, int start, string token, bool appendInline)
    {
        // the typed folder itself ("C:\a" → "C:\a\") is not a suggestion: Enter goes there
        var matching = suggestions.Where(s => !string.Equals(s, token, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.TrimEnd('\\', '/'), token.Trim('"'), StringComparison.OrdinalIgnoreCase)).ToList();
        _chosen = false;
        if (matching.Count == 0 || !_box.IsKeyboardFocusWithin)
        {
            Close();
            return;
        }
        _list.ItemsSource = matching;
        _list.SelectedIndex = 0;
        _popup.IsOpen = true;
        if (appendInline && matching[0].StartsWith(token, StringComparison.OrdinalIgnoreCase) && matching[0].Length > token.Length)
        {
            // the first suggestion written into the box, the added part selected
            var text = _box.Text ?? string.Empty;
            _applying = true;
            _box.Text = text[..start] + token + matching[0][token.Length..];
            // what the user typed (without the added part): typing over the selection is not deleting
            _lastText = text[..start] + token;
            _applying = false;
            _box.SelectionStart = start + token.Length;
            _box.SelectionEnd = _box.Text.Length;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.ItemCount - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                _chosen = true;
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                _list.ScrollIntoView(_list.SelectedIndex);
                _chosen = true;
                e.Handled = true;
                break;
            // Tab takes the suggestion; Enter only one chosen with the arrows, otherwise it goes on with what is typed
            case Key.Tab when _list.SelectedItem is string:
            case Key.Enter when _chosen && _list.SelectedItem is string:
                Accept();
                e.Handled = true;
                break;
            case Key.Enter:
                Close();
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Puts the chosen suggestion in place of the token; the caret goes to the end.</summary>
    public void Accept()
    {
        if (_list.SelectedItem is not string choice)
        {
            return;
        }
        var text = _box.Text ?? string.Empty;
        var (start, _) = Token(text);
        _applying = true;
        _box.Text = text[..start] + choice;
        _lastText = _box.Text;
        _applying = false;
        _box.CaretIndex = _box.Text.Length;
        _box.SelectionStart = _box.SelectionEnd = _box.Text.Length;
        Close();
        _box.Focus();
        OnTextChanged(); // the next level (a folder was chosen)
    }

    public void Close()
    {
        _pending?.Cancel();
        _popup.IsOpen = false;
    }
}
