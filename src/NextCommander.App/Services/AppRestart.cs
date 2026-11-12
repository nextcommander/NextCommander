using System.Diagnostics;

namespace NextCommander.App.Services;

/// <summary>
/// Starts the program again after this process ends (imported settings): the new process gets
/// <c>--restart-after &lt;pid&gt;</c> and waits for this one to exit before it takes the single-window lock.
/// </summary>
public static class AppRestart
{
    public const string Switch = "--restart-after";

    /// <summary>Starts the new process (it waits for this one); false when the program file is not known.</summary>
    public static bool Start()
    {
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program))
        {
            return false;
        }
        var info = new ProcessStartInfo(program) { UseShellExecute = false };
        // a "dotnet NextCommander.dll" run has no own executable: the host runs the dll
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && ProgramDll() is { } dll)
        {
            info.ArgumentList.Add(dll);
        }
        info.ArgumentList.Add(Switch);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            return Process.Start(info) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Could not start the program again");
            return false;
        }
    }

    /// <summary>
    /// The program's dll for a "dotnet NextCommander.dll" run (development), or null. From the program folder, not from
    /// Assembly.Location: that is empty in the single-file published program.
    /// </summary>
    public static string? ProgramDll()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "NextCommander.dll");
        return File.Exists(dll) ? dll : null;
    }

    /// <summary>In the new process: waits (at most 30 s) until the old one has ended and returns the remaining arguments.</summary>
    public static string[] WaitForPrevious(string[] args)
    {
        if (args.Length < 2 || args[0] != Switch)
        {
            return args;
        }
        if (int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // already gone
            }
            catch (InvalidOperationException)
            {
            }
        }
        return args[2..];
    }
}
