namespace NextCommander.Core.Config;

/// <summary>
/// A computer saved for Wake-on-LAN (Network tools; user's request, 22 November 2026): its network card's MAC address, the
/// broadcast address and port the magic packet goes to and, when known, its name or IP address, which is pinged to see it
/// wake.
/// </summary>
public sealed class WakeDevice
{
    public string Name { get; set; } = string.Empty;

    public string Mac { get; set; } = string.Empty;

    public string Broadcast { get; set; } = "255.255.255.255";

    public int Port { get; set; } = 9;

    /// <summary>Its host name or IP address (empty: not known; then it cannot be checked that it woke).</summary>
    public string? Address { get; set; }
}
