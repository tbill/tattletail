using System.Net;
using System.Net.Sockets;

namespace TattleTail.Core;

/// <summary>
/// Identifies a stream of traffic so that bytes reported by ETW can be matched to a
/// row of the connection table.
/// </summary>
/// <remarks>
/// TCP traffic is keyed by the full local/remote pair. UDP sockets are usually bound to
/// a wildcard address (0.0.0.0) and talk to many peers, so UDP traffic is keyed by the
/// local port only.
/// </remarks>
public readonly record struct TrafficKey(Protocol Protocol, int ProcessId, string Local, string Remote)
{
    public static TrafficKey For(Protocol protocol, int processId, IPEndPoint local, IPEndPoint? remote)
    {
        if (protocol == Protocol.Udp)
        {
            var family = IpAddresses.Normalize(local.Address).AddressFamily == AddressFamily.InterNetworkV6 ? "v6" : "v4";
            return new TrafficKey(protocol, processId, $"{family}:{local.Port}", string.Empty);
        }

        return new TrafficKey(protocol, processId, Describe(local), remote is null ? string.Empty : Describe(remote));
    }

    private static string Describe(IPEndPoint endpoint) =>
        new IPEndPoint(IpAddresses.Normalize(endpoint.Address), endpoint.Port).ToString();
}
