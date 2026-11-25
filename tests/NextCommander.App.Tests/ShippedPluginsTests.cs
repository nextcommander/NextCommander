using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Tests;

/// <summary>
/// The plugins shipped in plugins/ next to the program load as the program loads them (each in its own load context),
/// with the packages a plugin brings itself (the disk image plugin's DiscUtils) found in its own folder.
/// </summary>
public sealed class ShippedPluginsTests
{
    [Fact]
    public async Task Every_shipped_plugin_loads_with_its_own_packages()
    {
        var plugins = PluginLoader.LoadAll([Path.Combine(AppContext.BaseDirectory, "plugins")]);
        foreach (var name in new[] { "git", "rclone", "sqlite", "docker", "kubernetes", "diskimage", "viewers", "packages", "repositories" })
        {
            var plugin = Assert.Single(plugins, p => p.Name == name);
            Assert.True(plugin.Error is null, $"{name}: {plugin.Error}");
        }

        // the plugins with settings (their program ...) bring them; the disk image plugin has none
        foreach (var (name, title) in new[]
            { ("git", "Git"), ("rclone", "rclone"), ("sqlite", "SQLite"), ("docker", "Docker"), ("kubernetes", "Kubernetes"),
              ("repositories", "GitHub / GitLab repositories") })
        {
            Assert.Equal(title, Assert.Single(plugins.Single(p => p.Name == name).Configurables).SettingsTitle);
        }
        Assert.Empty(plugins.Single(p => p.Name == "diskimage").Configurables);
        Assert.Equal(10, plugins.Single(p => p.Name == "viewers").Viewers.Count);

        // the viewers plugin reaches the program's MetadataExtractor (not a copy of its own)
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", "viewers", "MetadataExtractor.dll")));
        var pictures = Assert.Single(plugins.Single(p => p.Name == "viewers").Viewers, v => v.Name == "Picture metadata");
        var photo = await pictures.ViewAsync(new MemoryStream(ViewersPluginTests.PhotoWithExif()), "photo.jpg", dark: false, CancellationToken.None);
        Assert.Contains("Canon EOS R5", photo.Html, StringComparison.Ordinal);
        Assert.Equal(["github", "gitlab"], plugins.Single(p => p.Name == "repositories").FileSystems.Select(f => f.Scheme).Order(StringComparer.Ordinal));

        // the disk image plugin reaches DiscUtils (from its folder): a file that is no disk is said to be one it cannot read
        var disks = Assert.Single(plugins.Single(p => p.Name == "diskimage").FileSystems, f => f.Scheme == "vdisk");
        var file = Path.Combine(Path.GetTempPath(), "nc-shipped-" + Guid.NewGuid().ToString("N")[..8] + ".vhdx");
        await File.WriteAllTextAsync(file, "not a disk");
        try
        {
            var root = ((IContainerProvider)disks).GetRoot(VfsPath.Local(file));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => disks.ListAsync(root, CancellationToken.None));
            Assert.Contains("not a readable virtual disk", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }

        // the packages plugin (the program's SharpCompress shared with it): its file system and its viewer
        var packages = plugins.Single(p => p.Name == "packages");
        Assert.Single(packages.Viewers);
        var pkg = Assert.Single(packages.FileSystems, f => f.Scheme == "pkg");
        var deb = Path.Combine(Path.GetTempPath(), "nc-shipped-" + Guid.NewGuid().ToString("N")[..8] + ".deb");
        await File.WriteAllTextAsync(deb, "not a package");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => pkg.ListAsync(((IContainerProvider)pkg).GetRoot(VfsPath.Local(deb)), CancellationToken.None));
            Assert.Contains("not a readable package", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(deb);
        }
    }
}
