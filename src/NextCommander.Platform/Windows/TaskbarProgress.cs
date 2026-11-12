using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NextCommander.Platform.Windows;

/// <summary>Progress in the Windows taskbar button (ITaskbarList3), like Explorer while copying.</summary>
[SupportedOSPlatform("windows")]
internal static class TaskbarProgress
{
    private const int TbpfNoProgress = 0;
    private const int TbpfNormal = 0x2;
    private const int TbpfPaused = 0x8;

    private static ITaskbarList3? s_taskbar;
    private static bool s_failed;

    public static void Set(IntPtr hwnd, double? fraction, bool paused)
    {
        if (hwnd == IntPtr.Zero || s_failed)
        {
            return;
        }
        try
        {
            if (s_taskbar is null)
            {
                s_taskbar = (ITaskbarList3)new TaskbarList();
                s_taskbar.HrInit();
            }
            if (fraction is not { } f)
            {
                s_taskbar.SetProgressState(hwnd, TbpfNoProgress);
                return;
            }
            s_taskbar.SetProgressState(hwnd, paused ? TbpfPaused : TbpfNormal);
            s_taskbar.SetProgressValue(hwnd, (ulong)Math.Round(Math.Clamp(f, 0, 1) * 1000), 1000);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            s_failed = true; // no taskbar (Server Core, some shells): stop trying
        }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    /// <summary>Only the methods up to SetProgressState are declared; the vtable order must match the SDK.</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

        void SetProgressState(IntPtr hwnd, int flags);
    }
}
