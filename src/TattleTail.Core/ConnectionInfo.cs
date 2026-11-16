using System.Net;

namespace TattleTail.Core;

public enum Protocol
{
    Tcp,
    Udp,
}

/// <summary>
/// One row of the Windows connection table: an open socket and the process that owns it.
/// </summary>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="Local">Local address and port.</param>
/// <param name="Remote">Remote address and port. Always null for UDP, which has no fixed peer.</param>
/// <param name="State">TCP state (e.g. "Established"); empty for UDP.</param>
/// <param name="ProcessId">Owning process id.</param>
public sealed record ConnectionInfo(
    Protocol Protocol,
    IPEndPoint Local,
    IPEndPoint? Remote,
    string State,
    int ProcessId)
{
    /// <summary>The key used to match this connection against recorded traffic.</summary>
    /// <param name="process">The program currently using <see cref="ProcessId"/>.</param>
    public TrafficKey TrafficKeyFor(ProcessInstance process) => TrafficKey.For(Protocol, process, Local, Remote);
}
