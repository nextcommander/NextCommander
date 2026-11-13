using Avalonia.Input.Platform;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Formatting;
using NextCommander.Core.Imaging;
using NextCommander.Core.Search;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;
using Serilog;

namespace NextCommander.App.Views.Lister;

public enum ListerMode
{
    Text,
    Binary,
    Hex,
    Image,
    /// <summary>Rendered HTML, Markdown or RTF.</summary>
    Preview,
    /// <summary>A viewer plugin (<see cref="Core.Plugins.IViewerPlugin"/>).</summary>
    Plugin,
    /// <summary>Audio / video through VLC.</summary>
    Media,
    /// <summary>File details only (media, directories, unreadable files).</summary>
    Info,
}

/// <summary>
/// The Lister (F3 window and Ctrl+Q quick view): text with highlighting (AvaloniaEdit) for normal sizes, a streaming
/// text / binary / hex view for any size, images and an HTML / Markdown / RTF preview. Keys: 1-5 mode, W wrap, A/S/U/8 encoding, F7 find, F3 next,
/// Ctrl+wheel zoom, L/R rotate, F fit.
/// </summary>
public sealed class ListerView : UserControl
{
    private readonly ToolContext _context;
    private readonly TextEditor _editor;
    private readonly ByteViewControl _bytes;
    private readonly ScrollBar _scrollBar;
    private readonly Grid _bytesHost;
    private readonly ImageViewControl _image;
    private readonly TextBlock _info;
    private readonly TheArtOfDev.HtmlRenderer.Avalonia.HtmlPanel _preview;
    private string? _previewText;
    private string? _pluginStatus;
    private MediaView? _mediaView;
    private readonly ContentControl _host;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly CheckBox _findCase;
    private readonly CheckBox _findWords;
    private readonly CheckBox _findRegex;
    private readonly CheckBox _findHex;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _findCts;
    private IRandomAccessSource? _source;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadTimer;
    private Encoding _detected = Encoding.UTF8;
    private Encoding? _encodingOverride;
    private bool _binary;
    private bool _syncingScrollBar;

    public ListerView(ToolContext context)
    {
        _context = context;
        EncodingDetector.EnsureCodePages();
        var settings = context.Settings;

        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = settings.ListerWrap,
            FontFamily = new FontFamily(settings.ListerFont),
            FontSize = settings.ListerFontSize,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 2),
        };
        _editor.Options.EnableHyperlinks = true;
        _editor.Options.EnableEmailHyperlinks = true;
        _editor.Options.RequireControlModifierForHyperlinkClick = false;
        _editor.TextArea.AddHandler(VisualLineLinkText.OpenUriEvent, (_, e) =>
        {
            context.Platform.OpenWithDefaultApp(e.Uri.ToString());
            e.Handled = true;
        });
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        ThemeAwareColors.Attach(_editor);

        _bytes = new ByteViewControl();
        _bytes.SetFont(settings.ListerFont, settings.ListerFontSize);
        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, AllowAutoHide = false };
        _scrollBar.Scroll += (_, e) =>
        {
            if (!_syncingScrollBar)
            {
                _bytes.ScrollTo((long)e.NewValue);
            }
        };
        _bytes.PositionChanged += (_, _) =>
        {
            _syncingScrollBar = true;
            _scrollBar.Maximum = Math.Max(1, _bytes.Length);
            _scrollBar.ViewportSize = Math.Max(1, _bytes.Length / 50.0);
            _scrollBar.Value = _bytes.Position;
            _syncingScrollBar = false;
            UpdateStatus();
        };
        _bytesHost = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _bytesHost.Children.Add(_bytes);
        Grid.SetColumn(_scrollBar, 1);
        _bytesHost.Children.Add(_scrollBar);

        _image = new ImageViewControl();
        _image.ViewChanged += (_, _) => UpdateStatus();

        _preview = new TheArtOfDev.HtmlRenderer.Avalonia.HtmlPanel { Focusable = true };
        _preview.LinkClicked += (_, e) =>
        {
            // Links open in the browser; in-page anchors (#x) are left to the panel.
            if (!e.Event.Link.StartsWith('#'))
            {
                e.Event.Handled = true;
                if (Uri.TryCreate(e.Event.Link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                {
                    context.Platform.OpenWithDefaultApp(uri.ToString());
                }
            }
        };
        _preview.ImageLoad += (_, e) =>
        {
            // Privacy: never fetch remote images; local relative images are not resolved either (archives, FTP).
            if (!e.Event.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Event.Handled = true;
            }
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            if (Mode == ListerMode.Preview && _previewText is not null && Entry is not null)
            {
                _preview.Text = DocumentPreview.Build(Entry.Extension, _previewText, ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
            }
        };

        _info = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, FontSize = 13 };

        _findBox = new TextBox { MinWidth = 260, PlaceholderText = "Text to find (Enter: next, Shift+Enter: previous)" };
        _findCase = new CheckBox { Content = "Case sensitive", Margin = new Thickness(8, 0, 0, 0) };
        _findWords = new CheckBox { Content = "Whole words", Margin = new Thickness(8, 0, 0, 0) };
        _findRegex = new CheckBox { Content = "Regex", Margin = new Thickness(8, 0, 0, 0) };
        _findHex = new CheckBox { Content = "Hex", Margin = new Thickness(8, 0, 0, 0) };
        var next = new Button { Content = "Next", Margin = new Thickness(8, 0, 0, 0) };
        next.Click += (_, _) => _ = FindAsync(forward: true);
        var previous = new Button { Content = "Previous", Margin = new Thickness(4, 0, 0, 0) };
        previous.Click += (_, _) => _ = FindAsync(forward: false);
        var close = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0) };
        close.Click += (_, _) => CloseFindBar();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = FindAsync(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFindBar();
                e.Handled = true;
            }
        };
        var findPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        findPanel.Children.AddRange([_findBox, _findCase, _findWords, _findRegex, _findHex, next, previous, close]);
        _findBar = new Border { Child = findPanel, Padding = new Thickness(6, 4), IsVisible = false };
        _findBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");

        _host = new ContentControl();
        // Color > Lister colors (resources exist only when set; otherwise the window background shows through)
        _host[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListerBackgroundBrush");
        _editor[!TextEditor.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListerTextBrush");
        _editor[!TextEditor.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListerBackgroundBrush");
        var root = new DockPanel();
        DockPanel.SetDock(_findBar, Dock.Top);
        root.Children.Add(_findBar);
        root.Children.Add(_host);
        Content = root;
        ContextMenu = BuildContextMenu();
        // Tunnel: the text editor would otherwise swallow keys like 1-4 before they bubble up.
        AddHandler(KeyDownEvent, (_, e) => HandleKey(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the status line text changes.</summary>
    public event EventHandler? StatusChanged;

    public VfsEntry? Entry { get; private set; }

    public ListerMode Mode { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    public Encoding CurrentEncoding => _encodingOverride ?? _detected;

    /// <summary>True when the text is shown in the full editor view (otherwise the streaming view).</summary>
    public bool UsesEditor => _host.Content == _editor;

    public TextEditor Editor => _editor;

    public ByteViewControl ByteView => _bytes;

    public ImageViewControl ImageView => _image;

    public string? LastFindMessage { get; private set; }

    /// <summary>Waits for the current load (tests).</summary>
    public Task? Loading { get; private set; }

    public static bool IsImage(VfsEntry entry) =>
        !entry.IsDirectory && (ThumbnailService.DecodableExtensions.Contains(entry.Extension) || ExifThumbnailReader.TiffRawExtensions.Contains(entry.Extension));

    public Task LoadAsync(VfsEntry entry, ListerMode? mode = null)
    {
        _encodingOverride = null;
        Loading = LoadCoreAsync(entry, mode, keepPosition: false);
        return Loading;
    }

    /// <summary>Re-reads the file keeping the mode and scroll position (F2 / file changed).</summary>
    public Task ReloadAsync()
    {
        if (Entry is null)
        {
            return Task.CompletedTask;
        }
        Loading = LoadCoreAsync(Entry, Mode, keepPosition: true);
        return Loading;
    }

    private async Task LoadCoreAsync(VfsEntry entry, ListerMode? mode, bool keepPosition)
    {
        if (_loadCts is not null)
        {
            await _loadCts.CancelAsync();
        }
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var editorOffset = keepPosition && UsesEditor ? _editor.TextArea.TextView.VerticalOffset : 0;
        var caret = keepPosition && UsesEditor ? _editor.CaretOffset : 0;
        var bytePosition = keepPosition ? _bytes.Position : 0;

        Unload(clearEntry: false);
        Entry = entry;
        if (entry.IsDirectory)
        {
            ShowInfo(entry, null);
            return;
        }
        try
        {
            var provider = _context.Registry.Resolve(entry.Path);
            var source = await RandomAccessSource.OpenAsync(provider, entry, cts.Token);
            if (cts.IsCancellationRequested)
            {
                source.Dispose();
                return;
            }
            _source = source;
            var sample = source.ReadBytes(0, 64 * 1024);
            _detected = EncodingDetector.Detect(sample, out _, complete: sample.Length == source.Length);
            _binary = EncodingDetector.LooksBinary(sample);
            await ShowModeAsync(mode ?? DefaultMode(entry), cts.Token);
            if (keepPosition)
            {
                if (UsesEditor)
                {
                    _editor.CaretOffset = Math.Min(caret, _editor.Document.TextLength);
                    _editor.ScrollToVerticalOffset(editorOffset);
                }
                else if (Mode is ListerMode.Text or ListerMode.Binary or ListerMode.Hex)
                {
                    _bytes.ScrollTo(bytePosition);
                }
            }
            Watch(entry);
        }
        catch (OperationCanceledException)
        {
            // a newer load replaced this one
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(entry, ex.Message);
        }
    }

    /// <summary>The first viewer plugin that wants the loaded file.</summary>
    private Core.Plugins.IViewerPlugin? FindViewer()
    {
        if (Entry is null || _source is null)
        {
            return null;
        }
        var header = _source.ReadBytes(0, (int)Math.Min(4096, _source.Length));
        foreach (var viewer in Core.Plugins.PluginCatalog.Viewers)
        {
            try
            {
                if (viewer.CanView(Entry.Name, header))
                {
                    return viewer;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning(ex, "Viewer plugin {Name} failed in CanView", viewer.Name);
            }
        }
        return null;
    }

    private ListerMode DefaultMode(VfsEntry entry)
    {
        if (FindViewer() is not null)
        {
            return ListerMode.Plugin;
        }
        if (IsImage(entry))
        {
            return ListerMode.Image;
        }
        if (FileKinds.Classify(entry) is FileKind.Audio or FileKind.Video)
        {
            return MediaSupport.IsAvailable ? ListerMode.Media : ListerMode.Info;
        }
        if (DocumentPreview.Supports(entry.Extension) && _context.Settings.ListerPreviewDocuments)
        {
            return ListerMode.Preview;
        }
        return _binary ? ListerMode.Hex : ListerMode.Text;
    }

    public Task SetModeAsync(ListerMode mode) => ShowModeAsync(mode, CancellationToken.None);

    private async Task ShowModeAsync(ListerMode mode, CancellationToken cancellationToken)
    {
        if (Entry is null || _source is null)
        {
            if (Entry is not null)
            {
                ShowInfo(Entry, null);
            }
            return;
        }
        Mode = mode;
        var encoding = CurrentEncoding;
        var sample = _source.ReadBytes(0, 4);
        var bom = EncodingDetector.BomLength(sample, encoding);
        switch (mode)
        {
            case ListerMode.Text when _source.Length <= _context.Settings.ListerMaxTextBytes:
            {
                var source = _source;
                var text = await Task.Run(() =>
                {
                    var bytes = source.ReadBytes(0, (int)source.Length);
                    return Sanitize(encoding.GetString(bytes, bom, bytes.Length - bom));
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _editor.Document = new TextDocument(text);
                _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension("." + Entry.Extension);
                _editor.ScrollToHome();
                _host.Content = _editor;
                break;
            }
            case ListerMode.Text:
                _bytes.SetSource(_source, encoding, bom, ByteViewMode.Text);
                _host.Content = _bytesHost;
                break;
            case ListerMode.Binary:
                _bytes.SetSource(_source, encoding, 0, ByteViewMode.Binary);
                _host.Content = _bytesHost;
                break;
            case ListerMode.Hex:
                _bytes.SetSource(_source, encoding, 0, ByteViewMode.Hex);
                _host.Content = _bytesHost;
                break;
            case ListerMode.Image:
                if (!await ShowImageAsync(cancellationToken))
                {
                    Mode = ListerMode.Hex;
                    _bytes.SetSource(_source, encoding, 0, ByteViewMode.Hex);
                    _host.Content = _bytesHost;
                }
                break;
            case ListerMode.Preview:
            {
                var source = _source;
                var extension = Entry.Extension;
                var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
                var limit = (int)Math.Min(source.Length, _context.Settings.ListerMaxTextBytes);
                var (text, html) = await Task.Run(() =>
                {
                    var bytes = source.ReadBytes(0, limit);
                    var t = encoding.GetString(bytes, bom, bytes.Length - bom);
                    return (t, DocumentPreview.Build(extension, t, dark));
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _previewText = text;
                _preview.Text = html;
                _host.Content = _preview;
                break;
            }
            case ListerMode.Media:
                if (MediaSupport.LibVlc is not { } libVlc)
                {
                    ShowInfo(Entry, null);
                    return;
                }
                if (_mediaView is null)
                {
                    _mediaView = new MediaView(libVlc, MediaSupport.Positions);
                    _mediaView.StatusChanged += (_, _) => UpdateStatus();
                }
                {
                    var entry = Entry;
                    var registry = _context.Registry;
                    _mediaView.Play(entry.Path.ToDisplayString(), entry.Path.IsLocal ? entry.Path.Path : null,
                        () => registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None).GetAwaiter().GetResult());
                }
                _host.Content = _mediaView;
                break;
            case ListerMode.Plugin:
                if (!await ShowPluginAsync(cancellationToken))
                {
                    await ShowModeAsync(_binary ? ListerMode.Hex : ListerMode.Text, cancellationToken);
                    return;
                }
                break;
            default:
                ShowInfo(Entry, null);
                break;
        }
        UpdateStatus();
    }

    /// <summary>Lets a viewer plugin build the view: HTML (shown like the preview), text, or its own control.</summary>
    private async Task<bool> ShowPluginAsync(CancellationToken cancellationToken)
    {
        if (FindViewer() is not { } viewer)
        {
            return false;
        }
        var entry = Entry!;
        try
        {
            var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            await using var stream = _source!.OpenStream(0);
            var content = await viewer.ViewAsync(stream, entry.Name, dark, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _pluginStatus = string.IsNullOrEmpty(content.Status) ? viewer.Name : $"{viewer.Name}: {content.Status}";
            if (content.Control is Control control)
            {
                _host.Content = control;
            }
            else if (content.Html is { } html)
            {
                _previewText = null;
                _preview.Text = html;
                _host.Content = _preview;
            }
            else
            {
                _editor.Document = new TextDocument(Sanitize(content.Text ?? string.Empty));
                _editor.SyntaxHighlighting = null;
                _host.Content = _editor;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warning(ex, "Viewer plugin {Name} failed for {Path}", viewer.Name, entry.Path);
            _pluginStatus = null;
            return false;
        }
    }

    /// <summary>NUL characters break the text layout: show them as a middle dot.</summary>
    private static string Sanitize(string text) => text.Contains('\0', StringComparison.Ordinal) ? text.Replace('\0', '·') : text;

    private async Task<bool> ShowImageAsync(CancellationToken cancellationToken)
    {
        var source = _source!;
        var entry = Entry!;
        try
        {
            var (bitmap, rotation) = await Task.Run(() =>
            {
                using var stream = source.OpenStream(0);
                var rotation = 0;
                if (ExifThumbnailReader.Read(stream) is { } exif)
                {
                    rotation = exif.RotationDegrees;
                    if (ExifThumbnailReader.TiffRawExtensions.Contains(entry.Extension))
                    {
                        // RAW: largest embedded JPEG preview.
                        return ExifThumbnailReader.Choose(exif.Previews, int.MaxValue) is { } preview
                            && ExifThumbnailReader.ReadPreview(stream, exif, preview) is { } jpeg
                            ? (new Bitmap(new MemoryStream(jpeg)), rotation)
                            : ((Bitmap?)null, 0);
                    }
                }
                stream.Position = 0;
                return (new Bitmap(stream), rotation);
            }, cancellationToken);
            if (bitmap is null)
            {
                return false;
            }
            _image.SetImage(bitmap, rotation);
            _host.Content = _image;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Lister image decode failed for {Path}", entry.Path);
            return false;
        }
    }

    private void ShowInfo(VfsEntry entry, string? error)
    {
        Mode = ListerMode.Info;
        var sb = new StringBuilder();
        sb.AppendLine(entry.Name).AppendLine();
        sb.AppendLine(entry.Path.ToDisplayString());
        if (!entry.IsDirectory)
        {
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes ({1})", entry.Size, SizeFormatter.Human(entry.Size)));
        }
        sb.AppendLine(entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        if (error is not null)
        {
            sb.AppendLine().AppendLine(error);
        }
        else if (!entry.IsDirectory)
        {
            if (FileKinds.Classify(entry) is FileKind.Audio or FileKind.Video && MediaSupport.Error is { } mediaError)
            {
                sb.AppendLine().AppendLine(mediaError);
            }
            sb.AppendLine().AppendLine("1: text   2: binary   3: hex   4: image   5: preview   7: media   Enter: open with system application");
        }
        _info.Text = sb.ToString();
        _host.Content = _info;
        UpdateStatus();
    }

    /// <summary>Opens the file with the system application (media files).</summary>
    public void OpenExternally()
    {
        if (Entry is { Path.IsLocal: true } entry)
        {
            _context.Platform.OpenWithDefaultApp(entry.Path.Path);
        }
    }

    public async Task SetEncodingAsync(Encoding encoding)
    {
        _encodingOverride = encoding;
        if (Mode is ListerMode.Text or ListerMode.Binary)
        {
            await ShowModeAsync(Mode, CancellationToken.None);
        }
        UpdateStatus();
    }

    public void ToggleWrap()
    {
        _context.Settings.ListerWrap = !_context.Settings.ListerWrap;
        _editor.WordWrap = _context.Settings.ListerWrap;
        UpdateStatus();
    }

    public void Zoom(int direction)
    {
        if (Mode == ListerMode.Image)
        {
            _image.ZoomBy(direction > 0 ? 1.25 : 0.8);
            return;
        }
        var size = Math.Clamp(_context.Settings.ListerFontSize + direction, 6, 48);
        _context.Settings.ListerFontSize = size;
        _editor.FontSize = size;
        _bytes.SetFont(_context.Settings.ListerFont, size);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- find

    public void OpenFindBar()
    {
        _findBar.IsVisible = true;
        if (UsesEditor && _editor.SelectionLength is > 0 and < 200)
        {
            _findBox.Text = _editor.SelectedText;
        }
        _findHex.IsChecked = Mode == ListerMode.Hex && _findHex.IsChecked == true;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    /// <summary>The find bar (F7) is open: Esc closes it before the window.</summary>
    public bool IsFindBarOpen => _findBar.IsVisible;

    public void CloseFindBar()
    {
        _findBar.IsVisible = false;
        FocusContent();
    }

    public void FocusContent()
    {
        FocusNow();
        // The editor template may not be applied yet: try again once layout has run.
        Dispatcher.UIThread.Post(FocusNow, DispatcherPriority.Loaded);
    }

    private void FocusNow()
    {
        switch (_host.Content)
        {
            case TextEditor:
                _editor.TextArea.Focus();
                break;
            case Grid:
                _bytes.Focus();
                break;
            case ImageViewControl:
                _image.Focus();
                break;
            case TheArtOfDev.HtmlRenderer.Avalonia.HtmlPanel:
                _preview.Focus();
                break;
            default:
                Focus();
                break;
        }
    }

    /// <summary>Sets the search text programmatically (tests, quick view).</summary>
    public void SetFindText(string text, bool caseSensitive = false, bool wholeWords = false, bool regex = false, bool hex = false)
    {
        _findBox.Text = text;
        _findCase.IsChecked = caseSensitive;
        _findWords.IsChecked = wholeWords;
        _findRegex.IsChecked = regex;
        _findHex.IsChecked = hex;
    }

    public async Task<bool> FindAsync(bool forward)
    {
        var text = _findBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            OpenFindBar();
            return false;
        }
        if (_findCts is not null)
        {
            await _findCts.CancelAsync();
        }
        var cts = new CancellationTokenSource();
        _findCts = cts;
        bool found;
        try
        {
            if (_findHex.IsChecked == true)
            {
                found = await FindBytesAsync(ParseHex(text), forward, cts.Token);
            }
            else
            {
                var matcher = new TextMatcher(text, _findCase.IsChecked == true, _findWords.IsChecked == true, _findRegex.IsChecked == true);
                found = UsesEditor ? FindInEditor(matcher, forward) : await FindInBytesAsync(matcher, forward, cts.Token);
            }
        }
        catch (ArgumentException ex)
        {
            LastFindMessage = ex.Message;
            UpdateStatus(ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        LastFindMessage = found ? null : $"\"{text}\" not found";
        UpdateStatus(LastFindMessage);
        return found;
    }

    private bool FindInEditor(TextMatcher matcher, bool forward)
    {
        var text = _editor.Text;
        (int Index, int Length) hit;
        if (forward)
        {
            var start = _editor.SelectionLength > 0 ? _editor.SelectionStart + 1 : _editor.CaretOffset;
            hit = matcher.Find(text, Math.Min(start, text.Length));
        }
        else
        {
            hit = matcher.FindLast(text, _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset);
        }
        if (hit.Index < 0)
        {
            return false;
        }
        _editor.Select(hit.Index, hit.Length);
        _editor.CaretOffset = hit.Index + hit.Length;
        var location = _editor.Document.GetLocation(hit.Index);
        _editor.ScrollTo(location.Line, location.Column);
        return true;
    }

    private async Task<bool> FindInBytesAsync(TextMatcher matcher, bool forward, CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return false;
        }
        UpdateStatus("Searching...");
        var source = _source;
        var encoding = _bytes.Mode == ByteViewMode.Hex ? Encoding.Latin1 : CurrentEncoding;
        var start = _bytes.SearchStart(forward);
        var hit = await Task.Run(async () =>
        {
            if (forward)
            {
                await using var stream = source.OpenStream(start);
                return await StreamTextScanner.FindAsync(stream, encoding, matcher, cancellationToken);
            }
            ByteHit? last = null;
            var pos = 0L;
            while (true)
            {
                await using var stream = source.OpenStream(pos);
                var h = await StreamTextScanner.FindAsync(stream, encoding, matcher, cancellationToken);
                if (h is null || h.Value.Offset >= start)
                {
                    return last;
                }
                last = h;
                pos = h.Value.Offset + 1;
            }
        }, cancellationToken);
        if (hit is not { } found)
        {
            return false;
        }
        _bytes.Highlight(found.Offset, found.Length);
        return true;
    }

    private async Task<bool> FindBytesAsync(byte[] pattern, bool forward, CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return false;
        }
        if (UsesEditor)
        {
            await SetModeAsync(ListerMode.Hex);
        }
        var source = _source;
        var start = _bytes.SearchStart(forward);
        var offset = await Task.Run(async () =>
        {
            if (forward)
            {
                await using var stream = source.OpenStream(start);
                return await StreamTextScanner.FindBytesAsync(stream, pattern, cancellationToken);
            }
            long? last = null;
            var pos = 0L;
            while (true)
            {
                await using var stream = source.OpenStream(pos);
                var h = await StreamTextScanner.FindBytesAsync(stream, pattern, cancellationToken);
                if (h is null || h >= start)
                {
                    return last;
                }
                last = h;
                pos = h.Value + 1;
            }
        }, cancellationToken);
        if (offset is not { } o)
        {
            return false;
        }
        _bytes.Highlight(o, pattern.Length);
        return true;
    }

    /// <summary>"4D 5A", "4d5a90" → bytes.</summary>
    public static byte[] ParseHex(string text)
    {
        var digits = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            throw new ArgumentException("For hex search, enter an even number of hex digits (e.g. 4D 5A).");
        }
        return Convert.FromHexString(digits);
    }

    // ---------------------------------------------------------------- keys, menu, status


    /// <summary>Lister keys; the window forwards keys that arrive while nothing inside the view has focus.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled || _findBox.IsFocused)
        {
            return;
        }
        var mods = e.KeyModifiers;
        if (Mode == ListerMode.Media && _mediaView is not null && _mediaView.HandleKey(e.Key, mods))
        {
            e.Handled = true;
            return;
        }
        var plain = mods == KeyModifiers.None;
        var textMode = Mode is ListerMode.Text or ListerMode.Binary;
        var handled = true;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1 when plain:
                _ = SetModeAsync(ListerMode.Text);
                break;
            case Key.D2 or Key.NumPad2 when plain:
                _ = SetModeAsync(ListerMode.Binary);
                break;
            case Key.D3 or Key.NumPad3 when plain:
                _ = SetModeAsync(ListerMode.Hex);
                break;
            case Key.D4 or Key.NumPad4 when plain:
                _ = SetModeAsync(ListerMode.Image);
                break;
            case Key.D5 or Key.NumPad5 when plain:
                _ = SetModeAsync(ListerMode.Preview);
                break;
            case Key.D6 or Key.NumPad6 when plain:
                _ = SetModeAsync(ListerMode.Plugin);
                break;
            case Key.D7 or Key.NumPad7 when plain:
                _ = SetModeAsync(ListerMode.Media);
                break;
            case Key.W when plain:
                ToggleWrap();
                break;
            case Key.A when plain && textMode:
                _ = SetEncodingAsync(EncodingDetector.Ansi);
                break;
            case Key.S when plain && textMode:
                _ = SetEncodingAsync(EncodingDetector.Oem);
                break;
            case Key.U when plain && textMode:
                _ = SetEncodingAsync(Encoding.Unicode);
                break;
            case Key.D8 when plain && textMode:
                _ = SetEncodingAsync(EncodingDetector.Utf8NoBom);
                break;
            case Key.F7:
                OpenFindBar();
                break;
            case Key.F3 when mods.HasFlag(KeyModifiers.Shift):
                _ = FindAsync(forward: false);
                break;
            case Key.F3 when plain:
                _ = FindAsync(forward: true);
                break;
            case Key.F2 when plain:
                _ = ReloadAsync();
                break;
            case Key.Add or Key.OemPlus when mods.HasFlag(KeyModifiers.Control) || (plain && Mode == ListerMode.Image):
                Zoom(1);
                break;
            case Key.Subtract or Key.OemMinus when mods.HasFlag(KeyModifiers.Control) || (plain && Mode == ListerMode.Image):
                Zoom(-1);
                break;
            case Key.L when plain && Mode == ListerMode.Image:
                _image.Rotate(-90);
                break;
            case Key.R when plain && Mode == ListerMode.Image:
                _image.Rotate(90);
                break;
            case Key.F when plain && Mode == ListerMode.Image:
                _image.ToggleFit();
                break;
            case Key.Enter when plain && Mode == ListerMode.Info:
                OpenExternally();
                break;
            case Key.C when mods == KeyModifiers.Control && !UsesEditor:
                _ = CopySelectionAsync();
                break;
            case Key.A when mods == KeyModifiers.Control && !UsesEditor:
                _bytes.SelectAll();
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Mode != ListerMode.Image)
        {
            Zoom(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    private async Task CopySelectionAsync()
    {
        if (_bytes.SelectedText() is { } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>The icons of the right-click menu (keys of <see cref="Services.MenuIcons"/>); the encoding names stay plain.</summary>
    private static readonly Dictionary<string, string> s_menuIcons = new(StringComparer.Ordinal)
    {
        ["Text"] = "lister.text",
        ["Binary (fixed width)"] = "lister.binary",
        ["Hex"] = "lister.hex",
        ["Image"] = "lister.image",
        ["Preview (HTML, Markdown, RTF)"] = "lister.preview",
        ["Plugin viewer"] = "lister.plugin",
        ["Media (VLC)"] = "lister.media",
        ["Encoding"] = "lister.encoding",
        ["Word wrap"] = "editor.wordwrap",
        ["Increase font size"] = "editor.fontbigger",
        ["Decrease font size"] = "editor.fontsmaller",
        ["Find..."] = "editor.find",
        ["Find next"] = "editor.findnext",
        ["Find previous"] = "lister.findprevious",
        ["Reload"] = "editor.reload",
        ["Open with system application"] = "nc_OpenWith",
    };

    /// <summary>The icons, made each time the menu opens (icon pack, "icons in the menus"); Opened too: a menu opened in code gets no Opening.</summary>
    private void SetMenuIcons(ContextMenu menu)
    {
        var show = _context.Settings.ShowMenuIcons;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Icon = show && item.Header is string header && s_menuIcons.TryGetValue(header, out var key) ? Services.MenuIcons.Create(key) : null;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) => SetMenuIcons(menu);
        menu.Opened += (_, _) => SetMenuIcons(menu);
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }
            item.Click += (_, _) => action();
            return item;
        }
        var encodings = new MenuItem { Header = "Encoding" };
        foreach (var choice in EncodingDetector.Choices)
        {
            encodings.Items.Add(Item(choice.Name, () => _ = SetEncodingAsync(choice.Encoding)));
        }
        menu.Items.Add(Item("Text", () => _ = SetModeAsync(ListerMode.Text), "D1"));
        menu.Items.Add(Item("Binary (fixed width)", () => _ = SetModeAsync(ListerMode.Binary), "D2"));
        menu.Items.Add(Item("Hex", () => _ = SetModeAsync(ListerMode.Hex), "D3"));
        menu.Items.Add(Item("Image", () => _ = SetModeAsync(ListerMode.Image), "D4"));
        menu.Items.Add(Item("Preview (HTML, Markdown, RTF)", () => _ = SetModeAsync(ListerMode.Preview), "D5"));
        menu.Items.Add(Item("Plugin viewer", () => _ = SetModeAsync(ListerMode.Plugin), "D6"));
        menu.Items.Add(Item("Media (VLC)", () => _ = SetModeAsync(ListerMode.Media), "D7"));
        menu.Items.Add(new Separator());
        menu.Items.Add(encodings);
        menu.Items.Add(Item("Word wrap", ToggleWrap, "W"));
        menu.Items.Add(Item("Increase font size", () => Zoom(1), "Ctrl+Add"));
        menu.Items.Add(Item("Decrease font size", () => Zoom(-1), "Ctrl+Subtract"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Find...", OpenFindBar, "F7"));
        menu.Items.Add(Item("Find next", () => _ = FindAsync(true), "F3"));
        menu.Items.Add(Item("Find previous", () => _ = FindAsync(false), "Shift+F3"));
        menu.Items.Add(Item("Reload", () => _ = ReloadAsync(), "F2"));
        menu.Items.Add(Item("Open with system application", OpenExternally));
        return menu;
    }

    private void UpdateStatus(string? message = null)
    {
        if (Entry is null)
        {
            StatusText = string.Empty;
        }
        else
        {
            var parts = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", Entry.Size),
                Mode switch
                {
                    ListerMode.Text => UsesEditor ? "Text" : "Text (flow)",
                    ListerMode.Binary => "Binary",
                    ListerMode.Hex => "Hex",
                    ListerMode.Image => "Image",
                    ListerMode.Preview => "Preview",
                    ListerMode.Plugin => "Plugin" + (_pluginStatus is { } ps ? $" ({ps})" : string.Empty),
                    ListerMode.Media => "Media " + _mediaView?.StatusText,
                    _ => "Info",
                },
            };
            switch (Mode)
            {
                case ListerMode.Text or ListerMode.Binary:
                    parts.Add(CurrentEncoding.WebName.ToUpperInvariant() + (_encodingOverride is null ? "" : " (manual)"));
                    if (UsesEditor)
                    {
                        parts.Add($"Line {_editor.TextArea.Caret.Line}/{_editor.Document.LineCount}");
                        parts.Add(_editor.WordWrap ? "Word wrap on" : "Word wrap off");
                    }
                    else
                    {
                        parts.Add(Percent());
                    }
                    break;
                case ListerMode.Hex:
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", _bytes.Position));
                    parts.Add(Percent());
                    break;
                case ListerMode.Image when _image.Bitmap is { } bmp:
                    parts.Add($"{bmp.PixelSize.Width}×{bmp.PixelSize.Height}");
                    parts.Add(_image.IsFit ? $"Fit ({_image.Zoom:P0})" : $"{_image.Zoom:P0}");
                    if (_image.Rotation != 0)
                    {
                        parts.Add($"{_image.Rotation}°");
                    }
                    break;
            }
            if (message is not null)
            {
                parts.Add(message);
            }
            StatusText = string.Join("   ", parts);
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Percent() => _bytes.Length == 0 ? "%100" : string.Format(CultureInfo.CurrentCulture, "%{0:0}", 100.0 * _bytes.Position / _bytes.Length);

    // ---------------------------------------------------------------- reload on change

    private void Watch(VfsEntry entry)
    {
        if (!_context.Settings.ListerAutoReload || !entry.Path.IsLocal)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(entry.Path.Path);
            if (dir is null)
            {
                return;
            }
            _watcher = new FileSystemWatcher(dir, entry.Name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(ScheduleReload);
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(ScheduleReload);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Lister watch failed");
        }
    }

    private void ScheduleReload()
    {
        _reloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (_, _) =>
        {
            _reloadTimer?.Stop();
            if (Entry is { } entry && await _context.Registry.Resolve(entry.Path).GetEntryAsync(entry.Path, CancellationToken.None) is { } fresh)
            {
                Entry = fresh;
                await ReloadAsync();
            }
        });
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    /// <summary>Releases the file (window closed, quick view hidden).</summary>
    public void Unload(bool clearEntry = true)
    {
        _mediaView?.Stop();
        if (clearEntry)
        {
            _mediaView?.Dispose();
            _mediaView = null;
        }
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer?.Stop();
        _bytes.SetSource(null, Encoding.UTF8, 0, ByteViewMode.Text);
        _source?.Dispose();
        _source = null;
        var old = _image.Bitmap;
        _image.SetImage(null, 0);
        old?.Dispose();
        if (clearEntry)
        {
            _loadCts?.Cancel();
            Entry = null;
            _editor.Document = new TextDocument();
            _host.Content = null;
        }
    }
}
