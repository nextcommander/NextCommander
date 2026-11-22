namespace NextCommander.App.Tests;

/// <summary>The program holding a file, through Windows' Restart Manager.</summary>
public sealed class FileLocksTests
{
    [Fact]
    public void The_program_that_has_a_file_open_is_named()
    {
        var path = Path.Combine(Path.GetTempPath(), "nc-lock-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            using (var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var names = NextCommander.Platform.Windows.FileLocks.WhoLocks(path);
                if (OperatingSystem.IsWindows())
                {
                    Assert.NotEmpty(names); // this test's own process
                }
                else
                {
                    Assert.Empty(names);
                }
            }
            Assert.Empty(NextCommander.Platform.Windows.FileLocks.WhoLocks(path)); // closed: nobody
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Dragging files out with the shell's own data object (what Explorer drags; Teams takes only that).</summary>
public sealed class ShellDragTests
{
    [Fact]
    public void The_shell_data_object_offers_the_file_list()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nc-drag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var a = Path.Combine(folder, "a.txt");
            var b = Path.Combine(folder, "b c.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            Assert.Equal(OperatingSystem.IsWindows(), NextCommander.Platform.Windows.ShellDrag.OffersFileList([a, b]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
