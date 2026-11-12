using NextCommander.App.ViewModels;

namespace NextCommander.App.Services.Icons;

/// <summary>Loads operating-system icons for rows asynchronously (sets <see cref="FileItemViewModel.SystemIcon"/>).</summary>
public interface IFileIconService
{
    void Request(IReadOnlyList<FileItemViewModel> items);
}

/// <summary>
/// The built-in type icons, except for the user folders of the system (home, documents, downloads ...), which get the
/// icon the operating system gives them (Linux icon theme, macOS Finder).
/// </summary>
public sealed class BuiltInIconService : IFileIconService
{
    public void Request(IReadOnlyList<FileItemViewModel> items)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        foreach (var item in items)
        {
            if (item.Entry is { IsDirectory: true } entry && entry.Path.IsLocal && SystemFolderIcons.ForPath(entry.Path.Path) is { } icon)
            {
                item.SystemIcon = icon;
            }
        }
    }
}

public static class FileIconServiceFactory
{
    public static IFileIconService Create() =>
        OperatingSystem.IsWindows() ? new WindowsShellIconService() : new BuiltInIconService();
}
