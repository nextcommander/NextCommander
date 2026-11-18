using System.Diagnostics;
using System.Runtime.InteropServices;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Platform;

/// <summary>
/// Opens an RDP connection in the system's client: Remote Desktop Connection (mstsc) on Windows, the app that opens
/// .rdp files on macOS (Microsoft's Windows App), FreeRDP or Remmina on Linux. A saved password goes to Windows'
/// credential store for this logon session only (as mstsc's own "remember me" would, but not kept) or to FreeRDP's
/// standard input; it is never put on a command line.
/// </summary>
public static partial class RemoteDesktopLauncher
{
    /// <summary>Whether the client takes a password from NextCommander (macOS's client always asks itself).</summary>
    public static bool TakesPassword => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Whether a password must be given (FreeRDP started without a terminal cannot ask for one).</summary>
    public static bool NeedsPassword => OperatingSystem.IsLinux() && FindProgram(RemoteDesktop.FreeRdpPrograms) is not null;

    /// <summary>Starts the client; an <see cref="InvalidOperationException"/> says what to install when there is none.</summary>
    /// <param name="folder">The active panel's folder, shared in the session when the connection says so.</param>
    public static async Task LaunchAsync(RemoteConnection connection, string? password, string? folder = null)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrEmpty(password))
            {
                StoreWindowsCredential(connection.Host, connection.User, password);
            }
            var file = WriteRdpFile(connection, folder);
            Start(new ProcessStartInfo("mstsc.exe") { ArgumentList = { file }, UseShellExecute = false });
            DeleteLater(file);
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var file = WriteRdpFile(connection);
            using var open = Start(new ProcessStartInfo("open") { ArgumentList = { file }, UseShellExecute = false, RedirectStandardError = true });
            await open.WaitForExitAsync().ConfigureAwait(false);
            if (open.ExitCode != 0)
            {
                File.Delete(file);
                throw new InvalidOperationException("No program opens remote desktop (.rdp) files. Install Microsoft's Windows App from the App Store.");
            }
            DeleteLater(file);
            return;
        }
        if (FindProgram(RemoteDesktop.FreeRdpPrograms) is { } freeRdp)
        {
            var stdin = !string.IsNullOrEmpty(password);
            var info = new ProcessStartInfo(freeRdp) { UseShellExecute = false, RedirectStandardInput = stdin };
            foreach (var arg in RemoteDesktop.FreeRdpArguments(connection, stdin, folder))
            {
                info.ArgumentList.Add(arg);
            }
            var process = Start(info);
            if (stdin)
            {
                await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            process.Dispose();
            return;
        }
        if (FindProgram(["remmina"]) is { } remmina)
        {
            Start(new ProcessStartInfo(remmina) { ArgumentList = { "-c", RemoteDesktop.RemminaAddress(connection) }, UseShellExecute = false }).Dispose();
            return;
        }
        throw new InvalidOperationException("No remote desktop client found. Install FreeRDP (the freerdp3-x11 or freerdp2-x11 package) or Remmina.");
    }

    /// <summary>The FreeRDP that can draw into another program's window (the X11 client: /parent-window), or null.</summary>
    public static string? EmbeddingFreeRdp => OperatingSystem.IsLinux() ? FindProgram(["xfreerdp3", "xfreerdp"]) : null;

    /// <summary>
    /// Whether a session can be shown in a tab of NextCommander: Windows' Remote Desktop control, or the X11 FreeRDP on
    /// Linux. macOS has neither (its client opens a window of its own).
    /// </summary>
    public static bool CanEmbed => OperatingSystem.IsWindows() ? Windows.RemoteDesktopControl.IsAvailable : EmbeddingFreeRdp is not null;

    /// <summary>
    /// Linux: FreeRDP drawing the session into the X11 window <paramref name="window"/> of <paramref name="width"/> ×
    /// <paramref name="height"/> pixels; the password (when given) goes to its standard input.
    /// </summary>
    public static async Task<Process> StartEmbeddedAsync(RemoteConnection connection, string? password, string? folder, ulong window, int width, int height)
    {
        var freeRdp = EmbeddingFreeRdp ?? throw new InvalidOperationException("No remote desktop client found. Install FreeRDP (the freerdp3-x11 or freerdp2-x11 package).");
        var stdin = !string.IsNullOrEmpty(password);
        var info = new ProcessStartInfo(freeRdp) { UseShellExecute = false, RedirectStandardInput = stdin };
        foreach (var arg in RemoteDesktop.EmbeddedFreeRdpArguments(connection, stdin, folder, window, width, height))
        {
            info.ArgumentList.Add(arg);
        }
        var process = Start(info);
        if (stdin)
        {
            await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        return process;
    }

    private static Process Start(ProcessStartInfo info) =>
        Process.Start(info) ?? throw new InvalidOperationException("The remote desktop client did not start: " + info.FileName);

    private static string WriteRdpFile(RemoteConnection connection, string? shared = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "NextCommander-rdp");
        Directory.CreateDirectory(folder);
        var name = string.Concat(connection.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        var file = Path.Combine(folder, (name.Length > 0 ? name : connection.Host) + ".rdp");
        File.WriteAllText(file, RemoteDesktop.RdpFile(connection, shared));
        return file;
    }

    /// <summary>The client reads the file when it starts; it is removed a minute later.</summary>
    private static void DeleteLater(string file) => _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ =>
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }, TaskScheduler.Default);

    /// <summary>The full path of the first program found on the PATH.</summary>
    public static string? FindProgram(IEnumerable<string> names)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- Windows credentials

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    private const int CredTypeDomainPassword = 2;
    private const int CredPersistSession = 1;

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, int flags);

    /// <summary>The password for TERMSRV/host, as mstsc looks it up; kept until the user logs off.</summary>
    private static void StoreWindowsCredential(string host, string user, string password)
    {
        var target = Marshal.StringToHGlobalUni("TERMSRV/" + host);
        var userName = Marshal.StringToHGlobalUni(user);
        var blob = Marshal.StringToHGlobalUni(password);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeDomainPassword,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = password.Length * 2,
                Persist = CredPersistSession,
            };
            CredWrite(credential, 0); // without it mstsc asks for the password
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(blob);
            Marshal.FreeHGlobal(userName);
            Marshal.FreeHGlobal(target);
        }
    }
}
