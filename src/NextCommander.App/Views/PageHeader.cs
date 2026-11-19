using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace NextCommander.App.Views;

/// <summary>
/// The title bar of a page of the main window (the options, synchronize directories, the connection manager, a remote
/// desktop; user's request, 18 November 2026): the same on every page: the accent line of the internal terminal's top
/// edge above, the page's icon and name, and the close button (✕) at the right as the connection manager had it.
/// </summary>
public static class PageHeader
{
    /// <summary>The page's title bar; <paramref name="closeButton"/> is its ✕ (<paramref name="translate"/>: false for a name of the user's).</summary>
    public static Border Create(Control? icon, string title, string closeTip, Action close, out Button closeButton, bool translate = true, IEnumerable<Control>? tools = null)
    {
        closeButton = new Button { Content = "✕", Padding = new Thickness(8, 2), Background = Brushes.Transparent, Focusable = false, Classes = { "noloc", "pageclose" } };
        ToolTip.SetTip(closeButton, closeTip);
        Avalonia.Automation.AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => close();
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            icon.Width = 16;
            icon.Height = 16;
            icon.VerticalAlignment = VerticalAlignment.Center;
            caption.Children.Add(icon);
        }
        var text = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!translate)
        {
            text.Classes.Add("noloc");
        }
        caption.Children.Add(text);
        var row = new DockPanel { Margin = new Thickness(12, 4) };
        DockPanel.SetDock(closeButton, Dock.Right);
        row.Children.Add(closeButton);
        if (tools is not null)
        {
            // the page's own options, left of ✕ (a remote desktop: full screen, fit, reconnect)
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            options.Children.AddRange(tools);
            DockPanel.SetDock(options, Dock.Right);
            row.Children.Add(options);
        }
        row.Children.Add(caption);
        var bar = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), Classes = { "pageheader" } };
        bar[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        // the line of the internal terminal's top edge
        var line = new Border { Child = bar, BorderThickness = new Thickness(0, 1, 0, 0) };
        line[!Border.BorderBrushProperty] = new DynamicResourceExtension("FmActiveAccentBrush");
        DockPanel.SetDock(line, Dock.Top);
        return line;
    }

    /// <summary>A button of a page's title bar (as the internal terminal's header buttons), with its tooltip.</summary>
    public static Button Tool(string text, string tip, Action click)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 2), MinHeight = 0, Background = Brushes.Transparent, Focusable = false };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The page's title bar (its ✕ not needed by the caller).</summary>
    public static Border Create(Control? icon, string title, string closeTip, Action close) => Create(icon, title, closeTip, close, out _);
}
