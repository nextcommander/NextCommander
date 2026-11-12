using NextCommander.App.ViewModels;

namespace NextCommander.App.Services.Icons;

/// <summary>Loads operating-system icons for rows asynchronously (sets <see cref="FileItemViewModel.SystemIcon"/>).</summary>
public interface IFileIconService
{
    void Request(IReadOnlyList<FileItemViewModel> items);
}

/// <summary>No OS icons: the built-in type icons are used.</summary>
public sealed class BuiltInIconService : IFileIconService
{
    public void Request(IReadOnlyList<FileItemViewModel> items)
    {
    }
}

public static class FileIconServiceFactory
{
    public static IFileIconService Create() =>
        OperatingSystem.IsWindows() ? new WindowsShellIconService() : new BuiltInIconService();
}
