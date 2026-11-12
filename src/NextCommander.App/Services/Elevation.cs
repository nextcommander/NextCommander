using System.Text.Json;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Local;

namespace NextCommander.App.Services;

/// <summary>What the elevated helper does: one file operation on local paths.</summary>
public enum ElevatedOperation
{
    Copy,
    Move,
    Delete,
    Trash,
    CreateFolder,
    Rename,
}

/// <summary>A file operation for the elevated helper (written as JSON next to its result).</summary>
public sealed record ElevatedRequest(ElevatedOperation Operation, IReadOnlyList<string> Sources, string? Target = null, string? NewName = null);

/// <summary>
/// Operations the user's rights do not allow (a protected folder, a file of another user, a system file): with the user's
/// consent the application starts itself once more with administrator / root rights — through the system's own dialog
/// (UAC on Windows, polkit on Linux, the authorization dialog on macOS) — does only that operation without a window, and
/// reports back.
/// </summary>
public static class Elevation
{
    /// <summary>The command line switch of the helper: <c>--elevated request.json result.json</c>.</summary>
    public const string Switch = "--elevated";

    /// <summary>Whether an error means missing rights.</summary>
    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException io && ((io.HResult & 0xFFFF) == 5 || io.Message.Contains("ermission denied", StringComparison.Ordinal)));

    /// <summary>
    /// Runs <paramref name="request"/> with administrator rights; null when the user refused the system's dialog (or it is
    /// not available), otherwise the errors (empty: done).
    /// </summary>
    public static async Task<IReadOnlyList<string>?> RunAsync(IPlatformService platform, ElevatedRequest request)
    {
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program))
        {
            return null;
        }
        var folder = Path.Combine(Path.GetTempPath(), "NextCommander-elevated");
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString("N")[..12];
        var requestFile = Path.Combine(folder, id + ".json");
        var resultFile = Path.Combine(folder, id + ".result.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request));
        try
        {
            // a "dotnet NextCommander.dll" run has no own executable: the host runs the dll
            var arguments = new List<string>();
            if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && AppRestart.ProgramDll() is { } dll)
            {
                arguments.Add(dll);
            }
            arguments.AddRange([Switch, requestFile, resultFile]);
            if (!await platform.RunElevatedAsync(program, arguments))
            {
                return null;
            }
            return File.Exists(resultFile)
                ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(resultFile)) ?? []
                : ["The operation with administrator rights did not report back."];
        }
        finally
        {
            foreach (var file in new[] { requestFile, resultFile })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // left in the temp folder
                }
            }
        }
    }

    /// <summary>The helper itself (started with administrator rights): does the request and writes the errors; returns the exit code.</summary>
    public static int Serve(string requestFile, string resultFile)
    {
        var errors = new List<string>();
        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestFile))
                ?? throw new InvalidDataException("Empty request.");
            errors.AddRange(Task.Run(() => Execute(request)).GetAwaiter().GetResult()); // no synchronization context to wait on
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            errors.Add(ex.Message);
        }
        try
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(errors));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 2;
        }
        return errors.Count == 0 ? 0 : 1;
    }

    private static async Task<IReadOnlyList<string>> Execute(ElevatedRequest request)
    {
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local]);
        var sources = new List<VfsEntry>();
        foreach (var path in request.Sources)
        {
            if (await local.GetEntryAsync(VfsPath.Local(path), CancellationToken.None) is { } entry)
            {
                sources.Add(entry);
            }
        }
        switch (request.Operation)
        {
            case ElevatedOperation.CreateFolder:
                await local.CreateDirectoryAsync(VfsPath.Local(request.Target!), CancellationToken.None);
                return [];
            case ElevatedOperation.Rename when sources.Count == 1 && request.NewName is { Length: > 0 } name:
                var parent = sources[0].Path.Parent!;
                await local.MoveAsync(sources[0].Path, parent.Combine(name), overwrite: false, CancellationToken.None);
                return [];
            case ElevatedOperation.Copy or ElevatedOperation.Move when request.Target is not null:
                var copy = new CopyMoveJob(registry, sources, VfsPath.Local(request.Target), request.Operation == ElevatedOperation.Move,
                    new CopyOptions { ConflictPolicy = ConflictPolicy.Overwrite });
                await copy.RunAsync();
                return [.. copy.Errors.Select(e => $"{e.Item}: {e.Message}")];
            case ElevatedOperation.Delete or ElevatedOperation.Trash:
                var delete = new DeleteJob(registry, sources, request.Operation == ElevatedOperation.Trash ? PlatformServiceFactory.Create() : null);
                await delete.RunAsync();
                return [.. delete.Errors.Select(e => $"{e.Item}: {e.Message}")];
            default:
                return ["Unknown operation."];
        }
    }
}
