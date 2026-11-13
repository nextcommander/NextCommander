using System.Text.Json;

namespace NextCommander.App.Tests;

/// <summary>
/// The native libraries of SkiaSharp and HarfBuzzSharp must be the version of their managed libraries on every system.
/// A package needing a newer SkiaSharp (Svg.Skia 5.2) raised the managed one and the Windows / macOS natives that come
/// with it, but the Linux native stayed on Avalonia's version: the app crashed at start on Linux only. Checked here from
/// the App's restore result, so it fails on Windows too.
/// </summary>
public sealed class NativeAssetsTests
{
    [Fact]
    public void Native_libraries_match_the_managed_ones_on_every_system()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Packages.props")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }
        var assets = Path.Combine(root, "src", "NextCommander.App", "obj", "project.assets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var versions = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name.Split('/'))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

        foreach (var family in new[] { "SkiaSharp", "HarfBuzzSharp" })
        {
            var managed = versions[family];
            foreach (var system in new[] { "Linux", "macOS", "Win32" })
            {
                var native = $"{family}.NativeAssets.{system}";
                Assert.True(versions.TryGetValue(native, out var version) && version == managed,
                    $"{native} is {version ?? "missing"}, {family} is {managed}: the app would not start on {system}.");
            }
        }
    }
}
