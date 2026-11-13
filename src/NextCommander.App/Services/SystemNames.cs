using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NextCommander.Core.Localization;

namespace NextCommander.App.Services;

/// <summary>
/// Names the operating system gives its own programs, in the application's language. Windows: the resource the Start
/// menu shows ("Command Prompt" is shell32.dll string 22022: "Komut İstemi", "Eingabeaufforderung" ...), asked in the
/// application's language and given by Windows in its own language when that language is not installed. Elsewhere, or
/// when Windows only has the English name for another language, the application's translation.
/// </summary>
public static partial class SystemNames
{
    private const string CommandPromptEnglish = "Command Prompt";
    private const string CommandPromptResource = "@%SystemRoot%\\system32\\shell32.dll,-22022";

    private static readonly ConcurrentDictionary<string, string> s_commandPrompt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shown name of cmd.exe in the current language.</summary>
    public static string CommandPrompt()
    {
        var language = Localizer.CurrentLanguage;
        return s_commandPrompt.GetOrAdd(language, l =>
        {
            var system = OperatingSystem.IsWindows() ? WindowsString(CommandPromptResource, l) : null;
            if (string.IsNullOrWhiteSpace(system))
            {
                return Localizer.T(CommandPromptEnglish);
            }
            // Windows without the language: its English name, which the application can translate
            return system == CommandPromptEnglish ? Localizer.T(CommandPromptEnglish) : system;
        });
    }

    /// <summary>An indirect resource string ("@file,-id") in <paramref name="language"/> when Windows has it; null when it cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? WindowsString(string resource, string language)
    {
        string? result = null;
        // a thread of its own: its preferred UI language is set for the resource lookup only
        var thread = new Thread(() =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                var name = culture.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(language).Name : culture.Name;
                if (name.Length > 0)
                {
                    _ = SetThreadPreferredUILanguages(MuiLanguageName, name + "\0\0", IntPtr.Zero);
                }
            }
            catch (CultureNotFoundException)
            {
                // the system's language then
            }
            var buffer = new char[512];
            if (SHLoadIndirectString(Environment.ExpandEnvironmentVariables(resource), buffer, (uint)buffer.Length, IntPtr.Zero) == 0)
            {
                var end = Array.IndexOf(buffer, '\0');
                result = new string(buffer, 0, end < 0 ? buffer.Length : end);
            }
        }) { IsBackground = true };
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(2)) ? result : null;
    }

    private const uint MuiLanguageName = 0x8;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPreferredUILanguages(uint flags, string languages, IntPtr count);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, [Out] char[] output, uint outputSize, IntPtr reserved);
}
