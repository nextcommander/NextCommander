using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.Core.Formatting;

namespace NextCommander.App.Views.Dialogs;

/// <summary>A drive of the system information: its name, label, file system, free and total space.</summary>
public sealed record SystemDrive(string Name, string? Label, string? Format, long Free, long Total);

/// <summary>What the system information shows (Help > System information, nc_SysInfo).</summary>
/// <param name="Report">The same as text, for "Copy details" (a problem report).</param>
public sealed record SystemInfo(
    string Version,
    string Framework,
    string OperatingSystem,
    string Architecture,
    string Computer,
    string User,
    int Cores,
    long Memory,
    string SettingsFolder,
    IReadOnlyList<SystemDrive> Drives,
    string Report);

/// <summary>
/// The system information: the program and its .NET, the system (operating system, computer, user, cores, memory,
/// uptime, the settings folder with copy / open) and the drives with how full they are; "Copy details" copies all of it
/// as text for a problem report.
/// </summary>
public sealed class SystemInfoDialog : Window
{
    private readonly SystemInfo _info;
    private readonly TextBlock _uptime = new() { FontSize = 13.5, FontWeight = FontWeight.Medium, Classes = { "noloc" } };
    private readonly TextBlock _note = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Func<Task> _openSettingsFolder;

    public SystemInfoDialog(SystemInfo info, Func<Task> openSettingsFolder)
    {
        _info = info;
        _openSettingsFolder = openSettingsFolder;
        Title = "System information";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18) };
        content.Children.AddRange([Hero(), Section("System", SystemGrid()), Section("Drives", Drives())]);

        var copy = new Button { Classes = { "dialog" } };
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        copyContent.Children.AddRange([DialogLook.Glyph("MiClipboard", 15), new TextBlock { Text = "Copy details", VerticalAlignment = VerticalAlignment.Center }]);
        copy.Content = copyContent;
        ToolTip.SetTip(copy, "Copy all of it as text, for a problem report (Ctrl+C)");
        copy.Click += async (_, _) => await CopyAsync(_info.Report, "Details copied.");
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Classes = { "dialog" } };
        ok.Click += (_, _) => Close();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.AddRange([copy, _note]);
        var foot = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(ok, Dock.Right);
        foot.Children.AddRange([ok, left]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        var header = DialogLook.Header("MiInfo", "System information", "Details to include when you report a problem");
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([header, footBorder, new ScrollViewer { Content = content, MaxHeight = 640 }]);
        Content = root;

        ShowUptime();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => ShowUptime());
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                await CopyAsync(_info.Report, "Details copied.");
            }
        };
    }

    /// <summary>Uptime as "2 days 6 h 34 min" (tests).</summary>
    public static string UptimeText(TimeSpan up)
    {
        var minutes = $"{up.Hours} h {up.Minutes} min";
        return up.Days switch
        {
            0 => minutes,
            1 => $"1 day {minutes}",
            _ => $"{up.Days} days {minutes}",
        };
    }

    private void ShowUptime() => _uptime.Text = UptimeText(TimeSpan.FromMilliseconds(Environment.TickCount64));

    private async Task CopyAsync(string text, string done)
    {
        if (Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            _note.Text = done;
        }
    }

    private Border Hero()
    {
        var logo = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11) };
        logo[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        logo.Child = new TextBlock { Text = "NC", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        names.Children.AddRange([
            new TextBlock { Text = "NextCommander", FontSize = 15, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
            new TextBlock { Text = _info.Version, FontSize = 12, Opacity = 0.7, FontFamily = Mono, Classes = { "noloc" } },
        ]);
        var tag = Pill(_info.Framework);
        tag.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        DockPanel.SetDock(tag, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.AddRange([logo, names]);
        row.Children.AddRange([tag, left]);
        var hero = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14) };
        hero[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        return hero;
    }

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");

    private static Border Pill(string text)
    {
        var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3) };
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundBaseLowBrush");
        pill.Child = new TextBlock { Text = text, FontSize = 11.5, Opacity = 0.8, Classes = { "noloc" } };
        return pill;
    }

    private static Control Section(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.AddRange([new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.6 }, content]);
        return stack;
    }

    private Grid SystemGrid()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto,8,Auto,8,Auto") };
        void Put(Control cell, int row, int column, bool wide = false)
        {
            Grid.SetRow(cell, row * 2);
            Grid.SetColumn(cell, column * 2);
            if (wide)
            {
                Grid.SetColumnSpan(cell, 3);
            }
            grid.Children.Add(cell);
        }
        Put(Cell("MiMonitor", "Operating system", Value($"{_info.OperatingSystem} · {_info.Architecture}")), 0, 0, wide: true);
        Put(Cell("MiDesktop", "Computer", Value(_info.Computer)), 1, 0);
        Put(Cell("MiKey", "User", Value(_info.User)), 1, 1);
        Put(Cell("MiChip", "CPU cores", Value(_info.Cores.ToString(CultureInfo.CurrentCulture))), 2, 0);
        Put(Cell("MiDrive", "Memory", Value(SizeFormatter.Human(_info.Memory))), 2, 1);
        Put(Cell("MiHistory", "Uptime", _uptime), 3, 0, wide: true);
        var folder = Value(_info.SettingsFolder, mono: true);
        ToolTip.SetTip(folder, _info.SettingsFolder);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.AddRange([
            Small(DialogLook.IconButton("MiCopy", "Copy path", () => CopyAsync(_info.SettingsFolder, "Path copied."))),
            Small(DialogLook.IconButton("MiFolderOpen", "Open folder", async () =>
            {
                await _openSettingsFolder();
                Close();
            })),
        ]);
        Put(Cell("MiGear", "Settings folder", folder, actions), 4, 0, wide: true);
        return grid;
    }

    private static Button Small(Button button)
    {
        button.Width = 30;
        button.Height = 30;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    private static TextBlock Value(string text, bool mono = false) => new()
    {
        Text = text,
        FontSize = mono ? 12.5 : 13.5,
        FontWeight = mono ? FontWeight.Normal : FontWeight.Medium,
        FontFamily = mono ? Mono : FontFamily.Default,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Classes = { "noloc" },
    };

    private static Border Cell(string glyph, string label, Control value, Control? actions = null)
    {
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Child = DialogLook.Glyph(glyph, 16) };
        icon[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundBaseLowBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.AddRange([new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.7 }, value]);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        grid.Children.AddRange([icon, text]);
        if (actions is not null)
        {
            Grid.SetColumn(actions, 2);
            actions.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(actions);
        }
        var cell = new Border { Child = grid, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10) };
        cell[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return cell;
    }

    private StackPanel Drives()
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var drive in _info.Drives)
        {
            var used = drive.Total > 0 ? 100.0 * (drive.Total - drive.Free) / drive.Total : 0;
            var color = used >= 90 ? "#E5484D" : used >= 75 ? "#E0A12B" : null;
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var free = new TextBlock { FontSize = 12.5, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Text = $"{SizeFormatter.Human(drive.Free)} free of {SizeFormatter.Human(drive.Total)}" };
            DockPanel.SetDock(free, Dock.Right);
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            name.Children.AddRange([
                DialogLook.Glyph("MiDrive", 17),
                new TextBlock { Text = drive.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            ]);
            if (!string.IsNullOrWhiteSpace(drive.Label))
            {
                name.Children.Add(new TextBlock { Text = drive.Label, FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            if (!string.IsNullOrWhiteSpace(drive.Format))
            {
                var format = Pill(drive.Format);
                format.VerticalAlignment = VerticalAlignment.Center;
                name.Children.Add(format);
            }
            top.Children.AddRange([free, name]);
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = used, Height = 8, MinHeight = 8, CornerRadius = new CornerRadius(4) };
            if (color is not null)
            {
                bar.Foreground = new SolidColorBrush(Color.Parse(color));
            }
            var percent = new TextBlock { Text = $"{Math.Round(used).ToString(CultureInfo.CurrentCulture)}% used", FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) };
            var body = new StackPanel();
            body.Children.AddRange([top, bar, percent]);
            var card = new Border { Child = body, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12) };
            card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            stack.Children.Add(card);
        }
        if (_info.Drives.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "No drives could be read.", Opacity = 0.7 });
        }
        return stack;
    }
}
