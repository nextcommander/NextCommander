namespace NextCommander.Core.Config;

/// <summary>
/// The folders of the connection manager: paths such as "Work/Prod" in <see cref="RemoteConnection.Folder"/>, with
/// <see cref="AppSettings.ConnectionFolders"/> keeping their colors and the empty ones. Renaming or moving a folder
/// takes its connections and subfolders along.
/// </summary>
public static class ConnectionFolders
{
    public static string Join(string folder, string name) =>
        string.Join('/', new[] { folder.Trim('/'), name.Trim('/') }.Where(p => p.Length > 0));

    public static string ParentOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    public static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The folder itself or one below it.</summary>
    public static bool IsInside(string path, string folder) =>
        folder.Length == 0 || string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder + "/", StringComparison.Ordinal);

    /// <summary>Every folder: the kept ones, those of the connections, and all their parents.</summary>
    public static IReadOnlyList<string> All(AppSettings settings)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in settings.ConnectionFolders.Select(f => f.Path).Concat(settings.Connections.Select(c => c.Folder)))
        {
            for (var p = path.Trim('/'); p.Length > 0; p = ParentOf(p))
            {
                all.Add(p);
            }
        }
        return [.. all.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The folders and connections directly in <paramref name="folder"/>.</summary>
    public static (IReadOnlyList<string> Folders, IReadOnlyList<RemoteConnection> Connections) ChildrenOf(AppSettings settings, string folder) =>
        ([.. All(settings).Where(f => ParentOf(f) == folder)],
         [.. settings.Connections.Where(c => c.Folder.Trim('/') == folder).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)]);

    /// <summary>The connections in a folder and its subfolders.</summary>
    public static IReadOnlyList<RemoteConnection> ConnectionsIn(AppSettings settings, string folder) =>
        [.. settings.Connections.Where(c => IsInside(c.Folder.Trim('/'), folder))];

    public static string ColorOf(AppSettings settings, string folder) =>
        settings.ConnectionFolders.FirstOrDefault(f => f.Path == folder)?.Color ?? string.Empty;

    /// <summary>Keeps a folder (an empty one stays in the list).</summary>
    public static void Ensure(AppSettings settings, string folder)
    {
        if (folder.Length > 0 && settings.ConnectionFolders.All(f => f.Path != folder))
        {
            settings.ConnectionFolders.Add(new ConnectionFolder { Path = folder });
        }
    }

    public static void SetColor(AppSettings settings, string folder, string color)
    {
        Ensure(settings, folder);
        settings.ConnectionFolders.First(f => f.Path == folder).Color = color;
    }

    /// <summary>A name not taken in <paramref name="parent"/> ("New folder", "New folder (2)" ...).</summary>
    public static string FreeName(AppSettings settings, string parent, string name)
    {
        var taken = All(settings).Where(f => ParentOf(f) == parent).Select(NameOf).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{name} ({i})";
        }
        return candidate;
    }

    /// <summary>
    /// Moves a folder to <paramref name="newPath"/> (a rename or a move to another parent) with everything in it; false
    /// when the new path is inside the folder itself or taken.
    /// </summary>
    public static bool Move(AppSettings settings, string folder, string newPath)
    {
        newPath = newPath.Trim('/');
        if (folder.Length == 0 || newPath.Length == 0 || newPath == folder || IsInside(newPath, folder)
            || All(settings).Contains(newPath, StringComparer.Ordinal))
        {
            return false;
        }
        string Moved(string path) => IsInside(path, folder) ? newPath + path[folder.Length..] : path;
        foreach (var connection in settings.Connections)
        {
            connection.Folder = Moved(connection.Folder.Trim('/'));
        }
        foreach (var entry in settings.ConnectionFolders)
        {
            entry.Path = Moved(entry.Path);
        }
        Ensure(settings, newPath);
        return true;
    }

    /// <summary>Removes a folder with its subfolders and connections; returns what was removed (to put back).</summary>
    public static (List<RemoteConnection> Connections, List<ConnectionFolder> Folders) Delete(AppSettings settings, string folder)
    {
        var connections = settings.Connections.Where(c => IsInside(c.Folder.Trim('/'), folder)).ToList();
        var folders = settings.ConnectionFolders.Where(f => IsInside(f.Path, folder)).ToList();
        settings.Connections.RemoveAll(connections.Contains);
        settings.ConnectionFolders.RemoveAll(folders.Contains);
        return (connections, folders);
    }
}

/// <summary>What keeps a connection from being saved or used.</summary>
public static class ConnectionChecks
{
    /// <summary>The first problem of a connection (null: it can be used).</summary>
    public static string? Problem(RemoteConnection connection)
    {
        if (OptionProblem(connection) is { } option)
        {
            return option;
        }
        var cloud = connection.IsCloudDrive;
        if (cloud)
        {
            if (string.IsNullOrWhiteSpace(connection.ClientId))
            {
                return "An application (client) id is needed; how to get one is written in the note.";
            }
            if (connection.Protocol == RemoteProtocol.Box && string.IsNullOrWhiteSpace(connection.ClientSecret))
            {
                return "Box needs the client secret of the application too.";
            }
            return null;
        }
        var host = connection.Host.Trim();
        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) || host.Contains(' ', StringComparison.Ordinal))
        {
            return "Enter a valid server name (e.g. server.example.com).";
        }
        if (host.Contains("ACCOUNT_ID", StringComparison.Ordinal) || host.Contains("REGION", StringComparison.Ordinal))
        {
            return "Replace the part in capitals of the server name (as the note says).";
        }
        if (connection.Port is < 0 or > 65535)
        {
            return "Port must be between 1 and 65535.";
        }
        if (connection.Protocol == RemoteProtocol.S3 && string.IsNullOrWhiteSpace(connection.User) && string.IsNullOrWhiteSpace(connection.AwsProfile))
        {
            return "S3 needs an access key id or an AWS profile.";
        }
        if (connection.Protocol is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles && string.IsNullOrWhiteSpace(connection.User))
        {
            return "Azure needs the name of the storage account.";
        }
        if (!string.IsNullOrWhiteSpace(connection.JumpHost) && connection.Protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp)
        {
            var jump = RemoteConnection.FromAuthority("sftp", connection.JumpHost.Trim());
            if (jump.Host.Length == 0 || jump.Host.Contains('/', StringComparison.Ordinal) || jump.Host.Contains(' ', StringComparison.Ordinal))
            {
                return "Enter the jump host as user@host:port.";
            }
        }
        return null;
    }

    /// <summary>What is wrong with the options of the Advanced and Proxy tabs (own proxy, FTP ports, sizes), or null.</summary>
    public static string? OptionProblem(RemoteConnection connection)
    {
        if (connection.Proxy == Net.ConnectionProxy.Custom)
        {
            var own = new Net.ProxyOptions(Net.ProxyMode.Manual, connection.ProxyHost ?? string.Empty,
                connection.ProxyPort > 0 ? connection.ProxyPort : Net.ProxyOptions.DefaultPort(connection.ProxyKind), Kind: connection.ProxyKind);
            if (own.Problem is { } problem)
            {
                return problem;
            }
        }
        if (!string.IsNullOrWhiteSpace(connection.FtpActivePorts) && PortRange(connection.FtpActivePorts) is null)
        {
            return "Enter the active mode ports as a range, e.g. 50000-50100.";
        }
        if (connection.RdpWidth < 0 || connection.RdpHeight < 0 || (connection.RdpWidth > 0) != (connection.RdpHeight > 0))
        {
            return "Enter both the width and the height of the remote desktop, or neither.";
        }
        return null;
    }

    /// <summary>"50000-50100" → the ports (null: not a range of valid ports).</summary>
    public static (int From, int To)? PortRange(string text)
    {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var from)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var to)
            && from is >= 1 and <= 65535 && to >= from && to <= 65535)
        {
            return (from, to);
        }
        return null;
    }

    /// <summary>The address a connection opens ("sftp://deploy@10.0.0.5/var/www").</summary>
    public static string Address(RemoteConnection connection) => $"{connection.Scheme}://{connection.Authority}{connection.InitialPath}";

    /// <summary>A name for a connection that has none: the account of a cloud drive, user@server, or the server.</summary>
    public static string DefaultName(RemoteConnection connection, string typeName) =>
        connection.IsCloudDrive ? $"{typeName} ({connection.User})"
        : connection.User.Length > 0 && !connection.IsObjectStorage ? $"{connection.User}@{connection.Host}" : connection.Host;
}
