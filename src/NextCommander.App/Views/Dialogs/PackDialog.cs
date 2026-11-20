using System.Globalization;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Formatting;
using NextCommander.Core.Localization;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Pack files (Alt+F5; the look of the user's template, 20 November 2026): what is packed and its size at the top; the
/// archive's path; the format as cards (what each is good for, whether it encrypts, makes volumes or self-extracting
/// archives); the compression level with an estimate of the size; the options as switches with their own settings
/// under them (volume size, password and its strength, the zip method or the 7-Zip file names); a summary line with
/// Add to queue, Cancel and Pack at the bottom.
/// </summary>
public sealed class PackDialog : Window
{
    /// <summary>A packer of the format cards.</summary>
    private sealed record Packer(string Text, PackFormat Format, IPackerPlugin? Plugin, string Hint, string Description, bool Available);

    /// <summary>The built-in packers with what they are good for.</summary>
    private static readonly (string Text, PackFormat Format, string Hint, string Description)[] s_builtIn =
    [
        ("ZIP", PackFormat.Zip, "Opens anywhere", "Works on every system without extra software."),
        ("7-Zip", PackFormat.SevenZip, "Smallest", "Best compression; needs 7-Zip or a modern unpacker to open."),
        ("TAR", PackFormat.Tar, "No compression", "Bundles the files without compressing them."),
        ("TGZ", PackFormat.TarGz, "Linux standard", "tar + gzip: the usual format on Linux and macOS."),
        ("TBZ", PackFormat.TarBz2, "Smaller, slower", "tar + bzip2: smaller than gzip, slower to pack."),
        ("TZST", PackFormat.TarZst, "Fast and small", "tar + Zstandard: fast packing with good compression."),
    ];

    /// <summary>Extensions the name may end in (swapped when the packer changes).</summary>
    private static readonly string[] s_extensions = ["zip", "7z", "tar", "tar.gz", "tgz", "tar.bz2", "tbz", "tar.zst", "tzst", "exe", "sfx"];

    /// <summary>The words for a password's strength 0..4.</summary>
    private static readonly string[] s_strengths = ["Very weak", "Weak", "Fair", "Good", "Strong"];

    /// <summary>The names of the levels 0..9.</summary>
    private static readonly string[] s_levels = ["Store", "Fastest", "Fastest", "Fast", "Fast", "Normal", "Normal", "Maximum", "Maximum", "Ultra"];

    /// <summary>Files that are compressed already (pictures, media, archives): they keep their size in the estimate.</summary>
    private static readonly HashSet<string> s_packed = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "heic", "avif", "mp3", "m4a", "aac", "ogg", "opus", "flac", "mp4", "mkv", "mov", "avi", "webm", "m4v",
        "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "cab", "jar", "apk", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "pdf",
    };

    /// <summary>The volume sizes offered (the box takes any other size too).</summary>
    private static readonly (string Text, string Size)[] s_volumes =
        [("10 MB", "10M"), ("25 MB (e-mail)", "25M"), ("100 MB", "100M"), ("700 MB (CD)", "700M"), ("4092 MB (FAT32)", "4092M")];

    /// <summary>Configuration > Packer: compression level chosen first (index into Store / Fast / Normal / Smallest).</summary>
    public static int DefaultLevelIndex { get; set; } = 2;

    /// <summary>Opens Configuration on the Packer page over this dialog ("Configure packer..."); set by the main window.</summary>
    public static Func<Window, Task>? OpenConfiguration { get; set; }

    private readonly List<Packer> _packers = [];
    private readonly List<Border> _cards = [];
    private readonly IReadOnlyList<VfsEntry> _items;
    private readonly long _filesSize;
    private readonly bool _hasFolders;
    private readonly TextBox _target;
    private readonly Slider _level = new() { Minimum = 0, Maximum = 9, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1 };
    private readonly TextBlock _levelName = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _levelNote = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _estimate = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly Border _estimateBar = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
    private readonly TextBlock _saved = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly TextBlock _speed = new() { FontSize = 14, FontWeight = FontWeight.SemiBold };
    private readonly Border _levelCard;
    private readonly TextBlock _formatDescription = new() { FontSize = 12.5, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _formatTags = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, CheckBox> _options = new(StringComparer.Ordinal);
    private readonly Dictionary<CheckBox, TextBlock> _notes = [];
    private readonly CheckBox _recursive = Switch(true);
    private readonly CheckBox _paths = new() { Content = "Keep folder paths inside the archive", IsChecked = true };
    private readonly CheckBox _separate = Switch(false);
    private readonly CheckBox _leaveOutBase = new() { Content = "Leave out the base folder when packing folders" };
    private readonly CheckBox _split = Switch(false);
    private readonly ComboBox _volumeChoice = new() { MinWidth = 170 };
    private readonly TextBox _volume = new() { Text = "100M", Width = 110, Classes = { "noloc" } };
    private readonly TextBlock _volumeNote = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _sfx = Switch(false);
    private readonly CheckBox _encrypt = Switch(false);
    private readonly TextBox _password = new() { PasswordChar = '●', PlaceholderText = "Password" };
    private readonly TextBox _repeat = new() { PasswordChar = '●', PlaceholderText = "Repeat password" };
    private readonly StackPanel _meter = new() { Orientation = Orientation.Horizontal, Spacing = 3, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _passwordNote = new() { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _encryptNames = new() { Content = "Also encrypt file names", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _zipMethod = new() { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _zipCryptoWarning;
    private readonly CheckBox _move = Switch(false);
    private readonly TextBlock _moveWarning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Classes = { "noloc" } };
    private readonly TextBlock _summary = new() { FontSize = 12.5, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#E5484D")), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, IsVisible = false };
    private readonly Button _pack = new() { Content = "Pack", IsDefault = true, Classes = { "tb", "primary" }, MinWidth = 96 };
    private readonly Dictionary<CheckBox, Control> _subs = [];
    private bool _updating;

    public PackDialog(string description, string target, PackFormat format, PackDialogInput? input = null)
    {
        Title = "Pack files";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _items = input?.Items ?? [];
        _filesSize = _items.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.Size));
        _hasFolders = _items.Any(e => e.IsDirectory);
        _target = new TextBox { Text = target, Classes = { "noloc" }, FontFamily = Mono, FontSize = 12.5 };
        _level.Value = new[] { 0, 1, 6, 9 }[Math.Clamp(DefaultLevelIndex, 0, 3)];
        _encryptNames.IsChecked = input?.EncryptNames ?? true;
        _zipMethod.ItemsSource = new[] { "AES-256", "AES-128", "ZipCrypto" };
        _zipMethod.SelectedIndex = (input?.ZipEncryption ?? ZipEncryptionMethod.Aes256) switch
        {
            ZipEncryptionMethod.Aes128 => 1,
            ZipEncryptionMethod.ZipCrypto => 2,
            _ => 0,
        };
        _zipCryptoWarning = Warning("ZipCrypto is easy to break. Use it only when the receiver cannot open AES.");
        _volumeChoice.ItemsSource = s_volumes.Select(v => v.Text).ToList();
        _volumeChoice.SelectedIndex = 2;

        foreach (var (text, packFormat, hint, about) in s_builtIn)
        {
            var available = packFormat != PackFormat.SevenZip || SevenZipCli.IsAvailable;
            _packers.Add(new Packer(text, packFormat, null, available ? hint : "Needs 7-Zip", about, available));
        }
        foreach (var plugin in PluginCatalog.Packers)
        {
            _packers.Add(new Packer(plugin.FormatName.ToUpperInvariant(), PackFormat.Zip, plugin, "Plugin",
                Localizer.T("Written by the packer plugin of {0}.").Replace("{0}", plugin.FormatName, StringComparison.Ordinal), true));
        }

        var main = new StackPanel { Margin = new Thickness(22, 20, 22, 6) };
        main.Children.Add(Head(description));
        main.Children.Add(Section("Archive", null, ArchiveCard()));
        var configure = new Button { Content = "Configure packer...", Classes = { "tb", "ghost" }, Height = 26, MinHeight = 26, Padding = new Thickness(6, 0), FontSize = 12.5, IsEnabled = OpenConfiguration is not null };
        configure[!ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        configure.Click += async (_, _) =>
        {
            if (OpenConfiguration is { } open)
            {
                await open(this);
            }
        };
        main.Children.Add(Section("Format", configure, FormatCard()));
        _levelCard = LevelCard();
        main.Children.Add(Section("Compression", null, _levelCard));
        main.Children.Add(Section("Options", null, OptionsCard()));
        main.Children.Add(_error);
        var scroll = new ScrollViewer { Content = main, MaxHeight = 640, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // ---- the footer: what will happen, then the buttons
        var queue = new Button { Content = "Add to queue", Classes = { "tb" } };
        queue.Click += (_, _) => Accept(queue: true);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "tb" }, MinWidth = 88 };
        cancel.Click += (_, _) => Close();
        _pack.Click += (_, _) => Accept(queue: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([queue, cancel, _pack]);
        var foot = new DockPanel { Margin = new Thickness(22, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        _summary.Margin = new Thickness(0, 0, 12, 0);
        foot.Children.AddRange([buttons, _summary]);
        var footer = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([footer, scroll]);
        Content = root;

        // ---- the changes
        _level.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                Update();
            }
        };
        _target.TextChanged += (_, _) => Update();
        foreach (var box in new[] { _recursive, _paths, _separate, _leaveOutBase, _split, _sfx, _encrypt, _encryptNames, _move })
        {
            box.IsCheckedChanged += (_, _) => Update();
        }
        _sfx.IsCheckedChanged += (_, _) => SwapExtension(); // the name ends in .exe
        _split.IsCheckedChanged += (_, _) =>
        {
            if (_split.IsChecked == true)
            {
                _volume.Focus();
                _volume.SelectAll();
            }
        };
        _encrypt.IsCheckedChanged += (_, _) =>
        {
            if (_encrypt.IsChecked == true)
            {
                _password.Focus();
            }
        };
        _volumeChoice.SelectionChanged += (_, _) =>
        {
            if (!_updating && _volumeChoice.SelectedIndex >= 0)
            {
                _volume.Text = s_volumes[_volumeChoice.SelectedIndex].Size;
            }
        };
        _volume.TextChanged += (_, _) => Update();
        _password.TextChanged += (_, _) => Update();
        _repeat.TextChanged += (_, _) => Update();
        _zipMethod.SelectionChanged += (_, _) => Update();

        var start = _packers.FindIndex(p => p.Format == format && p.Plugin is null && p.Available);
        Choose(Math.Max(0, start));
        Opened += (_, _) =>
        {
            _target.Focus();
            var text = _target.Text ?? string.Empty;
            var name = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/')) + 1;
            _target.SelectionStart = name;
            _target.SelectionEnd = text.Length;
        };
    }

    private static FontFamily Mono => new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static CheckBox Switch(bool on) => new() { Classes = { "switch" }, IsChecked = on, VerticalAlignment = VerticalAlignment.Center };

    private static Control Glyph(string key, double size = 16, IBrush? brush = null) =>
        IconPacks.Glyph(key, IconPacks.Current) is { } geometry
            ? new Viewbox { Width = size, Height = size, Child = IconPacks.Draw(geometry, brush ?? Brushes.Gray) }
            : new Panel { Width = size, Height = size };

    private static Border Card(Control content, Thickness? padding = null)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(14, 12) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    private static StackPanel Section(string title, Control? right, Control card)
    {
        var head = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
        }
        head.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        section.Children.AddRange([head, card]);
        return section;
    }

    private static Border Warning(string text)
    {
        var row = new DockPanel();
        var icon = Glyph("MiInfo", 14, new SolidColorBrush(Color.Parse("#C98A00")));
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.AddRange([icon, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 }]);
        return new Border { Child = row, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(40, 252, 200, 0)) };
    }

    // ---------------------------------------------------------------- the parts

    /// <summary>The head: the icon, "Pack 1 file", what is packed and its size.</summary>
    private Control Head(string description)
    {
        var icon = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromArgb(28, 76, 141, 255)),
            Child = new Viewbox { Width = 24, Height = 24, Child = MenuIcons.Create("nc_PackFiles") ?? new Panel() },
        };
        var folders = _items.Count(e => e.IsDirectory);
        var title = _items.Count switch
        {
            0 => description.TrimEnd(':'),
            1 => folders == 1 ? Localizer.T("Pack 1 folder") : Localizer.T("Pack 1 file"),
            _ => Localizer.T("Pack {0} items").Replace("{0}", _items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
        if (_items.Count > 0)
        {
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
            var names = string.Join(", ", _items.Select(e => e.Name));
            var name = new TextBlock { Text = names, FontFamily = Mono, FontSize = 12, MaxWidth = 460, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
            ToolTip.SetTip(name, names);
            source.Children.AddRange([Glyph(_items.Count > 1 || folders > 0 ? "MiFolderOpen" : "MiDoc", 14), name]);
            if (_filesSize > 0)
            {
                source.Children.Add(new TextBlock { Text = "· " + SizeFormatter.Human(_filesSize) + (_hasFolders ? " " + Localizer.T("+ folders") : string.Empty), FontSize = 12.5, Opacity = 0.7, Classes = { "noloc" } });
            }
            text.Children.Add(source);
        }
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.AddRange([icon, text]);
        return head;
    }

    private Border ArchiveCard()
    {
        var browse = new Button { Content = Glyph("MiFolderOpen", 15), Classes = { "tb", "icon" } };
        ToolTip.SetTip(browse, "Choose the folder of the archive");
        browse.Click += async (_, _) => await ChooseFolderAsync();
        var row = new DockPanel();
        browse.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.AddRange([browse, _target]);
        Avalonia.Automation.AutomationProperties.SetName(_target, "Archive path");
        var panel = new StackPanel();
        panel.Children.AddRange([row, new TextBlock { Text = "The extension follows the chosen format.", FontSize = 12, Opacity = 0.6, Margin = new Thickness(2, 6, 0, 0) }]);
        return Card(panel);
    }

    private Border FormatCard()
    {
        var grid = new UniformGrid { Columns = Math.Min(6, Math.Max(3, _packers.Count)) };
        for (var i = 0; i < _packers.Count; i++)
        {
            var p = _packers[i];
            var index = i;
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.AddRange([
                new TextBlock { Text = p.Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "noloc" } },
                new TextBlock { Text = p.Hint, FontSize = 11, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            ]);
            var card = new Border
            {
                Child = stack, Padding = new Thickness(6, 8, 6, 7), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Opacity = p.Available ? 1 : 0.45, Focusable = p.Available,
            };
            if (!p.Available)
            {
                ToolTip.SetTip(card, "Install 7-Zip (or set its path in Configuration > Packer > 7-Zip) to write 7z archives.");
            }
            card.Tapped += (_, _) =>
            {
                if (p.Available)
                {
                    Choose(index);
                }
            };
            _cards.Add(card);
            grid.Children.Add(card);
        }
        var info = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_formatTags, Dock.Right);
        info.Children.AddRange([_formatTags, _formatDescription]);
        var panel = new StackPanel();
        panel.Children.AddRange([grid, info]);
        return Card(panel);
    }

    private Border LevelCard()
    {
        var ticks = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*") };
        string[] names = ["Store", "Fast", "Normal", "Maximum", "Ultra"];
        for (var i = 0; i < names.Length; i++)
        {
            var tick = new TextBlock
            {
                Text = names[i], FontSize = 11, Opacity = 0.6,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == names.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            };
            Grid.SetColumn(tick, i);
            ticks.Children.Add(tick);
        }
        Avalonia.Automation.AutomationProperties.SetName(_level, "Compression level");
        var slider = new StackPanel();
        slider.Children.AddRange([_level, ticks]);
        var value = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        value.Children.AddRange([_levelName, _levelNote]);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,160"), ColumnSpacing = 18 };
        Grid.SetColumn(value, 1);
        top.Children.AddRange([slider, value]);

        Border Stat(string label, Control valueText, Control? extra = null)
        {
            var stack = new StackPanel();
            stack.Children.AddRange([new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.7 }, valueText]);
            if (extra is not null)
            {
                stack.Children.Add(extra);
            }
            var stat = new Border { Child = stack, Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
            stat[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            return stat;
        }
        _estimateBar[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 0, 0), Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), Child = _estimateBar, ClipToBounds = true };
        track.SizeChanged += (_, _) => Update();
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(0, 12, -8, 0) };
        var cells = new[] { Stat("Estimated size", _estimate, track), Stat("Saved", _saved), Stat("Speed", _speed) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            stats.Children.Add(cells[i]);
        }
        _estimateTrack = track;
        var panel = new StackPanel();
        panel.Children.AddRange([top, stats]);
        return Card(panel);
    }

    private Border? _estimateTrack;

    /// <summary>The options: a switch per row with its own settings under it while it is on.</summary>
    private Border OptionsCard()
    {
        var list = new StackPanel();
        void Row(string glyph, string title, string? note, CheckBox box, Control? sub = null)
        {
            _options[title] = box;
            Avalonia.Automation.AutomationProperties.SetName(box, title);
            var icon = Glyph(glyph, 16);
            icon.Margin = new Thickness(0, 0, 14, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var noteText = new TextBlock { Text = note, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0), IsVisible = note is not null };
            _notes[box] = noteText;
            text.Children.AddRange([new TextBlock { Text = title, FontSize = 13.5 }, noteText]);
            var row = new DockPanel { MinHeight = 56, Margin = new Thickness(14, 10) };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(box, Dock.Right);
            box.Margin = new Thickness(14, 0, 0, 0);
            row.Children.AddRange([icon, box, text]);
            var line = new Border { Child = row, BorderThickness = new Thickness(0, list.Children.Count == 0 ? 0 : 1, 0, 0) };
            line[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            list.Children.Add(line);
            if (sub is not null)
            {
                var under = new Border { Child = sub, Padding = new Thickness(48, 10, 14, 12), BorderThickness = new Thickness(0, 1, 0, 0), Background = new SolidColorBrush(Color.FromArgb(10, 128, 128, 128)) };
                under[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
                _subs[box] = under;
                list.Children.Add(under);
            }
        }

        _options[(string)_paths.Content!] = _paths;
        _options[(string)_leaveOutBase.Content!] = _leaveOutBase;
        _options[(string)_encryptNames.Content!] = _encryptNames;
        Row("MiStacked", "Include subfolders", "Packs the contents of selected folders too.", _recursive, _paths);
        Row("MiCopy", "One archive per selected item", null, _separate, _leaveOutBase);

        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ToolTip.SetTip(_volume, "Size of each volume: 100M, 650M (CD), 4092M (FAT32), 1.5G ... A plain number is megabytes.");
        Avalonia.Automation.AutomationProperties.SetName(_volume, "Volume size");
        volumeRow.Children.AddRange([new TextBlock { Text = "Volume size", FontSize = 12.5, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }, _volumeChoice, _volume, _volumeNote]);
        Row("MiSplit", "Split into volumes", null, _split, volumeRow);
        Row("MiCheckMark", "Self-extracting archive", null, _sfx);

        var passwords = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,Auto") };
        var show = new Button { Content = Glyph("MiEye", 14), Classes = { "tb", "icon" }, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(show, "Show the password");
        show.Click += (_, _) =>
        {
            var visible = _password.PasswordChar == '\0';
            _password.PasswordChar = _repeat.PasswordChar = visible ? '●' : '\0';
        };
        Grid.SetColumn(_repeat, 2);
        Grid.SetColumn(show, 3);
        passwords.Children.AddRange([_password, _repeat, show]);
        Avalonia.Automation.AutomationProperties.SetName(_password, "Password");
        Avalonia.Automation.AutomationProperties.SetName(_repeat, "Repeat password");
        for (var i = 0; i < 4; i++)
        {
            _meter.Children.Add(new Border { Height = 4, Width = 27, CornerRadius = new CornerRadius(2) });
        }
        ToolTip.SetTip(_meter, "Password strength");
        var strength = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var method = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        method.Children.AddRange([_encryptNames, _zipMethod]);
        DockPanel.SetDock(method, Dock.Right);
        _passwordNote.Margin = new Thickness(10, 0, 0, 0);
        var meterRow = new StackPanel { Orientation = Orientation.Horizontal };
        meterRow.Children.AddRange([_meter, _passwordNote]);
        strength.Children.AddRange([method, meterRow]);
        var encryption = new StackPanel { Spacing = 0 };
        _zipCryptoWarning.Margin = new Thickness(0, 8, 0, 0);
        encryption.Children.AddRange([passwords, strength, _zipCryptoWarning]);
        Row("MiLock", "Encrypt with a password", null, _encrypt, encryption);

        Row("MiTrash", "Delete originals after packing", "Moves the files into the archive. Only done when packing succeeds.", _move, Warning(string.Empty));
        if (_subs[_move] is Border { Child: Border { Child: DockPanel warning } })
        {
            warning.Children.RemoveAt(1);
            warning.Children.Add(_moveWarning);
        }
        return Card(list, new Thickness(0));
    }

    // ---------------------------------------------------------------- choices

    private int _selected;

    private Packer Selected => _packers[_selected];

    /// <summary>The packer chosen (tests choose one by its name: "ZIP", "TGZ" ...).</summary>
    public string SelectedPacker
    {
        get => Selected.Text;
        set => Choose(_packers.FindIndex(p => string.Equals(p.Text, value, StringComparison.OrdinalIgnoreCase)));
    }

    private void Choose(int index)
    {
        if (index < 0 || !_packers[index].Available)
        {
            return;
        }
        _selected = index;
        for (var i = 0; i < _cards.Count; i++)
        {
            var on = i == index;
            if (on)
            {
                _cards[i][!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                _cards[i].Background = new SolidColorBrush(Color.FromArgb(28, 76, 141, 255));
                _cards[i].BorderThickness = new Thickness(2);
            }
            else
            {
                _cards[i][!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
                _cards[i].Background = Brushes.Transparent;
                _cards[i].BorderThickness = new Thickness(1);
            }
        }
        SwapExtension();
        Update();
    }

    private bool SevenZipChosen => Selected.Format == PackFormat.SevenZip && Selected.Plugin is null;

    private bool ZipChosen => Selected.Format == PackFormat.Zip && Selected.Plugin is null;

    private bool HasLevel => Selected.Plugin is not null || Selected.Format != PackFormat.Tar;

    private bool CanEncrypt => Selected.Plugin is { } p ? p.SupportsPassword : Selected.Format is PackFormat.Zip or PackFormat.SevenZip;

    private bool CanSplit => SevenZipChosen;

    private bool CanSelfExtract => SevenZipChosen && SevenZipCli.SfxModule is not null;

    private bool SelfExtracting => CanSelfExtract && _sfx.IsChecked == true;

    private string SelectedExtension => SelfExtracting ? SevenZipCli.SfxExtension
        : Selected.Plugin is { } p ? "." + p.Extension : PackJob.Extension(Selected.Format);

    /// <summary>Swaps the archive extension for the one of the chosen format.</summary>
    private void SwapExtension()
    {
        var text = _target.Text ?? string.Empty;
        foreach (var ext in s_extensions.Concat(PluginCatalog.Packers.Select(p => p.Extension)).OrderByDescending(e => e.Length))
        {
            if (text.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^(ext.Length + 1)];
                break;
            }
        }
        _target.Text = text + SelectedExtension;
    }

    private int Level => (int)Math.Round(_level.Value);

    /// <summary>The estimated size: compressed files keep their size, the others shrink by the format and level.</summary>
    private (long Size, double Ratio) Estimate()
    {
        if (_filesSize <= 0)
        {
            return (0, 1);
        }
        var level = HasLevel ? Level : 0;
        var baseRatio = Selected.Plugin is not null ? 0.42 : Selected.Format switch
        {
            PackFormat.SevenZip => 0.32,
            PackFormat.TarBz2 => 0.36,
            PackFormat.TarZst => 0.35,
            PackFormat.Tar => 1.0,
            _ => 0.40,
        };
        var ratio = level == 0 ? 1.0 : Math.Min(1, baseRatio + level switch { <= 2 => 0.08, <= 4 => 0.04, <= 6 => 0, <= 8 => -0.02, _ => -0.04 });
        double size = 0;
        foreach (var e in _items.Where(e => !e.IsDirectory))
        {
            size += s_packed.Contains(e.Extension) ? e.Size : e.Size * ratio;
        }
        return ((long)size, size / _filesSize);
    }

    /// <summary>Everything that follows the choices: the format's line, the level, the estimate, what the options allow, the summary.</summary>
    private void Update()
    {
        if (_updating || _cards.Count == 0)
        {
            return;
        }
        _updating = true;
        try
        {
            var p = Selected;
            _formatDescription.Text = Localizer.T(p.Description);
            _formatTags.Children.Clear();
            foreach (var (label, on) in new[] { ("Encryption", CanEncrypt), ("Self-extracting", CanSelfExtract), ("Volumes", CanSplit) })
            {
                var tag = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128)) };
                tag[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
                var tagText = new TextBlock { Text = label, FontSize = 11.5, Opacity = on ? 1 : 0.5 };
                if (on)
                {
                    tagText.Foreground = new SolidColorBrush(Color.Parse("#2E9E44")); // a null brush would hide the text
                }
                else
                {
                    tagText.TextDecorations = TextDecorations.Strikethrough;
                }
                tag.Child = tagText;
                _formatTags.Children.Add(tag);
            }

            // the level and the estimate
            _levelCard.Opacity = HasLevel ? 1 : 0.45;
            _level.IsEnabled = HasLevel;
            var level = HasLevel ? Level : 0;
            _levelName.Text = HasLevel ? Localizer.T(s_levels[level]) + " · " + level.ToString(CultureInfo.InvariantCulture) : Localizer.T("No compression");
            _levelNote.Text = !HasLevel ? Localizer.T("{0} only bundles files.").Replace("{0}", p.Text, StringComparison.Ordinal)
                : Localizer.T(level >= 8 ? "Slow, uses more memory" : level <= 2 ? "Fast, larger file" : "Good balance");
            var (size, ratio) = Estimate();
            _estimate.Text = _filesSize > 0 ? "≈ " + SizeFormatter.Human(size) : "—";
            ToolTip.SetTip(_estimate, _hasFolders ? Localizer.T("The folders are not counted: their size is not known yet.") : null);
            _saved.Text = _filesSize > 0 && level > 0 ? Math.Round((1 - ratio) * 100).ToString(CultureInfo.CurrentCulture) + " %" : "—";
            _speed.Text = Localizer.T(level == 0 ? "Very fast" : (p.Format, level) switch
            {
                (PackFormat.TarBz2, _) or (_, >= 9) => "Very slow",
                (PackFormat.SevenZip, >= 5) or (_, >= 7) => "Slow",
                (PackFormat.TarZst, _) or (_, <= 2) => "Very fast",
                (_, <= 4) => "Fast",
                _ => "Medium",
            });
            if (_estimateTrack is { } track)
            {
                _estimateBar.Width = _filesSize > 0 ? track.Bounds.Width * Math.Clamp(ratio, 0, 1) : 0;
            }

            // what the format allows
            _split.IsEnabled = CanSplit && _sfx.IsChecked != true;
            _sfx.IsEnabled = CanSelfExtract && _split.IsChecked != true;
            _encrypt.IsEnabled = CanEncrypt;
            _separate.IsEnabled = _items.Count != 1;
            Note(_separate, _items.Count > 1 ? Localizer.T("Creates {0} archives instead of one.").Replace("{0}", _items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                : _items.Count == 1 ? Localizer.T("Has an effect when several files or folders are selected.") : Localizer.T("One archive for each selected file or folder."));
            Note(_split, CanSplit ? (_sfx.IsChecked == true ? Localizer.T("7-Zip cannot split a self-extracting archive.") : Localizer.T("For upload limits, e-mail or FAT32 disks."))
                : Localizer.T("Not available for {0}: only 7-Zip makes volumes.").Replace("{0}", p.Text, StringComparison.Ordinal));
            Note(_sfx, !SevenZipChosen ? Localizer.T("Not available for {0}: only 7-Zip makes them.").Replace("{0}", p.Text, StringComparison.Ordinal)
                : SevenZipCli.SfxModule is null ? Localizer.T("This 7-Zip has no module for self-extracting archives (7z.sfx).")
                : _split.IsChecked == true ? Localizer.T("7-Zip cannot split a self-extracting archive.")
                : Localizer.T("Opens without any packer installed (the name ends in {0}).").Replace("{0}", SevenZipCli.SfxExtension, StringComparison.Ordinal));
            Note(_encrypt, !CanEncrypt ? Localizer.T("{0} cannot be encrypted. Choose ZIP or 7-Zip.").Replace("{0}", p.Text, StringComparison.Ordinal)
                : SevenZipChosen ? Localizer.T("AES-256 encryption.")
                : ZipChosen ? Localizer.T("AES or the weaker but widely supported ZipCrypto.") : Localizer.T("The plugin encrypts the archive."));
            if (!CanEncrypt)
            {
                _encrypt.IsChecked = false;
            }
            Show(_recursive, _recursive.IsChecked == true);
            Show(_separate, _separate.IsChecked == true && _separate.IsEnabled);
            Show(_split, _split.IsChecked == true && _split.IsEnabled);
            Show(_encrypt, _encrypt.IsChecked == true && CanEncrypt);
            Show(_move, _move.IsChecked == true);
            _encryptNames.IsVisible = SevenZipChosen;
            _zipMethod.IsVisible = ZipChosen;
            _zipCryptoWarning.IsVisible = ZipChosen && _zipMethod.SelectedIndex == 2;
            var preset = Array.FindIndex(s_volumes, v => string.Equals(v.Size, (_volume.Text ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            _volumeChoice.SelectedIndex = preset;
            var volume = SevenZipCli.ParseVolumeSize(_volume.Text);
            _volumeNote.Text = volume <= 0 ? Localizer.T("Enter a size such as 100M, 700M or 4G.")
                : _filesSize <= 0 ? string.Empty
                : Math.Max(1, (long)Math.Ceiling((double)size / volume)) is var n && n == 1 ? Localizer.T("Fits in 1 volume.")
                : Localizer.T("{0} volumes").Replace("{0}", n.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

            // the password: its strength and whether the two agree
            var password = _password.Text ?? string.Empty;
            var strength = Math.Min(4, (password.Length >= 8 ? 1 : 0) + (password.Length >= 12 ? 1 : 0)
                + (password.Any(char.IsUpper) && password.Any(char.IsLower) ? 1 : 0) + (password.Any(c => char.IsDigit(c) || !char.IsLetterOrDigit(c)) ? 1 : 0));
            string[] colors = ["#E5484D", "#C98A00", "#C98A00", "#2E9E44"];
            for (var i = 0; i < _meter.Children.Count; i++)
            {
                ((Border)_meter.Children[i]).Background = i < strength
                    ? new SolidColorBrush(Color.Parse(colors[strength - 1]))
                    : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            }
            var mismatch = (_repeat.Text ?? string.Empty).Length > 0 && password != _repeat.Text;
            _repeat.Classes.Set(SettingsView.InvalidJsonClass, mismatch);
            _passwordNote.Text = Localizer.T(password.Length == 0 ? "Enter a password." : mismatch ? "The passwords do not match."
                : s_strengths[strength]);

            // the move warning, the button, the summary
            var what = _items.Count == 1 ? "“" + _items[0].Name + "”" : Localizer.T("{0} items").Replace("{0}", _items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
            _moveWarning.Text = Localizer.T("{0} will be deleted after the archive is made.").Replace("{0}", what, StringComparison.Ordinal);
            _pack.Content = _move.IsChecked == true ? "Pack and delete" : "Pack";
            var name = (_target.Text ?? string.Empty).Trim();
            name = name[(Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/')) + 1)..];
            var extra = new List<string>();
            if (_split.IsChecked == true && _split.IsEnabled)
            {
                extra.Add(Localizer.T("volumes of {0}").Replace("{0}", (_volume.Text ?? string.Empty).Trim(), StringComparison.Ordinal));
            }
            if (SelfExtracting)
            {
                extra.Add(Localizer.T("self-extracting"));
            }
            if (_encrypt.IsChecked == true && CanEncrypt)
            {
                extra.Add(Localizer.T("encrypted"));
            }
            if (_move.IsChecked == true)
            {
                extra.Add(Localizer.T("originals deleted"));
            }
            _summary.Text = string.Join(" · ", new[]
            {
                name, p.Text + (HasLevel ? ", " + Localizer.T(s_levels[level]).ToLower(CultureInfo.CurrentCulture) : string.Empty),
                _filesSize > 0 ? "≈ " + SizeFormatter.Human(size) : null, extra.Count > 0 ? string.Join(", ", extra) : null,
            }.Where(t => !string.IsNullOrEmpty(t)));
        }
        finally
        {
            _updating = false;
        }
    }

    private void Note(CheckBox box, string text)
    {
        if (_notes.TryGetValue(box, out var note))
        {
            note.Text = text;
            note.IsVisible = text.Length > 0;
        }
    }

    private void Show(CheckBox box, bool show)
    {
        if (_subs.TryGetValue(box, out var sub))
        {
            sub.IsVisible = show;
        }
    }

    /// <summary>The folder button: another folder for the archive, the name kept.</summary>
    private async Task ChooseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder of the archive" });
        if (folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault() is { } folder)
        {
            var text = _target.Text ?? string.Empty;
            var name = text[(Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/')) + 1)..];
            _target.Text = Path.Combine(folder, name.Length > 0 ? name : "archive" + SelectedExtension);
        }
    }

    // ---------------------------------------------------------------- tests, result

    public PackDialogResult? Result { get; private set; }

    /// <summary>The archive name as typed (tests).</summary>
    public string TargetText
    {
        get => _target.Text ?? string.Empty;
        set => _target.Text = value;
    }

    /// <summary>A switch or check box by its English text (tests).</summary>
    public CheckBox Option(string text) => _options[text];

    /// <summary>The volume size box (tests).</summary>
    public TextBox VolumeBox => _volume;

    /// <summary>The compression level 0..9 (tests).</summary>
    public Slider LevelSlider => _level;

    public TextBox PasswordBox => _password;

    public TextBox RepeatBox => _repeat;

    /// <summary>The zip method: 0 AES-256, 1 AES-128, 2 ZipCrypto (tests).</summary>
    public ComboBox ZipMethod => _zipMethod;

    public string ErrorText => _error.Text ?? string.Empty;

    /// <summary>The line above the buttons (tests).</summary>
    public string SummaryText => _summary.Text ?? string.Empty;

    /// <summary>The estimated size ("≈ 1.2 M", "—"; tests).</summary>
    public string EstimateText => _estimate.Text ?? string.Empty;

    /// <summary>Rate 0..9 → the level of the built-in packers (7-Zip gets the number itself).</summary>
    public static CompressionLevel LevelOf(int rate) => rate switch
    {
        <= 0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    private void ShowError(string text)
    {
        _error.Text = Localizer.T(text);
        _error.IsVisible = true;
    }

    public void Accept(bool queue)
    {
        string? password = null;
        if (_encrypt.IsChecked == true && CanEncrypt)
        {
            password = _password.Text ?? string.Empty;
            if (password.Length == 0)
            {
                ShowError("Enter a password, or turn encryption off.");
                _password.Focus();
                return;
            }
            if (password != _repeat.Text)
            {
                ShowError("The passwords do not match.");
                _repeat.Focus();
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(_target.Text))
        {
            ShowError("Enter an archive name.");
            _target.Focus();
            return;
        }
        long volumeSize = 0;
        if (CanSplit && _split.IsChecked == true && !SelfExtracting)
        {
            volumeSize = SevenZipCli.ParseVolumeSize(_volume.Text);
            if (volumeSize == 0)
            {
                ShowError("Enter a volume size such as 100M, 700M or 4G.");
                _volume.Focus();
                return;
            }
        }
        var rate = HasLevel ? Level : 0;
        Result = new PackDialogResult(_target.Text.Trim(), Selected.Format, LevelOf(rate), password, _move.IsChecked == true, _separate.IsChecked == true && _separate.IsEnabled,
            queue, Selected.Plugin, KeepPaths: _paths.IsChecked == true, Recursive: _recursive.IsChecked == true,
            LeaveOutBaseFolder: _separate.IsChecked == true && _separate.IsEnabled && _leaveOutBase.IsChecked == true, Rate: rate,
            VolumeSize: volumeSize, SelfExtracting: SelfExtracting)
        {
            ZipEncryption = ZipChosen && password is not null ? _zipMethod.SelectedIndex switch
            {
                1 => ZipEncryptionMethod.Aes128,
                2 => ZipEncryptionMethod.ZipCrypto,
                _ => ZipEncryptionMethod.Aes256,
            } : null,
            EncryptNames = SevenZipChosen && password is not null ? _encryptNames.IsChecked == true : null,
        };
        Close();
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

/// <summary>Password entry (archives).</summary>
public sealed class PasswordDialog : DialogWindow
{
    private readonly TextBox _input = new() { PasswordChar = '●' };

    public PasswordDialog(string title, string message)
        : base(title, 420)
    {
        Body.Children.Add(Label(message));
        Body.Children.Add(_input);
        AddButton("OK", () =>
        {
            Result = _input.Text ?? string.Empty;
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        Opened += (_, _) => _input.Focus();
    }

    public string? Result { get; private set; }
}
