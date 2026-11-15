namespace TattleTail.Core;

/// <summary>How remote servers are shown: by website name, by IP address, or both.</summary>
public enum AddressDisplay
{
    Names,
    Addresses,
    Both,
}

public static class AddressFormatter
{
    /// <summary>
    /// Formats a remote server for display. When no name is known the IP address is shown
    /// whatever the mode, so nothing is ever blank.
    /// </summary>
    public static string Format(string? name, string ip, AddressDisplay mode)
    {
        if (string.IsNullOrEmpty(name) || name == ip)
        {
            return ip;
        }

        return mode switch
        {
            AddressDisplay.Names => name,
            AddressDisplay.Addresses => ip,
            _ => $"{name} ({ip})",
        };
    }
}
