using System.Net;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TattleTail.Core;

/// <summary>
/// Listens to the Windows kernel's network events (Event Tracing for Windows) and feeds
/// every send/receive into a <see cref="TrafficAggregator"/>.
/// </summary>
/// <remarks>
/// Requires administrator rights. The kernel reports the local endpoint as the "source"
/// (saddr/sport) for both sends and receives.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EtwTrafficMonitor : IDisposable
{
    public const string SessionName = "TattleTail-Network";

    private readonly TrafficAggregator _aggregator;
    private TraceEventSession? _session;
    private Thread? _thread;

    public EtwTrafficMonitor(TrafficAggregator aggregator)
    {
        _aggregator = aggregator;
    }

    /// <summary>Raised (on a background thread) if the event session stops unexpectedly.</summary>
    public event Action<Exception>? Faulted;

    public bool IsRunning => _session is not null;

    public static bool IsElevated() => TraceEventSession.IsElevated() == true;

    public void Start()
    {
        if (_session is not null)
        {
            return;
        }

        if (!IsElevated())
        {
            throw new UnauthorizedAccessException("Traffic monitoring needs administrator rights.");
        }

        // If an earlier run crashed, a session with this name may still exist; the default
        // options stop it and start fresh.
        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = session.Source.Kernel;

        kernel.TcpIpSend += e => Record(Protocol.Tcp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Sent);
        kernel.TcpIpRecv += e => Record(Protocol.Tcp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Received);
        kernel.TcpIpSendIPV6 += e => Record(Protocol.Tcp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Sent);
        kernel.TcpIpRecvIPV6 += e => Record(Protocol.Tcp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Received);

        kernel.UdpIpSend += e => Record(Protocol.Udp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Sent);
        kernel.UdpIpRecv += e => Record(Protocol.Udp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Received);
        kernel.UdpIpSendIPV6 += e => Record(Protocol.Udp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Sent);
        kernel.UdpIpRecvIPV6 += e => Record(Protocol.Udp, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, Direction.Received);

        _session = session;
        _thread = new Thread(() =>
        {
            try
            {
                // Blocks until the session is disposed.
                session.Source.Process();
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TattleTail ETW",
        };
        _thread.Start();
    }

    private void Record(Protocol protocol, int processId, IPAddress localAddress, int localPort,
        IPAddress remoteAddress, int remotePort, int size, Direction direction)
    {
        _aggregator.Record(protocol, processId, new IPEndPoint(localAddress, localPort),
            new IPEndPoint(remoteAddress, remotePort), size, direction);
    }

    public void Stop()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();
}
