using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Editor;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// The two files of the compare window (the plan of 25 November 2026, phase 2): their lines as they are now and as they
/// were saved, their encoding, byte order mark and line ends (kept when saving, changed from the menu of each side),
/// saving, choosing another file, reloading a file another program changed, asking about unsaved changes on closing.
/// </summary>
public sealed partial class CompareWindow
{
    /// <summary>One side: its file, its lines now and as saved, how it is written.</summary>
    private sealed class Side(VfsEntry entry)
    {
        public VfsEntry Entry { get; set; } = entry;

        public List<string> Lines { get; set; } = [];

        public List<string> SavedLines { get; set; } = [];

        public Encoding Encoding { get; set; } = EncodingDetector.Utf8NoBom;

        public bool Bom { get; set; }

        public string NewLine { get; set; } = "\n";

        public bool EndsWithNewline { get; set; } = true;

        /// <summary>The encoding or the line ends were changed from the menu (the file differs even with the same lines).</summary>
        public bool FormatChanged { get; set; }

        public bool Loaded { get; set; }

        /// <summary>The backup of the file as it was (name.bak) was written at the first save.</summary>
        public bool BackedUp { get; set; }

        public bool ReadOnly { get; set; }

        /// <summary>The time of the file after this window saved it (its own change is not "changed by another program").</summary>
        public DateTime? WrittenAt { get; set; }

        public bool Dirty => Loaded && (FormatChanged || !Lines.SequenceEqual(SavedLines, StringComparer.Ordinal));

        public string FormatText =>
            (EncodingDetector.Choices.FirstOrDefault(c => c.Encoding.CodePage == Encoding.CodePage)?.Name ?? Encoding.WebName.ToUpperInvariant())
            + (Bom && Encoding.CodePage == 65001 ? " BOM" : string.Empty) + " · " + (NewLine == "\r\n" ? "CRLF" : NewLine == "\r" ? "CR" : "LF");

        public void Load(byte[] data, bool writable)
        {
            var encoding = EncodingDetector.Detect(data, out var bom, complete: true);
            var text = encoding.GetString(data, bom, data.Length - bom);
            Encoding = encoding;
            Bom = bom > 0;
            NewLine = text.Length == 0 ? Environment.NewLine : EncodingDetector.DetectNewLine(text);
            EndsWithNewline = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r');
            Lines = Split(text);
            SavedLines = [.. Lines];
            FormatChanged = false;
            Loaded = true;
            ReadOnly = !writable;
        }

        private static List<string> Split(string text)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1); // the final line break does not start a new line
            }
            return lines;
        }

        /// <summary>The file's bytes: the lines with its line ends, its encoding and byte order mark (an error for a character the encoding has not).</summary>
        public byte[] Bytes()
        {
            var text = Lines.Count == 0 ? string.Empty : string.Join(NewLine, Lines) + (EndsWithNewline ? NewLine : string.Empty);
            var strict = Encoding.GetEncoding(Encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            byte[] preamble = !Bom ? [] : Encoding.CodePage switch
            {
                65001 => [0xEF, 0xBB, 0xBF],
                1200 => [0xFF, 0xFE],
                1201 => [0xFE, 0xFF],
                _ => [],
            };
            return [.. preamble, .. strict.GetBytes(text)];
        }
    }

    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _closingConfirmed;
    private bool _reloading;

    /// <summary>Asked when closing with unsaved changes (replaceable in tests).</summary>
    public Func<string, Task<SaveChoice>> AskSaveOnClose { get; set; } = null!;

    /// <summary>Asked when a file with unsaved changes was changed by another program: reload it (replaceable in tests).</summary>
    public Func<string, Task<bool>> AskReload { get; set; } = null!;

    /// <summary>Message sink (replaceable in tests).</summary>
    public Func<string, Task> ShowMessage { get; set; } = null!;

    private void WireDialogs()
    {
        AskSaveOnClose = async names =>
        {
            var dialog = new ChoiceDialog<SaveChoice>("Unsaved changes", $"{names} changed. Save the changes?",
                [("Save", SaveChoice.Save), ("Don't save", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)], cancelValue: SaveChoice.Cancel);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        AskReload = async name =>
        {
            var dialog = new ChoiceDialog<bool>("Compare", $"{name} was changed by another program. Load it again? The changes made here are lost.",
                [("Load again", true), ("Keep my changes", false)], cancelValue: false);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        ShowMessage = async message =>
        {
            var dialog = new ChoiceDialog<bool>("Compare", message, [("OK", true)], cancelValue: false);
            await dialog.ShowDialog(this);
        };
    }

    private bool WritableOf(VfsEntry entry) => _context.Registry.Resolve(entry.Path).Capabilities.HasFlag(ProviderCapabilities.Write);

    private void UpdateTitle() =>
        Title = $"Compare: {_l.Entry.Name}{(_l.Dirty ? "*" : string.Empty)} ↔ {_r.Entry.Name}{(_r.Dirty ? "*" : string.Empty)}";

    // ---------------------------------------------------------------- saving

    /// <summary>Saves one side (left or right); true when it is saved or had nothing to save.</summary>
    public Task<bool> SaveSideAsync(bool right) => SaveAsync(right ? _r : _l);

    /// <summary>Saves every changed side.</summary>
    public async Task<bool> SaveAllAsync()
    {
        CommitEdits();
        var ok = true;
        foreach (var side in new[] { _l, _r }.Where(s => s.Dirty))
        {
            ok &= await SaveAsync(side);
        }
        return ok;
    }

    private async Task<bool> SaveAsync(Side side)
    {
        CommitEdits();
        if (!side.Dirty)
        {
            return true;
        }
        if (side.ReadOnly)
        {
            await ShowMessage($"{side.Entry.Name} cannot be written here.");
            return false;
        }
        byte[] bytes;
        try
        {
            bytes = side.Bytes();
        }
        catch (EncoderFallbackException ex)
        {
            await ShowMessage($"{side.Entry.Name} contains characters that cannot be represented in {side.Encoding.WebName} encoding (position {ex.Index}). Choose UTF-8 in its encoding menu.");
            return false;
        }
        try
        {
            var provider = _context.Registry.Resolve(side.Entry.Path);
            if (_context.Settings.CompareOptions.Backup && !side.BackedUp && side.Entry.Path.Parent is { } folder)
            {
                // the file as it was before the first save: name.bak next to it
                var backup = folder.Combine(side.Entry.Name + ".bak");
                await using (var original = await provider.OpenReadAsync(side.Entry.Path, CancellationToken.None))
                await using (var copy = await provider.OpenWriteAsync(backup, overwrite: true, CancellationToken.None))
                {
                    await original.CopyToAsync(copy);
                }
                side.BackedUp = true;
            }
            await using (var stream = await provider.OpenWriteAsync(side.Entry.Path, overwrite: true, CancellationToken.None))
            {
                await stream.WriteAsync(bytes);
            }
            if (await provider.GetEntryAsync(side.Entry.Path, CancellationToken.None) is { } fresh)
            {
                side.Entry = fresh;
            }
            side.WrittenAt = side.Entry.Path.IsLocal && File.Exists(side.Entry.Path.Path) ? File.GetLastWriteTimeUtc(side.Entry.Path.Path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage($"Could not save {side.Entry.Name}:\n{ex.Message}");
            return false;
        }
        side.SavedLines = [.. side.Lines];
        side.FormatChanged = false;
        UpdateTitle();
        BuildFileHeaders();
        UpdateButtons();
        ShowToast($"Saved {side.Entry.Name}");
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        CommitEdits();
        if (_closingConfirmed || (!_l.Dirty && !_r.Dirty))
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var names = string.Join(" and ", new[] { _l, _r }.Where(s => s.Dirty).Select(s => s.Entry.Name));
            var choice = await AskSaveOnClose(names);
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAllAsync()))
            {
                return;
            }
            _closingConfirmed = true;
            Close();
        });
    }

    // ---------------------------------------------------------------- encoding and line ends

    private void OpenFormatMenu(Control anchor, Side side)
    {
        var menu = new ContextMenu();
        foreach (var choice in EncodingDetector.Choices)
        {
            var item = new MenuItem { Header = choice.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = choice.Encoding.CodePage == side.Encoding.CodePage };
            item.Classes.Add("noloc");
            item.Click += (_, _) => SetFormat(side, choice.Encoding, (side.Bom && choice.Encoding.CodePage == 65001) || choice.Encoding.CodePage is 1200 or 1201, side.NewLine);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var bom = new MenuItem { Header = "Write BOM / no BOM", ToggleType = MenuItemToggleType.CheckBox, IsChecked = side.Bom };
        bom.Click += (_, _) => SetFormat(side, side.Encoding, !side.Bom, side.NewLine);
        menu.Items.Add(bom);
        menu.Items.Add(new Separator());
        foreach (var (header, newLine) in new[] { ("Line ending: Windows (CRLF)", "\r\n"), ("Line ending: Unix (LF)", "\n") })
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = side.NewLine == newLine };
            item.Click += (_, _) => SetFormat(side, side.Encoding, side.Bom, newLine);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    /// <summary>How a side is written from now on (marks it changed).</summary>
    private void SetFormat(Side side, Encoding encoding, bool bom, string newLine)
    {
        side.Encoding = encoding;
        side.Bom = bom;
        side.NewLine = newLine;
        side.FormatChanged = true;
        UpdateTitle();
        BuildFileHeaders();
        UpdateLanguage();
        UpdateButtons();
    }

    /// <summary>The encoding and line ends of a side as the menu would set them (tests).</summary>
    public void SetSideFormat(bool right, Encoding encoding, bool bom, string newLine) => SetFormat(right ? _r : _l, encoding, bom, newLine);

    // ---------------------------------------------------------------- another file, a file changed elsewhere

    private async Task BrowseAsync(bool right)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = right ? "Choose the right file" : "Choose the left file", AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }
        if (await _context.Registry.Resolve(VfsPath.Local(path)).GetEntryAsync(VfsPath.Local(path), CancellationToken.None) is { } entry)
        {
            await OpenSideAsync(right, entry);
        }
    }

    /// <summary>Another file on one side (its unsaved changes asked about first).</summary>
    public async Task OpenSideAsync(bool right, VfsEntry entry)
    {
        CommitEdits();
        var side = right ? _r : _l;
        if (side.Dirty)
        {
            var choice = await AskSaveOnClose(side.Entry.Name);
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAsync(side)))
            {
                return;
            }
        }
        var fresh = new Side(entry);
        if (right)
        {
            _r = fresh;
            _rightPane.Editor.SyntaxHighlighting = Highlighting(entry);
            _inlinePane.Editor.SyntaxHighlighting = Highlighting(entry);
        }
        else
        {
            _l = fresh;
            _leftPane.Editor.SyntaxHighlighting = Highlighting(entry);
        }
        await CompareAsync();
        UpdateTitle();
    }

    private void Watch()
    {
        StopWatching();
        foreach (var side in new[] { _l, _r })
        {
            if (!side.Entry.Path.IsLocal || Path.GetDirectoryName(side.Entry.Path.Path) is not { Length: > 0 } folder || !Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                var watcher = new FileSystemWatcher(folder, Path.GetFileName(side.Entry.Path.Path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = ExternalChangeAsync(side), DispatcherPriority.Background);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                // not watched: F5 reads it again
            }
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    /// <summary>A file was changed by another program: loaded again (asked first when it has unsaved changes here).</summary>
    internal async Task ExternalChangeAsync(object sideObject)
    {
        var side = (Side)sideObject;
        if (_reloading || !side.Loaded || !(side == _l || side == _r) || !File.Exists(side.Entry.Path.Path))
        {
            return;
        }
        var written = File.GetLastWriteTimeUtc(side.Entry.Path.Path);
        if (side.WrittenAt == written)
        {
            return; // this window's own save
        }
        _reloading = true;
        try
        {
            CommitEdits();
            if (side.Dirty && !await AskReload(side.Entry.Name))
            {
                side.WrittenAt = written; // asked once for this change
                return;
            }
            await ReloadSideAsync(side == _r);
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>Reads one side's file again (its changes here are dropped).</summary>
    public async Task ReloadSideAsync(bool right)
    {
        var side = right ? _r : _l;
        byte[] data;
        try
        {
            data = await ReadAsync(side.Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message);
            return;
        }
        side.Load(data, WritableOf(side.Entry));
        side.WrittenAt = side.Entry.Path.IsLocal && File.Exists(side.Entry.Path.Path) ? File.GetLastWriteTimeUtc(side.Entry.Path.Path) : null;
        _undoStack.Clear();
        _redoStack.Clear();
        _keepBlock = Math.Max(0, _currentBlock);
        await CompareAsync();
        BuildFileHeaders();
        ShowToast($"Loaded {side.Entry.Name} again");
    }

    /// <summary>The side object of the left or right file (tests: <see cref="ExternalChangeAsync"/>).</summary>
    internal object SideOf(bool right) => right ? _r : _l;

    /// <summary>The files compared now (after a save: as they are on disk).</summary>
    public VfsEntry LeftEntry => _l.Entry;

    public VfsEntry RightEntry => _r.Entry;

    /// <summary>A file of the window was saved (the folder compare updates its row).</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// The next (+1) or previous (-1) pair of different files of the folder compare that opened this window; null when
    /// there is none (the window was opened for two files alone).
    /// </summary>
    public Func<int, Task<(VfsEntry Left, VfsEntry Right)?>>? Neighbor { get; set; }

    /// <summary>Ctrl+Alt+↓ / ↑: the next / previous different files of the folder compare (unsaved changes asked about first).</summary>
    public async Task<bool> OtherFileAsync(int direction)
    {
        if (Neighbor is null)
        {
            return false;
        }
        CommitEdits();
        if (_l.Dirty || _r.Dirty)
        {
            var names = string.Join(" and ", new[] { _l, _r }.Where(s => s.Dirty).Select(s => s.Entry.Name));
            var choice = await AskSaveOnClose(names);
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAllAsync()))
            {
                return false;
            }
        }
        if (await Neighbor(direction) is not { } pair)
        {
            ShowToast(direction > 0 ? "No more different files" : "No different files before this one");
            return false;
        }
        _l = new Side(pair.Left);
        _r = new Side(pair.Right);
        _leftPane.Editor.SyntaxHighlighting = Highlighting(pair.Left);
        _rightPane.Editor.SyntaxHighlighting = Highlighting(pair.Right);
        _inlinePane.Editor.SyntaxHighlighting = Highlighting(pair.Right);
        _currentBlock = -1;
        await CompareAsync();
        UpdateTitle();
        return true;
    }
}
