using System.Text.Json;
using LibVLCSharp.Shared;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// Audio / video playback through the VLC installed on the system (not bundled: it would add ~150 MB). Found in
/// FM_LIBVLC, the VLC install folder (Windows, macOS) or the system library path (Linux).
/// </summary>
public static class MediaSupport
{
    private static readonly Lazy<LibVLC?> s_libVlc = new(Create);

    /// <summary>Why playback is unavailable (null when it works or was not tried yet).</summary>
    public static string? Error { get; private set; }

    /// <summary>Disables playback (tests, headless).</summary>
    public static bool Disabled { get; set; }

    public static LibVLC? LibVlc => Disabled ? null : s_libVlc.Value;

    public static bool IsAvailable => LibVlc is not null;

    /// <summary>Resume positions (set at startup next to the settings).</summary>
    public static MediaPositions? Positions { get; set; }

    /// <summary>Folder that holds libvlc for this platform, or null to let the system loader search.</summary>
    public static string? FindLibVlcDirectory()
    {
        if (Environment.GetEnvironmentVariable("FM_LIBVLC") is { Length: > 0 } env && Directory.Exists(env))
        {
            return env;
        }
        IEnumerable<string> candidates = [];
        if (OperatingSystem.IsWindows())
        {
            candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
                Path.Combine(AppContext.BaseDirectory, "libvlc", Environment.Is64BitProcess ? "win-x64" : "win-x86"),
            ];
            return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "libvlc.dll")));
        }
        if (OperatingSystem.IsMacOS())
        {
            candidates = ["/Applications/VLC.app/Contents/MacOS/lib", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/VLC.app/Contents/MacOS/lib")];
            return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "libvlc.dylib")));
        }
        return null; // Linux: libvlc.so.5 from the package manager
    }

    private static LibVLC? Create()
    {
        try
        {
            var directory = FindLibVlcDirectory();
            if (directory is null && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
            {
                Error = "VLC not found. Install VLC (videolan.org) to play audio and video, or point FM_LIBVLC to the libvlc folder.";
                return null;
            }
            LibVLCSharp.Shared.Core.Initialize(directory);
            return new LibVLC("--no-video-title-show", "--quiet");
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            Error = "Could not load VLC: " + ex.Message;
            Log.Information(ex, "libvlc not available");
            return null;
        }
    }
}

/// <summary>Where playback stopped per file, so the Lister can continue there (at most 200 files).</summary>
public sealed class MediaPositions
{
    private const int MaxEntries = 200;
    private readonly string _file;
    private readonly Dictionary<string, (double Seconds, DateTime When)> _positions = new(StringComparer.Ordinal);

    public MediaPositions(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file))
            {
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(file)) ?? [])
                {
                    if (value.Length == 2)
                    {
                        _positions[key] = (value[0], DateTime.FromBinary((long)value[1]));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start empty
        }
    }

    /// <summary>Saved position in seconds, or null.</summary>
    public double? Get(string key) => _positions.TryGetValue(key, out var p) ? p.Seconds : null;

    /// <summary>Remembers a position; near the start or the end the entry is removed (finished / barely started).</summary>
    public void Set(string key, double seconds, double length)
    {
        if (seconds < 10 || (length > 0 && length - seconds < 15))
        {
            _positions.Remove(key);
        }
        else
        {
            _positions[key] = (seconds, DateTime.UtcNow);
            foreach (var old in _positions.OrderBy(p => p.Value.When).Take(Math.Max(0, _positions.Count - MaxEntries)).Select(p => p.Key).ToList())
            {
                _positions.Remove(old);
            }
        }
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var data = _positions.ToDictionary(p => p.Key, p => new[] { p.Value.Seconds, (double)p.Value.When.ToBinary() });
            File.WriteAllText(_file, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // positions are a convenience
        }
    }
}
