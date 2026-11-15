using System.Net;
using System.Net.Sockets;

namespace TattleTail.Core;

/// <summary>Small helpers for comparing and classifying IP addresses.</summary>
public static class IpAddresses
{
    /// <summary>
    /// Puts an address in one canonical form so the same server always matches:
    /// IPv4-mapped IPv6 (::ffff:1.2.3.4) becomes plain IPv4, and IPv6 scope ids are dropped.
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }

    /// <summary>
    /// A fixed label for addresses that aren't real servers (loopback, multicast, ...),
    /// or null for ordinary addresses.
    /// </summary>
    public static string? SpecialName(IPAddress address)
    {
        address = Normalize(address);

        if (IPAddress.IsLoopback(address))
        {
            return "localhost";
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return "(any)";
        }

        if (address.Equals(IPAddress.Broadcast))
        {
            return "(broadcast)";
        }

        if (address.IsIPv6Multicast)
        {
            return "(multicast)";
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = address.GetAddressBytes()[0];
            if (first is >= 224 and <= 239)
            {
                return "(multicast)";
            }
        }

        return null;
    }

    /// <summary>True for link-local addresses (169.254.x.x, fe80::), which have no DNS names.</summary>
    public static bool IsLinkLocal(IPAddress address)
    {
        address = Normalize(address);
        if (address.IsIPv6LinkLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254;
    }
}
