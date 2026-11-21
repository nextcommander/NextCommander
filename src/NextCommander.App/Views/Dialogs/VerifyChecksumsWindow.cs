using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The checksum files to verify, the job that reads them, the registry to check one file again, how the files are
/// selected in the panel and where a report is saved.
/// </summary>
public sealed record VerifyChecksumsRequest(IReadOnlyList<VfsEntry> ChecksumFiles, Func<VerifyChecksumsJob> MakeJob, ProviderRegistry Registry,
    Func<IReadOnlyList<VfsPath>, Task> SelectInPanel, Func<string, string, Task<string?>> PickSaveFile);

/// <summary>
/// Verify checksums (the user's template, 21 November 2026): a verdict on top (intact, how many have a problem, the
/// progress while checking), filters by state and a name filter, a row per file (problems first; a damaged one shows the
/// expected and the actual checksum with the different digits marked), check a file again, show or select files in the
/// panel, save a report.
/// </summary>
public sealed class VerifyChecksumsWindow : Window
{
    private readonly VerifyChecksumsRequest _request;
    private readonly UserControl _view = new();
    private readonly Border _verdictTile = new() { Width = 52, Height = 52, CornerRadius = new CornerRadius(14) };
    private readonly TextBlock _verdictGlyph = new() { FontSize = 24, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _title = new() { FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
    private readonly TextBlock _subtitle = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Classes = { "noloc" } };
    private readonly Button _again = new() { Classes = { "tb" } };
    private readonly ProgressBar _progress = new() { Height = 3, MinHeight = 3, Minimum = 0, Maximum = 1, CornerRadius = new CornerRadius(0), IsVisible = false };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { PlaceholderText = "Filter by name", Width = 190 };
    private readonly Border _filters = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(22, 10) };
    private readonly StackPanel _rows = new() { Margin = new Thickness(22, 10, 22, 14) };
    private readonly TextBlock _info = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly Button _save = new() { Content = "Save report", Classes = { "tb" } };
    private readonly Button _close = new() { Content = "Close", Classes = { "tb" }, IsCancel = true };
    private readonly Button _action = new() { Classes = { "tb", "primary", "noloc" } };
    private readonly HashSet<ChecksumResult> _selected = [];
    private List<ChecksumResult> _results = [];
    private string _filter = "all";
    private bool _running;
    private DateTime? _checkedAt;
    private string? _note;
    private VerifyChecksumsJob? _job;

    public VerifyChecksumsWindow(VerifyChecksumsRequest request)
    {
        _request = request;
        Title = "Verify checksums";
        Width = 860;
        Height = 720;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConnectionLook.Apply(_view);
        _view.Content = Build();
        Content = _view;
        _again.Click += async (_, _) => await CheckAsync();
        _search.TextChanged += (_, _) => RenderRows();
        _save.Click += async (_, _) => await SaveReportAsync();
        _close.Click += (_, _) => Close();
        _action.Click += async (_, _) => await ActionAsync();
        Closed += (_, _) => _job?.Cancel();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await CheckAsync();
            }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _search.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _search.IsFocused && !string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = string.Empty;
                e.Handled = true;
            }
        };
        Opened += async (_, _) => await CheckAsync();
        Render();
    }

    /// <summary>The results shown (tests).</summary>
    public IReadOnlyList<ChecksumResult> Results => _results;

    /// <summary>The verdict's title (tests).</summary>
    public string Verdict => _title.Text ?? string.Empty;

    /// <summary>The rows shown now, in their order (tests): the names.</summary>
    public IReadOnlyList<string> ShownNames => [.. Shown().Select(r => r.Name)];

    /// <summary>The text of the main button (tests).</summary>
    public string ActionText => _actionText;

    private string _actionText = string.Empty;

    public void SetFilter(string filter)
    {
        _filter = filter;
        Render();
    }

    public void SetSearch(string text) => _search.Text = text;

    // ---------------------------------------------------------------- layout

    private Control Build()
    {
        // the verdict: its tile, the title, where the checksums are from; Check again
        _verdictTile.Child = _verdictGlyph;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        titles.Children.AddRange([_title, ConnectionLook.Muted(_subtitle)]);
        _again.VerticalAlignment = VerticalAlignment.Center;
        var verdict = new DockPanel { Margin = new Thickness(22, 20, 22, 16) };
        DockPanel.SetDock(_verdictTile, Dock.Left);
        DockPanel.SetDock(_again, Dock.Right);
        verdict.Children.AddRange([_verdictTile, _again, titles]);
        var verdictBar = new Border { Child = verdict, BorderThickness = new Thickness(0, 0, 0, 1) };
        verdictBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");

        // the filters (only with more than one file) and the name filter
        _search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 14, Opacity = 0.55, Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(_search, "Filter by name");
        var filterRow = new DockPanel();
        DockPanel.SetDock(_search, Dock.Right);
        filterRow.Children.AddRange([_search, _chips]);
        _filters.Child = filterRow;
        _filters[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");

        // the bar at the bottom: what was checked, Save report, Close and the main action
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([_save, _close, _action]);
        var footer = new DockPanel { Margin = new Thickness(22, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.AddRange([buttons, ConnectionLook.Muted(_info)]);
        var footerBar = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBar[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnSide");
        footerBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");

        var root = new DockPanel();
        foreach (var top in new Control[] { verdictBar, _progress, _filters })
        {
            DockPanel.SetDock(top, Dock.Top);
        }
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.AddRange([verdictBar, _progress, _filters, footerBar, new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }]);
        return root;
    }

    private IBrush Brush(string key) =>
        _view.TryFindResource(key, _view.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    private static string T(string text) => Core.Localization.Localizer.T(text);

    private static string Count(string pattern, int n) => T(pattern).Replace("{0}", n.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

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

    /// <summary>A state's word, its line and its colors (glyph, the color of its text, its tint).</summary>
    private static (string Tag, string Line, string Glyph, string Color, string Back) Look(ChecksumStatus status) => status switch
    {
        ChecksumStatus.Ok => ("OK", "Checksum matches", "✓", "ConnOk", "ConnOkBack"),
        ChecksumStatus.Mismatch => ("Damaged", "The checksum does not match: the file changed or the copy is broken", "✕", "ConnProblem", "ConnProblemBack"),
        ChecksumStatus.Missing => ("Missing", "Listed in the checksum file, but not found", "?", "ConnWarn", "ConnWarnBack"),
        _ => ("Unreadable", "Could not be read", "!", "ConnText2", "ConnHover"),
    };

    private string Folder => _request.ChecksumFiles.Count > 0 && _request.ChecksumFiles[0].Path.Parent is { } dir ? dir.ToDisplayString() : string.Empty;

    // ---------------------------------------------------------------- state

    private int Problems => _results.Count(r => r.Status != ChecksumStatus.Ok);

    private void Render()
    {
        var n = _results.Count;
        var problems = Problems;
        var (glyph, color, back) = _running ? ("↻", "ConnAccent", "ConnAccentSoft")
            : problems == 0 && n > 0 ? ("✓", "ConnOk", "ConnOkBack")
            : _results.Any(r => r.Status == ChecksumStatus.Mismatch) ? ("✕", "ConnProblem", "ConnProblemBack")
            : n == 0 ? ("?", "ConnWarn", "ConnWarnBack")
            : ("!", "ConnWarn", "ConnWarnBack");
        _verdictGlyph.Text = glyph;
        _verdictGlyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(color);
        _verdictTile[!Border.BackgroundProperty] = new DynamicResourceExtension(back);
        _title.Text = _running ? (n > 0 ? Count("Checking {0} files…", n) : T("Checking…"))
            : n == 0 ? T("No file is listed in the checksum file")
            : problems == 0 ? (n == 1 ? T("The file is intact") : Count("All {0} files are intact", n))
            : n == 1 ? T("The file has a problem")
            : T("{0} of {1} files have a problem").Replace("{0}", problems.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal).Replace("{1}", n.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        var kinds = _results.Select(r => r.Kind).Distinct().ToList();
        var algorithm = kinds.Count == 1 ? Checksums.DisplayName(kinds[0]) : kinds.Count == 0 && _request.ChecksumFiles.Count > 0 && Checksums.FromFileName(_request.ChecksumFiles[0].Name) is { } k ? Checksums.DisplayName(k) : T("Checksums");
        var source = _request.ChecksumFiles.Count == 1 ? _request.ChecksumFiles[0].Name : Count("{0} checksum files", _request.ChecksumFiles.Count);
        _subtitle.Text = T("{0} from {1}").Replace("{0}", algorithm, StringComparison.Ordinal).Replace("{1}", source, StringComparison.Ordinal)
            + " · " + Folder + (_checkedAt is { } at && !_running ? " · " + T("checked {0}").Replace("{0}", at.ToString("t", CultureInfo.CurrentCulture), StringComparison.Ordinal) : string.Empty);
        Title = T("Verification: {0} OK, {1} with problems").Replace("{0}", (n - problems).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{1}", problems.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        _again.Content = new TextBlock { Text = "↻  " + T("Check again"), Classes = { "noloc" } };
        _again.IsEnabled = !_running;
        _progress.IsVisible = _running;

        // the chips: All and Problems always, the states that occur
        _filters.IsVisible = n > 1;
        _chips.Children.Clear();
        (string Key, string Text, int Count, string? Color)[] chips =
        [
            ("all", "All", n, null),
            ("problems", "Problems", problems, "ConnProblem"),
            ("ok", "OK", _results.Count(r => r.Status == ChecksumStatus.Ok), "ConnOk"),
            ("bad", "Damaged", _results.Count(r => r.Status == ChecksumStatus.Mismatch), "ConnProblem"),
            ("miss", "Missing", _results.Count(r => r.Status == ChecksumStatus.Missing), "ConnWarn"),
            ("err", "Unreadable", _results.Count(r => r.Status == ChecksumStatus.ReadError), "ConnText3"),
        ];
        foreach (var (key, text, count, dot) in chips.Where(c => c.Key is "all" or "problems" || c.Count > 0))
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (dot is not null)
            {
                var mark = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
                mark[!Border.BackgroundProperty] = new DynamicResourceExtension(dot);
                label.Children.Add(mark);
            }
            label.Children.AddRange([
                new TextBlock { Text = T(text), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            ]);
            var chip = new ToggleButton { Content = label, Tag = key, IsChecked = _filter == key, IsEnabled = key == "all" || count > 0, Margin = new Thickness(0, 0, 6, 0), Classes = { "connchip" }, MinHeight = 28 };
            var chosen = key;
            chip.Click += (_, _) => SetFilter(chosen);
            _chips.Children.Add(chip);
        }
        RenderRows();
    }

    /// <summary>The results the filters leave: problems first (damaged, missing, unreadable), then the rest in order.</summary>
    private List<ChecksumResult> Shown()
    {
        var q = (_search.Text ?? string.Empty).Trim();
        static int Order(ChecksumStatus s) => s switch { ChecksumStatus.Mismatch => 0, ChecksumStatus.Missing => 1, ChecksumStatus.ReadError => 2, _ => 3 };
        return [.. _results.Where(r => _filter switch
            {
                "problems" => r.Status != ChecksumStatus.Ok,
                "ok" => r.Status == ChecksumStatus.Ok,
                "bad" => r.Status == ChecksumStatus.Mismatch,
                "miss" => r.Status == ChecksumStatus.Missing,
                "err" => r.Status == ChecksumStatus.ReadError,
                _ => true,
            } && (q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .Select((r, i) => (r, i)).OrderBy(x => Order(x.r.Status)).ThenBy(x => x.i).Select(x => x.r)];
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        var shown = Shown();
        if (shown.Count == 0 && _results.Count > 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
            if (_filter == "problems" && string.IsNullOrWhiteSpace(_search.Text))
            {
                empty.Children.AddRange([
                    new TextBlock { Text = "No problems", FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    ConnectionLook.Muted(new TextBlock { Text = "Every file matches its checksum.", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) }),
                ]);
            }
            else
            {
                empty.Children.Add(ConnectionLook.Muted(new TextBlock { Text = "Nothing matches.", FontSize = 13 }));
            }
            _rows.Children.Add(empty);
        }
        foreach (var result in shown)
        {
            _rows.Children.Add(Row(result));
        }
        UpdateFooter();
    }

    /// <summary>A file's row: its state's tile, the name with its tag, a line about it, the checksums of a damaged one, its actions.</summary>
    private Control Row(ChecksumResult r)
    {
        var (tag, line, glyph, color, back) = Look(r.Status);
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        tile[!Border.BackgroundProperty] = new DynamicResourceExtension(back);
        var sign = new TextBlock { Text = glyph, FontSize = 14, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        sign[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(color);
        tile.Child = sign;

        var tagText = new TextBlock { Text = T(tag), FontSize = 11, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        tagText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(color);
        var tagBox = new Border { Child = tagText, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tagBox[!Border.BackgroundProperty] = new DynamicResourceExtension(back);
        var name = new DockPanel();
        DockPanel.SetDock(tagBox, Dock.Right);
        name.Children.AddRange([tagBox, new TextBlock { Text = r.Name, FontSize = 13.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } }]);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(name, r.Name);

        var metaText = (r.Error ?? T(line)) + " · " + (r.Status == ChecksumStatus.Missing ? T("expected") : SizeText(r.Size));
        var folder = r.Path?.Parent?.ToDisplayString();
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 1, 0, 0) };
        meta.Children.Add(ConnectionLook.Muted(new TextBlock { Text = metaText, FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } }));
        if (folder is not null && folder != Folder)
        {
            meta.Children.Add(ConnectionLook.Muted(new TextBlock { Text = folder, FontFamily = ConnectionLook.Mono, FontSize = 11.5, Classes = { "noloc" } }, 3));
        }
        var text = new StackPanel();
        text.Children.AddRange([name, meta]);
        if (r.Status == ChecksumStatus.Mismatch && r.Actual is { } actual)
        {
            text.Children.Add(Hashes([("Expected", r.Expected.ToLowerInvariant(), null), ("Actual", actual.ToLowerInvariant(), r.Expected.ToLowerInvariant())]));
        }
        else if (r.Status == ChecksumStatus.Ok && _results.Count == 1)
        {
            text.Children.Add(Hashes([(Checksums.DisplayName(r.Kind), r.Expected.ToLowerInvariant(), null)]));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        if (r.Status != ChecksumStatus.Missing && r.Path is { } path)
        {
            actions.Children.Add(Action("◫", "Show in panel", async () => await _request.SelectInPanel([path])));
        }
        if (r.Actual is { } hash)
        {
            actions.Children.Add(Action("⧉", "Copy the checksum", async () =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(hash.ToLowerInvariant());
                    Note(T("Checksum copied"));
                }
            }));
        }
        if (r.Status != ChecksumStatus.Ok)
        {
            actions.Children.Add(Action("↻", "Check this file again", async () => await RecheckAsync(r)));
        }

        var row = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.AddRange([tile, actions, text]);
        var card = new Border { Child = row, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6), Tag = r, Focusable = true };
        var selected = _selected.Contains(r);
        card[!Border.BackgroundProperty] = new DynamicResourceExtension(selected ? "ConnAccentSoft" : "ConnCard");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension(selected ? "ConnAccent" : "ConnStroke");
        card.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual source && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(source, includeSelf: true) is not null)
            {
                return;
            }
            Select(r, e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                Select(r, e.KeyModifiers.HasFlag(KeyModifiers.Control));
                e.Handled = true;
            }
        };
        Avalonia.Automation.AutomationProperties.SetName(card, r.Name);
        return card;
    }

    /// <summary>Checksums one under the other, with their caption; the digits that differ from the expected ones are marked.</summary>
    private Grid Hashes(IEnumerable<(string Caption, string Hash, string? Against)> lines)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), Margin = new Thickness(0, 8, 0, 0) };
        var row = 0;
        foreach (var (caption, hash, against) in lines)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = ConnectionLook.Muted(new TextBlock { Text = caption, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }, 3);
            if (caption is "Expected" or "Actual")
            {
                label.Text = T(caption);
            }
            var value = new SelectableTextBlock { FontFamily = ConnectionLook.Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
            ToolTip.SetTip(value, hash);
            if (against is null)
            {
                value.Text = hash;
            }
            else
            {
                var problem = Brush("ConnProblem");
                var tint = Brush("ConnProblemBack");
                for (var i = 0; i < hash.Length; i++)
                {
                    var same = i < against.Length && against[i] == hash[i];
                    var run = new Run(hash[i].ToString());
                    if (!same)
                    {
                        run.Foreground = problem;
                        run.Background = tint;
                    }
                    value.Inlines!.Add(run);
                }
            }
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.AddRange([label, value]);
            row++;
        }
        return grid;
    }

    private static Button Action(string glyph, string tip, Func<Task> click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            Width = 28, Height = 28, MinHeight = 28, Classes = { "tb", "ghost", "icon" },
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    private void Select(ChecksumResult r, bool add)
    {
        if (add)
        {
            if (!_selected.Remove(r))
            {
                _selected.Add(r);
            }
        }
        else if (_selected.Count == 1 && _selected.Contains(r))
        {
            _selected.Clear();
        }
        else
        {
            _selected.Clear();
            _selected.Add(r);
        }
        RenderRows();
    }

    /// <summary>The bar at the bottom: the count (or the selection), and the main action that follows the result.</summary>
    private void UpdateFooter()
    {
        var problems = Problems;
        _info.Text = _note ?? (_selected.Count > 0 ? Count("{0} selected", _selected.Count)
            : Count(_results.Count == 1 ? "1 file" : "{0} files", _results.Count) + " · " + T("{0} checked").Replace("{0}", SizeText(_results.Sum(r => r.Size)), StringComparison.Ordinal));
        _actionText = _selected.Count > 0 ? Count("Select {0} in panel", _selected.Count)
            : problems > 0 ? Count("Select {0} with problems in panel", problems)
            : _results.Count == 1 ? T("Show in panel")
            : T("Select all in panel");
        _action.Content = _actionText; // translated here (a filled-in pattern): the button is noloc
        _action.IsEnabled = !_running && _results.Count > 0;
        _save.IsEnabled = !_running && _results.Count > 0;
    }

    private void Note(string text)
    {
        _note = text;
        UpdateFooter();
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _note = null;
            UpdateFooter();
        }, TimeSpan.FromSeconds(2.5));
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Reads every file of the checksum files (again), with the progress in the verdict.</summary>
    public async Task CheckAsync()
    {
        if (_running)
        {
            return;
        }
        var job = _request.MakeJob();
        _job = job;
        _running = true;
        _selected.Clear();
        Render();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => _progress.Value = job.Progress.Snapshot().Fraction;
        timer.Start();
        await job.RunAsync();
        timer.Stop();
        _running = false;
        _results = [.. job.Results];
        _checkedAt = DateTime.Now;
        if (job.State == JobState.Failed)
        {
            _note = job.Failure?.Message;
        }
        Render();
    }

    /// <summary>One file again (a damaged, missing or unreadable one): its row follows; a note says when it is unchanged.</summary>
    private async Task RecheckAsync(ChecksumResult r)
    {
        var again = await VerifyChecksumsJob.CheckAsync(_request.Registry, r, CancellationToken.None);
        var i = _results.IndexOf(r);
        if (i >= 0)
        {
            _results[i] = again;
        }
        if (_selected.Remove(r))
        {
            _selected.Add(again);
        }
        Render();
        Note(again.Status switch
        {
            ChecksumStatus.Ok => T("It matches now"),
            ChecksumStatus.ReadError => T("Still cannot be read: close the program using it and try again"),
            ChecksumStatus.Missing => T("Still not found"),
            _ => T("Still does not match"),
        });
    }

    /// <summary>The main action: the selected rows, else the files with problems, else every file, selected in the panel.</summary>
    private async Task ActionAsync()
    {
        var files = (_selected.Count > 0 ? _selected.ToList() : Problems > 0 ? _results.Where(r => r.Status != ChecksumStatus.Ok).ToList() : _results)
            .Where(r => r.Status != ChecksumStatus.Missing && r.Path is not null).Select(r => r.Path!).ToList();
        if (files.Count > 0)
        {
            await _request.SelectInPanel(files);
        }
    }

    /// <summary>A text report: the verdict, then every file with its state (and both checksums of a damaged one).</summary>
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine(_title.Text).AppendLine(_subtitle.Text).AppendLine();
        foreach (var r in _results)
        {
            text.Append(T(Look(r.Status).Tag).PadRight(12)).AppendLine(r.Name);
            if (r.Status == ChecksumStatus.Mismatch)
            {
                text.Append(' ', 12).Append(T("Expected")).Append(": ").AppendLine(r.Expected.ToLowerInvariant());
                text.Append(' ', 12).Append(T("Actual")).Append(": ").AppendLine(r.Actual?.ToLowerInvariant());
            }
            else if (r.Error is { } error)
            {
                text.Append(' ', 12).AppendLine(error);
            }
        }
        return text.ToString();
    }

    private async Task SaveReportAsync()
    {
        var name = Path.GetFileNameWithoutExtension(_request.ChecksumFiles.Count > 0 ? _request.ChecksumFiles[0].Name : "checksums") + " - verification.txt";
        if (await _request.PickSaveFile(T("Save report"), name) is { } path)
        {
            await File.WriteAllTextAsync(path, Report());
            Note(T("Report saved"));
        }
    }
}
