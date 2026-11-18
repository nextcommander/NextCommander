using System.Diagnostics;

namespace NextCommander.Platform.Mtp.LibMtp;

/// <summary>
/// macOS's camera service <c>ptpcamerad</c> (Image Capture) takes every phone and camera it sees: as long as it holds
/// the device's USB interface, libmtp cannot open it ("The device could not be opened", user's report of a Samsung
/// phone, 18 November 2026). An Android phone connects again after "Allow access to phone data?", and the service
/// takes it again at once. When libmtp cannot open a device, the service (the user's own process; launchd starts it
/// again when an application needs it) is stopped and the device opened again, a few times.
/// </summary>
internal static class MacImageCapture
{
    /// <summary>The processes that take phones and cameras on macOS.</summary>
    internal static readonly string[] Services = ["ptpcamerad"];

    /// <summary>Stops the service; true when one was running (and could be stopped).</summary>
    public static bool Release()
    {
        var stopped = false;
        foreach (var name in Services)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        stopped = true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // gone meanwhile, or not ours
                    }
                }
            }
        }
        return stopped;
    }

    /// <summary>How many times a device is opened again after the service was stopped, and how long it is waited for first.</summary>
    internal const int Attempts = 4;

    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Opens a device (<paramref name="open"/>: zero when it cannot); when it cannot and <paramref name="release"/> stopped
    /// the service holding it, opened again a few times (stopping the service again before each: it comes back).
    /// </summary>
    internal static IntPtr OpenReleasing(Func<IntPtr> open, Func<bool> release, Action<TimeSpan> wait)
    {
        var device = open();
        for (var attempt = 0; device == IntPtr.Zero && attempt < Attempts; attempt++)
        {
            if (!release() && attempt > 0)
            {
                break; // nothing holds it any more: another reason
            }
            wait(Delay);
            device = open();
        }
        return device;
    }
}
