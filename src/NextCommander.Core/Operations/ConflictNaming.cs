namespace NextCommander.Core.Operations;

public static class ConflictNaming
{
    /// <summary>Returns "name (2).ext", "name (3).ext" ... for the first index where <paramref name="exists"/> is false.</summary>
    public static async Task<string> NextFreeNameAsync(string name, bool isDirectory, Func<string, Task<bool>> exists)
    {
        string baseName = name, ext = string.Empty;
        if (!isDirectory)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                baseName = name[..dot];
                ext = name[dot..];
            }
        }
        for (var i = 2; i < 100_000; i++)
        {
            var candidate = $"{baseName} ({i}){ext}";
            if (!await exists(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }
        throw new IOException($"No free name found for '{name}'.");
    }
}
