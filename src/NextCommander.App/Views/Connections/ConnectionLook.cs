using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Connections;

/// <summary>The four kinds of connections of the manager: their name and color (the tiles, the filter chips).</summary>
public enum ConnectionCategory
{
    Servers,
    LocalNetwork,
    CloudStorage,
    RemoteSessions,
}

/// <summary>
/// The look shared by the connection manager and the new connection pages (the user's template, 21 November 2026): a
/// type's tile (its icon on a tint of its category's color, a dot while it is connected), cards, banners, the bottom bar.
/// </summary>
internal static class ConnectionLook
{
    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#0F7B0F"));
    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#9D5D00"));
    public static readonly IBrush Problem = new SolidColorBrush(Color.Parse("#C42B1C"));
    public static readonly IBrush Star = new SolidColorBrush(Color.Parse("#D79A1C"));
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>
    /// The colors of the template for the light and the dark theme (cards, frames, chips, the selected row, the second
    /// and third text): put into the view's resources, used by the "connlook" styles of NextCommanderStyles.axaml.
    /// </summary>
    public static void Apply(StyledElement view)
    {
        view.Classes.Add("connlook");
        var tokens = new ResourceDictionary();
        tokens.ThemeDictionaries[Avalonia.Styling.ThemeVariant.Light] = Tokens(dark: false);
        tokens.ThemeDictionaries[Avalonia.Styling.ThemeVariant.Dark] = Tokens(dark: true);
        view.Resources.MergedDictionaries.Add(tokens);
    }

    private static ResourceDictionary Tokens(bool dark)
    {
        static SolidColorBrush B(string color) => new(Color.Parse(color));
        return new ResourceDictionary
        {
            ["ConnCard"] = B(dark ? "#0DFFFFFF" : "#FBFBFB"),
            ["ConnCardHover"] = B(dark ? "#17FFFFFF" : "#F3F3F3"),
            ["ConnStroke"] = B(dark ? "#14FFFFFF" : "#14000000"),
            ["ConnStroke2"] = B(dark ? "#24FFFFFF" : "#26000000"),
            ["ConnControl"] = B(dark ? "#0FFFFFFF" : "#FFFFFF"),
            ["ConnHover"] = B(dark ? "#0DFFFFFF" : "#0A000000"),
            ["ConnSelected"] = B(dark ? "#17FFFFFF" : "#12000000"),
            ["ConnSide"] = B(dark ? "#33000000" : "#08000000"),
            ["ConnText"] = B(dark ? "#FFFFFF" : "#1A1A1A"),
            ["ConnText2"] = B(dark ? "#C8C8C8" : "#5C5C5C"),
            ["ConnText3"] = B(dark ? "#9A9A9A" : "#8B8B8B"),
            ["ConnAccent"] = B(dark ? "#60CDFF" : "#005FB8"),
            ["ConnAccentSoft"] = B(dark ? "#2160CDFF" : "#1A005FB8"),
            ["ConnOk"] = B(dark ? "#6CCB5F" : "#0F7B0F"),
            ["ConnOkBack"] = B(dark ? "#266CCB5F" : "#DFF6DD"),
            ["ConnWarn"] = B(dark ? "#FCE100" : "#9D5D00"),
            ["ConnWarnBack"] = B(dark ? "#1AFCE100" : "#FFF4CE"),
            ["ConnProblem"] = B(dark ? "#FF99A4" : "#C42B1C"),
            ["ConnProblemBack"] = B(dark ? "#24FF646E" : "#FDE7E9"),
            ["ConnInfoBack"] = B(dark ? "#1460CDFF" : "#EAF2FB"),
            ["ConnAccentLine"] = B(dark ? "#4D60CDFF" : "#40005FB8"),
            ["ConnAccentCard"] = B(dark ? "#0D60CDFF" : "#08005FB8"),
        };
    }

    /// <summary>A text in the second (captions) or third (notes) color of the template.</summary>
    public static T Muted<T>(T text, int level = 2)
        where T : TextBlock
    {
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(level == 3 ? "ConnText3" : "ConnText2");
        return text;
    }

    public static ConnectionCategory CategoryOf(RemoteProtocol protocol) =>
        ConnectionManagerView.Types.FirstOrDefault(t => t.Protocol == protocol).Group switch
        {
            "Local network" => ConnectionCategory.LocalNetwork,
            "Cloud storage" or "Cloud drives" => ConnectionCategory.CloudStorage,
            "Remote sessions" => ConnectionCategory.RemoteSessions,
            _ => ConnectionCategory.Servers,
        };

    public static string NameOf(ConnectionCategory category) => category switch
    {
        ConnectionCategory.LocalNetwork => "Local network",
        ConnectionCategory.CloudStorage => "Cloud storage",
        ConnectionCategory.RemoteSessions => "Remote sessions",
        _ => "Servers",
    };

    public static Color ColorOf(ConnectionCategory category) => Color.Parse(category switch
    {
        ConnectionCategory.LocalNetwork => "#0F8A7C",
        ConnectionCategory.CloudStorage => "#C7781A",
        ConnectionCategory.RemoteSessions => "#7A4FC4",
        _ => "#2F6FD0",
    });

    /// <summary>The types offered first (the first start's cards, "Common" on the type page).</summary>
    public static bool IsCommon(RemoteProtocol protocol) =>
        protocol is RemoteProtocol.Sftp or RemoteProtocol.FtpsExplicit or RemoteProtocol.Smb or RemoteProtocol.S3 or RemoteProtocol.OneDrive;

    /// <summary>Types that send the password as readable text.</summary>
    public static bool IsUnencrypted(RemoteProtocol protocol) =>
        protocol is RemoteProtocol.Ftp or RemoteProtocol.WebDav or RemoteProtocol.Telnet or RemoteProtocol.Rsh;

    /// <summary>A type's tile: its icon on its category's tint, a green dot while connected.</summary>
    public static Control Tile(RemoteProtocol protocol, double size = 32, bool connected = false)
    {
        var color = ColorOf(CategoryOf(protocol));
        var icon = size * 0.53;
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4),
            Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
            Child = new Viewbox { Width = icon, Height = icon, Child = BrandIcons.Create(protocol, icon) },
        };
        if (!connected)
        {
            return tile;
        }
        var dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#3BA55D")),
            BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
        };
        dot[!Border.BorderBrushProperty] = new DynamicResourceExtension("FmToolBarBrush");
        ToolTip.SetTip(dot, "Connected");
        var panel = new Panel { Width = size, Height = size };
        panel.Children.AddRange([tile, dot]);
        return panel;
    }

    /// <summary>Takes a control that is used again out of the panel it was in before.</summary>
    public static T Detached<T>(T control)
        where T : Control
    {
        if (control.Parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
        else if (control.Parent is ContentControl holder)
        {
            holder.Content = null;
        }
        else if (control.Parent is Decorator decorator)
        {
            decorator.Child = null;
        }
        return control;
    }

    /// <summary>A card: a rounded box on the page.</summary>
    public static Border Card(Control content, Thickness? padding = null)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(16), Margin = new Thickness(0, 0, 0, 14) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnCard");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        return card;
    }

    /// <summary>A card with a title over its content.</summary>
    public static Border Section(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.AddRange([new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold }, content]);
        return Card(stack);
    }

    /// <summary>The small title over a group of cards ("Recently used", "Servers").</summary>
    public static TextBlock GroupTitle(string text, Thickness? margin = null) =>
        Muted(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = margin ?? new Thickness(2, 18, 0, 8) });

    /// <summary>A small rounded label in the accent color ("Common", "Suggested").</summary>
    public static Border Badge(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("ConnAccent");
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = label };
        badge[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnAccentSoft");
        return badge;
    }

    /// <summary>A square button without a frame until the pointer is over it (back, forward, More).</summary>
    public static Button IconButton(string glyph, string tip, double size = 30, double fontSize = 15)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = fontSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            Width = size, Height = size, MinHeight = size, Classes = { "tb", "ghost", "icon" },
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>A line of information (kind: "info", "ok", "warn", "problem").</summary>
    public static Border Banner(string text, string kind = "info", Control? trailing = null)
    {
        var (glyph, color, back) = kind switch
        {
            "ok" => ("✓", "ConnOk", "ConnOkBack"),
            "warn" => ("!", "ConnWarn", "ConnWarnBack"),
            "problem" => ("!", "ConnProblem", "ConnProblemBack"),
            _ => ("i", "ConnAccent", "ConnInfoBack"),
        };
        var sign = new TextBlock { Text = glyph, FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        sign[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(color);
        var mark = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0),
            BorderThickness = new Thickness(1.5), Child = sign,
        };
        mark[!Border.BorderBrushProperty] = new DynamicResourceExtension(color);
        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            trailing.Margin = new Thickness(10, -4, 0, -4);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(trailing);
        }
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        if (kind != "info")
        {
            line[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(color);
        }
        row.Children.Add(line);
        var banner = new Border { Child = row, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 14) };
        banner[!Border.BackgroundProperty] = new DynamicResourceExtension(back);
        if (kind == "info")
        {
            banner.BorderThickness = new Thickness(1);
            banner[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        }
        return banner;
    }

    /// <summary>Sets a text's color to the ok / problem color of the theme (a test's result, a detected address).</summary>
    public static void Tint(TextBlock text, string? kind)
    {
        if (kind is null)
        {
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("ConnText2");
            return;
        }
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(kind == "ok" ? "ConnOk" : "ConnProblem");
    }

    /// <summary>A text in the problem color (a field's message, the * of a required field).</summary>
    public static T Red<T>(T text)
        where T : TextBlock
    {
        Tint(text, "problem");
        return text;
    }

    /// <summary>
    /// The height of the bars at the bottom (the page's buttons, the tree's count): the same, so that their lines meet
    /// in one line across the manager (the user's request, 22 November 2026).
    /// </summary>
    public const double BottomBarHeight = 58;

    /// <summary>The bar at the bottom of a page (its buttons).</summary>
    public static Border BottomBar(Control left, Control right)
    {
        var dock = new DockPanel { Margin = new Thickness(32, 8), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        left.VerticalAlignment = VerticalAlignment.Center;
        right.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.AddRange([right, left]);
        var bar = new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 0), MinHeight = BottomBarHeight };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnSide");
        bar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        return bar;
    }

    /// <summary>"1 Type ✓ — 2 Details": the steps of a new connection, the current one stressed.</summary>
    public static StackPanel Steps(int current, Action? backToFirst)
    {
        var steps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        string[] names = ["Type", "Details"];
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
            {
                steps.Children.Add(new Border { Width = 24, Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) });
            }
            var done = i < current;
            var on = i == current;
            var digit = new TextBlock { Text = done ? "✓" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            var circle = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Child = digit, BorderThickness = new Thickness(on || done ? 0 : 1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)) };
            if (on)
            {
                circle[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                digit.Foreground = Brushes.White;
            }
            else if (done)
            {
                circle.Background = new SolidColorBrush(Color.FromArgb(36, 0, 95, 184));
            }
            var label = new TextBlock { Text = names[i], FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, Opacity = on ? 1 : 0.7 };
            var step = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Background = Brushes.Transparent };
            step.Children.AddRange([circle, label]);
            if (done && backToFirst is not null)
            {
                step.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                step.PointerPressed += (_, _) => backToFirst();
            }
            steps.Children.Add(step);
        }
        return steps;
    }
}

/// <summary>
/// Cards in as many equal columns as fit (each at least <see cref="MinColumnWidth"/> wide); the cards of a row are as
/// high as its highest, so the rows line up (the template's grid of type cards).
/// </summary>
internal sealed class CardGrid : Panel
{
    public double MinColumnWidth { get; init; } = 220;

    public double Gap { get; init; } = 8;

    /// <summary>At most this many columns (0: as many as fit).</summary>
    public int MaxColumns { get; init; }

    private int Columns(double width)
    {
        var count = double.IsInfinity(width) ? Children.Count : (int)((width + Gap) / (MinColumnWidth + Gap));
        if (MaxColumns > 0)
        {
            count = Math.Min(count, MaxColumns);
        }
        return Math.Max(1, count);
    }

    private double ColumnWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinColumnWidth : Math.Max(0, (width - (Gap * (columns - 1))) / columns);

    private List<double> RowHeights(int columns)
    {
        var heights = new List<double>();
        var visible = Children.Where(c => c.IsVisible).ToList();
        for (var i = 0; i < visible.Count; i += columns)
        {
            heights.Add(visible.Skip(i).Take(columns).Max(c => c.DesiredSize.Height));
        }
        return heights;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var width = ColumnWidth(availableSize.Width, columns);
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
        }
        var heights = RowHeights(columns);
        var total = heights.Sum() + (Gap * Math.Max(0, heights.Count - 1));
        var used = double.IsInfinity(availableSize.Width) ? (width * columns) + (Gap * (columns - 1)) : availableSize.Width;
        return new Size(used, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = ColumnWidth(finalSize.Width, columns);
        var heights = RowHeights(columns);
        var visible = Children.Where(c => c.IsVisible).ToList();
        var y = 0.0;
        for (var i = 0; i < visible.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            if (column == 0 && row > 0)
            {
                y += heights[row - 1] + Gap;
            }
            visible[i].Arrange(new Rect(column * (width + Gap), y, width, heights[row]));
        }
        return finalSize;
    }
}
