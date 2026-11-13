using Avalonia.Headless.XUnit;
using NextCommander.App.Services.Icons;
using NextCommander.Platform;

namespace NextCommander.App.Tests;

/// <summary>The icons the operating system gives the user folders (documents, downloads ...).</summary>
public sealed class SystemFolderIconsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-sysicons-" + Guid.NewGuid().ToString("N"));

    public SystemFolderIconsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public void User_folders_are_found_by_path()
    {
        var downloads = KnownFolders.Downloads;
        Assert.Equal(UserFolder.Downloads, KnownFolders.KindOf(downloads));
        Assert.Equal(UserFolder.Downloads, KnownFolders.KindOf(downloads + Path.DirectorySeparatorChar));
        Assert.Equal(UserFolder.Home, KnownFolders.KindOf(KnownFolders.Home));
        Assert.Null(KnownFolders.KindOf(Path.Combine(downloads, "sub")));
        Assert.Null(KnownFolders.KindOf(null));
    }

    [AvaloniaFact]
    public void Windows_gives_the_downloads_folder_its_shell_icon()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(KnownFolders.Downloads))
        {
            return;
        }
        var icon = SystemFolderIcons.Icon(UserFolder.Downloads);
        Assert.NotNull(icon);
        Assert.Same(icon, SystemFolderIcons.ForPath(KnownFolders.Downloads)); // cached
        Assert.Same(icon, DriveIconConverter.IconFor(KnownFolders.Downloads)); // drive bar shortcut
    }

    [AvaloniaFact]
    public void An_svg_icon_of_an_icon_theme_is_drawn_at_the_wanted_size()
    {
        var file = Path.Combine(_dir, "folder-download.svg");
        File.WriteAllText(file, """
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">
              <rect x="8" y="24" width="112" height="88" rx="8" fill="#3d8fd1"/>
              <path d="M64 40v40m-16-16l16 16 16-16" stroke="#fff" stroke-width="8" fill="none"/>
            </svg>
            """);
        var bitmap = SystemFolderIcons.LoadFile(file, 32);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.PixelSize.Width);
        Assert.Equal(32, bitmap.PixelSize.Height);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            bitmap.SavePng(Path.Combine(shots, "theme-folder-download.png"));
        }
    }

    [AvaloniaFact]
    public void Icon_theme_settings_are_read_from_ini_files()
    {
        var file = Path.Combine(_dir, "index.theme");
        File.WriteAllText(file, "[Icon Theme]\nName=Yaru\nInherits=Humanity, hicolor\n\n[16x16/places]\nSize=16\n");
        Assert.Equal("Humanity, hicolor", FreedesktopIconTheme.IniValue(file, "Icon Theme", "Inherits"));
        Assert.Null(FreedesktopIconTheme.IniValue(file, "Icon Theme", "Size"));
        Assert.Equal("16", FreedesktopIconTheme.IniValue(file, "16x16/places", "Size"));
    }
}
