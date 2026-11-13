using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The language editor (Configuration > Language): every text of the program in English with a box for its translation,
/// for a shipped language, one of the user's or a new one. Saved into the languages folder of the settings (a file
/// there is used instead of a shipped one); takes effect after a restart.
/// </summary>
public sealed partial class LanguageEditorDialog : Window
{
    /// <summary>One text: its key, the English text, the translation being edited.</summary>
    public sealed class Row(string key, string english, string translation)
    {
        public string Key { get; } = key;

        public string English { get; } = english;

        public string Translation { get; set; } = translation;

        public bool IsTranslated => !string.IsNullOrWhiteSpace(Translation);
    }

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<Row> _rows;
    private readonly IReadOnlyList<(string Code, string Name)> _languages;
    private readonly ComboBox _language = new() { MinWidth = 220 };
    private readonly TextBox _code = new() { PlaceholderText = "Code, e.g. it", Width = 120, Classes = { "noloc" } };
    private readonly TextBox _name = new() { PlaceholderText = "Name in the language, e.g. Italiano", Width = 240, Classes = { "noloc" } };
    private readonly TextBox _search = new() { PlaceholderText = "Search", Width = 220 };
    private readonly CheckBox _onlyMissing = new() { Content = "Only texts not translated yet" };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _count = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, string> _english;
    private bool _loading;

    /// <param name="language">The language to start with ("tr", "de" ...); null or "en": a new language.</param>
    public LanguageEditorDialog(string? language)
    {
        Title = "Language editor";
        Width = 1000;
        Height = 720;
        MinWidth = 640;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _english = UiLocalization.ReadText(Localizer.BaseLanguage) is { } source ? Localizer.ParseFile(source) : [];
        _rows = [.. _english.Where(e => e.Key != Localizer.NameKey).Select(e => new Row(e.Key, e.Value, string.Empty))];
        _languages = [.. UiLocalization.Available().Where(l => l.Code != Localizer.BaseLanguage)];
        _language.ItemsSource = _languages.Select(l => $"{l.Name} ({l.Code})").Append("New language...").ToList();
        _language.SelectionChanged += (_, _) => LoadLanguage();
        _search.TextChanged += (_, _) => Filter();
        _onlyMissing.IsCheckedChanged += (_, _) => Filter();

        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => RowView(row));
        var top = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        top.Children.AddRange([
            new TextBlock { Text = "Language:", VerticalAlignment = VerticalAlignment.Center }, _language, _code, _name,
        ]);
        var filters = new WrapPanel { ItemSpacing = 12, LineSpacing = 6 };
        filters.Children.AddRange([_search, _onlyMissing, _count]);
        var hint = new TextBlock
        {
            Text = "Keep {0}, {1} ... (they stand for names and numbers), key names such as Ctrl and the underscore of menu access keys (_File). Untranslated texts stay in English. Used after a restart.",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14, 16, 8) };
        head.Children.AddRange([top, filters, hint, _error]);

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.AddRange([save, cancel]);
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        _list.Margin = new Thickness(16, 0, 16, 0);
        root.Children.AddRange([head, buttons, _list]);
        Content = root;

        var start = _languages.ToList().FindIndex(l => string.Equals(l.Code, language, StringComparison.OrdinalIgnoreCase));
        _language.SelectedIndex = start >= 0 ? start : _languages.Count; // "New language..."
    }

    /// <summary>The code of the language saved; null when cancelled.</summary>
    public string? Result { get; private set; }

    /// <summary>The texts (tests).</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>The texts shown with the filters (tests).</summary>
    public IReadOnlyList<Row> Shown => _list.ItemsSource as IReadOnlyList<Row> ?? [];

    public string Code
    {
        get => _code.Text ?? string.Empty;
        set => _code.Text = value;
    }

    public string LanguageName
    {
        get => _name.Text ?? string.Empty;
        set => _name.Text = value;
    }

    public string SearchText
    {
        get => _search.Text ?? string.Empty;
        set => _search.Text = value;
    }

    public bool OnlyMissing
    {
        get => _onlyMissing.IsChecked == true;
        set => _onlyMissing.IsChecked = value;
    }

    public string ErrorText => _error.Text ?? string.Empty;

    private bool IsNew => _language.SelectedIndex >= _languages.Count;

    /// <summary>The chosen language's texts into the boxes (a new language starts empty).</summary>
    private void LoadLanguage()
    {
        _loading = true;
        var translations = new Dictionary<string, string>();
        if (IsNew)
        {
            _code.Text = string.Empty;
            _name.Text = string.Empty;
            _code.IsEnabled = true;
        }
        else
        {
            var (code, name) = _languages[_language.SelectedIndex];
            _code.Text = code;
            _name.Text = name;
            _code.IsEnabled = false;
            if (UiLocalization.ReadText(code) is { } text)
            {
                try
                {
                    translations = Localizer.ParseFile(text);
                }
                catch (JsonException)
                {
                    // an unreadable file: start empty
                }
            }
        }
        foreach (var row in _rows)
        {
            row.Translation = translations.GetValueOrDefault(row.Key) ?? string.Empty;
        }
        _loading = false;
        Filter();
    }

    private void Filter()
    {
        if (_loading)
        {
            return;
        }
        var text = (_search.Text ?? string.Empty).Trim();
        var shown = _rows.Where(r => (_onlyMissing.IsChecked != true || !r.IsTranslated)
            && (text.Length == 0 || r.English.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || r.Translation.Contains(text, StringComparison.CurrentCultureIgnoreCase) || r.Key.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        _list.ItemsSource = shown;
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0} texts translated", _rows.Count(r => r.IsTranslated), _rows.Count);

    /// <summary>A line: the English text (and its key) on the left, the translation box on the right.</summary>
    private Control RowView(Row? row)
    {
        if (row is null)
        {
            return new Panel();
        }
        var english = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        english.Children.Add(new SelectableTextBlock { Text = row.English, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        english.Children.Add(new TextBlock { Text = row.Key, FontSize = 11, Opacity = 0.5, Classes = { "noloc" } });
        var box = new TextBox { AcceptsReturn = row.English.Contains('\n', StringComparison.Ordinal), TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
        box.Bind(TextBox.TextProperty, new Binding(nameof(Row.Translation)) { Mode = BindingMode.TwoWay });
        box.LostFocus += (_, _) => UpdateCount();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, Margin = new Thickness(0, 2) };
        Grid.SetColumn(box, 1);
        grid.Children.AddRange([english, box]);
        return grid;
    }

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Hole();

    public void Save()
    {
        var code = (_code.Text ?? string.Empty).Trim();
        var name = (_name.Text ?? string.Empty).Trim();
        if (!CodePattern().IsMatch(code) || code == Localizer.BaseLanguage)
        {
            _error.Text = "Give the language code of two or three small letters (it, pt, pt-BR ...); en is the original.";
            return;
        }
        if (name.Length == 0)
        {
            _error.Text = "Give the name of the language in that language.";
            return;
        }
        // a lost {0} would drop a file name or a number from a message
        if (_rows.FirstOrDefault(r => r.IsTranslated && Hole().Matches(r.English).Any(h => !r.Translation.Contains(h.Value, StringComparison.Ordinal))) is { } broken)
        {
            _error.Text = string.Format(CultureInfo.CurrentCulture, "The translation of \"{0}\" lacks a placeholder of the English text.", broken.English);
            _search.Text = broken.Key;
            return;
        }
        var file = new Dictionary<string, string> { [Localizer.NameKey] = name };
        foreach (var row in _rows.Where(r => r.IsTranslated))
        {
            file[row.Key] = row.Translation;
        }
        var dir = UiLocalization.UserDirectory ?? UiLocalization.BuiltInDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, code + ".json"),
                JsonSerializer.Serialize(file, s_write) + "\n",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = ex.Message;
            return;
        }
        Result = code;
        Close();
    }
}
