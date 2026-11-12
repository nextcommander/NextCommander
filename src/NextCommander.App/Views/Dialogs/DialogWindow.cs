using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Base for small code-built dialogs: title, content area, right-aligned button row.</summary>
public class DialogWindow : Window
{
    private readonly StackPanel _buttons;

    protected DialogWindow(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 8) };
        _buttons = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(16, 4, 16, 14) };
        var root = new DockPanel();
        DockPanel.SetDock(_buttons, Dock.Bottom);
        root.Children.Add(_buttons);
        root.Children.Add(Body);
        Content = root;
    }

    protected StackPanel Body { get; }

    protected Button AddButton(string text, Action onClick, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Classes = { "dialog" },
        };
        button.Click += (_, _) => onClick();
        _buttons.Children.Add(button);
        return button;
    }

    protected static TextBlock Label(string text, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

/// <summary>Message with a set of buttons; returns the value of the clicked button (default when closed).</summary>
public sealed class ChoiceDialog<T> : DialogWindow
{
    public ChoiceDialog(string title, string message, IReadOnlyList<(string Text, T Value)> choices, T cancelValue, int defaultIndex = 0, double width = 460)
        : base(title, width)
    {
        Result = cancelValue;
        Body.Children.Add(Label(message));
        for (var i = 0; i < choices.Count; i++)
        {
            var (text, value) = choices[i];
            AddButton(text, () =>
            {
                Result = value;
                Close();
            }, isDefault: i == defaultIndex, isCancel: EqualityComparer<T>.Default.Equals(value, cancelValue));
        }
    }

    public T Result { get; private set; }
}

/// <summary>Single text input.</summary>
public sealed class InputDialog : DialogWindow
{
    private readonly TextBox _input;

    public InputDialog(string title, string message, string initialText, bool selectBaseName)
        : base(title)
    {
        Body.Children.Add(Label(message));
        _input = new TextBox { Text = initialText };
        Body.Children.Add(_input);
        AddButton("OK", () =>
        {
            Result = _input.Text;
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);

        Opened += (_, _) =>
        {
            _input.Focus();
            var text = _input.Text ?? string.Empty;
            var dot = selectBaseName ? text.LastIndexOf('.') : -1;
            _input.SelectionStart = 0;
            _input.SelectionEnd = dot > 0 ? dot : text.Length;
        };
    }

    public string? Result { get; private set; }
}
