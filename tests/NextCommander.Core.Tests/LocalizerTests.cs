using System.Globalization;
using NextCommander.Core.Localization;

namespace NextCommander.Core.Tests;

public sealed class LocalizerTests
{
    private static readonly Localizer s_tr = new("tr", new Dictionary<string, string>
    {
        ["Copy"] = "Kopyala",
        ["_Files"] = "_Dosya",
        ["OK"] = "Tamam",
        ["Delete {0} files?"] = "{0} dosya silinsin mi?",
        ["{0} files"] = "{0} dosya",
        ["{0} / {1} files — {2}{3}"] = "{0} / {1} dosya — {2}{3}",
        ["Copy \"{0}\" to {1}?"] = "\"{0}\" {1} klasörüne kopyalansın mı?",
        ["Found {0} in {1}"] = "{1} içinde {0} bulundu",
        ["Done"] = "Bitti",
        ["{0}: {1:N0} results"] = "{0}: {1:N0} sonuç",
        ["{0}"] = "ignored",
        ["Line"] = "Satır",
    });

    [Theory]
    [InlineData("Copy", "Kopyala")]
    [InlineData("_Files", "_Dosya")]
    [InlineData("  OK ", "  Tamam ")]
    [InlineData("Delete 3 files?", "3 dosya silinsin mi?")]
    [InlineData("12 files", "12 dosya")]
    [InlineData("Copy \"a.txt\" to C:\\x?", "\"a.txt\" C:\\x klasörüne kopyalansın mı?")]
    [InlineData("Found x.log in C:\\", "C:\\ içinde x.log bulundu")] // placeholders in another order
    [InlineData("3 / 5 files — 1 MB", "3 / 5 dosya — 1 MB")] // empty last placeholder
    [InlineData("Done: 1.234 results", "Bitti: 1.234 sonuç")] // placeholder values translated too
    [InlineData("Line\nOK", "Satır\nTamam")] // multi-line, every line known
    [InlineData("unknown text", "unknown text")]
    [InlineData("12:30", "12:30")]
    [InlineData("", "")]
    public void Translates_exact_texts_and_patterns(string text, string expected) => Assert.Equal(expected, s_tr.Translate(text));

    [Fact]
    public void Language_setting_resolution()
    {
        Assert.Equal("en", Localizer.BaseLanguage);
        Assert.Equal("tr", Localizer.Resolve("system", new CultureInfo("tr-TR")));
        Assert.Equal("en", Localizer.Resolve("system", new CultureInfo("de-DE")));
        Assert.Equal("en", Localizer.Resolve(null, CultureInfo.InvariantCulture));
        Assert.Equal("tr", Localizer.Resolve("tr", new CultureInfo("en-US")));
    }

    [Fact]
    public void Loads_json()
    {
        var l = Localizer.FromJson("tr", """{ "Delete": "Sil", "{0} items": "{0} öğe", "Same": "Same" }""");
        Assert.Equal(2, l.Count);
        Assert.Equal("4 öğe", l.Translate("4 items"));
    }

    [Fact]
    public void Language_files_with_keys_are_linked_through_the_english_file()
    {
        var en = """{ "_name": "English", "Menu.nc_Delete": "Delete", "FilePanel.Items": "{0} items" }""";
        var tr = """{ "_name": "Türkçe", "Menu.nc_Delete": "Sil", "FilePanel.Items": "{0} öğe", "Copy": "Kopyala", "Unknown.Key": "x" }""";
        var l = Localizer.FromFiles("tr", en, tr);
        Assert.Equal("Sil", l.Translate("Delete"));
        Assert.Equal("4 öğe", l.Translate("4 items"));
        Assert.Equal("Kopyala", l.Translate("Copy")); // the English text as key
        Assert.Equal("Unknown.Key", l.Translate("Unknown.Key")); // a key without English text is no text
        Assert.Equal("Türkçe", Localizer.Join(Localizer.ParseFile(en), Localizer.ParseFile(tr))[Localizer.NameKey]);
    }
}
