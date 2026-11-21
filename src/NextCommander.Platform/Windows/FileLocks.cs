using System.Runtime.InteropServices;

namespace NextCommander.Platform.Windows;

/// <summary>
/// The programs that have a file open, through Windows' Restart Manager (the error dialog names them: "Open in
/// Microsoft Excel"). Empty elsewhere, or when Windows does not know.
/// </summary>
public static partial class FileLocks
{
    private const int CchRmSessionKey = 32;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public long StartTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        public fixed char AppName[256];
        public fixed char ServiceShortName[64];
        public int ApplicationType;
        public int AppStatus;
        public int TsSessionId;
        public int Restartable;
    }

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmStartSession")]
    private static unsafe partial int RmStartSession(out uint session, int flags, char* key);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmEndSession")]
    private static partial int RmEndSession(uint session);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmRegisterResources", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, IntPtr processes, uint services, IntPtr serviceNames);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmGetList")]
    private static unsafe partial int RmGetList(uint session, out uint needed, ref uint count, RmProcessInfo* info, out uint rebootReasons);

    /// <summary>The names of the programs that have <paramref name="path"/> open (none when it cannot be told).</summary>
    public static IReadOnlyList<string> WhoLocks(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path))
        {
            return [];
        }
        try
        {
            return Query(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    private static unsafe List<string> Query(string path)
    {
        var key = stackalloc char[CchRmSessionKey + 1];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
            {
                return [];
            }
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0)
            {
                return [];
            }
            var infos = new RmProcessInfo[needed];
            count = needed;
            fixed (RmProcessInfo* first = infos)
            {
                if (RmGetList(session, out _, ref count, first, out _) != 0)
                {
                    return [];
                }
                var names = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = new string(first[i].AppName);
                    if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                return names;
            }
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }
}
