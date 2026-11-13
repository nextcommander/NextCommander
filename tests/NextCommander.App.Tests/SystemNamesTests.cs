using NextCommander.App.Services;
using NextCommander.Core.Localization;

namespace NextCommander.App.Tests;

/// <summary>The name of cmd.exe follows the application's language: Windows' own name, else the application's translation.</summary>
[Collection("Appearance")]
public sealed class SystemNamesTests
{
    private const string Resource = "@%SystemRoot%\\system32\\shell32.dll,-22022";

    [Fact]
    public void Windows_gives_the_command_prompt_name_in_the_asked_language()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Equal("Command Prompt", SystemNames.WindowsString(Resource, "en"));
        // Turkish when the Turkish language pack is installed, else Windows' own language
        var turkish = SystemNames.WindowsString(Resource, "tr");
        Assert.False(string.IsNullOrWhiteSpace(turkish));
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "tr-TR", "shell32.dll.mui")))
        {
            Assert.Equal("Komut İstemi", turkish);
        }
    }

    [Fact]
    public void Command_prompt_name_follows_the_application_language()
    {
        var before = Localizer.Current;
        try
        {
            Localizer.Current = null;
            Assert.Equal("Command Prompt", SystemNames.CommandPrompt());
            foreach (var (language, expected) in new[] { ("tr", "Komut İstemi"), ("de", "Eingabeaufforderung") })
            {
                Localizer.Current = UiLocalization.Read(language);
                // Windows' name when it has the language, else the translation: the same words on Windows and elsewhere
                Assert.Equal(expected, SystemNames.CommandPrompt());
            }
        }
        finally
        {
            Localizer.Current = before;
        }
    }
}
