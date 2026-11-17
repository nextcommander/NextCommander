using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NextCommander.App.Services.Terminal;

/// <summary>
/// A shell on a pseudo terminal: what it writes arrives through <see cref="Output"/> (with the escape sequences of a
/// real terminal), <see cref="Write"/> sends key presses, <see cref="Resize"/> tells it the window size. Windows:
/// ConPTY (Windows 10 1809 and later); Linux and macOS: a pty pair with the shell started on its secondary side.
/// </summary>
public abstract class PseudoTerminal : IDisposable
{
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private int _exited;

    /// <summary>Text written by the shell, on a worker thread.</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended (exit code), on a worker thread.</summary>
    public event Action<int>? Exited;

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    /// <summary>Starts <paramref name="program"/> with <paramref name="arguments"/> in <paramref name="directory"/>.</summary>
    public static PseudoTerminal Start(string program, string arguments, string directory, int columns, int rows)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPseudoTerminal.Create(program, arguments, directory, columns, rows);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return UnixPseudoTerminal.Create(program, arguments, directory, columns, rows);
        }
        throw new PlatformNotSupportedException("There is no pseudo terminal on this system.");
    }

    /// <summary>Whether a pseudo terminal can be made here (Windows: ConPTY in kernel32).</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() ? WindowsPseudoTerminal.IsAvailable : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    /// <summary>Called once the events are hooked: a terminal that works in the background (Telnet) starts then.</summary>
    public virtual void Open()
    {
    }

    public abstract void Write(byte[] data);

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public abstract void Resize(int columns, int rows);

    protected void RaiseOutput(byte[] buffer, int count)
    {
        var chars = new char[_decoder.GetCharCount(buffer, 0, count)];
        var length = _decoder.GetChars(buffer, 0, count, chars, 0);
        if (length > 0)
        {
            Output?.Invoke(new string(chars, 0, length));
        }
    }

    protected void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0)
        {
            Exited?.Invoke(code);
        }
    }

    public abstract void Dispose();

    /// <summary>One command line argument quoted for Windows (CommandLineToArgvW rules).</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
        {
            return argument;
        }
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            result.Append(c);
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    /// <summary>Splits an argument string like a shell does with spaces and double quotes (for Unix exec).</summary>
    public static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}

/// <summary>ConPTY: CreatePseudoConsole with two pipes, the shell started with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPseudoTerminal : PseudoTerminal
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _console;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly object _lock = new();
    private bool _disposed;

    private WindowsPseudoTerminal(IntPtr console, IntPtr process, IntPtr thread, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        _thread = thread;
        _input = input;
        _output = output;
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return NativeLibrary.TryLoad("kernel32.dll", out var kernel) && NativeLibrary.TryGetExport(kernel, "CreatePseudoConsole", out _);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    public static WindowsPseudoTerminal Create(string program, string arguments, string directory, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var hr = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out var console);
        // the pseudo console has its own copies of these ends
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr);
        }
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // STARTF_USESTDHANDLES with no handles: the shell uses the pseudo console, not our (possibly redirected) standard handles
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = 0x00000100 },
                lpAttributeList = attributes,
            };
            var commandLine = (Quote(program) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments) + "\0").ToCharArray();
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero,
                Directory.Exists(directory) ? directory : null, ref startup, out var info))
            {
                var error = Marshal.GetLastWin32Error();
                ClosePseudoConsole(console);
                inputWrite.Dispose();
                outputRead.Dispose();
                throw new Win32Exception(error);
            }
            var terminal = new WindowsPseudoTerminal(console, info.hProcess, info.hThread,
                new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 1));
            terminal.Begin();
            return terminal;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private void Begin()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" }.Start();
        new Thread(() =>
        {
            _ = WaitForSingleObject(_process, Infinite);
            GetExitCodeProcess(_process, out var code);
            // the output pipe ends only when the pseudo console is closed
            lock (_lock)
            {
                if (!_disposed && _console != IntPtr.Zero)
                {
                    ClosePseudoConsole(_console);
                    _console = IntPtr.Zero;
                }
            }
            RaiseExited((int)code);
        }) { IsBackground = true, Name = "ConPTY wait" }.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                var read = _output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                RaiseOutput(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // closed
        }
    }

    public override void Write(byte[] data)
    {
        try
        {
            _input.Write(data, 0, data.Length);
            _input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the shell ended meanwhile
        }
    }

    public override void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            if (!_disposed && _console != IntPtr.Zero)
            {
                _ = ResizePseudoConsole(_console, new Coord((short)Math.Max(2, columns), (short)Math.Max(1, rows)));
            }
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_console != IntPtr.Zero)
            {
                ClosePseudoConsole(_console); // ends the shell too
                _console = IntPtr.Zero;
            }
        }
        if (!HasExited)
        {
            TerminateProcess(_process, 1);
        }
        _input.Dispose();
        _output.Dispose();
        CloseHandle(_thread);
        CloseHandle(_process);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    private static bool UpdateProcThreadAttribute(IntPtr list, uint flags, int attribute, IntPtr value, int size, IntPtr previous, IntPtr returnSize) =>
        UpdateProcThreadAttribute(list, flags, (IntPtr)attribute, value, (IntPtr)size, previous, returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? lpApplicationName, char[] lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Linux / macOS: posix_openpt for the primary side, the shell started with posix_spawn in a new session with the
/// secondary side as its standard input, output and error (so it becomes its controlling terminal on Linux).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
internal sealed class UnixPseudoTerminal : PseudoTerminal
{
    private const int ORdWr = 2;
    private const int SigHup = 1;
    private const int SigKill = 9;

    private readonly int _primary;
    private readonly int _pid;
    private bool _disposed;

    private UnixPseudoTerminal(int primary, int pid)
    {
        _primary = primary;
        _pid = pid;
    }

    private static int ONoCtty => OperatingSystem.IsLinux() ? 0x100 : 0x20000;

    private static ulong TiocSWinSz => OperatingSystem.IsLinux() ? 0x5414UL : 0x80087467UL;

    private static short SpawnSetSid => OperatingSystem.IsLinux() ? (short)0x80 : (short)0x400;

    public static UnixPseudoTerminal Create(string program, string arguments, string directory, int columns, int rows)
    {
        var primary = posix_openpt(ORdWr | ONoCtty);
        if (primary < 0 || grantpt(primary) != 0 || unlockpt(primary) != 0)
        {
            throw new IOException($"Could not open a pseudo terminal (errno {Marshal.GetLastPInvokeError()}).");
        }
        var namePointer = ptsname(primary);
        var secondaryName = Marshal.PtrToStringUTF8(namePointer) ?? throw new IOException("ptsname");
        SetSize(primary, columns, rows);

        // file actions: the secondary side as fd 0, 1 and 2, the working folder; attributes: a new session
        var actions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var argv = new List<IntPtr>();
        var envp = new List<IntPtr>();
        try
        {
            Check(posix_spawn_file_actions_init(actions), "file_actions_init");
            Check(posix_spawnattr_init(attributes), "spawnattr_init");
            Check(posix_spawnattr_setflags(attributes, SpawnSetSid), "setsid");
            var secondary = Marshal.StringToCoTaskMemUTF8(secondaryName);
            argv.Add(secondary);
            Check(posix_spawn_file_actions_addopen(actions, 0, secondary, ORdWr, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "dup2");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "dup2");
            Check(posix_spawn_file_actions_addclose(actions, primary), "addclose");
            if (Directory.Exists(directory))
            {
                try
                {
                    var folder = Marshal.StringToCoTaskMemUTF8(directory);
                    argv.Add(folder);
                    Check(posix_spawn_file_actions_addchdir_np(actions, folder), "addchdir");
                }
                catch (EntryPointNotFoundException)
                {
                    // an old C library: the shell starts in our folder
                }
            }
            var strings = argv.ToList(); // the helper strings, freed at the end
            argv.Clear();
            argv.Add(Marshal.StringToCoTaskMemUTF8(program));
            argv.AddRange(SplitArguments(arguments).Select(Marshal.StringToCoTaskMemUTF8));
            argv.Add(IntPtr.Zero);
            envp.AddRange(strings); // freed with the environment strings
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);
            environment["TERM"] = "xterm-256color";
            environment["COLORTERM"] = "truecolor";
            environment.Remove("COLUMNS");
            environment.Remove("LINES");
            var helpers = envp.Count;
            envp.AddRange(environment.Select(e => Marshal.StringToCoTaskMemUTF8(e.Key + "=" + e.Value)));
            envp.Add(IntPtr.Zero);
            var result = posix_spawnp(out var pid, argv[0], actions, attributes, [.. argv], [.. envp.Skip(helpers)]);
            if (result != 0)
            {
                _ = close(primary);
                throw new IOException($"{program}: could not be started (errno {result}).");
            }
            var terminal = new UnixPseudoTerminal(primary, pid);
            terminal.Begin();
            return terminal;
        }
        finally
        {
            _ = posix_spawn_file_actions_destroy(actions);
            _ = posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
            foreach (var p in argv.Concat(envp).Where(p => p != IntPtr.Zero))
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new IOException($"posix_spawn {what}: {result}");
        }
    }

    private static void SetSize(int fd, int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)Math.Max(1, rows), Columns = (ushort)Math.Max(2, columns) };
        _ = ioctl(fd, TiocSWinSz, ref size);
    }

    private void Begin()
    {
        new Thread(() =>
        {
            var buffer = new byte[16384];
            while (true)
            {
                var read = read_fd(_primary, buffer, buffer.Length);
                if (read <= 0)
                {
                    break; // EIO once the shell (and everything on the terminal) has ended
                }
                RaiseOutput(buffer, (int)read);
            }
        }) { IsBackground = true, Name = "pty output" }.Start();
        new Thread(() =>
        {
            var status = 0;
            while (waitpid(_pid, ref status, 0) < 0 && Marshal.GetLastPInvokeError() == 4)
            {
                // EINTR
            }
            RaiseExited((status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f));
        }) { IsBackground = true, Name = "pty wait" }.Start();
    }

    public override void Write(byte[] data)
    {
        var offset = 0;
        while (offset < data.Length && !_disposed)
        {
            var written = write_fd(_primary, data[offset..], data.Length - offset);
            if (written <= 0)
            {
                return;
            }
            offset += (int)written;
        }
    }

    public override void Resize(int columns, int rows)
    {
        if (!_disposed)
        {
            SetSize(_primary, columns, rows);
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!HasExited)
        {
            _ = kill(_pid, SigHup);
            Thread.Sleep(50);
            if (!HasExited)
            {
                _ = kill(_pid, SigKill);
            }
        }
        _ = close(_primary);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixels;
        public ushort YPixels;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref WinSize size);

    [DllImport("libc", SetLastError = true, EntryPoint = "read")]
    private static extern nint read_fd(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint write_fd(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, ref int status, int options);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, IntPtr path, int flags, int mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, IntPtr path);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(out int pid, IntPtr file, IntPtr actions, IntPtr attributes,
        IntPtr[] argv, IntPtr[] envp);
}
