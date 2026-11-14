using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.Core.Localization;

namespace NextCommander.App.Services;

/// <summary>
/// Applies <see cref="Localizer.Current"/> to the whole UI without touching the views: every TextBlock text (buttons,
/// menus, labels, list items, watermarks, tool tips and messages all end up in one) and window title is translated when
/// it is set or shown. File and folder names are left alone: file list cells, tabs, path crumbs, tree items and anything
/// marked with the class "noloc".
/// </summary>
public static class UiLocalization
{
    private static readonly string[] s_skipClasses = ["cell", "crumb", "tab", "noloc"];
    private static bool s_installed;

    /// <summary>
    /// &lt;program&gt;/Languages: the shipped language files (&lt;code&gt;.json, e.g. tr.json; en.json lists every text and
    /// is the template for a new language).
    /// </summary>
    public static string BuiltInDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>&lt;settings&gt;/languages: the user's own or corrected language files (win over the shipped ones).</summary>
    public static string? UserDirectory { get; set; }

    /// <summary>The language loaded last ("en", "tr" ...).</summary>
    public static string? Language { get; private set; }

    /// <summary>The language changed while the program runs (views that translate texts themselves build them again).</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// The English text a TextBlock showed before it was translated, and the translation it got: a text that is still
    /// that translation is translated again from the English one when the language changes.
    /// </summary>
    private static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("LocalizationSource", typeof(UiLocalization));

    private static readonly AttachedProperty<string?> ShownProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("LocalizationShown", typeof(UiLocalization));

    private static readonly AttachedProperty<string?> TitleSourceProperty =
        AvaloniaProperty.RegisterAttached<Window, string?>("LocalizationTitleSource", typeof(UiLocalization));

    private static readonly AttachedProperty<string?> TitleShownProperty =
        AvaloniaProperty.RegisterAttached<Window, string?>("LocalizationTitleShown", typeof(UiLocalization));

    /// <summary>Loads the language chosen in the settings ("system" or a language code); English needs no translation.</summary>
    public static void Load(string? setting)
    {
        var language = ResolveLanguage(setting, CultureInfo.CurrentUICulture);
        Language = language;
        Localizer.Current = language == Localizer.BaseLanguage ? null : Read(language);
        Install(); // in English too: the windows shown are known when the language is switched later
    }

    /// <summary>
    /// Switches the language while the program runs (Configuration > Display > Language, Apply): every text of the open
    /// windows is translated again from its English text; texts shown later follow by themselves.
    /// </summary>
    public static void Switch(string? setting)
    {
        if (ResolveLanguage(setting, CultureInfo.CurrentUICulture) == Language)
        {
            return;
        }
        Load(setting);
        Install(); // also back to English: the texts translated before go back to their English ones
        foreach (var top in OpenTopLevels())
        {
            if (top is Window window)
            {
                ApplyTitle(window);
            }
            foreach (var block in top.GetVisualDescendants().OfType<TextBlock>())
            {
                Apply(block);
            }
        }
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The windows texts were shown in (kept weakly): the ones still open are translated again on a switch.</summary>
    private static readonly List<WeakReference<TopLevel>> s_topLevels = [];

    private static void Remember(TopLevel top)
    {
        s_topLevels.RemoveAll(r => !r.TryGetTarget(out _));
        if (!s_topLevels.Any(r => r.TryGetTarget(out var known) && ReferenceEquals(known, top)))
        {
            s_topLevels.Add(new WeakReference<TopLevel>(top));
        }
    }

    private static IEnumerable<TopLevel> OpenTopLevels()
    {
        var tops = s_topLevels.Select(r => r.TryGetTarget(out var top) ? top : null).OfType<TopLevel>().ToList();
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            tops.AddRange(desktop.Windows.Where(w => !tops.Contains(w)));
        }
        return tops;
    }

    /// <summary>"system": the UI culture's language when there is a file for it, otherwise English.</summary>
    public static string ResolveLanguage(string? setting, CultureInfo culture)
    {
        if (setting is not (null or "" or "system"))
        {
            return setting;
        }
        var code = culture.TwoLetterISOLanguageName;
        return Available().Any(l => l.Code == code) ? code : Localizer.BaseLanguage;
    }

    /// <summary>The languages there are files for (code and own name), English (the language of the code) first.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Localizer.BaseLanguage] = "English" };
        foreach (var dir in new[] { BuiltInDirectory, UserDirectory })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (code != Localizer.BaseLanguage && ReadText(code) is { } text)
                {
                    names[code] = NameOf(code, text);
                }
            }
        }
        if (!names.ContainsKey("tr") && ReadText("tr") is { } tr)
        {
            names["tr"] = NameOf("tr", tr); // the built-in copy
        }
        return [.. names.OrderBy(n => n.Key == Localizer.BaseLanguage ? 0 : 1).ThenBy(n => n.Value, StringComparer.CurrentCultureIgnoreCase).Select(n => (n.Key, n.Value))];
    }

    private static string NameOf(string code, string json)
    {
        try
        {
            if (Localizer.ParseFile(json).TryGetValue(Localizer.NameKey, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        try
        {
            return CultureInfo.GetCultureInfo(code).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>
    /// Translation for a language: &lt;settings&gt;/languages, &lt;program&gt;/Languages, then the built-in copy; null when none.
    /// Keys are linked to the texts in the code through en.json.
    /// </summary>
    public static Localizer? Read(string language)
    {
        if (ReadText(language) is not { } text)
        {
            return null;
        }
        try
        {
            // en.json: the English text of every key (the texts in the code)
            return ReadText(Localizer.BaseLanguage) is { } source ? Localizer.FromFiles(language, source, text) : Localizer.FromJson(language, text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of a language file: the user's, the program's, or the built-in copy (the language editor reads it too).</summary>
    public static string? ReadText(string language)
    {
        foreach (var dir in new[] { UserDirectory, BuiltInDirectory })
        {
            if (!string.IsNullOrEmpty(dir) && Path.Combine(dir, language + ".json") is var file && File.Exists(file))
            {
                try
                {
                    return File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        using var stream = typeof(UiLocalization).Assembly.GetManifestResourceStream($"NextCommander.App.Localization.{language}.json");
        if (stream is null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Hooks text changes once per process.</summary>
    public static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (block.IsAttachedToVisualTree())
            {
                Apply(block);
            }
        });
        Control.LoadedEvent.AddClassHandler<TextBlock>((block, _) =>
        {
            if (TopLevel.GetTopLevel(block) is { } top)
            {
                Remember(top);
            }
            Apply(block);
        }, RoutingStrategies.Direct);
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, _) => ApplyTitle(window));
    }

    private static void ApplyTitle(Window window)
    {
        if (window.Title is not { } title)
        {
            return;
        }
        var source = window.GetValue(TitleSourceProperty) is { } own && title == window.GetValue(TitleShownProperty) ? own : title;
        if (Localizer.Current is null && window.GetValue(TitleSourceProperty) is null)
        {
            return;
        }
        var translated = Localizer.Current?.Translate(source) ?? source;
        window.SetValue(TitleSourceProperty, source);
        window.SetValue(TitleShownProperty, translated);
        if (translated != title)
        {
            window.SetCurrentValue(Window.TitleProperty, translated);
        }
    }

    /// <summary>Called with texts that stayed untranslated (finding missing entries; tests and debugging).</summary>
    public static Action<string>? Untranslated { get; set; }

    private static void Apply(TextBlock block)
    {
        if (block.Text is not { Length: > 0 } text)
        {
            return;
        }
        // the text is still the translation given before: translate its English text (the language may have changed)
        var earlier = block.GetValue(SourceProperty);
        var source = earlier is not null && text == block.GetValue(ShownProperty) ? earlier : text;
        if ((Localizer.Current is null && earlier is null) || IsExcluded(block))
        {
            return;
        }
        var translated = Localizer.Current?.Translate(source) ?? source;
        if (translated == source && Localizer.Current is not null)
        {
            Untranslated?.Invoke(source);
        }
        block.SetValue(SourceProperty, source);
        block.SetValue(ShownProperty, translated);
        if (translated != text)
        {
            // SetCurrentValue keeps bindings: a new bound value comes through here again.
            block.SetCurrentValue(TextBlock.TextProperty, translated);
        }
    }

    private static bool IsExcluded(TextBlock block)
    {
        Visual? v = block;
        for (var depth = 0; v is not null && depth < 12; depth++, v = v.GetVisualParent())
        {
            if (v is DataGridCell or TreeViewItem || (v is StyledElement e && s_skipClasses.Any(e.Classes.Contains)))
            {
                return true;
            }
            if (v is TopLevel)
            {
                break;
            }
        }
        return false;
    }
}
