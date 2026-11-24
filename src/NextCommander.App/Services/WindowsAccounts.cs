using System.Runtime.Versioning;
using System.Security.Principal;

namespace NextCommander.App.Services;

/// <summary>What kind of account a rule is for (its picture in the permissions window).</summary>
public enum AccountKind
{
    User,
    Group,
    System,
}

/// <summary>
/// Users and groups of Windows for the permissions window: a name's kind and a short description, the names offered
/// while typing, whether a name exists, the signed-in user and the groups they are in.
/// </summary>
public static class WindowsAccounts
{
    /// <summary>The usual users and groups (by their SID, shown by the name of this computer's language), and what they are.</summary>
    private static readonly (string Sid, string Description)[] s_wellKnown =
    [
        ("S-1-5-32-544", "Everyone with admin rights on this PC"),
        ("S-1-5-18", "Windows itself and its services"),
        ("S-1-5-11", "Anyone who signed in"),
        ("S-1-5-32-545", "All standard user accounts"),
        ("S-1-1-0", "Includes guests"),
        ("S-1-5-32-546", "Guest accounts"),
        ("S-1-3-0", "Whoever creates a file or folder here"),
    ];

    private static readonly Dictionary<string, (string? Sid, AccountKind Kind)> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The signed-in user ("PC\name"; empty elsewhere than Windows).</summary>
    public static string CurrentUser => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;

    /// <summary>The program runs with administrator rights (then the Administrators' rules count).</summary>
    public static bool IsElevated => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The names offered in the pickers: the usual groups and the signed-in user.</summary>
    public static IReadOnlyList<string> Suggestions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        var names = new List<string>();
        foreach (var (sid, _) in s_wellKnown)
        {
            if (NameOfSid(sid) is { } name)
            {
                names.Add(name);
            }
        }
        names.Add(CurrentUser);
        return names;
    }

    /// <summary>A short description of a usual group (empty for others); "(you)" for the signed-in user.</summary>
    public static string DescriptionOf(string name)
    {
        if (string.Equals(name, CurrentUser, StringComparison.OrdinalIgnoreCase))
        {
            return Core.Localization.Localizer.T("You");
        }
        var sid = SidOf(name);
        return s_wellKnown.FirstOrDefault(w => w.Sid == sid).Description is { } description ? Core.Localization.Localizer.T(description) : string.Empty;
    }

    /// <summary>A user, a group or Windows itself (by the SID: built-in and well-known groups, services, ordinary accounts).</summary>
    public static AccountKind KindOf(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return AccountKind.User;
        }
        Resolve(name);
        return s_cache.TryGetValue(name, out var known) ? known.Kind : AccountKind.Group;
    }

    /// <summary>Whether a user or group of that name (or SID) exists for this computer or its domain.</summary>
    public static bool Exists(string name) => OperatingSystem.IsWindows() && Resolve(name) is not null;

    /// <summary>The names (as rules show them) of the signed-in user and every group they are in.</summary>
    public static IReadOnlySet<string> CurrentGroups()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return names;
        }
        var identity = WindowsIdentity.GetCurrent();
        names.Add(identity.Name);
        foreach (var group in identity.Groups ?? [])
        {
            if (NameOfSid(group.Value) is { } name)
            {
                names.Add(name);
            }
        }
        if (NameOfSid("S-1-5-32-544") is { } admins && IsAdministratorMember(identity))
        {
            names.Add(admins);
        }
        return names;
    }

    /// <summary>The local name of the Administrators group (whose rules count only with administrator rights).</summary>
    public static string? AdministratorsName => OperatingSystem.IsWindows() ? NameOfSid("S-1-5-32-544") : null;

    [SupportedOSPlatform("windows")]
    private static bool IsAdministratorMember(WindowsIdentity identity) =>
        identity.Groups?.Any(g => g.Value == "S-1-5-32-544") == true || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

    private static string? SidOf(string name) => OperatingSystem.IsWindows() ? Resolve(name) : null;

    [SupportedOSPlatform("windows")]
    private static string? NameOfSid(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.ComponentModel.Win32Exception or SystemException)
        {
            return null;
        }
    }

    /// <summary>The SID of a name (null: unknown), with its kind kept for later.</summary>
    [SupportedOSPlatform("windows")]
    private static string? Resolve(string name)
    {
        lock (s_cache)
        {
            if (s_cache.TryGetValue(name, out var known))
            {
                return known.Sid;
            }
        }
        string? sid;
        try
        {
            sid = name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(name).Value
                : new NTAccount(name.Trim()).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.ComponentModel.Win32Exception or ArgumentException or SystemException)
        {
            sid = null;
        }
        var kind = sid switch
        {
            null => AccountKind.Group,
            "S-1-5-18" or "S-1-5-19" or "S-1-5-20" => AccountKind.System,
            _ when sid.StartsWith("S-1-5-80-", StringComparison.Ordinal) => AccountKind.System, // services (TrustedInstaller)
            _ when sid.StartsWith("S-1-5-32-", StringComparison.Ordinal) || !sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) => AccountKind.Group,
            // a domain or a computer: the small numbers are its groups (Domain Users ...), the others its accounts
            _ => int.TryParse(sid[(sid.LastIndexOf('-') + 1)..], out var rid) && rid < 1000 && rid != 500 && rid != 501 ? AccountKind.Group : AccountKind.User,
        };
        lock (s_cache)
        {
            s_cache[name] = (sid, kind);
        }
        return sid;
    }
}
