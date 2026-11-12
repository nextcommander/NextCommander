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

    /// <summary>Loads the language chosen in the settings ("system" or a language code); English needs no translation.</summary>
    public static void Load(string? setting)
    {
        var language = ResolveLanguage(setting, CultureInfo.CurrentUICulture);
        Localizer.Current = language == Localizer.BaseLanguage ? null : Read(language);
        if (Localizer.Current is not null)
        {
            Install();
        }
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
        Control.LoadedEvent.AddClassHandler<TextBlock>((block, _) => Apply(block), RoutingStrategies.Direct);
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, _) =>
        {
            if (Localizer.Current is { } l && window.Title is { } title && l.Translate(title) is var t && t != title)
            {
                window.SetCurrentValue(Window.TitleProperty, t);
            }
        });
    }

    /// <summary>Called with texts that stayed untranslated (finding missing entries; tests and debugging).</summary>
    public static Action<string>? Untranslated { get; set; }

    private static void Apply(TextBlock block)
    {
        if (Localizer.Current is not { } localizer || block.Text is not { Length: > 0 } text || IsExcluded(block))
        {
            return;
        }
        var translated = localizer.Translate(text);
        if (translated == text)
        {
            Untranslated?.Invoke(text);
        }
        if (!ReferenceEquals(translated, text) && translated != text)
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
