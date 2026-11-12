namespace NextCommander.Platform;

public static class PlatformServiceFactory
{
    public static IPlatformService Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Windows.WindowsPlatformService();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacOS.MacPlatformService();
        }
        return new Linux.LinuxPlatformService();
    }
}
