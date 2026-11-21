using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NextCommander.App.Views.Dialogs;

/// <summary>An open-source component the application is built with.</summary>
public sealed record AboutComponent(string Name, string License);

/// <summary>What the About window shows and what its buttons do.</summary>
public sealed record AboutInfo(
    string Version,
    string Copyright,
    string Runtime,
    string Avalonia,
    string OperatingSystem,
    string ProgramFolder,
    string SettingsFolder,
    IReadOnlyList<AboutComponent> Components,
    Func<Task> CheckForUpdates,
    Func<string, Task> OpenFolder)
{
    /// <summary>The project page of the application.</summary>
    public const string Website = "https://github.com/nextcommander/nextcommander";

    /// <summary>The details as plain text (Copy button).</summary>
    public string Text =>
        $"NextCommander {Version}\n{Copyright}\n\n.NET: {Runtime}\nAvalonia: {Avalonia}\nOperating system: {OperatingSystem}\n" +
        $"Program folder: {ProgramFolder}\nSettings folder: {SettingsFolder}\n{Website}";

    /// <summary>The open-source components of the application, with their licenses.</summary>
    public static IReadOnlyList<AboutComponent> BuiltInComponents { get; } =
    [
        new("Avalonia", "MIT"),
        new("AvaloniaEdit", "MIT"),
        new("Avalonia.HtmlRenderer", "BSD-3-Clause"),
        new("SkiaSharp", "MIT"),
        new("Svg.Skia", "MIT"),
        new("CommunityToolkit.Mvvm", "MIT"),
        new("SSH.NET", "MIT"),
        new("FluentFTP", "MIT"),
        new("SMBLibrary", "LGPL-3.0"),
        new("SharpCompress", "MIT"),
        new("SharpZipLib", "MIT"),
        new("DiscUtils", "MIT"),
        new("Blake3.NET", "BSD-2-Clause"),
        new("Markdig", "BSD-2-Clause"),
        new("PDFium", "BSD-3-Clause"),
        new("Jint", "BSD-2-Clause"),
        new("Acornima", "BSD-3-Clause"),
        new("MetadataExtractor", "Apache-2.0"),
        new("TagLibSharp", "LGPL-2.1"),
        new("LibVLCSharp", "LGPL-2.1"),
        new("Serilog", "Apache-2.0"),
    ];
}

/// <summary>Help > About: logo, version, system details, open-source components; copy, check for updates.</summary>
public sealed class AboutDialog : DialogWindow
{
    public AboutDialog(AboutInfo info)
        : base("About NextCommander", 560)
    {
        Body.Spacing = 14;
        Body.Children.Add(Header(info));
        Body.Children.Add(new Separator { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0) });
        Body.Children.Add(Details(info));
        Body.Children.Add(Components(info.Components));

        AddButton("Copy information", () => _ = CopyAsync(info.Text));
        AddButton("Check for updates", () =>
        {
            Close();
            _ = info.CheckForUpdates();
        });
        AddButton("OK", Close, isDefault: true, isCancel: true);
    }

    private static Control Header(AboutInfo info)
    {
        var logo = new Image
        {
            Width = 88,
            Height = 88,
            Source = Logo(),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "NextCommander", FontSize = 26, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
        text.Children.Add(new TextBlock { Text = $"Version {info.Version}", FontSize = 14, Opacity = 0.85 });
        text.Children.Add(new TextBlock
        {
            Text = "Dual-pane file manager for Windows, Linux and macOS",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        text.Children.Add(new TextBlock { Text = info.Copyright, Opacity = 0.7, Classes = { "noloc" } });
        // the project page, opened in the browser
        var link = new HyperlinkButton
        {
            Content = AboutInfo.Website,
            NavigateUri = new Uri(AboutInfo.Website),
            Padding = new Thickness(0),
            Classes = { "noloc" },
        };
        ToolTip.SetTip(link, "Open the project page in the browser");
        text.Children.Add(link);
        var row = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        logo.Margin = new Thickness(0, 0, 18, 0);
        row.Children.Add(logo);
        row.Children.Add(text);
        return row;
    }

    private Grid Details(AboutInfo info)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowSpacing = 4, ColumnSpacing = 12 };
        void Row(string label, string value, string? folder = null)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            // folders stay on one line, shortened in the middle part; the whole path is in the tooltip
            var text = new SelectableTextBlock
            {
                Text = value,
                TextWrapping = folder is null ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = folder is null ? TextTrimming.None : TextTrimming.PrefixCharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "noloc" },
            };
            if (folder is not null)
            {
                ToolTip.SetTip(text, value);
            }
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            if (folder is not null)
            {
                var open = new Button { Content = "Open", Padding = new Thickness(10, 2), VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(open, "Open the folder in the panel");
                open.Click += (_, _) =>
                {
                    Close();
                    _ = info.OpenFolder(folder);
                };
                Grid.SetRow(open, row);
                Grid.SetColumn(open, 2);
                grid.Children.Add(open);
            }
        }
        Row(".NET", info.Runtime);
        Row("Avalonia", info.Avalonia);
        Row("Operating system", info.OperatingSystem);
        Row("Program folder", info.ProgramFolder, info.ProgramFolder);
        Row("Settings folder", info.SettingsFolder, info.SettingsFolder);
        return grid;
    }

    private static Expander Components(IReadOnlyList<AboutComponent> components)
    {
        var list = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "noloc" }, LineHeight = 20 };
        foreach (var component in components)
        {
            list.Inlines!.Add(new Run(component.Name) { FontWeight = FontWeight.SemiBold });
            list.Inlines.Add(new Run($"  {component.License}") { Foreground = Brushes.Gray });
            list.Inlines.Add(new LineBreak());
        }
        return new Expander
        {
            Header = "Open-source components",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 180, Content = list },
        };
    }

    /// <summary>The application logo (Assets/NextCommander.png); null when it cannot be loaded.</summary>
    public static Bitmap? Logo()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NextCommander/Assets/NextCommander.png"));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
