using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TattleTail.Core;

/// <summary>
/// Watches the Windows DNS client's event log (ETW) so we see each name a program looks up
/// and the addresses it got back, and records them in a <see cref="HostNameCache"/>.
/// </summary>
/// <remarks>
/// Lookups made with the browser's own encrypted DNS (DNS-over-HTTPS) skip the Windows DNS
/// client and aren't seen here; those fall back to reverse DNS.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EtwDnsMonitor : IDisposable
{
    public const string SessionName = "TattleTail-Dns";
    private const string ProviderName = "Microsoft-Windows-DNS-Client";

    // Event 3008: "DNS query is completed". Logged in the process that asked.
    private const int QueryCompletedEventId = 3008;

    private readonly HostNameCache _names;
    private TraceEventSession? _session;
    private Thread? _thread;

    public EtwDnsMonitor(HostNameCache names)
    {
        _names = names;
    }

    public event Action<Exception>? Faulted;

    public void Start()
    {
        if (_session is not null)
        {
            return;
        }

        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableProvider(ProviderName);

        var parser = new RegisteredTraceEventParser(session.Source);
        parser.All += e =>
        {
            if ((int)e.ID != QueryCompletedEventId)
            {
                return;
            }

            try
            {
                var name = e.PayloadByName("QueryName") as string;
                var results = e.PayloadByName("QueryResults") as string;
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                var addresses = DnsQueryResultsParser.ParseAddresses(results);
                if (addresses.Count > 0)
                {
                    _names.LearnFromDnsLookup(e.ProcessID, name, addresses);
                }
            }
            catch
            {
                // A malformed event shouldn't stop the listener.
            }
        };

        _session = session;
        _thread = new Thread(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TattleTail DNS",
        };
        _thread.Start();
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
