using System.Diagnostics;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Platform;

/// <summary>
/// Opens a VNC connection in a viewer of the system: a vncviewer on the PATH (TigerVNC, UltraVNC, TurboVNC), RealVNC
/// or TightVNC (also where Windows installs them), macOS's Screen Sharing through a vnc:// address, or Remmina / KRDC
/// on Linux. The viewer asks for the password itself.
/// </summary>
public static class VncLauncher
{
    /// <summary>The viewer that would be started: its program and kind; null when there is none.</summary>
    public static (string Program, VncViewerKind Kind)? Find()
    {
        foreach (var (program, kind) in VncViewer.Programs)
        {
            if (RemoteDesktopLauncher.FindProgram([program]) is { } found)
            {
                return (found, kind);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                foreach (var (path, kind) in VncViewer.WindowsInstalls)
                {
                    var candidate = Path.Combine(root, path);
                    if (root.Length > 0 && File.Exists(candidate))
                    {
                        return (candidate, kind);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>The arguments for a found viewer (Remmina takes the address after -c).</summary>
    public static IReadOnlyList<string> ArgumentsFor(string program, VncViewerKind kind, RemoteConnection connection)
    {
        var args = VncViewer.Arguments(kind, connection);
        if (Path.GetFileNameWithoutExtension(program).Equals("remmina", StringComparison.OrdinalIgnoreCase))
        {
            return connection.FullScreen ? ["--enable-fullscreen", "-c", .. args] : ["-c", .. args];
        }
        return args;
    }

    /// <summary>Starts the viewer; an <see cref="InvalidOperationException"/> says what to install when there is none.</summary>
    public static async Task LaunchAsync(RemoteConnection connection)
    {
        if (Find() is { } viewer)
        {
            var info = new ProcessStartInfo(viewer.Program) { UseShellExecute = false };
            foreach (var arg in ArgumentsFor(viewer.Program, viewer.Kind, connection))
            {
                info.ArgumentList.Add(arg);
            }
            (Process.Start(info) ?? throw new InvalidOperationException("The VNC viewer did not start: " + viewer.Program)).Dispose();
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            // Screen Sharing opens vnc:// addresses
            using var open = Process.Start(new ProcessStartInfo("open") { ArgumentList = { VncViewer.Url(connection) }, UseShellExecute = false, RedirectStandardError = true })
                ?? throw new InvalidOperationException("Screen Sharing did not start.");
            await open.WaitForExitAsync().ConfigureAwait(false);
            if (open.ExitCode == 0)
            {
                return;
            }
        }
        throw new InvalidOperationException(OperatingSystem.IsWindows()
            ? "No VNC viewer found. Install TigerVNC, RealVNC Viewer, TightVNC or UltraVNC."
            : OperatingSystem.IsMacOS()
                ? "No program opens VNC addresses. Install TigerVNC or RealVNC Viewer."
                : "No VNC viewer found. Install TigerVNC (tigervnc-viewer), Remmina or KRDC.");
    }
}
