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
        foreach (var name in new[] { "git", "rclone", "sqlite", "docker", "diskimage" })
        {
            var plugin = Assert.Single(plugins, p => p.Name == name);
            Assert.True(plugin.Error is null, $"{name}: {plugin.Error}");
        }

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
    }
}
