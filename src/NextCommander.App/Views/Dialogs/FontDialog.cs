using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Font of the built-in editor: the fonts of the system (fixed-width ones by default), each shown in itself, a size
/// and a preview. <see cref="Result"/> is null when cancelled.
/// </summary>
public sealed class FontDialog : DialogWindow
{
    private const string Sample = "for (var i = 0; i < 10; i++) { Console.WriteLine($\"{i}: ÇĞİÖŞÜ çğıöşü\"); } // 0O 1lI";

    private readonly TextBox _filter = new() { Watermark = "Search" };
    private readonly CheckBox _fixedOnly = new() { Content = "Fixed-width fonts only", IsChecked = true };
    private readonly ListBox _list = new() { Height = 260, SelectionMode = SelectionMode.Single };
    private readonly NumericUpDown _size = new() { Minimum = 6, Maximum = 48, Increment = 1, FormatString = "0", Width = 130 };
    private readonly TextBlock _preview = new() { Text = Sample, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
    private readonly IReadOnlyList<string> _all;
    private readonly HashSet<string> _fixed;

    public FontDialog(string family, double size)
        : base("Editor font", 520)
    {
        _all = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        _fixed = new HashSet<string>(SettingsWindow.MonospaceFonts(), StringComparer.OrdinalIgnoreCase);
        // the setting may hold a fallback list ("Cascadia Mono, Consolas, ..."): the first installed one is the current font
        var current = family.Split(',').Select(f => f.Trim()).FirstOrDefault(f => _all.Contains(f, StringComparer.OrdinalIgnoreCase)) ?? family.Split(',')[0].Trim();
        if (!_fixed.Contains(current))
        {
            _fixedOnly.IsChecked = false;
        }
        _size.Value = (decimal)Math.Clamp(size, 6, 48);

        // each font shown in itself (the template is also built without an item while the list recycles)
        _list.ItemTemplate = new FuncDataTemplate<string>((name, _) => string.IsNullOrEmpty(name)
            ? new TextBlock()
            : new TextBlock { Text = name, FontFamily = new FontFamily(name), FontSize = 14, Classes = { "noloc" } });
        Fill(current);

        _filter.TextChanged += (_, _) => Fill(_list.SelectedItem as string);
        _fixedOnly.IsCheckedChanged += (_, _) => Fill(_list.SelectedItem as string);
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _size.ValueChanged += (_, _) => UpdatePreview();
        _list.DoubleTapped += (_, _) => Accept();

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "Size:", VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_size);
        var previewBox = new Border
        {
            Child = _preview,
            Padding = new Thickness(8),
            MinHeight = 64,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        previewBox[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmFunctionKeyBorderBrush");
        Body.Children.AddRange([_filter, _fixedOnly, _list, sizeRow, previewBox]);
        AddButton("OK", Accept, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        Opened += (_, _) => _filter.Focus();
        UpdatePreview();
    }

    /// <summary>The chosen font and size; null when cancelled.</summary>
    public (string Family, double Size)? Result { get; private set; }

    /// <summary>The fonts listed now (tests).</summary>
    public IReadOnlyList<string> Fonts => _list.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>The font selected now (tests).</summary>
    public string? Selected
    {
        get => _list.SelectedItem as string;
        set => _list.SelectedItem = value;
    }

    /// <summary>The size set now (tests).</summary>
    public double Size
    {
        get => (double)(_size.Value ?? 12);
        set => _size.Value = (decimal)value;
    }

    /// <summary>Only fixed-width fonts are listed (tests).</summary>
    public bool FixedOnly
    {
        get => _fixedOnly.IsChecked == true;
        set => _fixedOnly.IsChecked = value;
    }

    /// <summary>Text of the preview (tests).</summary>
    public TextBlock Preview => _preview;

    public void Accept()
    {
        if (_list.SelectedItem is string family)
        {
            Result = (family, Size);
        }
        Close();
    }

    private void Fill(string? keep)
    {
        var filter = (_filter.Text ?? string.Empty).Trim();
        var fonts = _all.Where(f => (_fixedOnly.IsChecked != true || _fixed.Contains(f))
            && (filter.Length == 0 || f.Contains(filter, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _list.ItemsSource = fonts;
        _list.SelectedItem = keep is not null && fonts.Contains(keep, StringComparer.OrdinalIgnoreCase)
            ? fonts.First(f => string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
            : fonts.FirstOrDefault();
        if (_list.SelectedItem is { } selected)
        {
            _list.ScrollIntoView(selected);
        }
    }

    private void UpdatePreview()
    {
        if (_list.SelectedItem is string family)
        {
            _preview.FontFamily = new FontFamily(family);
        }
        _preview.FontSize = Size;
    }
}
