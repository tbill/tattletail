using System.Net;

namespace TattleTail.Core;

/// <summary>
/// Parses the <c>QueryResults</c> text that the Windows DNS client logs when a lookup
/// finishes, for example:
/// <c>type:  5 github.map.fastly.net;::ffff:140.82.112.3;2606:50c0:8000::154;</c>
/// </summary>
public static class DnsQueryResultsParser
{
    public static List<IPAddress> ParseAddresses(string? results)
    {
        var addresses = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(results))
        {
            return addresses;
        }

        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "type: 5 name" entries are CNAME aliases, not addresses.
            if (part.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IPAddress.TryParse(part, out var address))
            {
                address = IpAddresses.Normalize(address);
                if (!addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }
}
