using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Where the checksums go: one file for all, a file beside each file, or only the clipboard.</summary>
public enum ChecksumOutput
{
    OneFile,
    EachFile,
    Clipboard,
}

/// <summary>What the checksum dialog asks for: the algorithm, where the result goes, the line format.</summary>
public sealed record ChecksumChoice(ChecksumKind Kind, ChecksumOutput Output, VfsPath? SingleFile, bool Bsd);

/// <summary>
/// The selection, its folder and the name suggested for one checksum file; <paramref name="MakeJob"/> makes the job that
/// reads the files (the dialog runs it and shows its progress and the checksums).
/// </summary>
public sealed record ChecksumDialogRequest(IReadOnlyList<VfsEntry> Sources, VfsPath Folder, string BaseName, ChecksumKind Kind, bool Bsd,
    Func<ChecksumChoice, CreateChecksumsJob> MakeJob);

/// <summary>
/// Create checksums (the user's template, 21 November 2026): the algorithm as cards (SHA-256 recommended, a note on each),
/// where the result goes (one file, a file beside each file, the clipboard), a preview of the lines in the standard or
/// BSD format, the progress while the files are read and a box to compare with an expected checksum.
/// </summary>
public sealed class ChecksumDialog : Window
{
    /// <summary>The algorithms: their card's line and the note under the cards (weak ones are marked).</summary>
    private static readonly (ChecksumKind Kind, string Hint, string Note, bool Weak)[] s_algorithms =
    [
        (ChecksumKind.Md5, "Old", "Only for comparing with old lists. MD5 is broken and can be forged.", true),
        (ChecksumKind.Sha1, "Old", "Only for comparing with old lists. SHA-1 is no longer secure.", true),
        (ChecksumKind.Sha256, "Standard", "The usual choice: what download pages and Linux images publish.", false),
        (ChecksumKind.Sha512, "Longest", "A longer checksum; often faster than SHA-256 on 64-bit computers.", false),
        (ChecksumKind.Blake3, "Fastest", "Very fast on large files and secure. Not every tool can read it yet.", false),
        (ChecksumKind.Crc32, "Quick check", "Finds copy errors only; not a security check. Saved as an .sfv file.", true),
        (ChecksumKind.Sha384, "SHA-2", "A SHA-2 checksum between SHA-256 and SHA-512 in length.", false),
        (ChecksumKind.Sha3256, "SHA-3", "The newer SHA-3 family; as long as SHA-256.", false),
        (ChecksumKind.Sha3512, "SHA-3", "The newer SHA-3 family; as long as SHA-512.", false),
    ];

    private readonly ChecksumDialogRequest _request;
    private readonly UserControl _view = new();
    private readonly Panel _cards = new UniformGrid { Columns = 6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ContentControl _algorithmNote = new();
    private readonly RadioButton _oneFile = new() { GroupName = "output" };
    private readonly RadioButton _eachFile = new() { GroupName = "output" };
    private readonly RadioButton _clipboard = new() { GroupName = "output" };
    private readonly TextBox _name = new() { FontFamily = ConnectionLook.Mono, FontSize = 12.5, Classes = { "noloc" } };
    private readonly TextBlock _nameHint = new() { FontSize = 12, Margin = new Thickness(1, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
    private readonly TextBlock _eachNames = new() { FontFamily = ConnectionLook.Mono, FontSize = 12, LineHeight = 20, Classes = { "noloc" } };
    private readonly ToggleButton _standard = new() { Content = "Standard", Classes = { "connchip" } };
    private readonly ToggleButton _bsdStyle = new() { Content = "BSD style", Classes = { "connchip" } };
    private readonly StackPanel _format = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _previewName = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly Button _copy = new() { Content = "Copy", Classes = { "tb", "ghost" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), IsEnabled = false };
    private readonly ProgressBar _progress = new() { Height = 3, MinHeight = 3, Minimum = 0, Maximum = 1, CornerRadius = new CornerRadius(0) };
    private readonly SelectableTextBlock _lines = new() { FontFamily = ConnectionLook.Mono, FontSize = 12, LineHeight = 20, Margin = new Thickness(12, 10), Classes = { "noloc" } };
    private readonly TextBox _expected = new() { FontFamily = ConnectionLook.Mono, FontSize = 12.5, PlaceholderText = "Optional: paste the expected checksum to compare", Classes = { "noloc" } };
    private readonly ContentControl _verdict = new() { MinHeight = 22, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _summary = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly Button _create = new() { Classes = { "tb", "primary" }, IsDefault = true, MinWidth = 96 };
    private readonly Button _cancel = new() { Content = "Cancel", Classes = { "tb" }, IsCancel = true, MinWidth = 80 };
    private readonly DispatcherTimerHolder _timer = new();

    private ChecksumKind _kind;
    private bool _bsd;
    private VfsPath _folder;
    private bool _nameTouched;
    private CreateChecksumsJob? _job;
    private bool _running;
    private string? _done;

    public ChecksumDialog(ChecksumDialogRequest request)
    {
        _request = request;
        _kind = Checksums.Available.Contains(request.Kind) ? request.Kind : ChecksumKind.Sha256;
        _bsd = request.Bsd;
        _folder = request.Folder;
        Title = "Create checksums";
        Width = 740;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConnectionLook.Apply(_view);
        _view.Content = Build();
        Content = _view;
        _name.Text = DefaultName();
        Avalonia.Automation.AutomationProperties.SetName(_name, "Checksum file name");
        Avalonia.Automation.AutomationProperties.SetName(_expected, "Expected checksum");
        _name.TextChanged += (_, _) =>
        {
            if (_settingName)
            {
                return;
            }
            _nameTouched = true;
            Refresh();
        };
        _expected.TextChanged += (_, _) => ExpectedChanged();
        _oneFile.IsCheckedChanged += (_, _) => OutputChanged();
        _eachFile.IsCheckedChanged += (_, _) => OutputChanged();
        _clipboard.IsCheckedChanged += (_, _) => OutputChanged();
        _oneFile.IsChecked = true;
        _standard.Click += (_, _) => SetFormat(bsd: false);
        _bsdStyle.Click += (_, _) => SetFormat(bsd: true);
        _copy.Click += async (_, _) => await CopyAsync();
        _create.Click += async (_, _) => await CreateOrCloseAsync();
        _cancel.Click += (_, _) =>
        {
            if (_running)
            {
                _job?.Cancel();
                return;
            }
            Close();
        };
        Closed += (_, _) =>
        {
            _job?.Cancel();
            _timer.Stop();
        };
        KeyDown += (_, e) =>
        {
            // ← → on the cards: the algorithm before / after
            if (e.Key is Key.Left or Key.Right && FocusManager?.GetFocusedElement() is Button { Tag: ChecksumKind } && !_running)
            {
                var kinds = Kinds();
                var i = kinds.IndexOf(_kind);
                SetKind(kinds[(i + (e.Key == Key.Right ? 1 : -1) + kinds.Count) % kinds.Count]);
                _cards.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, _kind))?.Focus();
                e.Handled = true;
            }
        };
        Refresh();
    }

    private bool _settingName;

    /// <summary>The job that ran (null: closed without creating anything).</summary>
    public CreateChecksumsJob? Result { get; private set; }

    /// <summary>The algorithm, the output and the line format chosen.</summary>
    public ChecksumChoice Choice => new(_kind, Output, Output == ChecksumOutput.OneFile ? _folder.Combine((_name.Text ?? string.Empty).Trim()) : null, _bsd && _kind != ChecksumKind.Crc32);

    public ChecksumKind Kind => _kind;

    public ChecksumOutput Output => _clipboard.IsChecked == true ? ChecksumOutput.Clipboard : _eachFile.IsChecked == true ? ChecksumOutput.EachFile : ChecksumOutput.OneFile;

    /// <summary>The lines shown in the preview (tests).</summary>
    public string PreviewText => _lines.Text ?? string.Empty;

    /// <summary>The comparison with the expected checksum (tests).</summary>
    public string VerdictText => (_verdict.Content as Control)?.GetLogicalDescendantsText() ?? string.Empty;

    public void SetKind(ChecksumKind kind)
    {
        if (_running)
        {
            return;
        }
        _kind = kind;
        _job = null;
        _done = null;
        if (!_nameTouched)
        {
            SetName(DefaultName());
        }
        else
        {
            // the extension follows the algorithm
            var text = (_name.Text ?? string.Empty).Trim();
            if (Checksums.FromFileName(text) is not null)
            {
                text = Path.GetFileNameWithoutExtension(text);
            }
            SetName(text + Checksums.Extension(kind));
        }
        Refresh();
    }

    public void SetOutput(ChecksumOutput output)
    {
        (output switch { ChecksumOutput.EachFile => _eachFile, ChecksumOutput.Clipboard => _clipboard, _ => _oneFile }).IsChecked = true;
    }

    public void SetFormat(bool bsd)
    {
        _bsd = bsd;
        if (_job is { } job && !_running)
        {
            _job = null; // the lines are made again in the other format by the next Create
            _done = null;
        }
        Refresh();
    }

    public void SetExpected(string text)
    {
        _expected.Text = text;
        ExpectedChanged(); // at once (TextChanged comes later)
    }

    private List<ChecksumKind> Kinds() => [.. s_algorithms.Select(a => a.Kind).Where(Checksums.Available.Contains)];

    private void SetName(string text)
    {
        _settingName = true;
        try
        {
            _name.Text = text;
        }
        finally
        {
            _settingName = false;
        }
    }

    private string DefaultName() => _request.BaseName + Checksums.Extension(_kind);

    private void OutputChanged()
    {
        _job = null;
        _done = null;
        Refresh();
    }

    // ---------------------------------------------------------------- layout

    private Control Build()
    {
        var page = new StackPanel { Margin = new Thickness(22, 20, 22, 8) };

        // the header: the tile, the title, what is selected
        var tile = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Child = MenuIcons.Create("nc_CRCcreate") is { } icon ? new Viewbox { Width = 22, Height = 22, Child = icon } : null };
        tile[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnAccentSoft");
        var sources = _request.Sources;
        var files = sources.Where(s => !s.IsDirectory).ToList();
        var what = sources.Count == 1 ? sources[0].Name : Core.Localization.Localizer.T("{0} items").Replace("{0}", sources.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        source.Children.Add(new TextBlock { Text = what, FontFamily = ConnectionLook.Mono, FontSize = 12, MaxWidth = 460, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
        if (files.Count == sources.Count)
        {
            source.Children.Add(ConnectionLook.Muted(new TextBlock { Text = "· " + SizeText(files.Sum(f => f.Size)), FontSize = 12.5, Classes = { "noloc" } }));
        }
        ToolTip.SetTip(source, string.Join(", ", sources.Select(s => s.Name)));
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        titles.Children.AddRange([new TextBlock { Text = "Create checksums", FontSize = 17, FontWeight = FontWeight.SemiBold }, source]);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.AddRange([tile, titles]);
        page.Children.Add(head);

        // the algorithm
        page.Children.Add(Heading("Algorithm"));
        foreach (var (kind, hint, _, _) in s_algorithms.Where(a => Checksums.Available.Contains(a.Kind)))
        {
            _cards.Children.Add(AlgorithmCard(kind, hint));
        }
        page.Children.Add(_cards);
        _algorithmNote.Margin = new Thickness(0, 10, 0, 16);
        page.Children.Add(_algorithmNote);

        // where the result goes
        page.Children.Add(Heading("Save the result"));
        var browse = new Button { Content = "…", Classes = { "tb", "icon", "noloc" } };
        ToolTip.SetTip(browse, "Choose the folder");
        browse.Click += async (_, _) => await BrowseAsync();
        var nameRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 0, 0, 0);
        nameRow.Children.AddRange([browse, _name]);
        var oneMore = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        oneMore.Children.AddRange([nameRow, ConnectionLook.Muted(_nameHint)]);
        var many = sources.Count > 1 || files.Count < sources.Count;
        var options = new StackPanel();
        options.Children.Add(Option(_oneFile, "One checksum file",
            many ? "All the checksums in one list, next to the files." : "Saved next to the file.", oneMore));
        options.Children.Add(Option(_eachFile, "A checksum file for each file",
            many ? "Small files, one beside each file." : "The same as above for a single file.", ConnectionLook.Muted(_eachNames)));
        options.Children.Add(Option(_clipboard, "Copy to the clipboard only", "No file is written; paste the checksum into a message or a web page.", null));
        page.Children.Add(ConnectionLook.Card(options, new Thickness(0)));

        // the preview: the lines (placeholders until they are read), the line format, the progress
        var previewHead = new DockPanel { Margin = new Thickness(2, 4, 0, 8) };
        _format.Children.AddRange([_standard, _bsdStyle]);
        ToolTip.SetTip(_standard, "hash *name: read by sha256sum and most tools");
        ToolTip.SetTip(_bsdStyle, "SHA256 (name) = hash: the macOS and BSD style");
        DockPanel.SetDock(_format, Dock.Right);
        previewHead.Children.AddRange([_format, new TextBlock { Text = "Preview", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }]);
        page.Children.Add(previewHead);
        var bar = new DockPanel { Margin = new Thickness(12, 4, 6, 4) };
        DockPanel.SetDock(_copy, Dock.Right);
        bar.Children.AddRange([_copy, ConnectionLook.Muted(_previewName)]);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        var scroller = new ScrollViewer { Content = _lines, MaxHeight = 150, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var preview = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        preview.Children.AddRange([barBorder, _progress, scroller]);
        var previewCard = ConnectionLook.Card(preview, new Thickness(0));
        previewCard.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(previewCard);
        page.Children.Add(_expected);
        page.Children.Add(_verdict);

        // the bar at the bottom: what will be done, Cancel, Create
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([_cancel, _create]);
        var footer = new DockPanel { Margin = new Thickness(22, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        _summary.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.AddRange([buttons, ConnectionLook.Muted(_summary)]);
        var footerBar = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBar[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnSide");
        footerBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");

        var root = new DockPanel();
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.AddRange([footerBar, new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }]);
        return root;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 0) };

    /// <summary>An algorithm's card: its name and a word about it; SHA-256 has "Recommended" over its top edge.</summary>
    private Control AlgorithmCard(ChecksumKind kind, string hint)
    {
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        text.Children.AddRange([
            new TextBlock { Text = Checksums.DisplayName(kind).Replace(" (SFV)", string.Empty, StringComparison.Ordinal), FontSize = 13.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "noloc" } },
            new TextBlock { Text = hint, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "hint" } },
        ]);
        var card = new Button { Content = text, Tag = kind, Padding = new Thickness(6, 9, 6, 8), Margin = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "conncard", "algcard" } };
        Avalonia.Automation.AutomationProperties.SetName(card, Checksums.DisplayName(kind));
        card.Click += (_, _) => SetKind(kind);
        if (kind != ChecksumKind.Sha256)
        {
            return card;
        }
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, 0, 0), IsHitTestVisible = false };
        badge[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnAccent");
        var label = new TextBlock { Text = "Recommended", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
        badge.Child = label;
        var panel = new Panel { ClipToBounds = false };
        panel.Children.AddRange([card, badge]);
        return panel;
    }

    /// <summary>A choice of where the result goes: its radio button, title, line and the part shown while it is chosen.</summary>
    private static Control Option(RadioButton radio, string title, string description, Control? more)
    {
        var text = new StackPanel();
        text.Children.AddRange([
            new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.Medium },
            ConnectionLook.Muted(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap }),
        ]);
        if (more is not null)
        {
            more[!IsVisibleProperty] = radio[!ToggleButton.IsCheckedProperty];
            text.Children.Add(more);
        }
        radio.Content = text;
        radio.HorizontalAlignment = HorizontalAlignment.Stretch;
        radio.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        radio.VerticalContentAlignment = VerticalAlignment.Top;
        radio.Padding = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(radio, title);
        var row = new Border { Child = radio, Padding = new Thickness(14, 12), BorderThickness = new Thickness(0, 0, 0, 1) };
        row[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        return row;
    }

    private static string SizeText(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return (i == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture)) + " " + units[i];
    }

    // ---------------------------------------------------------------- state

    /// <summary>Everything that follows the choices: the cards, the note, the names, the preview, the summary, the buttons.</summary>
    private void Refresh()
    {
        var algorithm = s_algorithms.First(a => a.Kind == _kind);
        foreach (var card in _cards.GetLogicalDescendantsOf<Button>())
        {
            card.Classes.Set("on", Equals(card.Tag, _kind));
        }
        var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var noteText = new TextBlock { Text = algorithm.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var sign = new TextBlock { Text = algorithm.Weak ? "!" : "i", FontWeight = FontWeight.Bold, FontSize = 12, Width = 14, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        foreach (var t in new[] { noteText, sign })
        {
            t[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(algorithm.Weak ? "ConnWarn" : "ConnText2");
        }
        note.Children.AddRange([sign, noteText]);
        _algorithmNote.Content = note;

        var output = Output;
        var extension = Checksums.Extension(_kind);
        var name = (_name.Text ?? string.Empty).Trim();
        _nameHint.Text = name.Length == 0
            ? Core.Localization.Localizer.T("Enter a file name.")
            : Core.Localization.Localizer.T("In {0} · the extension follows the algorithm").Replace("{0}", _folder.ToDisplayString(), StringComparison.Ordinal);
        if (name.Length == 0)
        {
            ConnectionLook.Tint(_nameHint, "problem");
        }
        else
        {
            ConnectionLook.Muted(_nameHint, 2);
        }
        var sources = _request.Sources;
        var eachNames = sources.Take(3).Select(s => s.IsDirectory ? s.Name + "/…" + extension : s.Name + extension).ToList();
        if (sources.Count > 3)
        {
            eachNames.Add(Core.Localization.Localizer.T("… and {0} more").Replace("{0}", (sources.Count - 3).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal));
        }
        _eachNames.Text = string.Join("\n", eachNames);
        _eachNames.Margin = new Thickness(0, 8, 0, 0);

        _format.IsVisible = _kind != ChecksumKind.Crc32;
        _standard.IsChecked = !_bsd;
        _bsdStyle.IsChecked = _bsd;
        _previewName.Text = output switch
        {
            ChecksumOutput.Clipboard => Core.Localization.Localizer.T("Clipboard"),
            ChecksumOutput.EachFile => (sources[0].Name + extension) + (sources.Count > 1 ? $" (+{sources.Count - 1})" : string.Empty),
            _ => name.Length > 0 ? name : DefaultName(),
        };
        if (_job is { } job && !_running)
        {
            _lines.Text = string.Join("\n", output == ChecksumOutput.EachFile ? job.Lines.Take(1) : job.Lines);
            _lines.ClearValue(TextBlock.ForegroundProperty);
            _copy.IsEnabled = job.Lines.Count > 0;
        }
        else
        {
            // placeholders as long as the algorithm's checksum, in the chosen format
            var dots = new string('·', Checksums.HexLength(_kind));
            var shown = output == ChecksumOutput.EachFile ? sources.Take(1) : sources.Take(8);
            var bsd = _bsd && _kind != ChecksumKind.Crc32;
            _lines.Text = string.Join("\n", shown.Select(s => Checksums.FormatLine(_kind, dots, s.IsDirectory ? s.Name + "/…" : s.Name, bsd)))
                + "\n" + Core.Localization.Localizer.T("# The checksums appear here after Create.");
            ConnectionLook.Muted(_lines, 3);
            _copy.IsEnabled = false;
        }

        _create.Content = _running ? Core.Localization.Localizer.T("Calculating…")
            : _job is not null && output != ChecksumOutput.Clipboard ? Core.Localization.Localizer.T("Done")
            : output == ChecksumOutput.Clipboard ? Core.Localization.Localizer.T("Copy to clipboard")
            : output == ChecksumOutput.EachFile && sources.Count > 1 ? Core.Localization.Localizer.T("Create {0} files").Replace("{0}", sources.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            : Core.Localization.Localizer.T("Create");
        _create.IsEnabled = !_running && !(output == ChecksumOutput.OneFile && name.Length == 0);
        _cards.IsEnabled = !_running;
        _summary.Text = _done ?? Checksums.DisplayName(_kind) + " · " + output switch
        {
            ChecksumOutput.OneFile => name,
            ChecksumOutput.EachFile => sources.Count + " × " + extension,
            _ => Core.Localization.Localizer.T("clipboard"),
        };
        if (_done is not null)
        {
            ConnectionLook.Tint(_summary, "ok");
        }
        else
        {
            ConnectionLook.Muted(_summary, 2);
        }
        Verify();
    }

    /// <summary>A checksum pasted before reading: the algorithm of its length is chosen when only one has it.</summary>
    private void ExpectedChanged()
    {
        var text = new string((_expected.Text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (_job is null && !_running && text.Length > 0 && text.All(Uri.IsHexDigit))
        {
            var fits = Kinds().Where(k => Checksums.HexLength(k) == text.Length).ToList();
            if (fits.Count == 1 && fits[0] != _kind)
            {
                SetKind(fits[0]);
                _done = null;
                _summary.Text = Core.Localization.Localizer.T("Switched to {0} to match the pasted checksum").Replace("{0}", Checksums.DisplayName(fits[0]), StringComparison.Ordinal);
                return;
            }
        }
        Verify();
    }

    /// <summary>The comparison with the expected checksum: match (which file), no match (another algorithm of its length?).</summary>
    private void Verify()
    {
        var text = new string((_expected.Text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (text.Length == 0)
        {
            _verdict.Content = null;
            return;
        }
        if (_job is not { } job || _running)
        {
            _verdict.Content = Line("i", Core.Localization.Localizer.T("Compared once the checksum is calculated."), null);
            return;
        }
        var match = job.Results.FirstOrDefault(r => string.Equals(r.Hash, text, StringComparison.OrdinalIgnoreCase));
        if (match.File is not null)
        {
            _verdict.Content = Line("✓", Core.Localization.Localizer.T("Match: {0} is exactly the file it should be.").Replace("{0}", match.File.Name, StringComparison.Ordinal), "ok");
            return;
        }
        // a checksum of another length: another algorithm made it (the first one of that length)
        if (Checksums.HexLength(_kind) != text.Length && Kinds().Where(k => Checksums.HexLength(k) == text.Length).ToList() is [var guess, ..])
        {
            var change = new Button { Content = Core.Localization.Localizer.T("Switch to {0}").Replace("{0}", Checksums.DisplayName(guess), StringComparison.Ordinal), Classes = { "tb", "ghost" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Margin = new Thickness(6, -2, 0, -2) };
            change.Click += async (_, _) =>
            {
                SetKind(guess);
                await CreateOrCloseAsync();
            };
            _verdict.Content = Line("!", Core.Localization.Localizer.T("No match. This looks like a {0} checksum.").Replace("{0}", Checksums.DisplayName(guess), StringComparison.Ordinal), "problem", change);
            return;
        }
        _verdict.Content = Line("!", Core.Localization.Localizer.T("No match. The file may be damaged or a different version."), "problem");
    }

    private static StackPanel Line(string glyph, string text, string? kind, Control? trailing = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var sign = new TextBlock { Text = glyph, FontWeight = FontWeight.Bold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var words = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
        foreach (var t in new[] { sign, words })
        {
            if (kind is null)
            {
                ConnectionLook.Muted(t, 2);
            }
            else
            {
                ConnectionLook.Tint(t, kind);
            }
        }
        line.Children.AddRange([sign, words]);
        if (trailing is not null)
        {
            line.Children.Add(trailing);
        }
        return line;
    }

    // ---------------------------------------------------------------- actions

    private async Task BrowseAsync()
    {
        if (!_folder.IsLocal)
        {
            return;
        }
        var start = await StorageProvider.TryGetFolderFromPathAsync(_folder.Path);
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder of the checksum file", SuggestedStartLocation = start });
        if (picked is [var folder, ..] && folder.TryGetLocalPath() is { } path)
        {
            _folder = VfsPath.Local(path);
            Refresh();
        }
    }

    /// <summary>Create (or Copy): reads the files with the chosen algorithm; after it, Done closes the dialog.</summary>
    public async Task CreateOrCloseAsync()
    {
        if (_running)
        {
            return;
        }
        if (_job is not null && Output != ChecksumOutput.Clipboard)
        {
            Close();
            return;
        }
        var job = _request.MakeJob(Choice);
        _job = job;
        _running = true;
        _done = null;
        Refresh();
        _timer.Start(TimeSpan.FromMilliseconds(100), () => _progress.Value = job.Progress.Snapshot().Fraction);
        await job.RunAsync();
        _timer.Stop();
        _running = false;
        _progress.Value = 0;
        Result = job;
        var count = job.Results.Count;
        _done = job.State switch
        {
            JobState.Cancelled => Core.Localization.Localizer.T("Cancelled."),
            JobState.Failed => job.Failure?.Message ?? Core.Localization.Localizer.T("Failed."),
            _ when count == 0 => Core.Localization.Localizer.T("No file could be read."),
            _ when Output == ChecksumOutput.Clipboard => Core.Localization.Localizer.T("{0} checksums copied to the clipboard").Replace("{0}", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal),
            _ when Output == ChecksumOutput.OneFile => Core.Localization.Localizer.T("Saved {0}").Replace("{0}", (_name.Text ?? string.Empty).Trim(), StringComparison.Ordinal),
            _ => Core.Localization.Localizer.T("Saved {0} checksum files").Replace("{0}", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal),
        };
        if (job.State is JobState.Cancelled or JobState.Failed)
        {
            _job = null;
        }
        else if (Output == ChecksumOutput.Clipboard)
        {
            await CopyAsync();
        }
        Refresh();
        if (job.State is JobState.Cancelled or JobState.Failed or JobState.CompletedWithErrors)
        {
            ConnectionLook.Tint(_summary, "problem");
        }
    }

    private async Task CopyAsync()
    {
        if (_job is { Lines.Count: > 0 } job && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, job.Lines));
        }
    }

    /// <summary>A timer that runs one action until stopped.</summary>
    private sealed class DispatcherTimerHolder
    {
        private Avalonia.Threading.DispatcherTimer? _timer;

        public void Start(TimeSpan interval, Action tick)
        {
            Stop();
            _timer = new Avalonia.Threading.DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => tick();
            _timer.Start();
        }

        public void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }
    }
}

internal static class ChecksumDialogExtensions
{
    /// <summary>The descendants of one type in the logical tree.</summary>
    public static IEnumerable<T> GetLogicalDescendantsOf<T>(this Control control)
        where T : class => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(control).OfType<T>();

    /// <summary>The texts of a control and the controls in it, one after the other.</summary>
    public static string GetLogicalDescendantsText(this Control control) =>
        string.Join(" ", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(control).OfType<TextBlock>().Select(t => t.Text));
}
