using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Editor;

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Built-in F4 editor on top of AvaloniaEdit. Works on any provider (saves back through the VFS), keeps the encoding,
/// byte order mark and line endings of the file, and warns about unsaved changes and changes made by other programs.
/// </summary>
public sealed class EditorWindow : Window
{
    private readonly ToolContext _context;
    private readonly TextBlock _status;
    private readonly SearchPanel _search;
    private VfsEntry _entry;
    private Encoding _encoding = EncodingDetector.Utf8NoBom;
    private bool _bom;
    private string _newLine = Environment.NewLine;
    private bool _closingConfirmed;
    private bool _checkingExternal;

    public EditorWindow(VfsEntry entry, ToolContext context)
    {
        _entry = entry;
        _context = context;
        EncodingDetector.EnsureCodePages();
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Editor = new TextEditor
        {
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = new FontFamily(context.Settings.GetEditorFont()),
            FontSize = context.Settings.GetEditorFontSize(),
            Padding = new Thickness(4, 2),
        };
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextChanged += (_, _) => UpdateTitleAndStatus();
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextEditor.IsModifiedProperty)
            {
                UpdateTitleAndStatus();
            }
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateTitleAndStatus();
        _search = SearchPanel.Install(Editor);
        Lister.ThemeAwareColors.Attach(Editor);

        _status = new TextBlock { Margin = new Thickness(8, 3), FontSize = 12 };
        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(statusBar);
        root.Children.Add(Editor);
        Content = root;

        Opened += async (_, _) =>
        {
            await LoadAsync();
            Editor.TextArea.Focus();
        };
        Activated += async (_, _) => await CheckExternalChangeAsync();
        // Esc closes the editor wherever the focus is (the text area would keep it otherwise); an open search panel first
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            {
                return;
            }
            if (_search.IsOpened)
            {
                _search.Close();
            }
            else
            {
                Close(); // asks about unsaved changes
            }
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AskSaveOnClose = async () =>
        {
            var dialog = new ChoiceDialog<SaveChoice>("Unsaved changes", $"{_entry.Name} has changed. Save it?",
                [("Save", SaveChoice.Save), ("Don't save", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)], cancelValue: SaveChoice.Cancel);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        ShowMessage = async message =>
        {
            var dialog = new ChoiceDialog<bool>("Edit", message, [("OK", true)], cancelValue: false);
            await dialog.ShowDialog(this);
        };
    }

    public TextEditor Editor { get; }

    public VfsEntry Entry => _entry;

    public Encoding SaveEncoding => _encoding;

    public bool WritesBom => _bom;

    public string NewLine => _newLine;

    public bool IsModified => Editor.IsModified;

    /// <summary>Asked when closing with unsaved changes (replaceable in tests).</summary>
    public Func<Task<SaveChoice>> AskSaveOnClose { get; set; }

    /// <summary>Message sink (replaceable in tests).</summary>
    public Func<string, Task> ShowMessage { get; set; }

    public Task? Loading { get; private set; }

    public Task LoadAsync()
    {
        Loading = LoadCoreAsync();
        return Loading;
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            var provider = _context.Registry.Resolve(_entry.Path);
            byte[] data;
            await using (var stream = await provider.OpenReadAsync(_entry.Path, CancellationToken.None))
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            _encoding = EncodingDetector.Detect(data, out var bomLength, complete: true);
            _bom = bomLength > 0;
            if (_encoding.CodePage == 65001)
            {
                _encoding = EncodingDetector.Utf8NoBom;
            }
            var text = _encoding.GetString(data, bomLength, data.Length - bomLength);
            _newLine = EncodingDetector.DetectNewLine(text);
            Editor.Document = new TextDocument(text);
            Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension("." + _entry.Extension);
            Editor.IsModified = false;
            Editor.Document.UndoStack.ClearAll();
            if (await provider.GetEntryAsync(_entry.Path, CancellationToken.None) is { } fresh)
            {
                _entry = fresh;
            }
            UpdateTitleAndStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("Could not read file:\n" + ex.Message);
            _closingConfirmed = true;
            Close();
        }
    }

    /// <summary>Writes the text back with the current encoding, BOM and line endings.</summary>
    public async Task<bool> SaveAsync()
    {
        var text = Editor.Text;
        byte[] bytes;
        try
        {
            var strict = Encoding.GetEncoding(_encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            var body = strict.GetBytes(text);
            var preamble = _bom ? Preamble(_encoding) : [];
            bytes = [.. preamble, .. body];
        }
        catch (EncoderFallbackException ex)
        {
            await ShowMessage($"The text contains characters that cannot be represented in {_encoding.WebName} encoding (position {ex.Index}).\nSelect UTF-8 from the Encoding menu.");
            return false;
        }
        try
        {
            var provider = _context.Registry.Resolve(_entry.Path);
            await using (var stream = await provider.OpenWriteAsync(_entry.Path, overwrite: true, CancellationToken.None))
            {
                await stream.WriteAsync(bytes);
            }
            if (await provider.GetEntryAsync(_entry.Path, CancellationToken.None) is { } fresh)
            {
                _entry = fresh;
            }
            Editor.IsModified = false;
            UpdateTitleAndStatus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("Could not save:\n" + ex.Message);
            return false;
        }
    }

    private static byte[] Preamble(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        _ => [],
    };

    public void SetEncoding(Encoding encoding, bool? bom = null)
    {
        _encoding = encoding;
        _bom = bom ?? (encoding.CodePage is 1200 or 1201 || (_bom && encoding.CodePage == 65001));
        Editor.IsModified = true;
        UpdateTitleAndStatus();
    }

    /// <summary>Converts all line endings of the document.</summary>
    public void SetNewLine(string newLine)
    {
        _newLine = newLine;
        var doc = Editor.Document;
        var converted = doc.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newLine, StringComparison.Ordinal);
        if (converted != doc.Text)
        {
            doc.Text = converted;
        }
        Editor.IsModified = true;
        UpdateTitleAndStatus();
    }

    private async Task CheckExternalChangeAsync()
    {
        if (_checkingExternal || Loading is null || !Loading.IsCompleted)
        {
            return;
        }
        _checkingExternal = true;
        try
        {
            var fresh = await _context.Registry.Resolve(_entry.Path).GetEntryAsync(_entry.Path, CancellationToken.None);
            if (fresh is null || (fresh.LastWriteTime == _entry.LastWriteTime && fresh.Size == _entry.Size))
            {
                return;
            }
            _entry = fresh;
            if (!Editor.IsModified)
            {
                await LoadAsync();
                return;
            }
            var dialog = new ChoiceDialog<bool>("File changed", $"{_entry.Name} was changed by another program.\nLoad the version on disk? (Your changes will be lost)",
                [("Reload", true), ("Keep mine", false)], cancelValue: false, defaultIndex: 1);
            await dialog.ShowDialog(this);
            if (dialog.Result)
            {
                await LoadAsync();
            }
        }
        finally
        {
            _checkingExternal = false;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || !Editor.IsModified)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var choice = await AskSaveOnClose();
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAsync()))
            {
                return;
            }
            _closingConfirmed = true;
            Close();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.S when ctrl:
            case Key.F2 when e.KeyModifiers == KeyModifiers.None:
                _ = SaveAsync();
                e.Handled = true;
                break;
            case Key.H when ctrl:
                _search.IsReplaceMode = true;
                _search.Open();
                e.Handled = true;
                break;
            case Key.G when ctrl:
                _ = GoToLineAsync();
                e.Handled = true;
                break;
            case Key.F3 when e.KeyModifiers == KeyModifiers.None:
                _search.FindNext();
                e.Handled = true;
                break;
            case Key.F3 when e.KeyModifiers == KeyModifiers.Shift:
                _search.FindPrevious();
                e.Handled = true;
                break;
        }
    }

    private async Task GoToLineAsync()
    {
        var dialog = new InputDialog("Go to line", $"Line (1-{Editor.Document.LineCount}):", Editor.TextArea.Caret.Line.ToString(CultureInfo.CurrentCulture), false);
        await dialog.ShowDialog(this);
        if (int.TryParse(dialog.Result, NumberStyles.Integer, CultureInfo.CurrentCulture, out var line))
        {
            line = Math.Clamp(line, 1, Editor.Document.LineCount);
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = 1;
            Editor.ScrollToLine(line);
        }
        Editor.TextArea.Focus();
    }

    /// <summary>The icon of an editor menu item (a key of <see cref="Services.MenuIcons"/>); items without one stay plain.</summary>
    private static readonly Dictionary<string, string> s_menuIcons = new(StringComparer.Ordinal)
    {
        ["Save"] = "editor.save",
        ["Reload from disk"] = "editor.reload",
        ["Close"] = "editor.close",
        ["Undo"] = "editor.undo",
        ["Redo"] = "editor.redo",
        ["Find"] = "editor.find",
        ["Replace"] = "editor.replace",
        ["Find next"] = "editor.findnext",
        ["Go to line..."] = "editor.gotoline",
        ["Word wrap"] = "editor.wordwrap",
        ["Line numbers"] = "editor.linenumbers",
        ["Font..."] = "editor.font",
        ["Increase font size"] = "editor.fontbigger",
        ["Decrease font size"] = "editor.fontsmaller",
        ["Write BOM / no BOM"] = "editor.bom",
        ["Reload the file from disk with this encoding..."] = "editor.reinterpret",
    };

    private Menu BuildMenu()
    {
        var icons = _context.Settings.ShowMenuIcons; // Configuration > Icons > icons in the menus
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (icons && s_menuIcons.TryGetValue(header, out var icon))
            {
                item.Icon = Services.MenuIcons.Create(icon);
            }
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }
            item.Click += (_, _) => action();
            return item;
        }
        var file = new MenuItem { Header = "_Files" };
        file.Items.Add(Item("Save", () => _ = SaveAsync(), "Ctrl+S"));
        file.Items.Add(Item("Reload from disk", () => _ = LoadAsync()));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Close", Close, "Escape"));

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(Item("Undo", () => Editor.Undo(), "Ctrl+Z"));
        edit.Items.Add(Item("Redo", () => Editor.Redo(), "Ctrl+Y"));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Find", () => _search.Open(), "Ctrl+F"));
        edit.Items.Add(Item("Replace", () =>
        {
            _search.IsReplaceMode = true;
            _search.Open();
        }, "Ctrl+H"));
        edit.Items.Add(Item("Find next", () => _search.FindNext(), "F3"));
        edit.Items.Add(Item("Go to line...", () => _ = GoToLineAsync(), "Ctrl+G"));

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("Word wrap", () => Editor.WordWrap = !Editor.WordWrap));
        view.Items.Add(Item("Line numbers", () => Editor.ShowLineNumbers = !Editor.ShowLineNumbers));
        view.Items.Add(new Separator());
        view.Items.Add(Item("Font...", () => _ = ChooseFontAsync()));
        view.Items.Add(Item("Increase font size", () => Editor.FontSize = Math.Min(48, Editor.FontSize + 1), "Ctrl+Add"));
        view.Items.Add(Item("Decrease font size", () => Editor.FontSize = Math.Max(6, Editor.FontSize - 1), "Ctrl+Subtract"));

        var encoding = new MenuItem { Header = "_Encoding" };
        foreach (var choice in EncodingDetector.Choices)
        {
            encoding.Items.Add(Item(choice.Name, () => SetEncoding(choice.Encoding)));
        }
        encoding.Items.Add(new Separator());
        encoding.Items.Add(Item("Write BOM / no BOM", () => SetEncoding(_encoding, !_bom)));
        encoding.Items.Add(Item("Reload the file from disk with this encoding...", () => _ = ReinterpretAsync()));
        encoding.Items.Add(new Separator());
        encoding.Items.Add(Item("Line ending: Windows (CRLF)", () => SetNewLine("\r\n")));
        encoding.Items.Add(Item("Line ending: Unix (LF)", () => SetNewLine("\n")));
        encoding.Items.Add(Item("Line ending: old Mac (CR)", () => SetNewLine("\r")));

        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        menu.Items.Add(encoding);
        return menu;
    }

    /// <summary>View > Font...: the editor's font and size, kept for the next files (Configuration > Font > Editor font).</summary>
    private async Task ChooseFontAsync()
    {
        var dialog = new FontDialog(_context.Settings.GetEditorFont(), Editor.FontSize);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } chosen)
        {
            ApplyFont(chosen.Family, chosen.Size);
        }
    }

    /// <summary>Uses a font in the editor and keeps it in the settings.</summary>
    public void ApplyFont(string family, double size)
    {
        Editor.FontFamily = new FontFamily(family);
        Editor.FontSize = size;
        _context.Settings.EditorFont = family;
        _context.Settings.EditorFontSize = size;
    }

    /// <summary>Re-decodes the file with a chosen encoding (wrongly detected legacy text).</summary>
    private async Task ReinterpretAsync()
    {
        var items = EncodingDetector.Choices.Select(c => new QuickListItem(c.Name, null, c)).ToList();
        var dialog = new QuickListDialog("Encoding", items, null);
        await dialog.ShowDialog(this);
        if (dialog.Result?.Item.Value is not EncodingChoice choice)
        {
            return;
        }
        await using var stream = await _context.Registry.Resolve(_entry.Path).OpenReadAsync(_entry.Path, CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var data = ms.ToArray();
        var bom = EncodingDetector.BomLength(data, choice.Encoding);
        Editor.Document.Text = choice.Encoding.GetString(data, bom, data.Length - bom);
        _encoding = choice.Encoding;
        _bom = bom > 0;
        Editor.IsModified = false;
        UpdateTitleAndStatus();
    }

    private void UpdateTitleAndStatus()
    {
        Title = (Editor.IsModified ? "● " : string.Empty) + _entry.Name + " — Edit";
        var newLine = _newLine switch
        {
            "\r\n" => "CRLF",
            "\n" => "LF",
            _ => "CR",
        };
        var caret = Editor.TextArea.Caret;
        _status.Text = string.Format(CultureInfo.CurrentCulture, "Ln {0}, Col {1}   {2}{3}   {4}   {5}{6}",
            caret.Line, caret.Column, _encoding.WebName.ToUpperInvariant(), _bom ? " (BOM)" : string.Empty, newLine,
            _entry.Path.ToDisplayString(), Editor.IsModified ? "   (modified)" : string.Empty);
    }
}
