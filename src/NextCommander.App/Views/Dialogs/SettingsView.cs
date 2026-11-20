using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// One editable setting on a page ("Görünüm/Renk") inside a group box. <see cref="Editor"/> edits the window's working
/// copy; <see cref="Copy"/> writes the value from the working copy to the live settings (Apply / OK).
/// </summary>
public sealed record SettingItem(string Page, string Group, string Label, string? Description, Func<AppSettings, Action, Control> Editor,
    Action<AppSettings, AppSettings> Copy, bool IsCheckBox = false, bool Indent = false, bool FullWidth = false);

/// <summary>
/// The configuration: shown in the main window in place of the panels (like the maximized internal terminal), or in a
/// <see cref="SettingsWindow"/> where there is no main window to show it in.
/// <list type="bullet">
/// <item>Left: the pages with their icons, a search box (Ctrl+F) and the number of pages; a dot marks a page with settings
/// changed from the default.</item>
/// <item>Right: the page's place, its title, how many of its settings differ from the default (Reset page), and its groups
/// as cards; each setting is a row with its name and help text on the left and its control on the right. Settings that
/// depend on the one above are indented below it and greyed out while it is off.</item>
/// <item>Below: Help, the number of changes not applied yet, Cancel, Apply, OK.</item>
/// </list>
/// Changes go to a working copy and reach the live settings only with Apply or OK; Cancel drops them.
/// </summary>
public sealed class SettingsView : UserControl
{
    private static readonly string[] s_pages =
    [
        "Layout",
        "Display", "Display/Icons", "Display/Font", "Display/Color", "Display/Data formats", "Display/Folder tabs", "Display/View modes",
        "Display/Language",
        "Operation", "Operation/Edit and view", "Operation/Copy and delete", "Operation/Quick search", "Operation/FTP", "Operation/Proxy", "Operation/Plugins", "Operation/Thumbnails",
        "Operation/Associations", "Operation/File index", "Operation/PC link",
        "Packer", "Packer/Zip", "Packer/7-Zip",
        "Refresh", "Ignore list", "History", "Log file",
        "Misc.",
    ];

    /// <summary>The icon of each page in the navigation (glyphs of the icon pack).</summary>
    private static readonly Dictionary<string, string> s_pageIcons = new(StringComparer.Ordinal)
    {
        ["Layout"] = "MiSplitView", ["Display"] = "MiEye", ["Display/Icons"] = "MiStar", ["Display/Font"] = "MiAttrib", ["Display/Color"] = "MiTheme",
        ["Display/Data formats"] = "MiHash", ["Display/Folder tabs"] = "MiTabs", ["Display/View modes"] = "MiColumns", ["Display/Language"] = "MiGlobe",
        ["Operation"] = "MiGear", ["Operation/Edit and view"] = "MiPencil", ["Operation/Copy and delete"] = "MiCopy", ["Operation/Quick search"] = "MiSearch",
        ["Operation/FTP"] = "MiNetwork", ["Operation/Proxy"] = "MiPlug", ["Operation/Plugins"] = "MiChip", ["Operation/Thumbnails"] = "MiFullView",
        ["Operation/Associations"] = "MiExchange", ["Operation/File index"] = "MiDrive", ["Operation/PC link"] = "MiMonitor", ["Packer"] = "MiStacked", ["Packer/Zip"] = "MiCombine",
        ["Packer/7-Zip"] = "MiSplit", ["Refresh"] = "MiRefresh", ["Ignore list"] = "MiFilter", ["History"] = "MiHistory", ["Log file"] = "MiDoc",
        ["Misc."] = "MiHammer",
    };

    private readonly AppSettings _settings;
    private readonly AppSettings _work;
    private readonly Action _changed;
    private readonly IReadOnlyList<SettingItem> _items;
    private readonly IReadOnlyList<(string Text, Func<Task> Action)> _extras;
    private readonly TextBox _search = new() { PlaceholderText = "Search settings", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _nav = new() { Margin = new Thickness(8, 2, 8, 12) };
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _breadcrumb = new() { FontSize = 12, Opacity = 0.6 };
    private readonly StackPanel _meta = new() { Orientation = Orientation.Horizontal, Spacing = 8, MinHeight = 20, Margin = new Thickness(0, 0, 0, 22) };
    private readonly TextBlock _changedCount = new() { FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new() { Content = "Reset page", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rows = new() { Spacing = 26 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A fresh settings object as JSON: what "default" means for the changed marks and Reset page.</summary>
    private static readonly string s_defaultJson = JsonSerializer.Serialize(new AppSettings(), SettingsStore.JsonOptions);
    private readonly Button _apply = new() { Content = "Apply", IsEnabled = false, Classes = { "dialog" } };
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private readonly TextBlock _unsaved = new() { FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _unsavedDot = Dot();

    /// <summary>The navigation row of every page, and its "changed from the default" dot.</summary>
    private readonly Dictionary<string, (Border Row, Ellipse Dot)> _navItems = [];

    /// <summary>The child pages of a group in the navigation, shown or folded.</summary>
    private readonly Dictionary<string, StackPanel> _navKids = [];

    /// <summary>Pages that have settings changed from the default (computed in the background, then kept up to date).</summary>
    private readonly HashSet<string> _changedPages = [];

    /// <summary>Settings changed since opening or the last Apply.</summary>
    private readonly HashSet<SettingItem> _unapplied = [];
    private string _page = s_pages[0];

    /// <summary>The height of the footer under the page and of the page count under the navigation.</summary>
    private const double FooterHeight = 57;

    /// <param name="changed">Called after the changes were applied (the main window re-applies the settings).</param>
    /// <param name="extraActions">Buttons for things with their own editors (hotkeys, hotlist, connections...), on the Misc. page.</param>
    public SettingsView(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions)
    {
        _settings = settings;
        _work = Clone(settings);
        _changed = changed;
        _extras = extraActions;
        _items = BuildItems();
        if (s_pages.Contains(settings.SettingsLastPage))
        {
            _page = settings.SettingsLastPage!;
        }
        Focusable = true;
        ClipToBounds = true;

        // ---- navigation: brand, search, pages, page count
        var logo = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), [!Border.BackgroundProperty] = Resource("SystemControlHighlightAccentBrush") };
        logo.Child = new TextBlock { Text = "NC", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 6, 14) };
        brand.Children.AddRange([logo, new TextBlock { Text = "Options", FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }]);
        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 14), Margin = new Thickness(10, 0, 2, 0), Opacity = 0.6 };
        _search.InnerRightContent = new Border
        {
            Child = new TextBlock { Text = "Ctrl F", FontSize = 11, Opacity = 0.6, Classes = { "noloc" } },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BorderBrushProperty] = Resource("SystemControlForegroundBaseLowBrush"),
        };
        _search.TextChanged += (_, _) => ShowItems();
        var navTop = new StackPanel { Margin = new Thickness(12, 16, 12, 10) };
        navTop.Children.AddRange([brand, _search]);
        BuildNav();
        var pageCount = new TextBlock { Text = $"{_navItems.Count} pages", FontSize = 12, Opacity = 0.55, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
        // as tall as the footer of the page: their top lines meet in one line
        var navFoot = new Border { Child = pageCount, Height = FooterHeight, BorderThickness = new Thickness(0, 1, 0, 0), [!Border.BorderBrushProperty] = Resource("SystemControlForegroundBaseLowBrush") };
        var nav = new DockPanel();
        DockPanel.SetDock(navTop, Dock.Top);
        DockPanel.SetDock(navFoot, Dock.Bottom);
        nav.Children.AddRange([navTop, navFoot, new ScrollViewer { Content = _nav, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }]);
        var navBorder = new Border { Child = nav, Width = 276, BorderThickness = new Thickness(0, 0, 1, 0), Classes = { "settingsnav" } };
        navBorder[!Border.BorderBrushProperty] = Resource("SystemControlForegroundBaseLowBrush");

        // ---- the page: place, title, changed count, the groups
        ToolTip.SetTip(_reset, "Sets every setting of this page back to its default (until Apply / OK nothing is written).");
        _reset[!Button.ForegroundProperty] = Resource("SystemControlHighlightAccentBrush");
        _reset.Cursor = new Cursor(StandardCursorType.Hand);
        _reset.Click += (_, _) => ResetPage();
        var page = new StackPanel { MaxWidth = 940, Margin = new Thickness(40, 28, 40, 48) };
        page.Children.AddRange([_breadcrumb, _title, _meta, _rows]);
        _scroll.Content = page;

        // ---- the footer: Help, changes not applied, Cancel / Apply / OK
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "dialog" } };
        var cancel = new Button { Content = "Cancel", Classes = { "dialog" } };
        var help = new Button { Classes = { "dialog" }, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var helpContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        helpContent.Children.AddRange([DialogLook.Glyph("MiInfo", 15), new TextBlock { Text = "Help", VerticalAlignment = VerticalAlignment.Center }]);
        help.Content = helpContent;
        ok.Click += (_, _) =>
        {
            if (!CanApply())
            {
                return;
            }
            Apply();
            Close();
        };
        cancel.Click += (_, _) => Close();
        help.Click += (_, _) => ShowHelp();
        _apply.Click += (_, _) =>
        {
            if (CanApply())
            {
                Apply();
            }
        };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        info.Children.AddRange([_unsavedDot, _unsaved, _status]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, _apply, ok]);
        var footRow = new DockPanel();
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        help.Margin = new Thickness(0, 0, 12, 0);
        footRow.Children.AddRange([help, buttons, info]);
        footRow.VerticalAlignment = VerticalAlignment.Center;
        var foot = new Border { Child = footRow, Height = FooterHeight, Padding = new Thickness(40, 0), BorderThickness = new Thickness(0, 1, 0, 0), Classes = { "settingsnav" } };
        foot[!Border.BorderBrushProperty] = Resource("SystemControlForegroundBaseLowBrush");

        var main = new DockPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        main.Children.AddRange([foot, _scroll]);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(navBorder);
        Grid.SetColumn(main, 1);
        body.Children.Add(main);
        // above: the title bar of every page (the internal terminal's top line, the name, ✕ at the right)
        var top = PageHeader.Create(Services.MenuIcons.Create("nc_Config"), "Options", "Close (Esc)", Close, out var close);
        CloseButton = close;
        var frame = new DockPanel();
        frame.Children.AddRange([top, body]);
        var root = new Border { Child = frame, Classes = { "settingspage" } };
        Content = root;
        ApplyLook();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyLook();
            ShowItems();
        };
        UpdateUnapplied();

        AttachedToVisualTree += OnAttached;
        // the page names are translated here (not by the text hook): built again in the new language
        EventHandler relocalize = (_, _) => RebuildNav();
        AttachedToVisualTree += (_, _) => Services.UiLocalization.LanguageChanged += relocalize;
        DetachedFromVisualTree += (_, _) => Services.UiLocalization.LanguageChanged -= relocalize;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !e.Handled)
            {
                // Esc: first the search, then the configuration (like Cancel)
                if (!string.IsNullOrEmpty(_search.Text))
                {
                    _search.Text = string.Empty;
                }
                else
                {
                    Close();
                }
                e.Handled = true;
            }
        };
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachedToVisualTree -= OnAttached;
        SelectPage(_page);
        Dispatcher.UIThread.Post(() => _search.Focus());
        _ = FindChangedPagesAsync();
    }

    /// <summary>The close button at the top right (tests).</summary>
    public Button CloseButton { get; }

    /// <summary>Ends when the configuration is closed (OK, Cancel, Esc, the close button).</summary>
    public Task Done => _done.Task;

    /// <summary>Whether the configuration was closed.</summary>
    public bool IsClosed => _done.Task.IsCompleted;

    /// <summary>Closes the configuration; changes not applied are dropped.</summary>
    public void Close() => _done.TrySetResult();

    /// <summary>Visible setting labels (tests).</summary>
    public IReadOnlyList<string> VisibleLabels { get; private set; } = [];

    /// <summary>The paths of all pages, in the order of the navigation ("Display/Color" ...; screenshots).</summary>
    public IReadOnlyList<string> Pages => [.. _navItems.Keys];

    /// <summary>Title of the page shown (tests).</summary>
    public string PageTitle => _title.Text ?? string.Empty;

    /// <summary>Whether there are changes not applied yet.</summary>
    public bool IsDirty => _apply.IsEnabled;

    public void Search(string text) => _search.Text = text;

    /// <summary>Shows a page by its path ("Display/Color").</summary>
    public void SelectPage(string page)
    {
        _page = page;
        _settings.SettingsLastPage = page; // the configuration opens there again (not a setting: no Apply needed)
        if (!string.IsNullOrEmpty(_search.Text))
        {
            _search.Text = string.Empty; // shows the page
            return;
        }
        ShowItems();
    }

    /// <summary>
    /// False (and a note next to the buttons) while a list of the page has an entry with an error: OK / Apply would
    /// otherwise keep the previous list without the user noticing.
    /// </summary>
    public bool CanApply()
    {
        var invalid = _rows.GetVisualDescendants().OfType<Control>().FirstOrDefault(b => b.Classes.Contains(InvalidJsonClass));
        if (invalid is null)
        {
            _status.Text = string.Empty;
            return true;
        }
        _status.Text = "A list has an error (shown in red): correct it or leave with Cancel.";
        invalid.Focus();
        invalid.BringIntoView();
        return false;
    }

    /// <summary>Writes every setting from the working copy to the live settings.</summary>
    public void Apply()
    {
        foreach (var item in _items)
        {
            item.Copy(_work, _settings);
        }
        _changed();
        _unapplied.Clear();
        UpdateUnapplied();
    }

    private void MarkDirty(SettingItem? item = null)
    {
        if (item is not null)
        {
            _unapplied.Add(item);
        }
        UpdateUnapplied();
    }

    private void UpdateUnapplied()
    {
        // only what really differs from the settings in use (an editor may report a change while it is built)
        _unapplied.RemoveWhere(i => !IsUnapplied(i));
        var count = _unapplied.Count;
        _unsaved.Text = count == 0 ? string.Empty : count == 1 ? "1 unsaved change" : $"{count} unsaved changes";
        _unsavedDot.IsVisible = count > 0;
        _apply.IsEnabled = count > 0;
    }

    /// <summary>Whether the working copy holds something else for <paramref name="item"/> than the settings in use.</summary>
    private bool IsUnapplied(SettingItem item)
    {
        var working = new AppSettings();
        var live = new AppSettings();
        item.Copy(_work, working);
        item.Copy(_settings, live);
        return JsonSerializer.Serialize(working, SettingsStore.JsonOptions) != JsonSerializer.Serialize(live, SettingsStore.JsonOptions);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    private static Avalonia.Data.BindingBase Resource(string key) => new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);

    private static Ellipse Dot() => new()
    {
        Width = 6,
        Height = 6,
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false,
        [!Shape.FillProperty] = Resource("SystemControlHighlightAccentBrush"),
    };

    // ---------------------------------------------------------------- look

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    private IBrush PageBrush => new SolidColorBrush(Color.Parse(IsDark ? "#202020" : "#F3F3F3"));

    private IBrush NavBrush => new SolidColorBrush(Color.Parse(IsDark ? "#1B1B1B" : "#EBEBEB"));

    private IBrush CardBrush => new SolidColorBrush(Color.Parse(IsDark ? "#2B2B2B" : "#FBFBFB"));

    private IBrush SubBrush => new SolidColorBrush(Color.Parse(IsDark ? "#262626" : "#F5F5F5"));

    private IBrush StrokeBrush => new SolidColorBrush(Color.Parse(IsDark ? "#14FFFFFF" : "#14000000"));

    private IBrush HoverBrush => new SolidColorBrush(Color.Parse(IsDark ? "#0DFFFFFF" : "#0A000000"));

    private IBrush SelectedBrush => new SolidColorBrush(Color.Parse(IsDark ? "#16FFFFFF" : "#11000000"));

    /// <summary>The backgrounds of the page and of the navigation / footer (light or dark).</summary>
    private void ApplyLook()
    {
        foreach (var border in this.GetLogicalDescendants().OfType<Border>())
        {
            if (border.Classes.Contains("settingspage"))
            {
                border.Background = PageBrush;
            }
            else if (border.Classes.Contains("settingsnav"))
            {
                border.Background = NavBrush;
            }
        }
        if (Content is Border root)
        {
            root.Background = PageBrush;
        }
        foreach (var (page, (row, _)) in _navItems)
        {
            row.Background = page == _page && string.IsNullOrEmpty(_search.Text) ? SelectedBrush : Brushes.Transparent;
        }
    }

    // ---------------------------------------------------------------- navigation

    private void RebuildNav()
    {
        _nav.Children.Clear();
        _navItems.Clear();
        _navKids.Clear();
        BuildNav();
        ShowItems();
    }

    private void BuildNav()
    {
        var pages = s_pages.Where(p => p == "Misc." || _items.Any(i => i.Page == p) || s_pages.Any(c => c.StartsWith(p + "/", StringComparison.Ordinal))).ToList();
        foreach (var page in pages)
        {
            var slash = page.LastIndexOf('/');
            var hasKids = pages.Any(c => c.StartsWith(page + "/", StringComparison.Ordinal));
            var row = NavRow(page, slash >= 0, hasKids);
            if (slash < 0)
            {
                _nav.Children.Add(row);
                if (hasKids)
                {
                    var kids = new StackPanel { IsVisible = _page == page || _page.StartsWith(page + "/", StringComparison.Ordinal) };
                    _navKids[page] = kids;
                    _nav.Children.Add(kids);
                }
            }
            else if (_navKids.TryGetValue(page[..slash], out var kids))
            {
                kids.Children.Add(row);
            }
        }
    }

    private Border NavRow(string page, bool child, bool hasKids)
    {
        var slash = page.LastIndexOf('/');
        var bar = new Border { Width = 3, Margin = new Thickness(0, 10), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Tag = "bar" };
        bar[!Border.BackgroundProperty] = Resource("SystemControlHighlightAccentBrush");
        var icon = s_pageIcons.TryGetValue(page, out var glyph) ? DialogLook.Glyph(glyph, child ? 15 : 16) : new Panel();
        // the page names are translated here: navigation rows are not TextBlocks of a plain window text
        var text = new TextBlock { Text = Core.Localization.Localizer.T(page[(slash + 1)..]), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        var dot = Dot();
        ToolTip.SetTip(dot, "Changed from the default");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(dot);
        if (hasKids)
        {
            var chevron = new Button
            {
                Content = new TextBlock { Text = "›", FontSize = 16, Opacity = 0.6, Classes = { "noloc" } },
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            ToolTip.SetTip(chevron, "Show or hide the pages below");
            chevron.Click += (_, e) =>
            {
                if (_navKids.TryGetValue(page, out var kids))
                {
                    kids.IsVisible = !kids.IsVisible;
                    UpdateChevrons();
                }
                e.Handled = true;
            };
            chevron.Tag = page;
            right.Children.Add(chevron);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(child ? 40 : 12, 0, 8, 0) };
        grid.Children.Add(icon);
        icon.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var content = new Panel();
        content.Children.AddRange([grid, bar]);
        var row = new Border { Child = content, Height = child ? 34 : 36, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 1), Cursor = new Cursor(StandardCursorType.Hand), Tag = page };
        row.PointerEntered += (_, _) =>
        {
            if (page != _page || !string.IsNullOrEmpty(_search.Text))
            {
                row.Background = HoverBrush;
            }
        };
        row.PointerExited += (_, _) => row.Background = page == _page && string.IsNullOrEmpty(_search.Text) ? SelectedBrush : Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                SelectPage(page);
                e.Handled = true;
            }
        };
        _navItems[page] = (row, dot);
        return row;
    }

    private void UpdateChevrons()
    {
        foreach (var chevron in _nav.GetLogicalDescendants().OfType<Button>().Where(b => b.Tag is string))
        {
            var open = _navKids.TryGetValue((string)chevron.Tag!, out var kids) && kids.IsVisible;
            chevron.RenderTransform = open ? new RotateTransform(90) : null;
        }
    }

    /// <summary>Marks the page in the navigation: the selected one with a bar, its group opened.</summary>
    private void UpdateNav()
    {
        var searching = !string.IsNullOrEmpty(_search.Text);
        foreach (var (page, (row, dot)) in _navItems)
        {
            var selected = page == _page && !searching;
            row.Background = selected ? SelectedBrush : Brushes.Transparent;
            if (row.Child is Panel content && content.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "bar")) is { } bar)
            {
                bar.IsVisible = selected;
            }
            dot.IsVisible = _changedPages.Contains(page);
        }
        var slash = _page.IndexOf('/');
        if (_navKids.TryGetValue(slash < 0 ? _page : _page[..slash], out var kids))
        {
            kids.IsVisible = true;
        }
        UpdateChevrons();
    }

    /// <summary>The pages with settings changed from the default, one page at a time in the background.</summary>
    private async Task FindChangedPagesAsync()
    {
        foreach (var page in _navItems.Keys.ToList())
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_items.Any(i => i.Page == page && IsChanged(i)))
                {
                    _changedPages.Add(page);
                }
                if (_navItems.TryGetValue(page, out var nav))
                {
                    nav.Dot.IsVisible = _changedPages.Contains(page);
                }
            }, DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------- the page

    public void ShowItems()
    {
        var query = Fold(_search.Text?.Trim() ?? string.Empty);
        // the texts as shown (translated) and as written in the code: a Turkish UI finds "çöp", an English one "recycle"
        bool Has(string? s) => s is not null && (Fold(s).Contains(query, StringComparison.Ordinal)
            || Fold(Core.Localization.Localizer.T(s)).Contains(query, StringComparison.Ordinal));
        bool HasPage(string page) => page.Split('/').Any(Has);
        var searching = query.Length > 0;
        var visible = searching
            ? _items.Where(i => Has(i.Label) || Has(i.Description) || HasPage(i.Page) || Has(i.Group)).ToList()
            : _items.Where(i => i.Page == _page).ToList();
        var slash = _page.LastIndexOf('/');
        _title.Text = searching ? $"Results for “{_search.Text!.Trim()}”" : _page[(slash + 1)..];
        _breadcrumb.Text = searching ? "Search" : slash < 0 ? "Configuration" : "Configuration › " + string.Join(" › ", _page[..slash].Split('/').Select(Core.Localization.Localizer.T));
        _rows.Children.Clear();
        _changedMarks.Clear();
        foreach (var group in visible.GroupBy(i => (i.Page, i.Group)))
        {
            var title = searching ? $"{group.Key.Page.Replace("/", " › ", StringComparison.Ordinal)} › {group.Key.Group}" : group.Key.Group;
            _rows.Children.Add(Section(title, Card(group.ToList()), searching ? group.Key.Page : null));
        }
        if (!searching && _page == "Misc." && _extras.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(16, 14, 16, 8) };
            foreach (var (text, action) in _extras)
            {
                var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
                button.Click += async (_, _) => await action();
                wrap.Children.Add(button);
            }
            _rows.Children.Add(Section("Other editors", CardBorder(wrap)));
        }
        if (visible.Count == 0 && (searching || _page != "Misc."))
        {
            var empty = new StackPanel { Spacing = 6, Margin = new Thickness(16, 40) };
            empty.Children.Add(new Border { Child = DialogLook.Glyph(searching ? "MiSearch" : "MiInfo", 36), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = searching ? "No matching settings" : "No settings on this page; see its subpages.",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (searching)
            {
                empty.Children.Add(new TextBlock { Text = "Try a shorter word, such as “tab”, “font” or “proxy”.", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
            }
            _rows.Children.Add(CardBorder(empty));
        }
        VisibleLabels = visible.Select(i => i.Label).ToList();
        UpdateMeta(searching, visible.Count);
        UpdateNav();
        _scroll.Offset = default;
    }

    /// <summary>Under the title: how many settings differ from the default (and Reset page), or the number of results.</summary>
    private void UpdateMeta(bool searching, int found)
    {
        _meta.Children.Clear();
        if (searching)
        {
            _meta.Children.Add(new TextBlock { Text = found == 1 ? "1 setting found" : $"{found} settings found", FontSize = 12.5, Opacity = 0.75 });
            return;
        }
        var count = _changedMarks.Values.Count(m => m.IsVisible);
        _changedCount.Text = count == 0 ? "All settings on this page are at their defaults" : $"{count} changed from the default";
        if (count > 0)
        {
            var dot = Dot();
            dot.IsVisible = true;
            _meta.Children.Add(dot);
        }
        _meta.Children.Add(_changedCount);
        if (count > 0)
        {
            _meta.Children.Add(new TextBlock { Text = "·", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            _meta.Children.Add(_reset);
        }
        if (_changedMarks.Count > 0 || _items.Any(i => i.Page == _page))
        {
            if (count > 0)
            {
                _changedPages.Add(_page);
            }
            else
            {
                _changedPages.Remove(_page);
            }
        }
    }

    /// <summary>The changed-from-default marks of the rows shown, by item.</summary>
    private readonly Dictionary<SettingItem, Control> _changedMarks = [];

    /// <summary>Whether the working copy holds something other than the default for <paramref name="item"/>.</summary>
    public bool IsChanged(SettingItem item)
    {
        var probe = new AppSettings();
        item.Copy(_work, probe);
        return JsonSerializer.Serialize(probe, SettingsStore.JsonOptions) != s_defaultJson;
    }

    /// <summary>Settings of the page shown that differ from the default (tests).</summary>
    public IReadOnlyList<string> ChangedLabels => _changedMarks.Where(m => m.Value.IsVisible).Select(m => m.Key.Label).ToList();

    /// <summary>Every setting of the page shown back to its default (in the working copy; Apply / OK writes it).</summary>
    public void ResetPage()
    {
        var defaults = new AppSettings();
        foreach (var item in _items.Where(i => i.Page == _page))
        {
            item.Copy(defaults, _work);
            _unapplied.Add(item);
        }
        MarkDirty();
        ShowItems();
    }

    /// <summary>A group: its title above its card (in search results a link to its page).</summary>
    private static Control Section(string title, Control card, string? page = null)
    {
        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 8) };
        var section = new StackPanel();
        section.Children.AddRange([heading, card]);
        if (page is not null)
        {
            // search results: the title opens the page of the setting
            heading.Cursor = new Cursor(StandardCursorType.Hand);
            heading.TextDecorations = TextDecorations.Underline;
            ToolTip.SetTip(heading, "Open this page");
            heading.PointerPressed += (_, e) =>
            {
                heading.FindAncestorOfType<SettingsView>()?.SelectPage(page);
                e.Handled = true;
            };
        }
        return section;
    }

    private Border CardBorder(Control content) => new()
    {
        Child = content,
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = StrokeBrush,
        Background = CardBrush,
        ClipToBounds = true,
    };

    /// <summary>
    /// The rows of a group in a card. An indented item belongs to the check box above it: its row sits below that one,
    /// on a slightly different background, greyed out while the check box is off.
    /// </summary>
    private Control Card(List<SettingItem> items)
    {
        var stack = new StackPanel();
        CheckBox? parent = null;
        StackPanel? subs = null;
        foreach (var item in items)
        {
            var row = Row(item, out var editor);
            if (item.Indent && parent is { } box)
            {
                if (subs is null)
                {
                    subs = new StackPanel { Background = SubBrush };
                    var group = subs;
                    void Enable() => group.Opacity = box.IsChecked == true ? 1 : 0.42;
                    group.IsEnabled = box.IsChecked == true;
                    Enable();
                    box.IsCheckedChanged += (_, _) =>
                    {
                        group.IsEnabled = box.IsChecked == true;
                        Enable();
                    };
                    stack.Children.Add(subs);
                }
                row.Padding = new Thickness(52, row.Padding.Top, row.Padding.Right, row.Padding.Bottom);
                row.MinHeight = 54;
                subs.Children.Add(row);
                continue;
            }
            subs = null;
            parent = item.IsCheckBox ? editor as CheckBox : null;
            stack.Children.Add(row);
        }
        // a line between the rows (not above the first)
        var first = true;
        foreach (var row in stack.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("settingrow")))
        {
            row.BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0);
            row.BorderBrush = StrokeBrush;
            first = false;
        }
        return CardBorder(stack);
    }

    private Border Row(SettingItem item, out Control editor)
    {
        // a dot after the name: this setting differs from the default
        var mark = Dot();
        ToolTip.SetTip(mark, "Changed from the default");
        _changedMarks[item] = mark;
        void Changed()
        {
            MarkDirty(item);
            mark.IsVisible = IsChanged(item);
            UpdateMeta(!string.IsNullOrWhiteSpace(_search.Text), VisibleLabels.Count);
            if (_navItems.TryGetValue(item.Page, out var nav))
            {
                var changed = _items.Any(i => i.Page == item.Page && (_changedMarks.TryGetValue(i, out var m) ? m.IsVisible : IsChanged(i)));
                if (changed)
                {
                    _changedPages.Add(item.Page);
                }
                else
                {
                    _changedPages.Remove(item.Page);
                }
                nav.Dot.IsVisible = changed;
            }
        }
        editor = item.Editor(_work, Changed);
        mark.IsVisible = IsChanged(item);

        var name = new TextBlock { Text = item.Label, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameLine.Children.AddRange([name, mark]);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(nameLine);
        if (item.Description is { } description)
        {
            label.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        if (item.IsCheckBox && editor is CheckBox box)
        {
            // the check box drawn as a switch (On / Off) at the right; its text is the row's name
            box.Classes.Add("switch");
            box.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(box, item.Label);
        }
        Control content;
        if (item.FullWidth)
        {
            var block = new StackPanel { Spacing = 12 };
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            block.Children.AddRange([label, editor]);
            content = block;
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(label);
            editor.VerticalAlignment = VerticalAlignment.Center;
            editor.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            content = grid;
        }
        return new Border { Child = content, Padding = new Thickness(16, 12), MinHeight = 62, Classes = { "settingrow" } };
    }

    private async void ShowHelp()
    {
        var dialog = new Window
        {
            Title = "Help",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "Choose a page on the left or type the name of a setting into the search box (Ctrl+F; e.g. \"recycle\", \"theme\"). A search result's title opens its page.\n\n"
                    + "A dot after a setting shows that it differs from the default; Reset page sets the whole page back to the defaults.\n\n"
                    + "OK: applies the changes and closes the configuration.\nApply: applies without closing.\nCancel (Esc): drops changes not applied yet.\n\n"
                    + "Shortcuts, hotlist and column sets are edited with the buttons on the Misc. page.",
            },
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    /// <summary>Lower case without accents (ç→c, ğ→g, ı/İ→i, ö→o, ş→s, ü→u): "editor" finds "editör".</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Replace('ı', 'i').Replace('İ', 'i').ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- editors

    private static Action<AppSettings, AppSettings> CopyOf<T>(Func<AppSettings, T> get, Action<AppSettings, T> set) => (from, to) => set(to, get(from));

    private static SettingItem Bool(string page, string group, string label, string? description, Func<AppSettings, bool> get, Action<AppSettings, bool> set,
        bool indent = false) =>
        new(page, group, label, description, (s, changed) =>
        {
            var box = new CheckBox { Content = label, IsChecked = get(s) };
            box.IsCheckedChanged += (_, _) =>
            {
                set(s, box.IsChecked == true);
                changed();
            };
            return box;
        }, CopyOf(get, set), IsCheckBox: true, Indent: indent);

    private static SettingItem Choice<T>(string page, string group, string label, string? description, (string Text, T Value)[] options,
        Func<AppSettings, T> get, Action<AppSettings, T> set) =>
        new(page, group, label, description, (s, changed) =>
        {
            var combo = new ComboBox { ItemsSource = options.Select(o => o.Text).ToList(), MinWidth = 260 };
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, get(s))));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                {
                    set(s, options[combo.SelectedIndex].Value);
                    changed();
                }
            };
            return combo;
        }, CopyOf(get, set));

    private static SettingItem Text(string page, string group, string label, string? description, Func<AppSettings, string?> get, Action<AppSettings, string?> set,
        string? watermark = null, bool indent = false) =>
        new(page, group, label, description, (s, changed) =>
        {
            var box = new TextBox { Text = get(s), PlaceholderText = watermark, MinWidth = 360, MaxWidth = 560 };
            box.TextChanged += (_, _) =>
            {
                set(s, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                changed();
            };
            return box;
        }, CopyOf(get, set), Indent: indent);

    private static SettingItem Number(string page, string group, string label, string? description, double min, double max, Func<AppSettings, double> get,
        Action<AppSettings, double> set, double step = 1, bool indent = false) =>
        new(page, group, label, description, (s, changed) =>
        {
            var box = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, Value = (decimal)get(s), Width = 160 };
            box.ValueChanged += (_, _) =>
            {
                if (box.Value is { } v)
                {
                    set(s, (double)v);
                    changed();
                }
            };
            return box;
        }, CopyOf(get, set), Indent: indent);

    private static SettingItem Font(string page, string group, string label, string? description, Func<AppSettings, string> get, Action<AppSettings, string> set,
        string firstChoice = "(system font)") =>
        new(page, group, label, description, (s, changed) =>
        {
            var fonts = new List<string> { firstChoice };
            fonts.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name).Order(StringComparer.CurrentCultureIgnoreCase));
            var current = get(s);
            var combo = new ComboBox { ItemsSource = fonts, MinWidth = 280, MaxDropDownHeight = 400 };
            combo.SelectedIndex = string.IsNullOrWhiteSpace(current) ? 0 : Math.Max(0, fonts.FindIndex(f => current.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
            combo.SelectionChanged += (_, _) =>
            {
                set(s, combo.SelectedIndex <= 0 ? string.Empty : fonts[combo.SelectedIndex]);
                changed();
            };
            return combo;
        }, CopyOf(get, set));

    /// <summary>Fixed-width fonts of the system (every character as wide as "M"), found once.</summary>
    private static List<string>? s_monospaceFonts;

    public static IReadOnlyList<string> MonospaceFonts()
    {
        if (s_monospaceFonts is not null)
        {
            return s_monospaceFonts;
        }
        var result = new List<string>();
        foreach (var family in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphs) || !string.Equals(glyphs.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // a substitute was found instead
                }
                double Width(char c) => glyphs.CharacterToGlyphMap.TryGetGlyph(c, out var g) && glyphs.TryGetHorizontalGlyphAdvance(g, out var advance) ? advance : -1;
                var m = Width('M');
                if (m > 0 && Math.Abs(Width('i') - m) < 0.5 && Math.Abs(Width('W') - m) < 0.5 && Math.Abs(Width('.') - m) < 0.5)
                {
                    result.Add(family);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // a font that cannot be read is left out
            }
        }
        return s_monospaceFonts = result;
    }

    /// <summary>A font choice among the fixed-width fonts; the first entry is the default list.</summary>
    private static SettingItem MonospaceFont(string page, string group, string label, string? description, Func<AppSettings, string> get, Action<AppSettings, string> set) =>
        new(page, group, label, description, (s, changed) =>
        {
            var fonts = new List<string> { "(default)" };
            fonts.AddRange(MonospaceFonts());
            var current = get(s);
            var combo = new ComboBox { ItemsSource = fonts, MinWidth = 280, MaxDropDownHeight = 400 };
            combo.SelectedIndex = string.IsNullOrWhiteSpace(current) || current == AppSettings.DefaultTerminalFont
                ? 0 : Math.Max(0, fonts.FindIndex(f => current.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
            combo.SelectionChanged += (_, _) =>
            {
                set(s, combo.SelectedIndex <= 0 ? string.Empty : fonts[combo.SelectedIndex]);
                changed();
            };
            return combo;
        }, CopyOf(get, set));

    /// <summary>The state of the own file index (files, folders, last update, what it does) and a button to build it again.</summary>
    private static SettingItem IndexStatus(string page, string group) =>
        new(page, group, "Status", null, (_, _) =>
        {
            var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var rebuild = new Button { Content = "Rebuild" };
            void Show()
            {
                var index = Services.FileIndexService.Current;
                status.Text = index is null
                    ? "Off (starts when the settings are applied)"
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0}: {1:N0} files, {2:N0} folders{3}", index.Status, index.Files, index.Folders,
                        index.LastUpdated is { } at ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "; last update {0:g}", at) : string.Empty);
                rebuild.IsEnabled = index is not null;
            }
            rebuild.Click += (_, _) =>
            {
                _ = Services.FileIndexService.Current?.RebuildAsync();
                Show();
            };
            var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Show());
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            panel.Children.AddRange([rebuild, status]);
            panel.AttachedToVisualTree += (_, _) => timer.Start();
            panel.DetachedFromVisualTree += (_, _) => timer.Stop();
            Show();
            return panel;
        }, (_, _) => { });

    /// <summary>PC link: what the sharing does now (set by the main window; null in tests).</summary>
    public static Func<string>? LinkStatus { get; set; }

    /// <summary>PC link: opens the port in the firewall (with administrator / root rights); false when refused or not done.</summary>
    public static Func<int, Task<bool>>? AllowLinkThroughFirewall { get; set; }

    /// <summary>PC link: a button that lets the link's port through the firewall of this computer.</summary>
    private static SettingItem LinkFirewallItem(string page, string group) =>
        new(page, group, "Firewall",
            "When the other computer is not found (or cannot connect), the firewall of this computer may drop the link: this opens its port for every network (Windows Defender Firewall; Linux: ufw or firewalld). Asks for administrator rights.",
            (s, _) =>
            {
                var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8 };
                var button = new Button { Content = "Allow through the firewall" };
                button.IsEnabled = AllowLinkThroughFirewall is not null && Services.LinkFirewall.Command(s.LinkPort) is not null;
                if (Services.LinkFirewall.Command(s.LinkPort) is null)
                {
                    result.Text = "Nothing to open on this system.";
                }
                button.Click += async (_, _) =>
                {
                    if (AllowLinkThroughFirewall is not { } allow)
                    {
                        return;
                    }
                    button.IsEnabled = false;
                    var done = await allow(s.LinkPort);
                    button.IsEnabled = true;
                    result.Text = done ? $"The firewall lets PC link through (port {s.LinkPort})." : "Not changed (cancelled or not allowed).";
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.AddRange([button, result]);
                return row;
            }, (_, _) => { });

    /// <summary>PC link: a shared folder (name the other computer sees, folder, read only).</summary>
    private static ListEditorOptions<LinkShare> LinkShareOptions() => new()
    {
        Create = () => new LinkShare { Name = "Shared" },
        Clone = CloneEntry,
        Summary = s => (string.IsNullOrWhiteSpace(s.Name) ? "(no name)" : s.Name) + " — " + (s.Path.Length > 0 ? s.Path : "(no folder)") + (s.ReadOnly ? " (read only)" : string.Empty),
        Validate = s => s.Problem(),
        AddText = "Add folder",
        Fields =
        [
            new("Name", ListFieldKind.Text, s => s.Name, (s, v) => s.Name = v?.Trim() ?? string.Empty) { PlaceholderText = "e.g. Photos" },
            new("Folder", ListFieldKind.Folder, s => s.Path, (s, v) => s.Path = v?.Trim() ?? string.Empty),
            new("Read only", ListFieldKind.Bool, s => s.ReadOnly ? "True" : "False", (s, v) => s.ReadOnly = v == "True"),
        ],
    };

    /// <summary>PC link: the state of sharing, renewed every second while the page is shown.</summary>
    private static SettingItem LinkStatusItem(string page, string group) =>
        new(page, group, "Status", null, (_, _) =>
        {
            var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            void Show() => status.Text = LinkStatus?.Invoke() ?? string.Empty;
            var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Show());
            status.AttachedToVisualTree += (_, _) => timer.Start();
            status.DetachedFromVisualTree += (_, _) => timer.Stop();
            Show();
            return status;
        }, (_, _) => { });

    /// <summary>PC link: the computers allowed to connect without the code; "Forget" asks for the code again next time.</summary>
    private static SettingItem LinkPeersItem(string page, string group) =>
        new(page, group, "Allowed computers", "These computers connect without the code. Forget one to be asked again.", (s, changed) =>
        {
            var list = new StackPanel { Spacing = 4 };
            void Fill()
            {
                list.Children.Clear();
                if (s.LinkTrustedPeers.Count == 0)
                {
                    list.Children.Add(new TextBlock { Text = "(none)", Opacity = 0.7 });
                }
                foreach (var peer in s.LinkTrustedPeers.ToList())
                {
                    var row = new DockPanel();
                    var forget = new Button { Content = "Forget", Margin = new Thickness(8, 0, 0, 0) };
                    forget.Click += (_, _) =>
                    {
                        s.LinkTrustedPeers = [.. s.LinkTrustedPeers.Where(p => !ReferenceEquals(p, peer))];
                        changed();
                        Fill();
                    };
                    DockPanel.SetDock(forget, Dock.Right);
                    row.Children.Add(forget);
                    row.Children.Add(new TextBlock
                    {
                        Text = string.Format(CultureInfo.CurrentCulture, "{0} — {1:g}", peer.Name, peer.Allowed),
                        VerticalAlignment = VerticalAlignment.Center,
                        Classes = { "noloc" },
                    });
                    ToolTip.SetTip(row, peer.Fingerprint);
                    list.Children.Add(row);
                }
            }
            Fill();
            return list;
        }, CopyList(s => s.LinkTrustedPeers, (s, v) => s.LinkTrustedPeers = v), FullWidth: true);

    /// <summary>A choice whose options are read each time the page is shown (theme and language files).</summary>
    private static SettingItem FileChoice(string page, string group, string label, string? description, Func<(string Text, string Value)[]> options,
        Func<AppSettings, string> get, Action<AppSettings, string> set, Func<string, Control?>? icon = null) =>
        new(page, group, label, description, (s, changed) =>
        {
            var list = options();
            var combo = new ComboBox { ItemsSource = list.Select(o => o.Text).ToList(), MinWidth = 260 };
            if (icon is not null)
            {
                // an icon before each entry (the flags of the languages)
                combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var index = Array.FindIndex(list, o => o.Text == text);
                    row.Children.Add(index >= 0 && icon(list[index].Value) is { } picture ? picture : new Border { Width = 21 });
                    row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                    return row;
                });
            }
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(list, o => string.Equals(o.Value, get(s), StringComparison.OrdinalIgnoreCase)));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                {
                    set(s, list[combo.SelectedIndex].Value);
                    changed();
                }
            };
            return combo;
        }, CopyOf(get, set));

    /// <summary>A few icons drawn with an icon pack (the entries of the icon pack list).</summary>
    private static Control? IconPackPreview(string id)
    {
        if (Services.Icons.IconPacks.Find(id) is not { } pack)
        {
            return null;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var command in new[] { "nc_MkDir", "nc_Copy", "nc_RenMov", "nc_Delete", "nc_PackFiles", "nc_List", "nc_SearchFor" })
        {
            if (Services.MenuIcons.Create(command, pack) is { } icon)
            {
                row.Children.Add(icon);
            }
        }
        if (Services.Icons.IconPacks.Glyph("MiDrive", pack) is { } drive)
        {
            var color = Services.Icons.IconPacks.Color("Gray", Brushes.SlateGray, pack);
            row.Children.Add(new Panel { Width = 16, Height = 16, Margin = new Thickness(1), Children = { Services.Icons.IconPacks.Draw(drive, color, pack) } });
        }
        return row;
    }

    /// <summary>How much memory 7-Zip needs with the chosen dictionary (updated while the page is shown).</summary>
    private static SettingItem SevenZipMemory(string page, string group) =>
        new(page, group, "Memory usage", "An estimate for LZMA / LZMA2 (7-Zip's own numbers can differ a little).", (s, _) =>
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            void Show()
            {
                var level = s.SevenZipLevel is >= 0 and <= 9 ? s.SevenZipLevel : 5;
                var dictionary = s.SevenZipDictionaryMb > 0 ? s.SevenZipDictionaryMb : NextCommander.Vfs.Archive.SevenZipOptions.DefaultDictionaryMb(level);
                text.Text = NextCommander.Vfs.Archive.SevenZipOptions.MemoryMb(s.SevenZipMethod, dictionary, s.SevenZipThreads) is { } memory
                    ? string.Format(CultureInfo.CurrentCulture, "about {0:N0} MB to pack, {1:N0} MB to unpack", memory.Compress, memory.Decompress)
                    : "not known for this method";
            }
            var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(400), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Show());
            text.AttachedToVisualTree += (_, _) => timer.Start();
            text.DetachedFromVisualTree += (_, _) => timer.Stop();
            Show();
            return text;
        }, (_, _) => { });

    /// <summary>The thumbnails of deleted files out of the disk cache.</summary>
    private static SettingItem RemoveStaleThumbnails(string page, string group) =>
        new(page, group, "Remove thumbnails of deleted files", null, (_, _) =>
        {
            var button = new Button { Content = "Remove now", HorizontalAlignment = HorizontalAlignment.Left };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(10, 0, 0, 0) };
            button.IsEnabled = App.ThumbnailCachePath is { } dir && Directory.Exists(dir);
            button.Click += (_, _) =>
            {
                if (App.ThumbnailCachePath is { } dir && Directory.Exists(dir))
                {
                    status.Text = string.Format(CultureInfo.CurrentCulture, "{0} thumbnails removed", Services.ThumbnailSources.RemoveStale(dir));
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.AddRange([button, status]);
            return row;
        }, (_, _) => { });

    /// <summary>The icon of a button at a size (the button bar's own drawing); set by the main window.</summary>
    public static Func<ButtonDefinition, int, Control?>? ButtonIcon { get; set; }

    /// <summary>Chooses a program file for a button; set by the main window.</summary>
    public static Func<string, Task<string?>>? PickProgram { get; set; }

    /// <summary>
    /// A button bar shown as its icons with an "Edit..." button that opens the button bar editor on the working copy
    /// (instead of JSON); the icon size chosen there is taken too.
    /// </summary>
    private static SettingItem ButtonBarItem(string page, string group, string label, string? description, bool vertical) =>
        new(page, group, label, description, (s, changed) =>
        {
            List<ButtonDefinition> Buttons() => vertical ? s.VerticalButtonBar : s.ButtonBar;
            var preview = new WrapPanel { ItemSpacing = 2, LineSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock { Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            void Show()
            {
                preview.Children.Clear();
                foreach (var button in Buttons())
                {
                    if (button.IsSeparator)
                    {
                        preview.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0), [!Border.BackgroundProperty] = Resource("SystemControlForegroundBaseLowBrush") });
                        continue;
                    }
                    var icon = ButtonIcon?.Invoke(button, 16) ?? new TextBlock { Text = button.Label ?? button.Command, FontSize = 11, Classes = { "noloc" } };
                    icon.Margin = new Thickness(2);
                    ToolTip.SetTip(icon, button.Tooltip ?? button.Label ?? button.Command);
                    preview.Children.Add(icon);
                }
                count.Text = string.Format(CultureInfo.CurrentCulture, "{0} buttons", Buttons().Count(b => !b.IsSeparator));
            }
            var edit = new Button { Content = "Edit...", VerticalAlignment = VerticalAlignment.Center };
            edit.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(edit) is not Window owner)
                {
                    return;
                }
                var size = vertical ? s.VerticalButtonBarIconSize : s.ButtonBarIconSize;
                var dialog = new ButtonBarEditorDialog(vertical ? "Change vertical button bar" : "Change button bar", Buttons(), size, vertical,
                    (b, iconSize) => ButtonIcon?.Invoke(b, iconSize), PickProgram ?? (_ => Task.FromResult<string?>(null)));
                await dialog.ShowDialog(owner);
                if (dialog.Result is not { } result)
                {
                    return;
                }
                if (vertical)
                {
                    s.VerticalButtonBar = result.Buttons;
                    s.VerticalButtonBarIconSize = result.IconSize;
                }
                else
                {
                    s.ButtonBar = result.Buttons;
                    s.ButtonBarIconSize = result.IconSize;
                }
                changed();
                edit.FindAncestorOfType<SettingsView>()?.ShowItems(); // the icon size row shows the new size too
            };
            var row = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 0, 0, 0) };
            right.Children.AddRange([count, edit]);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new Border
            {
                Child = preview,
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                MinHeight = 30,
                [!Border.BorderBrushProperty] = Resource("SystemControlForegroundBaseLowBrush"),
            });
            Show();
            return row;
        }, (from, to) =>
        {
            if (vertical)
            {
                to.VerticalButtonBar = from.VerticalButtonBar;
                to.VerticalButtonBarIconSize = from.VerticalButtonBarIconSize;
            }
            else
            {
                to.ButtonBar = from.ButtonBar;
                to.ButtonBarIconSize = from.ButtonBarIconSize;
            }
        }, FullWidth: true);

    /// <summary>The names selected in the active panel (with or without their folder); set by the main window.</summary>
    public static Func<bool, IReadOnlyList<string>>? SelectedNames { get; set; }

    /// <summary>Adds the names selected in the panel to the ignore list.</summary>
    private static SettingItem IgnoreAddButtons(string page, string group) =>
        new(page, group, "Selected in the panel", "Adds the files and folders selected in the active panel.", (s, changed) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (text, fullPath) in new[] { ("Add the selected names", false), ("Add them with their full path", true) })
            {
                var button = new Button { Content = text };
                button.IsEnabled = SelectedNames is not null;
                button.Click += (_, _) =>
                {
                    if (SelectedNames?.Invoke(fullPath) is not { Count: > 0 } names)
                    {
                        return;
                    }
                    s.IgnoreList = [.. s.IgnoreList.Concat(names.Where(n => !s.IgnoreList.Contains(n, StringComparer.OrdinalIgnoreCase)))];
                    changed();
                    button.FindAncestorOfType<SettingsView>()?.ShowItems(); // the list box shows the new entries
                };
                row.Children.Add(button);
            }
            return row;
        }, (_, _) => { });

    /// <summary>Deletes the thumbnails kept on disk.</summary>
    private static SettingItem ClearThumbnailCache(string page, string group) =>
        new(page, group, "Clear the cache", "Deletes the thumbnails kept on disk; they are made again when shown.", (_, _) =>
        {
            var button = new Button { Content = "Clear the thumbnail cache", HorizontalAlignment = HorizontalAlignment.Left };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(10, 0, 0, 0) };
            button.IsEnabled = App.ThumbnailCachePath is { } dir && Directory.Exists(dir);
            button.Click += (_, _) =>
            {
                if (App.ThumbnailCachePath is not { } dir || !Directory.Exists(dir))
                {
                    return;
                }
                var deleted = 0;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // in use: left for the next time
                    }
                }
                status.Text = string.Format(CultureInfo.CurrentCulture, "{0} files deleted", deleted);
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.AddRange([button, status]);
            return row;
        }, (_, _) => { });

    /// <summary>"System" in the language list: a monitor (the operating system's language).</summary>
    private static Control LanguageSystemIcon() => new Border
    {
        Width = 21, Height = 14,
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Application.Current?.FindResource("MiMonitor") as Avalonia.Media.Geometry, Stretch = Avalonia.Media.Stretch.Uniform,
            Fill = Application.Current?.FindResource("FmDriveIconBrush") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Gray, Height = 14,
        },
    };

    /// <summary>Two buttons: "New..." and "Edit..." (or one); each opens an editor window and picks what it saved.</summary>
    private static SettingItem EditorButtons(string page, string group, string label, string? description,
        params (string Text, Func<AppSettings, Window?> Create, Func<Window, string?> Result, Action<AppSettings, string> Use)[] buttons) =>
        new(page, group, label, description, (s, changed) =>
        {
            var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
            foreach (var (text, create, result, use) in buttons)
            {
                var button = new Button { Content = text };
                button.Click += async (_, _) =>
                {
                    if (TopLevel.GetTopLevel(button) is not Window owner || create(s) is not { } dialog)
                    {
                        return;
                    }
                    await dialog.ShowDialog(owner);
                    if (result(dialog) is not { } id)
                    {
                        return;
                    }
                    use(s, id);
                    changed();
                    button.FindAncestorOfType<SettingsView>()?.ShowItems(); // the list above shows the new entry
                };
                row.Children.Add(button);
            }
            return row;
        }, (_, _) => { });

    /// <summary>The theme to edit: the chosen one, or the colors in effect when it has no file (system colors).</summary>
    private static ColorTheme ThemeToEdit(AppSettings s) =>
        Services.ColorThemes.Find(s.ColorThemeFile) is { FilePath: not null } theme ? theme
            : Services.ColorThemes.Capture(Application.Current!, "My theme");

    private static SettingItem ThemeEditors(string page, string group) =>
        EditorButtons(page, group, "Edit colors", "Change the colors of a theme with color pickers, or start a new theme from the colors in effect. A shipped theme is saved as your own copy.",
            ("New theme...", _ => new ThemeEditorDialog(Services.ColorThemes.Capture(Application.Current!, "My theme")),
                d => ((ThemeEditorDialog)d).Result?.Id, (s, id) => s.ColorThemeFile = id),
            ("Edit theme...", s => new ThemeEditorDialog(ThemeToEdit(s)), d => ((ThemeEditorDialog)d).Result?.Id, (s, id) => s.ColorThemeFile = id));

    private static SettingItem IconPackEditors(string page, string group) =>
        EditorButtons(page, group, "Edit icons", "Change the style, the corner marks and the colors of an icon pack, or make a new pack from the chosen one. A shipped pack is saved as your own copy.",
            ("New icon pack...", s =>
                {
                    var pack = (Services.Icons.IconPacks.Find(s.IconPack) ?? Services.Icons.IconPacks.Current).Clone();
                    pack.Name = "My icons";
                    pack.Id = string.Empty;
                    pack.FilePath = null;
                    return new IconPackEditorDialog(pack);
                }, d => ((IconPackEditorDialog)d).Result?.Id, (s, id) => s.IconPack = id),
            ("Edit icon pack...", s => new IconPackEditorDialog(Services.Icons.IconPacks.Find(s.IconPack) ?? Services.Icons.IconPacks.Current),
                d => ((IconPackEditorDialog)d).Result?.Id, (s, id) => s.IconPack = id));

    private static SettingItem LanguageEditor(string page, string group) =>
        EditorButtons(page, group, "Translate", "Translate the texts of the program into a new language, or improve a translation; saved into the languages folder of the settings and used after a restart.",
            ("Translate...", s => new LanguageEditorDialog(s.Language is "system" or "" ? Services.UiLocalization.Available().Select(l => l.Code).FirstOrDefault(c => c == System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) : s.Language),
                d => ((LanguageEditorDialog)d).Result, (s, code) => s.Language = code));

    private static readonly JsonSerializerOptions s_json = new(SettingsStore.JsonOptions) { WriteIndented = true };

    /// <summary>Reading a JSON list: a field that does not exist (a typo) is an error, not silently dropped.</summary>
    private static readonly JsonSerializerOptions s_strictJson = new(SettingsStore.JsonOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The class of a JSON box whose text is not taken (OK / Apply refuse while one is shown).</summary>
    public const string InvalidJsonClass = "jsoninvalid";

    /// <summary>
    /// Checks the text of a JSON list: the syntax, unknown fields, and the values of each entry (<see cref="ValidateEntry"/>).
    /// Returns the list, or the error ("Satır 3, sütun 12: ...").
    /// </summary>
    public static (List<T>? Value, string? Error) ParseJsonList<T>(string? text)
    {
        List<T>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<T>>(string.IsNullOrWhiteSpace(text) ? "[]" : text, s_strictJson) ?? [];
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $"Line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}: " : string.Empty;
            var unknown = System.Text.RegularExpressions.Regex.Match(ex.Message, "JSON property '([^']+)' could not be mapped");
            var message = unknown.Success ? $"unknown field \"{unknown.Groups[1].Value}\""
                : ex.Message.Contains("could not be converted", StringComparison.Ordinal) ? "wrong type of value (e.g. text instead of a number)"
                : "JSON syntax error (missing comma, quote or bracket)";
            return (null, where + message);
        }
        for (var i = 0; i < list.Count; i++)
        {
            if (ValidateEntry(list[i]) is { } problem)
            {
                return (null, $"Entry {i + 1}: {problem}");
            }
        }
        return (list, null);
    }

    /// <summary>What is wrong with one entry of a settings list, or null.</summary>
    public static string? ValidateEntry(object? entry)
    {
        switch (entry)
        {
            case null:
                return "empty entry (null)";
            case string text when string.IsNullOrWhiteSpace(text):
                return "empty text";
            case ButtonDefinition button:
                if (button.IsSeparator)
                {
                    return null;
                }
                if (string.IsNullOrWhiteSpace(button.Command) && button.Menu is not { Count: > 0 })
                {
                    return "Command is empty (a nc_ command, a program path, or \"-\" for a separator)";
                }
                foreach (var item in button.Menu ?? [])
                {
                    if (ValidateEntry(item) is { } inner)
                    {
                        return "In Menu: " + inner;
                    }
                }
                return null;
            case ColorRule rule:
                if (string.IsNullOrWhiteSpace(rule.Mask))
                {
                    return "Mask is empty (e.g. \"*.txt\")";
                }
                if (!Color.TryParse(rule.LightColor, out _))
                {
                    return $"LightColor is not a valid color: \"{rule.LightColor}\" (#RRGGBB)";
                }
                if (!Color.TryParse(rule.DarkColor, out _))
                {
                    return $"DarkColor is not a valid color: \"{rule.DarkColor}\" (#RRGGBB)";
                }
                return rule.MaxAgeMinutes is < 0 ? "MaxAgeMinutes cannot be negative" : null;
            case EditorAssociation editor:
                return string.IsNullOrWhiteSpace(editor.Mask) ? "Mask is empty"
                    : string.IsNullOrWhiteSpace(editor.Program) ? "Program is empty (a path, \"internal\" or \"system\")" : null;
            case ThumbnailFieldRule rule:
                return string.IsNullOrWhiteSpace(rule.Mask) ? "Mask is empty"
                    : string.IsNullOrWhiteSpace(rule.Fields) ? "Fields is empty (e.g. \"image.dimensions\")" : null;
            case FileAssociation association:
                return string.IsNullOrWhiteSpace(association.Mask) ? "Mask is empty"
                    : string.IsNullOrWhiteSpace(association.Command) ? "Command is empty (a program path, a nc_ command, \"lister\", \"editor\" or \"system\")" : null;
            default:
                return null;
        }
    }

    /// <summary>A copy of a settings object through JSON (list entries are edited on copies, never on the live settings).</summary>
    public static T CloneEntry<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, s_json), s_json)!;

    /// <summary>Apply / OK hand over copies of the entries, so editing after Apply does not reach the live settings.</summary>
    private static Action<AppSettings, AppSettings> CopyList<T>(Func<AppSettings, List<T>> get, Action<AppSettings, List<T>> set) =>
        (from, to) => set(to, get(from).Select(CloneEntry).ToList());

    /// <summary>A list edited with controls (<see cref="ListEditor{T}"/>): entries on the left, the fields of the selected one on the right.</summary>
    private static SettingItem ListItem<T>(string page, string group, string label, string? description, Func<AppSettings, List<T>> get,
        Action<AppSettings, List<T>> set, ListEditorOptions<T> options) where T : class =>
        new(page, group, label, description, (s, changed) => new ListEditor<T>(get(s), options, list =>
        {
            set(s, list);
            changed();
        }), CopyList(get, set), FullWidth: true);

    /// <summary>One text of a list of texts (ignore list ...), editable in a <see cref="ListEditor{T}"/>.</summary>
    public sealed class TextEntry
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>A list of texts edited one entry per line of the list.</summary>
    private static SettingItem TextListItem(string page, string group, string label, string? description, string fieldLabel, string? watermark,
        Func<AppSettings, List<string>> get, Action<AppSettings, List<string>> set) =>
        new(page, group, label, description, (s, changed) => new ListEditor<TextEntry>(get(s).Select(v => new TextEntry { Value = v }), new ListEditorOptions<TextEntry>
        {
            Create = () => new TextEntry(),
            Clone = e => new TextEntry { Value = e.Value },
            Summary = e => e.Value.Length > 0 ? e.Value : "(empty)",
            Validate = e => string.IsNullOrWhiteSpace(e.Value) ? "empty text" : null,
            Fields = [new(fieldLabel, ListFieldKind.Text, e => e.Value, (e, v) => e.Value = v ?? string.Empty) { PlaceholderText = watermark }],
        }, list =>
        {
            set(s, list.Select(e => e.Value).ToList());
            changed();
        }), CopyOf(s => get(s).ToList(), set), FullWidth: true);

    /// <summary>Text of a nullable number field; empty = none.</summary>
    private static int? ParseNumber(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) ? n : null;

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>Configuration > Color: colors by file type.</summary>
    private static ListEditorOptions<ColorRule> ColorRuleOptions() => new()
    {
        Create = () => new ColorRule { Name = "New rule", Mask = "*.txt", LightColor = "#1565C0", DarkColor = "#90CAF9" },
        Clone = CloneEntry,
        Summary = r => (string.IsNullOrWhiteSpace(r.Name) ? string.Empty : r.Name + " — ") + r.Mask,
        Swatches = r => [r.LightColor, r.DarkColor],
        Validate = r => ValidateEntry(r),
        AddText = "Add rule",
        Fields =
        [
            new("Name", ListFieldKind.Text, r => r.Name, (r, v) => r.Name = v ?? string.Empty) { PlaceholderText = "e.g. Pictures" },
            new("Mask", ListFieldKind.Text, r => r.Mask, (r, v) => r.Mask = v ?? string.Empty) { PlaceholderText = "e.g. *.jpg *.png | thumbs\\ (name\\ for folders)" },
            new("Color (light)", ListFieldKind.Color, r => r.LightColor, (r, v) => r.LightColor = v ?? string.Empty),
            new("Color (dark)", ListFieldKind.Color, r => r.DarkColor, (r, v) => r.DarkColor = v ?? string.Empty),
            new("Changed within (minutes)", ListFieldKind.Number, r => r.MaxAgeMinutes?.ToString(CultureInfo.CurrentCulture), (r, v) => r.MaxAgeMinutes = ParseNumber(v))
                { PlaceholderText = "any age" },
            new("Executable files only", ListFieldKind.Bool, r => r.ExecutableOnly.ToString(CultureInfo.InvariantCulture), (r, v) => r.ExecutableOnly = v == bool.TrueString),
        ],
    };

    /// <summary>Configuration > Edit/View: editor by file type.</summary>
    private static ListEditorOptions<EditorAssociation> EditorOptions() => new()
    {
        Create = () => new EditorAssociation { Mask = "*.txt", Program = "internal" },
        Clone = CloneEntry,
        Summary = e => e.Mask + "  →  " + e.Program,
        Validate = e => ValidateEntry(e),
        AddText = "Add file type",
        PickProgram = title => PickProgram?.Invoke(title) ?? Task.FromResult<string?>(null),
        Fields =
        [
            new("Mask", ListFieldKind.Text, e => e.Mask, (e, v) => e.Mask = v ?? string.Empty) { PlaceholderText = "e.g. *.md *.txt" },
            new("Editor", ListFieldKind.Program, e => e.Program, (e, v) => e.Program = v ?? string.Empty)
            {
                PlaceholderText = "program path",
                Options = [("internal", "Built-in"), ("system", "System")],
            },
        ],
    };

    /// <summary>Configuration > Associations: internal associations (Enter).</summary>
    private static ListEditorOptions<FileAssociation> AssociationOptions() => new()
    {
        Create = () => new FileAssociation { Mask = "*.txt", Command = "editor" },
        Clone = CloneEntry,
        Summary = a => (string.IsNullOrWhiteSpace(a.Label) ? string.Empty : a.Label + " — ") + a.Mask + "  →  " + a.Command,
        Validate = a => ValidateEntry(a),
        AddText = "Add association",
        PickProgram = title => PickProgram?.Invoke(title) ?? Task.FromResult<string?>(null),
        Fields =
        [
            new("Label", ListFieldKind.Text, a => a.Label, (a, v) => a.Label = EmptyToNull(v)) { PlaceholderText = "name in the Open with menu" },
            new("Mask", ListFieldKind.Text, a => a.Mask, (a, v) => a.Mask = v ?? string.Empty) { PlaceholderText = "e.g. *.log *.txt" },
            new("Command", ListFieldKind.Program, a => a.Command, (a, v) => a.Command = v ?? string.Empty)
            {
                PlaceholderText = "program path or nc_ command",
                Options = [("lister", "Lister"), ("editor", "Editor"), ("system", "System")],
                PickCommand = true,
            },
            new("Parameters", ListFieldKind.Text, a => a.Parameters, (a, v) => a.Parameters = EmptyToNull(v)) { PlaceholderText = "default \"%P%N\" (%P path, %N name ...)" },
        ],
    };

    /// <summary>Configuration > Thumbnails: fields under thumbnails by file type.</summary>
    private static ListEditorOptions<ThumbnailFieldRule> ThumbnailFieldOptions() => new()
    {
        Create = () => new ThumbnailFieldRule { Mask = "*.jpg *.png", Fields = "image.dimensions" },
        Clone = CloneEntry,
        Summary = r => r.Mask + "  →  " + r.Fields,
        Validate = r => ValidateEntry(r),
        AddText = "Add file type",
        Fields =
        [
            new("Mask", ListFieldKind.Text, r => r.Mask, (r, v) => r.Mask = v ?? string.Empty) { PlaceholderText = "e.g. *.mp4 *.mkv" },
            new("Fields", ListFieldKind.Text, r => r.Fields, (r, v) => r.Fields = v ?? string.Empty) { PlaceholderText = "e.g. image.dimensions media.duration (separated by spaces)" },
        ],
    };

    /// <summary>Configuration > Misc.: the Start menu.</summary>
    private static ListEditorOptions<ButtonDefinition> StartMenuOptions() => new()
    {
        Create = () => new ButtonDefinition { Label = "New program", Command = string.Empty },
        CreateSeparator = ButtonDefinition.Separator,
        Clone = CloneEntry,
        Summary = b => b.IsSeparator ? "──────────" : (b.Label ?? b.Command) + (b.IsMenu ? "  ▸" : string.Empty),
        Validate = b => ValidateEntry(b),
        AddText = "Add item",
        PickProgram = title => PickProgram?.Invoke(title) ?? Task.FromResult<string?>(null),
        Fields =
        [
            new("Name in the menu", ListFieldKind.Text, b => b.Label, (b, v) => b.Label = EmptyToNull(v)) { IsVisibleFor = b => !b.IsSeparator },
            new("Command", ListFieldKind.Program, b => b.Command, (b, v) => b.Command = v ?? string.Empty)
                { PlaceholderText = "program path or nc_ command", IsVisibleFor = b => !b.IsSeparator, PickCommand = true },
            new("Parameters", ListFieldKind.Text, b => b.Parameters, (b, v) => b.Parameters = EmptyToNull(v))
                { PlaceholderText = "e.g. %P%N", IsVisibleFor = b => !b.IsSeparator },
            new("Start path", ListFieldKind.Text, b => b.StartPath, (b, v) => b.StartPath = EmptyToNull(v))
                { PlaceholderText = "empty = the folder of the panel", IsVisibleFor = b => !b.IsSeparator },
            new("Window", ListFieldKind.Choice, b => b.WindowState.ToString(), (b, v) => b.WindowState = Enum.TryParse<ProgramWindowState>(v, out var w) ? w : ProgramWindowState.Normal)
            {
                Options = [(nameof(ProgramWindowState.Normal), "Normal"), (nameof(ProgramWindowState.Minimized), "Minimized"), (nameof(ProgramWindowState.Maximized), "Maximized")],
                IsVisibleFor = b => !b.IsSeparator,
            },
        ],
    };

    /// <summary>
    /// Configuration > Proxy: how web requests (WebDAV, S3, Azure, cloud drives, URL downloads, the update check) reach the
    /// internet. The server fields are used only with "Use this proxy"; the password goes to the password store.
    /// </summary>
    private static SettingItem ProxyItem(string page, string group) =>
        new(page, group, "Connections", "WebDAV, S3, Azure Blob, Google Drive, OneDrive, Dropbox, URL downloads and the update check go this way; FTP, SFTP and SMB too with a proxy of your own (when chosen below). Open connections keep their way until they connect again.",
            (s, changed) =>
            {
                var modes = new (string Text, Core.Net.ProxyMode Mode)[]
                {
                    ("Use the system's proxy", Core.Net.ProxyMode.System),
                    ("No proxy (connect directly)", Core.Net.ProxyMode.None),
                    ("Use this proxy:", Core.Net.ProxyMode.Manual),
                    ("Automatic configuration script (PAC):", Core.Net.ProxyMode.Pac),
                };
                var host = new TextBox { Text = s.ProxyHost, PlaceholderText = "proxy.example.com or http://proxy:3128", MinWidth = 300, Classes = { "noloc" } };
                var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = s.ProxyPort, Width = 140, FormatString = "0" };
                var kinds = ProxyKinds;
                var kind = new ComboBox { ItemsSource = kinds.Select(k => k.Text).ToList(), Width = 220, SelectedIndex = Math.Max(0, Array.FindIndex(kinds, k => k.Kind == s.ProxyKind)) };
                var keyFile = new TextBox { Text = s.ProxyKeyFile, PlaceholderText = "empty: the password logs in", MinWidth = 300, Classes = { "noloc" } };
                var pac = new TextBox { Text = s.ProxyPacUrl, PlaceholderText = "http://wpad.example.com/wpad.dat", MinWidth = 300, Classes = { "noloc" }, Margin = new Thickness(26, 2, 0, 0) };
                var fileTransfers = new CheckBox { Content = "Also for FTP, SFTP and SMB connections", IsChecked = s.ProxyFileTransfers };
                ToolTip.SetTip(fileTransfers, "An HTTP proxy has to allow CONNECT to their ports (21, 22, 445 ...); SOCKS and SSH proxies take any connection.");
                var user = new TextBox { Text = s.ProxyUser, PlaceholderText = "only when the proxy asks for one", MinWidth = 300, Classes = { "noloc" } };
                var password = new TextBox
                {
                    PasswordChar = '•', MinWidth = 300,
                    PlaceholderText = Core.Net.WebProxyConfig.Password is { Length: > 0 } ? "saved (type to change)" : "kept encrypted in the password store",
                };
                var bypass = new TextBox { Text = s.ProxyBypass, PlaceholderText = "localhost; *.local; 10.*", MinWidth = 300, Classes = { "noloc" } };
                var bypassLocal = new CheckBox { Content = "Also for local names (without a dot)", IsChecked = s.ProxyBypassLocal };
                var problem = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap };

                var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 10, Margin = new Thickness(26, 2, 0, 0) };
                var rows = new (string Label, Control Editor)[]
                {
                    ("Type", kind), ("Server", host), ("Port", port), ("User name", user), ("Password", password), ("Private key", keyFile),
                    ("Do not use it for", bypass), (string.Empty, bypassLocal), (string.Empty, fileTransfers),
                };
                for (var i = 0; i < rows.Length; i++)
                {
                    fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var label = new TextBlock { Text = rows[i].Label, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetRow(label, i);
                    Grid.SetRow(rows[i].Editor, i);
                    Grid.SetColumn(rows[i].Editor, 1);
                    rows[i].Editor.HorizontalAlignment = HorizontalAlignment.Left;
                    fields.Children.AddRange([label, rows[i].Editor]);
                }

                void Update()
                {
                    fields.IsEnabled = s.ProxyMode is Core.Net.ProxyMode.Manual or Core.Net.ProxyMode.Pac;
                    pac.IsEnabled = s.ProxyMode == Core.Net.ProxyMode.Pac;
                    // the server of the PAC mode comes from the script; its user, password and the file transfer choice stay
                    foreach (var control in new Control[] { kind, host, port, bypass, bypassLocal })
                    {
                        control.IsEnabled = s.ProxyMode == Core.Net.ProxyMode.Manual;
                    }
                    keyFile.IsEnabled = s.ProxyMode == Core.Net.ProxyMode.Manual && s.ProxyKind == Core.Net.ProxyKind.Ssh;
                    problem.Text = s.GetProxyOptions().Problem ?? string.Empty;
                    // a manual proxy without a server (or a PAC mode without a script) cannot be taken
                    var wrong = s.ProxyMode == Core.Net.ProxyMode.Pac ? pac : host;
                    host.Classes.Remove(InvalidJsonClass);
                    pac.Classes.Remove(InvalidJsonClass);
                    if (problem.Text.Length > 0)
                    {
                        wrong.Classes.Add(InvalidJsonClass);
                    }
                }

                var choices = new StackPanel { Spacing = 4 };
                foreach (var (text, mode) in modes)
                {
                    var radio = new RadioButton { Content = text, GroupName = "proxymode", IsChecked = s.ProxyMode == mode };
                    radio.IsCheckedChanged += (_, _) =>
                    {
                        if (radio.IsChecked == true)
                        {
                            s.ProxyMode = mode;
                            Update();
                            changed();
                        }
                    };
                    choices.Children.Add(radio);
                    if (mode == Core.Net.ProxyMode.Pac)
                    {
                        choices.Children.Add(pac);
                    }
                }
                host.TextChanged += (_, _) => { s.ProxyHost = host.Text?.Trim() ?? string.Empty; Update(); changed(); };
                port.ValueChanged += (_, _) => { s.ProxyPort = (int)(port.Value ?? 8080); Update(); changed(); };
                user.TextChanged += (_, _) => { s.ProxyUser = string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim(); changed(); };
                password.TextChanged += (_, _) => { s.ProxyPasswordChange = password.Text ?? string.Empty; changed(); };
                bypass.TextChanged += (_, _) => { s.ProxyBypass = bypass.Text ?? string.Empty; changed(); };
                keyFile.TextChanged += (_, _) => { s.ProxyKeyFile = string.IsNullOrWhiteSpace(keyFile.Text) ? null : keyFile.Text.Trim(); changed(); };
                pac.TextChanged += (_, _) => { s.ProxyPacUrl = pac.Text?.Trim() ?? string.Empty; Update(); changed(); };
                bypassLocal.IsCheckedChanged += (_, _) => { s.ProxyBypassLocal = bypassLocal.IsChecked == true; changed(); };
                fileTransfers.IsCheckedChanged += (_, _) => { s.ProxyFileTransfers = fileTransfers.IsChecked == true; changed(); };
                kind.SelectionChanged += (_, _) =>
                {
                    var chosen = kinds[Math.Max(0, kind.SelectedIndex)].Kind;
                    if (chosen != s.ProxyKind)
                    {
                        // the usual port of the kind, when the port was the old kind's usual one
                        if (s.ProxyPort == Core.Net.ProxyOptions.DefaultPort(s.ProxyKind))
                        {
                            port.Value = Core.Net.ProxyOptions.DefaultPort(chosen);
                        }
                        s.ProxyKind = chosen;
                        Update();
                        changed();
                    }
                };

                // tries the settings as they are in the window (before OK)
                var testAddress = new TextBox { Text = string.IsNullOrWhiteSpace(s.UpdateCheckUrl) ? "https://github.com" : s.UpdateCheckUrl, MinWidth = 300, Classes = { "noloc" } };
                var test = new Button { Content = "Test" };
                var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                test.Click += async (_, _) =>
                {
                    if (!Uri.TryCreate(testAddress.Text?.Trim(), UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
                    {
                        result.Text = "Type an http or https address to try.";
                        return;
                    }
                    test.IsEnabled = false;
                    result.Text = "Connecting...";
                    result.Text = await TestProxyAsync(s.GetProxyOptions(), s.ProxyPasswordChange ?? Core.Net.WebProxyConfig.Password, target);
                    test.IsEnabled = true;
                };
                var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                testRow.Children.AddRange([testAddress, test]);

                var panel = new StackPanel { Spacing = 6 };
                panel.Children.AddRange([choices, fields, problem, new TextBlock { Text = "Try the connection", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) }, testRow, result]);
                Update();
                return panel;
            },
            (from, to) =>
            {
                to.ProxyMode = from.ProxyMode;
                to.ProxyHost = from.ProxyHost;
                to.ProxyPort = from.ProxyPort;
                to.ProxyUser = from.ProxyUser;
                to.ProxyBypass = from.ProxyBypass;
                to.ProxyBypassLocal = from.ProxyBypassLocal;
                to.ProxyKind = from.ProxyKind;
                to.ProxyKeyFile = from.ProxyKeyFile;
                to.ProxyPacUrl = from.ProxyPacUrl;
                to.ProxyFileTransfers = from.ProxyFileTransfers;
                to.ProxyPasswordChange = from.ProxyPasswordChange; // the main window stores it and clears it
            }, FullWidth: true);

    /// <summary>The proxy kinds offered (settings and the connection manager).</summary>
    public static (string Text, Core.Net.ProxyKind Kind)[] ProxyKinds { get; } =
    [
        ("HTTP", Core.Net.ProxyKind.Http),
        ("HTTPS (TLS to the proxy)", Core.Net.ProxyKind.Https),
        ("SOCKS5", Core.Net.ProxyKind.Socks5),
        ("SOCKS4a", Core.Net.ProxyKind.Socks4a),
        ("SOCKS4", Core.Net.ProxyKind.Socks4),
        ("SSH tunnel", Core.Net.ProxyKind.Ssh),
    ];

    /// <summary>Requests an address with a proxy setting and tells how it went (which proxy, the answer, the time).</summary>
    public static async Task<string> TestProxyAsync(Core.Net.ProxyOptions options, string? password, Uri target)
    {
        if (options.Problem is { } problem)
        {
            return problem;
        }
        var via = Core.Net.WebProxyConfig.ProxyFor(target, options);
        var started = DateTime.UtcNow;
        try
        {
            using var http = new HttpClient(Core.Net.WebProxyConfig.CreateHandler(options, password)) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            if (response.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired)
            {
                return "The proxy asks for a user name and password (407).";
            }
            return string.Format(CultureInfo.CurrentCulture, "{0}: HTTP {1} {2} in {3:0.0} s.",
                via is null ? "Connected directly" : string.Format(CultureInfo.CurrentCulture, "Connected through {0}", via.Authority),
                (int)response.StatusCode, response.ReasonPhrase, seconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            var reason = ex is TaskCanceledException ? "no answer in 15 s" : ex.GetBaseException().Message;
            return via is null
                ? string.Format(CultureInfo.CurrentCulture, "Could not connect directly: {0}", reason)
                : string.Format(CultureInfo.CurrentCulture, "Could not connect through {0}: {1}", via.Authority, reason);
        }
    }

    /// <summary>The plugin folders (settings folder and program folder); set by the main window.</summary>
    public static IReadOnlyList<string> PluginRoots { get; set; } = [];

    /// <summary>Configuration > Plugins: every installed plugin with a check box; unchecked ones are not loaded.</summary>
    private static SettingItem PluginsItem(string page, string group) =>
        new(page, group, "Installed plugins", "Unchecked plugins are not loaded. Takes effect after a restart.", (s, changed) =>
        {
            var panel = new StackPanel { Spacing = 6 };
            var found = NextCommander.Core.Plugins.PluginLoader.Discover(PluginRoots);
            var names = found.Select(p => p.Name).ToList();
            // disabled plugins that are not installed any more stay in the list (unchecked) until they are removed
            names.AddRange(s.DisabledPlugins.Where(d => !names.Contains(d, StringComparer.OrdinalIgnoreCase)));
            if (names.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No plugins are installed. A plugin is a folder with a plugin.json in the plugins folder of the settings folder or of the program.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                });
                return panel;
            }
            foreach (var name in names)
            {
                var info = found.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                var details = info.Folder is null ? "(not installed)"
                    : string.Join("  ·  ", new[] { info.Manifest?.Version, info.Manifest?.Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
                var header = new StackPanel { Spacing = 1 };
                header.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
                if (details.Length > 0)
                {
                    header.Children.Add(new TextBlock { Text = details, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
                }
                var box = new CheckBox { Content = header, Tag = name, IsChecked = !s.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase) };
                if (info.Folder is not null)
                {
                    ToolTip.SetTip(box, info.Folder);
                }
                box.IsCheckedChanged += (_, _) =>
                {
                    var others = s.DisabledPlugins.Where(d => !string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
                    s.DisabledPlugins = box.IsChecked == true ? [.. others] : [.. others, name];
                    changed();
                };
                panel.Children.Add(box);
            }
            return panel;
        }, CopyOf(s => s.DisabledPlugins.ToList(), (s, v) => s.DisabledPlugins = v), FullWidth: true);

    /// <summary>The column fields for the view modes (column providers, plugins); set by the main window.</summary>
    public static Func<IReadOnlyList<NextCommander.Core.Columns.ColumnField>>? ColumnFields { get; set; }

    /// <summary>Configuration > View modes: the sets in a list and their editor (columns, automatic folders, colors, commands).</summary>
    private static SettingItem ViewModesItem(string page, string group) =>
        new(page, group, "View modes", "Column sets: their columns, the folders that switch to them automatically, background and tab colors, commands run on entering and leaving.",
            (s, changed) =>
            {
                var list = new StackPanel { Spacing = 2 };
                foreach (var set in s.ColumnSets)
                {
                    var details = new List<string> { string.Format(CultureInfo.CurrentCulture, "{0} columns", set.Columns.Count) };
                    if (!string.IsNullOrWhiteSpace(set.AutoPathMask))
                    {
                        details.Add(string.Format(CultureInfo.CurrentCulture, "automatic in {0}", set.AutoPathMask));
                    }
                    list.Children.Add(new TextBlock { Text = set.Name + "   (" + string.Join(", ", details) + ")", Classes = { "noloc" } });
                }
                if (s.ColumnSets.Count == 0)
                {
                    list.Children.Add(new TextBlock { Text = "No view modes yet.", Opacity = 0.6 });
                }
                var edit = new Button { Content = "Edit...", HorizontalAlignment = HorizontalAlignment.Left };
                edit.Click += async (_, _) =>
                {
                    if (TopLevel.GetTopLevel(edit) is not Window owner)
                    {
                        return;
                    }
                    var sets = s.ColumnSets.Select(CloneEntry).ToList();
                    await new ColumnSetDialog(sets, ColumnFields?.Invoke() ?? []).ShowDialog(owner);
                    s.ColumnSets = sets;
                    changed();
                    edit.FindAncestorOfType<SettingsView>()?.ShowItems();
                };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.AddRange([list, edit]);
                return panel;
            }, CopyList(s => s.ColumnSets, (s, v) => s.ColumnSets = v), FullWidth: true);

    private static IReadOnlyList<SettingItem> BuildItems()
    {
        const string layout = "Layout";
        const string display = "Display";
        const string icons = "Display/Icons";
        const string font = "Display/Font";
        const string color = "Display/Color";
        const string formats = "Display/Data formats";
        const string tabs = "Display/Folder tabs";
        const string viewModes = "Display/View modes";
        const string language = "Display/Language";
        const string operation = "Operation";
        const string edit = "Operation/Edit and view";
        const string copy = "Operation/Copy and delete";
        const string quick = "Operation/Quick search";
        const string ftp = "Operation/FTP";
        const string proxy = "Operation/Proxy";
        const string plugins = "Operation/Plugins";
        const string thumbs = "Operation/Thumbnails";
        const string assoc = "Operation/Associations";
        const string index = "Operation/File index";
        const string link = "Operation/PC link";
        const string packer = "Packer";
        const string sevenZip = "Packer/7-Zip";
        const string misc = "Misc.";
        const string zip = "Packer/Zip";
        const string refresh = "Refresh";
        const string ignore = "Ignore list";
        const string history = "History";
        const string log = "Log file";
        const string screen = "Screen layout";
        return
        [
            // Layout
            Bool(layout, screen, "Show button bar", null, s => s.ShowButtonBar, (s, v) => s.ShowButtonBar = v),
            Bool(layout, screen, "Show button bar 2 (vertical)", null, s => s.ShowVerticalButtonBar, (s, v) => s.ShowVerticalButtonBar = v),
            Bool(layout, screen, "Show drive buttons", "Drive button bar above each panel.", s => s.ShowDriveButtons, (s, v) => s.ShowDriveButtons = v),
            Bool(layout, screen, "Two drive button bars (above the file windows)", "When off, one bar for the active panel below the button bar.",
                s => s.TwoDriveBars, (s, v) => s.TwoDriveBars = v, indent: true),
            Bool(layout, screen, "Flat icons", "When off, drive buttons have a frame.", s => s.FlatDriveButtons, (s, v) => s.FlatDriveButtons = v, indent: true),
            Bool(layout, screen, "Shortcut: home folder", "The user's home folder at the end of the drive buttons.", s => s.DriveBarHome, (s, v) => s.DriveBarHome = v, indent: true),
            Bool(layout, screen, "Shortcut: Documents", "The Documents folder at the end of the drive buttons.", s => s.DriveBarDocuments, (s, v) => s.DriveBarDocuments = v, indent: true),
            Bool(layout, screen, "Shortcut: Downloads", "The Downloads folder at the end of the drive buttons.", s => s.DriveBarDownloads, (s, v) => s.DriveBarDownloads = v, indent: true),
            Bool(layout, screen, "Shortcut: Desktop", "The Desktop folder at the end of the drive buttons.", s => s.DriveBarDesktop, (s, v) => s.DriveBarDesktop = v, indent: true),
            Bool(layout, screen, "Shortcuts: cloud folders and WSL",
                "The local folders of OneDrive, iCloud Drive, Dropbox, Google Drive ... and the WSL distributions at the end of the drive buttons (the Computer view always lists them).",
                s => s.DriveBarSyncFolders, (s, v) => s.DriveBarSyncFolders = v, indent: true),
            Bool(layout, screen, "Trash / Recycle Bin button", "The trash after Network on the drive buttons: deleted files in the panel.",
                s => s.DriveBarTrash, (s, v) => s.DriveBarTrash = v, indent: true),
            Bool(layout, screen, "Show drive combobox", "Drive box, free space, \\ and .. buttons.", s => s.ShowDriveCombo, (s, v) => s.ShowDriveCombo = v),
            Bool(layout, screen, "Show folder tabs", null, s => s.ShowFolderTabs, (s, v) => s.ShowFolderTabs = v),
            Bool(layout, screen, "Show current directory", "The path line above the file list.", s => s.ShowPathLine, (s, v) => s.ShowPathLine = v),
            Bool(layout, screen, "Show clickable path parts ('breadcrumb bar')", "When off, the path is shown as plain text.", s => s.ShowBreadcrumbs, (s, v) => s.ShowBreadcrumbs = v, indent: true),
            Bool(layout, screen, "Open automatically on mouse hover", "Hovering over a path separator opens the subfolder list.",
                s => s.BreadcrumbAutoOpen, (s, v) => s.BreadcrumbAutoOpen = v, indent: true),
            Bool(layout, screen, "Show buttons for history list and hotlist", null, s => s.ShowPathButtons, (s, v) => s.ShowPathButtons = v, indent: true),
            Bool(layout, screen, "Show tabstop header", null, s => s.ShowColumnHeaders, (s, v) => s.ShowColumnHeaders = v),
            Bool(layout, screen, "Show status bar", null, s => s.ShowStatusBar, (s, v) => s.ShowStatusBar = v),
            Bool(layout, screen, "Show command line", null, s => s.ShowCommandLine, (s, v) => s.ShowCommandLine = v),
            Bool(layout, screen, "Command line only when it has the focus", "Typing in the panel goes to the command line, the focus stays in the panel; Enter opens the file under the cursor.",
                s => s.CommandLineOnlyWhenFocused, (s, v) => s.CommandLineOnlyWhenFocused = v, indent: true),
            Bool(layout, screen, "Show function key buttons", null, s => s.ShowFunctionKeys, (s, v) => s.ShowFunctionKeys = v),
            Bool(layout, "Panels", "Folder tree panel", null, s => s.ShowTreePane, (s, v) => s.ShowTreePane = v),
            Bool(layout, "Panels", "Vertical arrangement (panels above each other)", null, s => s.VerticalPanels, (s, v) => s.VerticalPanels = v),
            Choice(layout, "Button bars", "Button bar icon size", "To edit the buttons one by one: Configuration > Button bar...",
                [.. Controls.ButtonBarControl.IconSizes.Select(n => ($"{n} x {n}", n))], s => Controls.ButtonBarControl.NormalizeSize(s.ButtonBarIconSize), (s, v) => s.ButtonBarIconSize = v),
            Choice(layout, "Button bars", "Vertical button bar icon size", null,
                [.. Controls.ButtonBarControl.IconSizes.Select(n => ($"{n} x {n}", n))], s => Controls.ButtonBarControl.NormalizeSize(s.VerticalButtonBarIconSize), (s, v) => s.VerticalButtonBarIconSize = v),
            Choice(layout, "Button bars", "Button bar style",
                "Classic: small buttons in rows. Mica: Windows 11. Graphite: a low, dense bar. Aurora: a floating bar with round buttons. The new styles keep one row; what does not fit goes into the ⋯ menu.",
                [("Classic", Core.Config.ToolbarStyle.Classic), ("Mica (Windows 11)", Core.Config.ToolbarStyle.Mica), ("Graphite (compact)", Core.Config.ToolbarStyle.Graphite),
                    ("Aurora (floating, round)", Core.Config.ToolbarStyle.Aurora)],
                s => s.ToolbarStyle, (s, v) => s.ToolbarStyle = v),
            Bool(layout, "Button bars", "Names next to the icons", "The buttons of the bar at the top show their name too.",
                s => s.ToolbarLabels, (s, v) => s.ToolbarLabels = v),
            ButtonBarItem(layout, "Button bars", "Button bar buttons", "Add, remove, move and change buttons (command, parameters, icon, tooltip) in the button bar editor.",
                vertical: false),
            ButtonBarItem(layout, "Button bars", "Vertical button bar buttons", "The bar between the panels.", vertical: true),
            Bool(layout, "Button bars", "Plugins button", "A menu of the loaded plugins and their places (rclone remotes ...) in the button bar; shown only when plugins are installed.",
                s => s.ShowPluginsButton, (s, v) => s.ShowPluginsButton = v),

            // Display
            Bool(display, "File display", "Show hidden files", "Can also be changed with Ctrl+H.", s => s.ShowHidden, (s, v) => s.ShowHidden = v),
            Bool(display, "File display", "Show system files", null, s => s.ShowSystem, (s, v) => s.ShowSystem = v),
            Bool(display, "File display", "Show the parent folder [..] in drive roots too", null, s => s.ShowParentInRoot, (s, v) => s.ShowParentInRoot = v),
            Bool(display, "File display", "Show square brackets [ ] around directories", "[Documents] style.", s => s.DirectoryBrackets, (s, v) => s.DirectoryBrackets = v),
            Bool(display, "File display", "Append \\ to folder names", null, s => s.AppendDirectorySeparator, (s, v) => s.AppendDirectorySeparator = v),
            Number(display, "File display", "Brief view column width", "Pixels.", 80, 600, s => s.BriefColumnWidth, (s, v) => s.BriefColumnWidth = v, 10),
            Choice(display, "File display", "Sort method", null,
                [("Natural: alphabetical, numbers by value (file2 < file10)", SortMethod.Natural),
                    ("Alphabetical, considering accents", SortMethod.Alphabetical),
                    ("Strict character code order", SortMethod.Ordinal)], s => s.SortMethod, (s, v) => s.SortMethod = v),
            Bool(display, "Tree", "Always load the whole folder tree", "When the tree panel opens, all subfolders are read in the background.", s => s.TreeLoadAll, (s, v) => s.TreeLoadAll = v),
            Bool(display, "Tree", "Enter in the tree opens the folder in the other panel", null, s => s.TreeEnterOpensInOtherPanel, (s, v) => s.TreeEnterOpensInOtherPanel = v),
            Choice(display, "Sorting of folders", "Folders", null,
                [("Always by name", true), ("Like files (also by date)", false)], s => s.SortDirectoriesByNameOnly, (s, v) => s.SortDirectoriesByNameOnly = v),
            Bool(display, "Help texts", "Show help texts (tooltips) for buttons", null, s => s.ShowToolTips, (s, v) => s.ShowToolTips = v),
            Bool(display, "Help texts", "Show volume labels for drives", "In the drive list.", s => s.ShowDriveLabels, (s, v) => s.ShowDriveLabels = v),
            Bool(display, "Help texts", "Show file names that do not fit as a tooltip", null, s => s.ShowTruncatedNameTips, (s, v) => s.ShowTruncatedNameTips = v),
            Bool(display, "Help texts", "Show file comments in the tooltip (descript.ion)", null, s => s.ShowCommentTips, (s, v) => s.ShowCommentTips = v, indent: true),
            Choice(display, "Folder sizes", "Calculate folder sizes automatically", null, [("Off", DirectorySizeMode.Off), ("Local drives only", DirectorySizeMode.LocalOnly)],
                s => s.AutoDirectorySizes, (s, v) => s.AutoDirectorySizes = v),

            FileChoice(icons, "Icon pack", "Icon pack",
                "The icons of the menus and button bars; when the operating system's icons are not used, also those of the drive buttons and the file list.",
                () => [.. Services.Icons.IconPacks.Available().Select(p => (p.DisplayName, p.Id))],
                s => s.IconPack, (s, v) => s.IconPack = v, IconPackPreview),
            IconPackEditors(icons, "Icon pack"),
            Choice(icons, "Icons left of the file name", "Icons", null,
                [("All associated + EXE/LNK (slow)", IconMode.AllWithExe), ("All associated", IconMode.Associated), ("Standard icons only", IconMode.Standard),
                    ("No icons", IconMode.None)], s => s.IconMode, (s, v) => s.IconMode = v),
            Bool(icons, "Icons left of the file name", "Do not load EXE/LNK icons on removable disks", null,
                s => s.NoExeIconsOnRemovable, (s, v) => s.NoExeIconsOnRemovable = v, indent: true),
            Bool(icons, "Icons left of the file name", "Do not load EXE/LNK icons on the network", null, s => s.NoExeIconsOnNetwork, (s, v) => s.NoExeIconsOnNetwork = v, indent: true),
            Bool(icons, "Icons left of the file name", "Show icons of special folders", "Folders customized with desktop.ini show their own icon (Windows).",
                s => s.SpecialFolderIcons, (s, v) => s.SpecialFolderIcons = v),
            Bool(icons, "Icons left of the file name", "Show overlay icons", "E.g. cloud sync status (Windows; loaded per file).",
                s => s.OverlayIcons, (s, v) => s.OverlayIcons = v),
            Bool(icons, "Icons left of the file name", "Show '!' on hidden/system files", null, s => s.HiddenMark, (s, v) => s.HiddenMark = v),
            Bool(icons, "Icons left of the file name", "Show hidden files dimmed", null, s => s.DimHiddenFiles, (s, v) => s.DimHiddenFiles = v),
            Choice(icons, "Icon size", "Icon size", null, [("16x16", 16), ("24x24", 24), ("32x32", 32), ("48x48", 48)], s => s.IconSize, (s, v) => s.IconSize = v),
            Number(icons, "Icon size", "Extra spacing: horizontal", "Pixels.", 0, 12, s => s.IconSpacingH, (s, v) => s.IconSpacingH = (int)v),
            Number(icons, "Icon size", "Extra spacing: vertical", "Pixels.", 0, 12, s => s.IconSpacingV, (s, v) => s.IconSpacingV = (int)v),
            Bool(icons, "Icons in main menu", "Show icons for commands in the main menu", null, s => s.ShowMenuIcons, (s, v) => s.ShowMenuIcons = v),
            Choice(icons, "Icons in main menu", "Menu icon size", null, [("16x16", 16), ("20x20", 20), ("24x24", 24)], s => s.MenuIconSize, (s, v) => s.MenuIconSize = v),
            Bool(icons, "Icons in main menu", "Show a frame around the icon", null, s => s.MenuIconFrame, (s, v) => s.MenuIconFrame = v, indent: true),
            Bool(icons, "Icons in main menu", "Show checked items as pressed buttons instead of check marks", null,
                s => s.MenuChecksAsPressed, (s, v) => s.MenuChecksAsPressed = v, indent: true),
            Bool(icons, "Drives", "Take drive icons from the system", "Windows drive icons; when off, the built-in icon.",
                s => s.SystemDriveIcons, (s, v) => s.SystemDriveIcons = v),

            Number(font, "Dialog scaling (does not scale the main window)", "Dialog scale (%)", "100 = normal. Applies to windows opened afterwards.",
                50, 300, s => s.DialogScalePercent, (s, v) => s.DialogScalePercent = (int)v, 10),
            Font(font, "File list font", "Panel font", "Font of the file lists.", s => s.PanelFontFamily, (s, v) => s.PanelFontFamily = v),
            Number(font, "File list font", "Panel font size", null, 8, 32, s => s.PanelFontSize, (s, v) => s.PanelFontSize = v),
            Font(font, "Main window font", "Main window font", "Menus, bars, command line.", s => s.MainFontFamily, (s, v) => s.MainFontFamily = v),
            Number(font, "Main window font", "Main window font size", null, 8, 32, s => s.MainFontSize, (s, v) => s.MainFontSize = v),
            Font(font, "Dialog font", "Dialog font", "Dialogs and tool windows.", s => s.DialogFontFamily, (s, v) => s.DialogFontFamily = v),
            Number(font, "Dialog font", "Dialog font size", null, 8, 32, s => s.DialogFontSize, (s, v) => s.DialogFontSize = v),
            Font(font, "Lister font", "Lister font", null, s => s.ListerFont,
                (s, v) => s.ListerFont = string.IsNullOrEmpty(v) ? "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace" : v),
            Number(font, "Lister font", "Lister font size", null, 6, 48, s => s.ListerFontSize, (s, v) => s.ListerFontSize = v),
            Font(font, "Editor font", "Editor font", "The built-in editor (F4); also chosen in the editor with View > Font....", s => s.EditorFont,
                (s, v) => s.EditorFont = v, firstChoice: "(as the Lister font)"),
            Number(font, "Editor font", "Editor font size", "0 = the size of the Lister font.", 0, 48, s => s.EditorFontSize, (s, v) => s.EditorFontSize = v),
            MonospaceFont(font, "Internal terminal font", "Terminal font",
                "Fixed-width fonts only. Default: Fixedsys Excelsior (when installed; the Fixedsys of Windows is a bitmap font and cannot be drawn), otherwise Consolas / Cascadia Mono.",
                s => s.TerminalFont, (s, v) => s.TerminalFont = string.IsNullOrEmpty(v) ? AppSettings.DefaultTerminalFont : v),
            Number(font, "Internal terminal font", "Terminal font size", null, 6, 48, s => s.TerminalFontSize, (s, v) => s.TerminalFontSize = v),

            FileChoice(color, "Color", "Color theme",
                "Without a theme the system colors are used.",
                () => [("Follow the system (light / dark)", ColorTheme.SystemThemeId), ("System colors (light)", string.Empty), .. Services.ColorThemes.Available().Select(t => (t.DisplayName, t.Id))],
                s => s.ColorThemeFile, (s, v) => s.ColorThemeFile = v),
            ThemeEditors(color, "Color"),
            Bool(color, "Color", "File type colors", "Color rules are edited below.", s => s.ColorsEnabled, (s, v) => s.ColorsEnabled = v),
            Bool(color, "Color", "Inverted selection", "Marked files with the mark color as background.", s => s.InvertedSelection, (s, v) => s.InvertedSelection = v),
            ListItem(color, "Colors by file type", "Color rules",
                "A color for the light and one for the dark look; optional age and executable conditions. The first matching rule wins (order with ▲ ▼).",
                s => s.ColorRules, (s, v) => s.ColorRules = v, ColorRuleOptions()),

            Choice(formats, "Data formats", "Size format", null, [("Bytes (1,234,567)", SizeDisplay.Bytes), ("Short (1.2 M)", SizeDisplay.Dynamic)],
                s => s.SizeDisplay, (s, v) => s.SizeDisplay = v),
            Bool(formats, "Data formats", "1 k = 1000 bytes", "When off, 1 k = 1024 bytes.", s => s.DecimalSizeUnits, (s, v) => s.DecimalSizeUnits = v),
            Text(formats, "Data formats", "Date format", ".NET format, e.g. dd.MM.yyyy HH:mm:ss.fff (empty = system format; the century and 12 hour options below apply to the system format only).",
                s => s.DateFormat, (s, v) => s.DateFormat = v, "system format"),
            Bool(formats, "Data formats", "Show the century (2026)", "When off, the year has two digits.", s => s.ShowCentury, (s, v) => s.ShowCentury = v),
            Bool(formats, "Data formats", "12 hours (AM/PM)", null, s => s.TwelveHourClock, (s, v) => s.TwelveHourClock = v),
            Text(formats, "Separators", "Date separator", "Empty = the system's separator.", s => s.DateSeparator, (s, v) => s.DateSeparator = v ?? string.Empty, "system"),
            Text(formats, "Separators", "Time separator", null, s => s.TimeSeparator, (s, v) => s.TimeSeparator = v ?? string.Empty, "system"),
            Text(formats, "Separators", "Thousands separator", null, s => s.ThousandsSeparator, (s, v) => s.ThousandsSeparator = v ?? string.Empty, "system"),
            Text(formats, "Separators", "Decimal separator", null, s => s.DecimalSeparator, (s, v) => s.DecimalSeparator = v ?? string.Empty, "system"),
            Bool(formats, "Columns", "Show the extension directly after the name", "The extension column is hidden.", s => s.ExtensionInName, (s, v) => s.ExtensionInName = v),
            Bool(formats, "Columns", "Name column fills the window width", "When off, the name column has the width below.", s => s.FitColumnsToWidth, (s, v) => s.FitColumnsToWidth = v),
            Number(formats, "Columns", "Name column width", null, 60, 2000, s => s.NameColumnWidth, (s, v) => s.NameColumnWidth = v, 10),
            Number(formats, "Columns", "Extension column width", null, 30, 500, s => s.ExtensionColumnWidth, (s, v) => s.ExtensionColumnWidth = v, 4),
            Number(formats, "Columns", "Size column width", null, 30, 500, s => s.SizeColumnWidth, (s, v) => s.SizeColumnWidth = v, 4),
            Number(formats, "Columns", "Date column width", null, 30, 500, s => s.DateColumnWidth, (s, v) => s.DateColumnWidth = v, 4),
            Number(formats, "Columns", "Attributes column width", null, 30, 500, s => s.AttributesColumnWidth, (s, v) => s.AttributesColumnWidth = v, 4),
            Choice(formats, "Header and footer", "Size unit", "In the free space line and the status line.",
                [("Bytes", FooterSizeUnit.Bytes), ("Kilobytes", FooterSizeUnit.Kilobytes), ("Dynamic (1.2 G)", FooterSizeUnit.Dynamic)],
                s => s.FooterSizeUnit, (s, v) => s.FooterSizeUnit = v),
            Bool(formats, "Header and footer", "Show the folder count in the footer", null, s => s.ShowFolderCount, (s, v) => s.ShowFolderCount = v),

            ViewModesItem(viewModes, "Custom columns and view modes"),
            Bool(tabs, "Folder tab headers", "Show tab header also when there is only one tab", null, s => s.AlwaysShowTabs, (s, v) => s.AlwaysShowTabs = v),
            Bool(tabs, "Folder tab headers", "Tabs on several rows", "Tabs that do not fit go to the next row instead of scrolling.", s => s.TabsMultiline, (s, v) => s.TabsMultiline = v),
            Bool(tabs, "Folder tab headers", "All tabs have the same width", null, s => s.TabsSameWidth, (s, v) => s.TabsSameWidth = v),
            Number(tabs, "Folder tab headers", "Title length limit (characters)", "0 = no limit.", 0, 200, s => s.TabTitleMaxLength, (s, v) => s.TabTitleMaxLength = (int)v),
            Bool(tabs, "Folder tab headers", "Show locked tabs with *", null, s => s.ShowLockedTabStar, (s, v) => s.ShowLockedTabStar = v),
            Bool(tabs, "Folder tab headers", "Show the drive letter in the tab title", "On Windows, e.g. \"C: Windows\".", s => s.TabShowDriveLetter, (s, v) => s.TabShowDriveLetter = v),
            Bool(tabs, "Folder tab headers", "Show icons on tabs", "Folder icon; a lock on locked tabs. Drives and system folders (documents, downloads, desktop, home) always show their own icon.", s => s.ShowTabIcons, (s, v) => s.ShowTabIcons = v),
            Bool(tabs, "Behavior", "Ask before closing all tabs", null, s => s.ConfirmCloseAllTabs, (s, v) => s.ConfirmCloseAllTabs = v),
            Bool(tabs, "Behavior", "Double click closes the tab", null, s => s.DoubleClickClosesTab, (s, v) => s.DoubleClickClosesTab = v),
            Bool(tabs, "Behavior", "Ctrl+Up opens the new tab in the foreground", "When off, the new tab opens in the background.", s => s.OpenDirInNewTabForeground, (s, v) => s.OpenDirInNewTabForeground = v),
            Bool(tabs, "Behavior", "Ctrl+Tab switches to the most recently used tab", "When off, switches to the tab on the right.", s => s.CtrlTabRecentTab, (s, v) => s.CtrlTabRecentTab = v),
            Bool(tabs, "Behavior", "Ctrl+Tab shows the list of tabs", "While Ctrl is held, Tab moves through the list; releasing Ctrl switches to the selected tab.", s => s.CtrlTabShowsList, (s, v) => s.CtrlTabShowsList = v),
            Bool(tabs, "Behavior", "Open new tabs next to the current tab", "When off, they are added at the end.", s => s.NewTabNextToCurrent, (s, v) => s.NewTabNextToCurrent = v),
            Bool(tabs, "Behavior", "Clicking a tab activates its panel", "When off, clicking a tab of the other panel only switches the tab.", s => s.TabClickActivatesPanel, (s, v) => s.TabClickActivatesPanel = v),
            Bool(tabs, "Behavior", "Save the file type filter of each tab", null, s => s.SaveTabFilters, (s, v) => s.SaveTabFilters = v),

            FileChoice(language, "Language", "Language",
                "Applied at once with Apply or OK.",
                () => [("System", "system"), .. Services.UiLocalization.Available().Select(l => (l.Name, l.Code))], s => s.Language, (s, v) => s.Language = v,
                code => code == "system" ? LanguageSystemIcon() : Services.Icons.Flags.Create(code)),
            LanguageEditor(language, "Language"),

            // Operation
            Bool(operation, "Main program", "Single instance", "A second launch sends the folder/file to the running window.", s => s.SingleInstance, (s, v) => s.SingleInstance = v),
            Bool(operation, "Main program", "Move to the system tray when minimized", "The window leaves the taskbar; clicking the tray icon brings it back.",
                s => s.MinimizeToTray, (s, v) => s.MinimizeToTray = v),
            Bool(operation, "Main program", "Show progress in the title", "Percentage in the window title during long operations.", s => s.ProgressInTitle, (s, v) => s.ProgressInTitle = v),
            Bool(operation, "Navigation", "Always go to the root when changing drives", "When off, goes back to the folder last used on that drive.",
                s => s.DriveChangeGoesToRoot, (s, v) => s.DriveChangeGoesToRoot = v),
            Bool(operation, "Navigation", "Open with a single click", "Files and folders open with one click (Ctrl / Shift click selects).", s => s.SingleClickOpen, (s, v) => s.SingleClickOpen = v),
            Bool(operation, "Navigation", "Complete paths automatically", "In the path bar, the copy / move target and the command line, folders matching the typed path are listed (Tab, or Enter after choosing with ↑ / ↓, takes a suggestion; Enter alone goes to what is typed).",
                s => s.AutoCompletePaths, (s, v) => s.AutoCompletePaths = v),
            Bool(operation, "Navigation", "Append the suggested name", "The first suggestion is added, selected, after what was typed; typing on replaces it.",
                s => s.AutoCompleteAppend, (s, v) => s.AutoCompleteAppend = v, indent: true),
            Number(operation, "Navigation", "Rows kept visible below and above the cursor", "While scrolling; 0 = off. In the full view.", 0, 20,
                s => s.ExtraLinesAroundCursor, (s, v) => s.ExtraLinesAroundCursor = (int)v),
            Bool(operation, "Selection", "Select with the right button (Norton Commander style)", "Right-clicking marks the file; held (0.6 s) it opens the context menu. On macOS a right click with the mouse (two fingers, Ctrl+click) always opens the menu, marking is Cmd+click.",
                s => s.RightButtonSelects, (s, v) => s.RightButtonSelects = v),
            Bool(operation, "Selection", "Selection mode only on touch screens", "Tapping selects, holding opens the context menu; the mouse works as usual.",
                s => s.SelectionModeTouchOnly, (s, v) => s.SelectionModeTouchOnly = v, indent: true),
            .. (OperatingSystem.IsWindows()
                ? new[] { Bool(operation, "Context menu", "Show the system's context menu (Windows Explorer)",
                    "The menu of the files as in Explorer (open with, send to, share, the entries of installed programs). Shift+right click opens NextCommander's own menu.",
                    s => s.SystemContextMenu, (s, v) => s.SystemContextMenu = v) }
                : []),
            Bool(operation, "Selection", "Select by clicking the icon", "A left click on a file's icon selects it (full and brief view).",
                s => s.IconClickSelects, (s, v) => s.IconClickSelects = v),
            Choice(operation, "Selection", "Selection (with Num +)",
                "Num+ / Num- patterns, Num* (invert selection) and Select all. A pattern ending in \\ (name\\) selects folders either way.",
                [("Select files only", false), ("Also select directories", true)],
                s => s.PatternSelectsFolders, (s, v) => s.PatternSelectsFolders = v),
            Bool(operation, "Selection", "Calculate the folder size when selecting with Space", null, s => s.SpaceCalculatesFolderSize, (s, v) => s.SpaceCalculatesFolderSize = v),
            Bool(operation, "Selection", "Use the file index for folder sizes", "Folder sizes come at once from the application's own file index (when on) or from Everything (Windows, es.exe); without either the folder is walked.",
                s => s.FolderSizeFromEverything, (s, v) => s.FolderSizeFromEverything = v, indent: true),
            Bool(operation, "Selection", "Select only the name when renaming", "The extension is not selected.", s => s.RenameSelectsNameOnly, (s, v) => s.RenameSelectsNameOnly = v),
            Bool(operation, "Save on exit", "Folders and tabs", "When off, the folders last saved with \"Save settings\" open.",
                s => s.SaveDirsOnExit, (s, v) => s.SaveDirsOnExit = v),
            Bool(operation, "Save on exit", "Panel views (view, sorting, columns)", null, s => s.SavePanelViewsOnExit, (s, v) => s.SavePanelViewsOnExit = v),
            Bool(operation, "Save on exit", "Command line history", null, s => s.SaveCommandLineHistoryOnExit, (s, v) => s.SaveCommandLineHistoryOnExit = v),
            Choice(operation, "File comments (descript.ion)", "Character set when writing", null,
                [("Automatic (ASCII or UTF-8)", DescriptionEncoding.Auto), ("ANSI", DescriptionEncoding.Ansi), ("DOS (OEM)", DescriptionEncoding.Oem),
                    ("UTF-8", DescriptionEncoding.Utf8), ("UTF-16", DescriptionEncoding.Utf16)],
                s => s.DescriptionEncoding, (s, v) => s.DescriptionEncoding = v),
            Bool(operation, "File comments (descript.ion)", "Copy / move comments with the files", "In local folders; also when renaming.",
                s => s.CopyCommentsWithFiles, (s, v) => s.CopyCommentsWithFiles = v),
            Bool(operation, "Updates", "Check for updates at startup", "At most once a day; notifies only if a new version is available.", s => s.CheckUpdatesOnStart, (s, v) => s.CheckUpdatesOnStart = v),
            Text(operation, "Updates", "Update URL", "URL of a JSON file with version info ({\"version\",\"url\"}) or a GitHub releases/latest API URL. When empty, updates are never checked.",
                s => s.UpdateCheckUrl, (s, v) => s.UpdateCheckUrl = v ?? string.Empty, "https://api.github.com/repos/<owner>/<repo>/releases/latest"),

            Bool(edit, "Lister", "Word wrap in Lister", null, s => s.ListerWrap, (s, v) => s.ListerWrap = v),
            Bool(edit, "Lister", "Open PDF, Office, HTML, Markdown and RTF files with preview", "When off, opens as text; key 5 switches to preview.", s => s.ListerPreviewDocuments, (s, v) => s.ListerPreviewDocuments = v),
            Bool(edit, "Lister", "Reload Lister when file changes", null, s => s.ListerAutoReload, (s, v) => s.ListerAutoReload = v),
            Number(edit, "Lister", "Full text view limit (MB)", "Larger texts open in streaming view.", 1, 1024, s => s.ListerMaxTextBytes / 1024d / 1024, (s, v) => s.ListerMaxTextBytes = (long)(v * 1024 * 1024)),
            Bool(edit, "Editor", "F4 uses the internal editor", "When off, the program below or the system application opens.", s => s.UseInternalEditor, (s, v) => s.UseInternalEditor = v),
            Text(edit, "Editor", "External editor", "Used for binary files and when the internal editor is off (empty = system application).", s => s.Editor, (s, v) => s.Editor = v, "e.g. C:\\Program Files\\Notepad++\\notepad++.exe"),
            ListItem(edit, "Editor", "Editor by file type", "F4 opens these file types with the program given (Built-in: the internal editor, System: the system's program).",
                s => s.EditorAssociations, (s, v) => s.EditorAssociations = v, EditorOptions()),

            Bool(copy, "Deleting", "Delete to Recycle Bin", "F8 / Del moves to the Recycle Bin, Shift+Del deletes permanently. When off, F8 also deletes permanently.", s => s.DeleteToTrash, (s, v) => s.DeleteToTrash = v),
            Bool(copy, "Deleting", "Confirm before deleting", null, s => s.ConfirmDelete, (s, v) => s.ConfirmDelete = v),
            Bool(copy, "Copying", "Verify after copying", "Default for the option in the Copy dialog.", s => s.VerifyCopy, (s, v) => s.VerifyCopy = v),
            Bool(copy, "Copying", "Copy symbolic links as links", "When off, the contents of the target file are copied.", s => s.CopyFileLinksAsLinks, (s, v) => s.CopyFileLinksAsLinks = v),
            Choice(copy, "Copying", "When a target file exists", "The answer chosen first in the Copy dialog (and used by drag and drop without the dialog).",
                [.. CopyDialog.Policies], s => s.DefaultConflictPolicy, (s, v) => s.DefaultConflictPolicy = v),
            Bool(copy, "Drag and drop", "Show the Copy dialog when dropping files", "When off, dropped files are copied or moved at once with the default answers.",
                s => s.ConfirmDragDrop, (s, v) => s.ConfirmDragDrop = v),

            Choice(quick, "Quick search", "Quick search", null, [("Ctrl+Alt+letter", QuickSearchTrigger.CtrlAltLetter), ("Letters only (as you type)", QuickSearchTrigger.LetterOnly)],
                s => s.QuickSearchTrigger, (s, v) => s.QuickSearchTrigger = v),
            Choice(quick, "Quick search", "Quick search matching", null, [("From start", Core.Matching.QuickMatchMode.Prefix), ("Anywhere", Core.Matching.QuickMatchMode.Anywhere),
                ("Fuzzy", Core.Matching.QuickMatchMode.Fuzzy)], s => s.QuickSearchMode, (s, v) => s.QuickSearchMode = v),

            Bool(ftp, "FTP", "Show hidden files on FTP", "LIST -a.", s => s.FtpShowHidden, (s, v) => s.FtpShowHidden = v),
            Bool(ftp, "FTP", "Use passive mode for new connections", "The default of the passive mode option of a new FTP / FTPS connection.",
                s => s.FtpPassiveDefault, (s, v) => s.FtpPassiveDefault = v),
            Number(ftp, "FTP", "Retries when the server is busy", "How often a command is tried again after a dropped connection or a busy server (applies to new connections).",
                0, 20, s => s.FtpRetries, (s, v) => s.FtpRetries = (int)v),

            PluginsItem(plugins, "Plugins"),
            ProxyItem(proxy, "Proxy"),

            Number(thumbs, "Thumbnails", "Thumbnail size", "Also changes with Ctrl+wheel.", 32, 400, s => s.ThumbnailSize, (s, v) => s.ThumbnailSize = (int)v, 8),
            Bool(thumbs, "Cache", "Keep thumbnails on disk", "Thumbnails are stored in the settings folder (\"thumbs\") and appear at once the next time. Takes effect after a restart.",
                s => s.ThumbnailDiskCache, (s, v) => s.ThumbnailDiskCache = v),
            ClearThumbnailCache(thumbs, "Cache"),

            // File index
            Bool(index, "File index", "Use the application's own file index",
                "The names, sizes and times of the files and folders on the chosen disks are indexed in the background; find files, duplicate files and folder sizes answer at once. No Everything, plocate or Spotlight needed. The first scan can take a few minutes; later only changed folders are read, and changes are followed live while running.",
                s => s.FileIndexEnabled, (s, v) => s.FileIndexEnabled = v),
            Text(index, "File index", "Folders to index", OperatingSystem.IsWindows() ? "Separated by ;. Empty: all fixed drives." : "Separated by ;. Empty: the home folder.",
                s => s.FileIndexRoots, (s, v) => s.FileIndexRoots = v ?? string.Empty, string.Join("; ", Services.FileIndexService.DefaultRoots())),
            Text(index, "File index", "Left out", "Separated by ;: folder names (node_modules) or full paths (C:\\Windows\\WinSxS). The trash and system folders are always left out.",
                s => s.FileIndexExcluded, (s, v) => s.FileIndexExcluded = v ?? string.Empty, "e.g. node_modules; .git"),
            .. (OperatingSystem.IsWindows()
                ? new[] { Bool(index, "Finding changes", "Use the NTFS change journal at start",
                    "The disk keeps a list of every change; at start only the folders named there are read again, including files whose contents changed while the program was closed. Needs no administrator rights. Where the list cannot tell (other file systems, too many changes), the folder times are compared.",
                    s => s.FileIndexUseChangeJournal, (s, v) => s.FileIndexUseChangeJournal = v) }
                : []),
            Number(index, "Finding changes", "Check every folder again (hours)",
                "Reads all folders again after this many hours (at start or while running), also those whose time did not change, so that sizes and times of files changed while the program was closed are corrected. Takes as long as the first scan. 0 = never.",
                0, 8760, s => s.FileIndexFullCheckHours, (s, v) => s.FileIndexFullCheckHours = (int)v, 24),
            IndexStatus(index, "File index"),

            // PC link: the folders this computer shares with other NextCommanders
            Bool(link, "Sharing", "Share these folders with other computers",
                "Another NextCommander connected by a cable (USB or Ethernet) or over the network opens them with Net > PC link. The first time, both screens show the same code and this computer allows the other one. Same as Net > Share with PC link.",
                s => s.LinkSharing, (s, v) => s.LinkSharing = v),
            ListItem(link, "Sharing", "Shared folders", "Only these folders can be reached; nothing above them. A read-only folder cannot be changed by the other computer.",
                s => s.LinkShares, (s, v) => s.LinkShares = v, LinkShareOptions()),
            LinkStatusItem(link, "Sharing"),
            Text(link, "This computer", "Computer name", "The name the other computers see. Empty: the name of the machine.",
                s => s.LinkComputerName, (s, v) => s.LinkComputerName = v?.Trim() ?? string.Empty, Environment.MachineName),
            Number(link, "This computer", "Port", "TCP port of the link and UDP port of finding computers; the same on both computers.",
                1024, 65535, s => s.LinkPort, (s, v) => s.LinkPort = (int)v),
            LinkFirewallItem(link, "This computer"),
            LinkPeersItem(link, "Allowed computers"),

            ListItem(assoc, "Internal associations", "Internal associations (Enter)",
                "Enter opens these file types with the command given: a program (with parameters), a nc_ command, Lister, the editor or the system's program. Shift+Enter: open together.",
                s => s.Associations, (s, v) => s.Associations = v, AssociationOptions()),

            // Packer
            Choice(packer, "Packer", "Default archive format", "Format preselected in the Alt+F5 dialog.",
                [("zip", "zip"), ("7z", "7z"), ("tar", "tar"), ("tar.gz", "tar.gz"), ("tar.bz2", "tar.bz2"), ("tar.zst", "tar.zst")], s => s.PackFormat, (s, v) => s.PackFormat = v),
            Bool(packer, "Archives", "Set the archive date to the date of its newest file", "Applies to archives packed to a local disk.",
                s => s.PackSetArchiveDate, (s, v) => s.PackSetArchiveDate = v),
            Bool(packer, "Archives", "Enter opens archives", "When off, archives open with the system application (Ctrl+PgDn always enters).", s => s.OpenArchivesOnEnter, (s, v) => s.OpenArchivesOnEnter = v),
            Text(sevenZip, "7-Zip program", "7-Zip path", "7z archives are written with this program; when empty, PATH and the usual install folders are searched.", s => s.SevenZipPath, (s, v) => s.SevenZipPath = v,
                "e.g. C:\\Program Files\\7-Zip\\7z.exe"),
            Text(sevenZip, "7-Zip program", "Extra parameters", "Added to the 7-Zip command line when packing (e.g. -mqs=on -mtc=on).", s => s.SevenZipArguments, (s, v) => s.SevenZipArguments = v,
                "e.g. -mqs=on"),
            Choice(sevenZip, "7-Zip compression", "Compression level", null,
                [("Like the pack dialog", -1), ("Store (0)", 0), ("Fastest (1)", 1), ("Fast (3)", 3), ("Normal (5)", 5), ("Maximum (7)", 7), ("Ultra (9)", 9)],
                s => s.SevenZipLevel, (s, v) => s.SevenZipLevel = v),
            Choice(sevenZip, "7-Zip compression", "Compression method", null,
                [("Default (LZMA2)", string.Empty), ("LZMA2", "LZMA2"), ("LZMA", "LZMA"), ("PPMd (text)", "PPMd"), ("BZip2", "BZip2")],
                s => s.SevenZipMethod, (s, v) => s.SevenZipMethod = v),
            Bool(sevenZip, "7-Zip compression", "Create solid archives", "Better compression; reading or changing a single file of the archive is slower.",
                s => s.SevenZipSolid, (s, v) => s.SevenZipSolid = v),
            Number(sevenZip, "7-Zip compression", "Number of CPU threads", string.Format(CultureInfo.CurrentCulture, "0 = 7-Zip decides ({0} processors).", Environment.ProcessorCount),
                0, 256, s => s.SevenZipThreads, (s, v) => s.SevenZipThreads = (int)v),
            Bool(sevenZip, "Encryption", "Also encrypt the file names", "With a password the names in the archive can only be listed after entering it.",
                s => s.SevenZipEncryptNames, (s, v) => s.SevenZipEncryptNames = v),

            Choice(zip, "Compression", "Default compression level", "Level selected first in the Alt+F5 dialog (zip, 7z, tar.gz ...).",
                [("No compression", 0), ("Fast", 1), ("Normal", 2), ("Smallest", 3)], s => s.PackLevel, (s, v) => s.PackLevel = v),
            Bool(zip, "File names", "Write file names as UTF-8", "When off, names are written in the system's OEM code page (for old programs that do not know UTF-8).",
                s => s.ZipUtf8Names, (s, v) => s.ZipUtf8Names = v),
            Choice(zip, "Encryption", "Encryption of password protected zips", "ZipCrypto is weak but Windows Explorer and old programs can open it; AES needs a program such as 7-Zip or WinRAR.",
                [("AES-256 (recommended)", ZipEncryptionMethod.Aes256), ("AES-128", ZipEncryptionMethod.Aes128), ("ZipCrypto (old, compatible)", ZipEncryptionMethod.ZipCrypto)],
                s => s.ZipEncryption, (s, v) => s.ZipEncryption = v),

            // Refresh
            Bool(refresh, "File lists", "Refresh file lists automatically", "The list updates itself when the shown folder changes.", s => s.AutoRefresh, (s, v) => s.AutoRefresh = v),
            Bool(refresh, "File lists", "On network drives too", null, s => s.AutoRefreshNetworkDrives, (s, v) => s.AutoRefreshNetworkDrives = v, indent: true),
            Bool(refresh, "File lists", "On removable drives too", null, s => s.AutoRefreshRemovableDrives, (s, v) => s.AutoRefreshRemovableDrives = v, indent: true),
            Bool(refresh, "File lists", "Not while the program is in the background", "Changes made while another program has the focus are shown when NextCommander gets it back.",
                s => s.NoRefreshInBackground, (s, v) => s.NoRefreshInBackground = v, indent: true),
            Text(refresh, "File lists", "No automatic refresh in these folders", "Path masks separated by ; (e.g. C:\\Temp*;*\\node_modules*).",
                s => s.AutoRefreshExcluded, (s, v) => s.AutoRefreshExcluded = v, "none", indent: true),
            Bool(refresh, "File lists", "Refresh the panels when the main window gets the focus", "Useful when automatic refresh is off.", s => s.RefreshOnWindowActivate, (s, v) => s.RefreshOnWindowActivate = v),

            // Ignore list
            Bool(ignore, "Ignore list", "Do not show the files and folders below", "Can also be toggled with nc_SwitchIgnoreList.", s => s.IgnoreListEnabled, (s, v) => s.IgnoreListEnabled = v),
            TextListItem(ignore, "Ignore list", "Hidden entries", "Name or mask (\"Thumbs.db\", \"*.tmp\") or full path (\"C:\\pagefile.sys\").",
                "Name, mask or path", "e.g. Thumbs.db, *.tmp, C:\\pagefile.sys", s => s.IgnoreList, (s, v) => s.IgnoreList = v),

            // History
            Number(history, "History", "Folder history length", "Number of folders in the Alt+Down list.", 5, 500, s => s.FolderHistoryLength, (s, v) => s.FolderHistoryLength = (int)v),
            Number(history, "History", "Back / forward steps", "Folders each tab keeps for Back and Forward (Alt+Left / Alt+Right, the arrow buttons of the button bar; right click on them lists the folders). Kept on exit with the folder history.",
                1, 200, s => s.BackForwardLength, (s, v) => s.BackForwardLength = (int)v),
            Number(history, "History", "Command line history length", null, 5, 500, s => s.CommandLineHistoryLength, (s, v) => s.CommandLineHistoryLength = (int)v),

            // Log file
            Bool(log, "Log file", "Write a log of file operations", "Copying, moving, deleting, packing ... are appended to a text file (what is written: below).",
                s => s.LogEnabled, (s, v) => s.LogEnabled = v),
            Text(log, "Log file", "Log file", "Empty: operations.log in the logs folder of the settings. Environment variables can be used (%USERPROFILE%).",
                s => s.LogFile, (s, v) => s.LogFile = v, "operations.log (logs folder)", indent: true),
            Bool(log, "Program log", "Write the program log",
                "Errors and events of NextCommander itself, for finding problems: nextcommander-<date>.log in the logs folder of the settings, one file a day, the last 7 days kept.",
                s => s.ProgramLog, (s, v) => s.ProgramLog = v),
            Choice(log, "Log file rotation", "Start a new log file", "The old file keeps its date in the name (e.g. log-20261110.txt).",
                [("Never (always the same file)", LogRotation.Never), ("Every day", LogRotation.Daily), ("When it is larger than the size below", LogRotation.BySize)],
                s => s.LogRotation, (s, v) => s.LogRotation = v),
            Number(log, "Log file rotation", "Maximum size (KB)", null, 10, 1_000_000, s => s.LogMaxSizeKb, (s, v) => s.LogMaxSizeKb = (int)v, 100),
            Number(log, "Log file rotation", "Keep old log files", "The oldest ones beyond this number are deleted.", 0, 1000, s => s.LogKeepCopies, (s, v) => s.LogKeepCopies = (int)v),
            Bool(log, "Write to the log", "Copying and moving", null, s => s.LogCopyMove, (s, v) => s.LogCopyMove = v),
            Bool(log, "Write to the log", "Deleting", null, s => s.LogDelete, (s, v) => s.LogDelete = v),
            Bool(log, "Write to the log", "Packing", null, s => s.LogPack, (s, v) => s.LogPack = v),
            Bool(log, "Write to the log", "Rename and multi-rename", null, s => s.LogRename, (s, v) => s.LogRename = v),
            Bool(log, "Write to the log", "FTP / SFTP / WebDAV / S3 operations", "Transfers to and from servers, deleting on servers.", s => s.LogRemote, (s, v) => s.LogRemote = v),
            Bool(log, "Write to the log", "Unpacking", "Copying out of archives (Alt+F9, or F5 inside an archive).", s => s.LogUnpack, (s, v) => s.LogUnpack = v),
            Bool(log, "Write to the log", "Errors only", null, s => s.LogErrorsOnly, (s, v) => s.LogErrorsOnly = v),

            // Misc.
            Bool(misc, "Exit", "Ask before closing", null, s => s.ConfirmExit, (s, v) => s.ConfirmExit = v),
            Text(misc, "Internal terminal", "Shell", "Shell of the terminal opened with the terminal button at the end of the command line (e.g. pwsh, powershell, /bin/zsh). Empty: cmd.exe on Windows, $SHELL elsewhere. Applies to a newly started shell.",
                s => s.InternalTerminalShell, (s, v) => s.InternalTerminalShell = v ?? string.Empty, "system shell"),
            Bool(misc, "Internal terminal", "Real terminal (colors, full screen programs)",
                "The shell runs on a pseudo terminal (ConPTY on Windows, a pty on Linux and macOS): colors, cursor movement and full screen programs such as vim, mc and top work. When off, a simple line console is used. Applies to newly opened tabs.",
                s => s.TerminalEmulation, (s, v) => s.TerminalEmulation = v),
            ListItem(misc, "Start menu", "Start menu", "The items of the Start menu: a program (with parameters and start folder) or a nc_ command; separators group them.",
                s => s.StartMenu, (s, v) => s.StartMenu = v, StartMenuOptions()),

            // ---- further options ----
            Choice(sevenZip, "LZMA / LZMA2", "Dictionary size", "Larger: better compression of big archives, more memory.",
                [("Like the level", 0), ("1 MB", 1), ("4 MB", 4), ("16 MB", 16), ("32 MB", 32), ("64 MB", 64), ("128 MB", 128), ("256 MB", 256), ("512 MB", 512), ("1024 MB", 1024)],
                s => s.SevenZipDictionaryMb, (s, v) => s.SevenZipDictionaryMb = v),
            Choice(sevenZip, "LZMA / LZMA2", "Word size", "Larger: a little better compression, slower.",
                [("Like the level", 0), ("8", 8), ("16", 16), ("32", 32), ("64", 64), ("128", 128), ("273", 273)], s => s.SevenZipWordSize, (s, v) => s.SevenZipWordSize = v),
            SevenZipMemory(sevenZip, "LZMA / LZMA2"),

            Text(zip, "Temporary files", "Folder for temporary files", "Used while packing to a server and while changing archives (empty = the system's temp folder).",
                s => s.ArchiveTempDirectory, (s, v) => s.ArchiveTempDirectory = v, "system temp folder"),
            Bool(zip, "Damaged and partial archives", "Open partial zip files",
                "A zip without its end (an unfinished download, one part of a split archive) is listed from the headers of its files; it can only be read.",
                s => s.OpenPartialZip, (s, v) => s.OpenPartialZip = v),
            Bool(zip, "Damaged and partial archives", "Keep damaged files (bad CRC)", "A file unpacked from a damaged archive is kept instead of deleted; the error is still shown.",
                s => s.KeepDamagedFiles, (s, v) => s.KeepDamagedFiles = v),
            .. (OperatingSystem.IsWindows()
                ? new[] { Bool(zip, "File names", "Use 8.3 short names", "Names such as LONGNA~1.TXT, for very old unpackers (where the disk keeps short names).",
                    s => s.ZipShortNames, (s, v) => s.ZipShortNames = v) }
                : []),

            Number(copy, "Copy method", "Copy buffer (KB)", "Blocks read and written at a time.", 4, 262144, s => s.CopyBufferKb, (s, v) => s.CopyBufferKb = (int)v, 64),
            Bool(copy, "Copy method", "Big file mode (without the system cache)", "Large files are written straight to the disk (write-through) with their own buffer; useful for very large files and USB disks.",
                s => s.BigFileMode, (s, v) => s.BigFileMode = v),
            Number(copy, "Copy method", "Files from (MB)", null, 1, 1_000_000, s => s.BigFileThresholdMb, (s, v) => s.BigFileThresholdMb = (int)v, 16, indent: true),
            Number(copy, "Copy method", "Buffer for big files (KB)", null, 64, 262144, s => s.BigFileBufferKb, (s, v) => s.BigFileBufferKb = (int)v, 1024, indent: true),
            Text(copy, "Copy method", "Compatibility mode for these drives", "Drive letters or folders separated by ; (e.g. E;F;\\\\nas): copied in small blocks (64 KB) through the normal file system.",
                s => s.CompatibilityDrives, (s, v) => s.CompatibilityDrives = v, "none"),
            .. (OperatingSystem.IsWindows()
                ? new[]
                {
                    Bool(copy, "Windows Explorer", "Copy and move with Windows Explorer", "Only for copy problems: Explorer copies local files with its own window and questions (no filter, no renaming, no queue).",
                        s => s.CopyViaExplorer, (s, v) => s.CopyViaExplorer = v),
                    Bool(copy, "Windows Explorer", "Delete with Windows Explorer", "Faster for many files; file comments (descript.ion) are not updated.",
                        s => s.DeleteViaExplorer, (s, v) => s.DeleteViaExplorer = v),
                }
                : []),

            Bool(refresh, "Changes in the folder", "Put files that appear at the end of the list", "Until the list is sorted or refreshed again (Ctrl+R).",
                s => s.AddedFilesAtEnd, (s, v) => s.AddedFilesAtEnd = v),
            Text(refresh, "On exit", "Do not remember these drives", "Drive letters or folders separated by ; (e.g. E;F): tabs there open the home folder the next time.",
                s => s.ForgetDrivesOnExit, (s, v) => s.ForgetDrivesOnExit = v, "none"),

            Choice(ftp, "Transfer", "Transfer type", "Text (ASCII) changes line ends between systems; binary keeps every byte.",
                [("Binary (archives, documents ...)", FtpTransferMode.Binary), ("Text (ASCII)", FtpTransferMode.Ascii), ("Text for the files below, binary for the rest", FtpTransferMode.Auto)],
                s => s.FtpTransferMode, (s, v) => s.FtpTransferMode = v),
            Text(ftp, "Transfer", "Text files", null, s => s.FtpAsciiMasks, (s, v) => s.FtpAsciiMasks = v ?? string.Empty, "*.txt *.html", indent: true),
            Number(ftp, "Transfer", "Break off a transfer without data after (s)", "It is then tried again (see the retries above).", 5, 3600, s => s.FtpStallSeconds, (s, v) => s.FtpStallSeconds = (int)v),
            Text(ftp, "Log", "FTP log file", "Commands and server replies are appended to this file (applies to new connections); empty = no log.",
                s => s.FtpLogFile, (s, v) => s.FtpLogFile = v, "no log"),
            Number(ftp, "Active mode (PORT)", "Ports from", "Only these ports are opened for active connections; 0 = any.", 0, 65535, s => s.FtpActivePortFrom, (s, v) => s.FtpActivePortFrom = (int)v),
            Number(ftp, "Active mode (PORT)", "Ports to", null, 0, 65535, s => s.FtpActivePortTo, (s, v) => s.FtpActivePortTo = (int)v),
            Text(ftp, "Active mode (PORT)", "External address", "The address told to the server behind a router with port forwarding (IP or name); empty = the computer's own.",
                s => s.FtpExternalAddress, (s, v) => s.FtpExternalAddress = v, "automatic"),

            Text(thumbs, "Fields under thumbnails", "Fields", "Column fields shown under every thumbnail, separated by spaces (e.g. image.dimensions media.duration).",
                s => s.ThumbnailFields, (s, v) => s.ThumbnailFields = v, "none"),
            ListItem(thumbs, "Fields under thumbnails", "Fields by type", "Files matching a mask show these fields instead; the first matching mask wins.",
                s => s.ThumbnailFieldsByType, (s, v) => s.ThumbnailFieldsByType = v, ThumbnailFieldOptions()),
            Text(thumbs, "Other files", "Text files", "Shown as their first lines.", s => s.ThumbnailTextMask, (s, v) => s.ThumbnailTextMask = v, "off"),
            Text(thumbs, "Other files", "Image viewer program", "IrfanView (i_view64.exe) or XnView nconvert converts these files to a thumbnail.",
                s => s.ThumbnailViewerProgram, (s, v) => s.ThumbnailViewerProgram = v, "e.g. C:\\Program Files\\IrfanView\\i_view64.exe"),
            Text(thumbs, "Other files", "Files for the viewer program", null, s => s.ThumbnailViewerMask, (s, v) => s.ThumbnailViewerMask = v ?? string.Empty, "*.psd *.tga", indent: true),
            .. (OperatingSystem.IsWindows()
                ? new[] { Text(thumbs, "Other files", "Thumbnails from Explorer for", "Explorer's thumbnail handlers (videos, PDF, Office files ...), e.g. *.mp4 *.pdf or *.*; empty = off.",
                    s => s.ThumbnailShellMask, (s, v) => s.ThumbnailShellMask = v, "off") }
                : []),
            RemoveStaleThumbnails(thumbs, "Cache"),

            Bool(log, "Write to the log", "Skipped files", "When \"Skip\" was chosen for an existing file.", s => s.LogSkipped, (s, v) => s.LogSkipped = v),
            Bool(log, "Write to the log", "File system plugin operations", null, s => s.LogPlugins, (s, v) => s.LogPlugins = v),

            Bool(history, "History", "Leave out folders only passed through", "Folders left again within a second without doing anything are not kept.",
                s => s.HistoryThinning, (s, v) => s.HistoryThinning = v),
            Bool(history, "History", "Save the folder history on exit", null, s => s.SaveFolderHistoryOnExit, (s, v) => s.SaveFolderHistoryOnExit = v),
            Bool(history, "Frequently used folders", "Save the frequently used folders on exit", null, s => s.SaveFrequentOnExit, (s, v) => s.SaveFrequentOnExit = v),
            Number(history, "Frequently used folders", "Folders kept", null, 10, 5000, s => s.FrequentMaxSaved, (s, v) => s.FrequentMaxSaved = (int)v, 10),
            Number(history, "Frequently used folders", "Folders shown (Alt+Shift+Down)", null, 5, 500, s => s.FrequentMaxShown, (s, v) => s.FrequentMaxShown = (int)v, 5),

            Text(ignore, "Ignore list", "Keep the list in this file", "One entry per line (# starts a comment); written with Apply / OK. Empty = in the settings.",
                s => s.IgnoreListFile, (s, v) => s.IgnoreListFile = v, "in the settings"),
            IgnoreAddButtons(ignore, "Ignore list"),

            Bool(misc, "Confirmation", "Deleting folders that are not empty", "Asked also when deleting is not confirmed otherwise.",
                s => s.ConfirmDeleteNonEmpty, (s, v) => s.ConfirmDeleteNonEmpty = v),
            Bool(misc, "Confirmation", "Overwriting read-only files", "Asked even when \"Overwrite\" was chosen.", s => s.ConfirmOverwriteReadOnly, (s, v) => s.ConfirmOverwriteReadOnly = v),
            Bool(misc, "Confirmation", "Overwriting hidden or system files", "Asked even when \"Overwrite\" was chosen.", s => s.ConfirmOverwriteHidden, (s, v) => s.ConfirmOverwriteHidden = v),
            Bool(misc, "Confirmation", "Files can be dropped on the F8 Delete button", null, s => s.DropOnDeleteButton, (s, v) => s.DropOnDeleteButton = v),
            Bool(misc, "Confirmation", "Ask before deleting them", null, s => s.ConfirmDropOnDeleteButton, (s, v) => s.ConfirmDropOnDeleteButton = v, indent: true),
            Text(misc, "Sounds", "Sound when an operation ends", "A sound file (.wav; .mp3 too on Linux and macOS) played when copying, deleting, packing ... is over.",
                s => s.SoundFile, (s, v) => s.SoundFile = v, "no sound"),
            Number(misc, "Sounds", "Only for operations longer than (s)", null, 0, 3600, s => s.SoundMinSeconds, (s, v) => s.SoundMinSeconds = (int)v),
        ];
    }
}
