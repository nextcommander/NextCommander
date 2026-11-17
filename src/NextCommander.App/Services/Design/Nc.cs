using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace NextCommander.App.Services.Design;

/// <summary>
/// Building blocks of the tool window templates (Styles/Design.axaml, docs/design.md) for windows built in code. They set
/// the style classes; colors come from the Nc* brushes (light / dark, Styles/DesignTokens.axaml).
/// </summary>
public static class Nc
{
    /// <summary>Outline icons of the templates (16 × 16, drawn with a stroke).</summary>
    public static class Icons
    {
        public const string Folder = "M1.75,4.25 C1.75,3.45 2.4,2.75 3.25,2.75 L6.05,2.75 L7.45,4.25 L12.75,4.25 C13.55,4.25 14.25,4.95 14.25,5.75 L14.25,12.25 C14.25,13.05 13.55,13.75 12.75,13.75 L3.25,13.75 C2.4,13.75 1.75,13.05 1.75,12.25 Z";
        public const string File = "M4,1.75 L9,1.75 L12,4.75 L12,14.25 L4,14.25 Z M9,1.75 L9,4.75 L12,4.75";
        public const string Filter = "M2,3 L14,3 L9.5,8.5 L9.5,13 L6.5,11.5 L6.5,8.5 Z";
        public const string Plus = "M8,3 L8,13 M3,8 L13,8";
        public const string ChevronDown = "M4,6 L8,10 L12,6";
        public const string ChevronRight = "M6,4 L10,8 L6,12";
        public const string Search = "M11.5,7 A4.5,4.5 0 1 1 2.5,7 A4.5,4.5 0 1 1 11.5,7 Z M10.5,10.5 L14,14";
        public const string Lines = "M3,4 L13,4 M3,7 L13,7 M3,10 L9,10";
        public const string Disk = "M2.5,4 A5.5,2 0 1 0 13.5,4 A5.5,2 0 1 0 2.5,4 Z M2.5,4 L2.5,12 C2.5,13.1 5,14 8,14 C11,14 13.5,13.1 13.5,12 L13.5,4 M2.5,8 C2.5,9.1 5,10 8,10 C11,10 13.5,9.1 13.5,8";
        public const string Pin = "M10,1.75 L14.25,6 L12.25,7 L9.75,9.5 L10,12.5 L8.75,13.75 L6,11 L2.5,14.5 M6,11 L3.25,8.25 L4.5,7 L7.5,7.25 L10,4.75 Z";
        public const string Save = "M2.75,2.75 L11.25,2.75 L13.25,4.75 L13.25,13.25 L2.75,13.25 Z M5,2.75 L5,6.25 L10.5,6.25 L10.5,2.75 M5,13.25 L5,9.5 L11,9.5 L11,13.25";
        public const string Tree = "M3,2.5 L3,12 L6,12 M3,6.5 L6,6.5 M7,4.5 L13,4.5 L13,8.5 L7,8.5 Z M7,10 L13,10 L13,14 L7,14 Z";
        public const string Check = "M3,8.5 L6,11.5 L13,4.5";
        public const string Undo = "M6,3.5 L2.5,7 L6,10.5 M2.5,7 L9.5,7 A4,4 0 0 1 9.5,15 L8,15";
        public const string Reset = "M2.5,8 A5.5,5.5 0 1 0 4.1,4.1 M3.5,1.5 L3.5,4.5 L6.5,4.5";

        /// <summary>A filled folder (24 × 24) for the big badge.</summary>
        public const string FolderFilled = "M3,6.5 C3,5.12 4.12,4 5.5,4 L9.6,4 C10.2,4 10.8,4.25 11.2,4.7 L12.6,6 L18.5,6 C19.88,6 21,7.12 21,8.5 L21,17.5 C21,18.88 19.88,20 18.5,20 L5.5,20 C4.12,20 3,18.88 3,17.5 Z";
    }

    /// <summary>An outline icon (a path of <see cref="Icons"/>), in the color of <paramref name="brushKey"/> or the parent's text.</summary>
    public static Control Icon(string data, double size = 16, string? brushKey = "NcFaintBrush", double stroke = 1.5)
    {
        var path = new ShapePath
        {
            Data = Geometry.Parse(data),
            StrokeThickness = stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            Width = 16,
            Height = 16,
        };
        if (brushKey is null)
        {
            path.Bind(Shape.StrokeProperty, path.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
        }
        else
        {
            path[!Shape.StrokeProperty] = new DynamicResourceExtension(brushKey);
        }
        return new Viewbox { Width = size, Height = size, Child = path, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    /// <summary>The window ground and text colors of the templates.</summary>
    public static void Apply(Window window)
    {
        window.Classes.Add("nc-window");
    }

    public static TextBlock Text(string text, string css, bool translate = true)
    {
        var block = new TextBlock { Text = text, Classes = { css } };
        if (!translate)
        {
            block.Classes.Add("noloc");
        }
        return block;
    }

    /// <summary>A key cap ("F7", "Enter").</summary>
    public static Border Kbd(string key) => new() { Classes = { "nc-kbd" }, Child = new TextBlock { Text = key, Classes = { "noloc" } } };

    /// <summary>A button: kind "" (plain), "primary", "ghost", "stop", "side", plus "sm" / "big"; an optional key cap after the text.</summary>
    public static Button Button(string text, string kind = "", string? key = null, string? icon = null)
    {
        var button = new Button { Classes = { "nc" } };
        foreach (var css in kind.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            button.Classes.Add(css);
        }
        if (key is null && icon is null)
        {
            button.Content = text;
            return button;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            row.Children.Add(Icon(icon, 15, null));
        }
        if (text.Length > 0)
        {
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        if (key is not null)
        {
            row.Children.Add(Kbd(key));
        }
        button.Content = row;
        return button;
    }

    /// <summary>The text of a button made by <see cref="Button"/> (tests).</summary>
    public static string TextOf(Button button) => button.Content switch
    {
        string s => s,
        StackPanel row => row.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>A square icon button; <paramref name="toggle"/> makes a toggle (accent when on).</summary>
    public static T IconButton<T>(string icon, string tip) where T : Avalonia.Controls.Button, new()
    {
        var button = new T { Content = Icon(icon, 15, null), Classes = { "nc-icon" } };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>An option chip (pressed when on).</summary>
    public static ToggleButton Chip(string text, bool isChecked = false, bool withCheck = false)
    {
        var chip = new ToggleButton { IsChecked = isChecked, Classes = { "nc-chip" } };
        if (!withCheck)
        {
            chip.Content = text;
            return chip;
        }
        var check = Icon(Icons.Check, 12, null, 2);
        check.IsVisible = isChecked;
        chip.IsCheckedChanged += (_, _) => check.IsVisible = chip.IsChecked == true;
        chip.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { check, new TextBlock { Text = text } } };
        return chip;
    }

    /// <summary>A small toggle inside a field: ".*", "Aa", "ab".</summary>
    public static ToggleButton Tog(string text, string tip, bool isChecked = false)
    {
        var tog = new ToggleButton { Content = new TextBlock { Text = text, Classes = { "noloc" } }, IsChecked = isChecked, Classes = { "nc-tog" } };
        ToolTip.SetTip(tog, tip);
        Avalonia.Automation.AutomationProperties.SetName(tog, tip);
        return tog;
    }

    /// <summary>
    /// A field box: an icon on the left, the text box (made bare), then <paramref name="tail"/> controls (toggles, a
    /// drop-down button). The box is framed in the accent color while the text box has the focus.
    /// </summary>
    public static Border Combo(Control box, string? icon, params Control[] tail)
    {
        MakeBare(box);
        var dock = new DockPanel();
        if (icon is not null)
        {
            var lead = new Border { Width = 38, Child = Icon(icon), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lead, Dock.Left);
            dock.Children.Add(lead);
        }
        else
        {
            box.Margin = new Thickness(10, 0, 0, 0);
        }
        for (var i = tail.Length - 1; i >= 0; i--)
        {
            DockPanel.SetDock(tail[i], Dock.Right);
            dock.Children.Add(tail[i]);
        }
        // the box in a panel of its own: path completion puts its popup next to it (a last child of the dock panel would fill it)
        dock.Children.Add(new Grid { Children = { box } });
        return new Border { Classes = { "nc-combo" }, Child = dock };
    }

    /// <summary>A text box (or the text box inside an auto-complete box) without its own frame, for a field box.</summary>
    public static void MakeBare(Control box)
    {
        switch (box)
        {
            case TextBox text:
                text.Classes.Add("nc-bare");
                break;
            case AutoCompleteBox auto:
                auto.Background = Brushes.Transparent;
                auto.BorderThickness = new Thickness(0);
                auto.VerticalAlignment = VerticalAlignment.Center;
                auto.TemplateApplied += (_, e) =>
                {
                    if (e.NameScope.Find<TextBox>("PART_TextBox") is { } inner)
                    {
                        inner.Classes.Add("nc-bare");
                        foreach (var css in auto.Classes.Where(c => c.StartsWith("nc-", StringComparison.Ordinal)))
                        {
                            inner.Classes.Add(css);
                        }
                    }
                };
                break;
        }
    }

    /// <summary>A label (and a hint on its right) above a field.</summary>
    public static StackPanel Field(string label, Control field, string? hint = null, params Control[] below)
    {
        var head = new DockPanel();
        if (hint is not null)
        {
            var h = new TextBlock { Text = hint, Classes = { "nc-hint" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(h, Dock.Right);
            head.Children.Add(h);
        }
        head.Children.Add(new TextBlock { Text = label, Classes = { "nc-label" }, VerticalAlignment = VerticalAlignment.Bottom });
        var stack = new StackPanel { Spacing = 7, Children = { head, field } };
        foreach (var control in below)
        {
            stack.Children.Add(control);
        }
        return stack;
    }

    /// <summary>The main control with a control on its right (a field and its "+ F7" button).</summary>
    public static DockPanel Line(Control main, params Control[] side)
    {
        var line = new DockPanel();
        for (var i = side.Length - 1; i >= 0; i--)
        {
            side[i].Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(side[i], Dock.Right);
            line.Children.Add(side[i]);
        }
        line.Children.Add(main);
        return line;
    }

    /// <summary>A card with a small upper section title.</summary>
    public static Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "nc-section" } });
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }
        return new Border { Classes = { "nc-card" }, Child = stack };
    }

    /// <summary>A card whose title row carries a control on its right (a sample, a hint).</summary>
    public static Border Card(Control titleRight, string title, params Control[] children)
    {
        var head = new DockPanel();
        titleRight.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(titleRight, Dock.Right);
        head.Children.Add(titleRight);
        head.Children.Add(new TextBlock { Text = title, Classes = { "nc-section" }, VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel { Spacing = 10, Children = { head } };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }
        return new Border { Classes = { "nc-card" }, Child = stack };
    }

    /// <summary>
    /// An option row: its name (and a tag such as "Admin"), a description, and the switch on the right; a click anywhere on
    /// the row turns the switch (the check box stays the control: IsChecked, keyboard, tests).
    /// </summary>
    public static Border Option(CheckBox check, string title, string? description = null, string? tag = null)
    {
        check.Classes.Add("nc-switch");
        check.Content = null;
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap } } };
        if (tag is not null)
        {
            name.Children.Add(new Border { Classes = { "nc-tag" }, Child = new TextBlock { Text = tag } });
        }
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (description is not null)
        {
            texts.Children.Add(new TextBlock { Text = description, Classes = { "nc-hint" } });
        }
        var dock = new DockPanel();
        check.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(check, Dock.Right);
        dock.Children.Add(check);
        dock.Children.Add(texts);
        var row = new Border { Classes = { "nc-opt" }, Child = dock };
        row.Bind(InputElement.IsEnabledProperty, check.GetObservable(InputElement.IsEnabledProperty));
        Avalonia.Automation.AutomationProperties.SetName(check, title);
        row.PointerReleased += (_, e) =>
        {
            if (check.IsEnabled && e.InitialPressMouseButton == MouseButton.Left && !ReferenceEquals(e.Source, check)
                && (e.Source as Visual)?.FindAncestorOfType<CheckBox>() is null)
            {
                check.IsChecked = check.IsChecked != true;
                e.Handled = true;
            }
        };
        return row;
    }

    /// <summary>Option rows framed together.</summary>
    public static Border Options(params Border[] rows)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var row in rows)
        {
            stack.Children.Add(row);
        }
        return new Border { Classes = { "nc-options" }, Child = stack };
    }

    /// <summary>One of a few, as a segmented control (one group).</summary>
    public static (Border Box, RadioButton[] Options) Segmented(IReadOnlyList<string> options, int selected, bool translate = true)
    {
        var group = "nc-seg-" + Guid.NewGuid().ToString("N");
        var buttons = new RadioButton[options.Count];
        var grid = new UniformGrid { Rows = 1, Columns = options.Count };
        for (var i = 0; i < options.Count; i++)
        {
            var text = new TextBlock { Text = options[i] };
            if (!translate)
            {
                text.Classes.Add("noloc");
            }
            buttons[i] = new RadioButton { Content = text, GroupName = group, IsChecked = i == selected, Classes = { "nc-seg" } };
            grid.Children.Add(buttons[i]);
        }
        return (new Border { Classes = { "nc-seg" }, Child = grid }, buttons);
    }

    /// <summary>A count pill: kind "", "ok", "warn", "bad".</summary>
    public static Border Count(TextBlock text, string kind = "")
    {
        var border = new Border { Classes = { "nc-count" }, Child = text };
        if (kind.Length > 0)
        {
            border.Classes.Add(kind);
        }
        return border;
    }

    /// <summary>The footer bar: <paramref name="left"/> controls, a spacer, then <paramref name="right"/> ones.</summary>
    public static Border Footer(Control[] left, params Control[] right)
    {
        var dock = new DockPanel { LastChildFill = true };
        var rightRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var control in right)
        {
            rightRow.Children.Add(control);
        }
        DockPanel.SetDock(rightRow, Dock.Right);
        dock.Children.Add(rightRow);
        var leftRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in left)
        {
            leftRow.Children.Add(control);
        }
        dock.Children.Add(leftRow);
        return new Border { Classes = { "nc-footer" }, Child = dock };
    }
}

/// <summary>A whole number field with − and + buttons (the templates' counter fields).</summary>
public sealed class NcNumber : Border
{
    private readonly TextBox _box = new() { Classes = { "nc-mono", "noloc" }, HorizontalContentAlignment = HorizontalAlignment.Left };
    private decimal? _value;

    public NcNumber(decimal value, decimal minimum, decimal maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
        _value = value;
        _box.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _box.TextChanged += (_, _) =>
        {
            if (decimal.TryParse(_box.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                SetValue(Math.Clamp(parsed, Minimum, Maximum), updateText: false);
            }
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
            {
                Value = (Value ?? 0) + (e.Key == Key.Up ? 1 : -1);
                e.Handled = true;
            }
        };
        Button Step(string text, int by, string name)
        {
            var button = new Avalonia.Controls.Button
            {
                Content = new TextBlock { Text = text, Classes = { "noloc" } },
                Width = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            button[!Avalonia.Controls.Button.BorderBrushProperty] = new DynamicResourceExtension("NcLineBrush");
            button[!Avalonia.Controls.Button.ForegroundProperty] = new DynamicResourceExtension("NcMutedBrush");
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => Value = (Value ?? 0) + by;
            return button;
        }
        var minus = Step("−", -1, "Decrease");
        var plus = Step("+", 1, "Increase");
        var dock = new DockPanel();
        DockPanel.SetDock(plus, Dock.Right);
        DockPanel.SetDock(minus, Dock.Right);
        dock.Children.AddRange([plus, minus]);
        Nc.MakeBare(_box);
        _box.Margin = new Thickness(10, 0, 0, 0);
        dock.Children.Add(_box);
        Classes.Add("nc-combo");
        MinHeight = 38;
        Child = dock;
        ClipToBounds = true;
    }

    public decimal Minimum { get; set; }

    public decimal Maximum { get; set; }

    public decimal? Value
    {
        get => _value;
        set => SetValue(value is null ? null : Math.Clamp(value.Value, Minimum, Maximum), updateText: true);
    }

    public event EventHandler? ValueChanged;

    /// <summary>Styled as a Border (Border.nc-combo).</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    private void SetValue(decimal? value, bool updateText)
    {
        var changed = value != _value;
        _value = value;
        if (updateText)
        {
            _box.Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (changed)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// A section that opens and closes (the templates' "Advanced options"): a header row with a chevron, the title, a summary
/// shown while closed and tool buttons; the body below it.
/// </summary>
public sealed class NcDisclosure : Border
{
    private readonly Control _chevron;
    private readonly TextBlock _summary;
    private readonly Border _body;

    public NcDisclosure(string title, Control body, params Control[] tools)
    {
        Classes.Add("nc-disclosure");
        _chevron = Nc.Icon(Nc.Icons.ChevronRight, 14, "NcMutedBrush", 1.6);
        _chevron.RenderTransform = new RotateTransform(0);
        _summary = new TextBlock { Classes = { "nc-hint", "noloc" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var tool in tools)
        {
            toolRow.Children.Add(tool);
        }
        var head = new DockPanel();
        DockPanel.SetDock(_chevron, Dock.Left);
        head.Children.Add(_chevron);
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(titleBlock, Dock.Left);
        head.Children.Add(titleBlock);
        DockPanel.SetDock(toolRow, Dock.Right);
        head.Children.Add(toolRow);
        head.Children.Add(_summary);
        Header = new Border { Classes = { "nc-summary" }, Child = head, Focusable = true };
        Header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && (e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.Button>() is null)
            {
                IsOpen = !IsOpen;
            }
        };
        Header.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                IsOpen = !IsOpen;
                e.Handled = true;
            }
        };
        _body = new Border
        {
            Child = body,
            Padding = new Thickness(14, 4, 14, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false,
        };
        _body[!BorderBrushProperty] = new DynamicResourceExtension("NcLineBrush");
        Child = new StackPanel { Children = { Header, _body } };
        Avalonia.Automation.AutomationProperties.SetName(Header, title);
    }

    public Border Header { get; }

    /// <summary>Styled as a Border (Border.nc-disclosure).</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public bool IsOpen
    {
        get => _body.IsVisible;
        set
        {
            _body.IsVisible = value;
            ((RotateTransform)_chevron.RenderTransform!).Angle = value ? 90 : 0;
            _summary.IsVisible = !value;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? OpenChanged;

    /// <summary>The line shown in the header while closed.</summary>
    public string? Summary
    {
        get => _summary.Text;
        set => _summary.Text = value;
    }
}
