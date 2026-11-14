using System.Runtime.InteropServices;

namespace NextCommander.Platform.Windows;

/// <summary>
/// The USB serial number of a portable device, to know a phone seen both over MTP and over ADB. With USB debugging on,
/// the phone is a composite device: the MTP interface's id ("\\?\usb#vid_04e8&amp;pid_6860&amp;mi_00#6&amp;1a2b&amp;0&amp;0000#{guid}")
/// has no serial; its parent ("USB\VID_04E8&amp;PID_6860\R5CX15QTV5J") has, as the last part of its id.
/// </summary>
internal static partial class UsbSerials
{
    /// <summary>"\\?\usb#vid_x&amp;pid_y&amp;mi_00#inst#{guid}" → "USB\VID_X&amp;PID_Y&amp;MI_00\INST"; null for other ids.</summary>
    public static string? InstanceIdOf(string interfacePath)
    {
        var path = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? interfacePath[4..] : interfacePath;
        var parts = path.Split('#');
        return parts.Length >= 3 && parts[0].Length > 0 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

    /// <summary>The serial in a USB device instance id: its last part, unless Windows made it up (it then holds '&amp;').</summary>
    public static string? SerialOf(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
            && !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) && !parts[2].Contains('&', StringComparison.Ordinal)
            ? parts[2] : null;
    }

    /// <summary>The serial of the USB device behind a portable device's interface (its own id or its composite parent's).</summary>
    public static string? Of(string interfacePath)
    {
        if (!OperatingSystem.IsWindows() || InstanceIdOf(interfacePath) is not { } instance)
        {
            return null;
        }
        if (SerialOf(instance) is { } own)
        {
            return own;
        }
        try
        {
            if (CM_Locate_DevNodeW(out var node, instance, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0)
            {
                return null;
            }
            var buffer = new char[256];
            return CM_Get_Device_IDW(parent, buffer, (uint)buffer.Length, 0) == 0 ? SerialOf(new string(buffer).TrimEnd('\0')) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_IDW(uint node, [Out] char[] buffer, uint length, uint flags);
}
