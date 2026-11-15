using System.Net;
using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class TrafficAggregatorTests
{
    private static readonly IPEndPoint Local = IPEndPoint.Parse("192.168.1.10:51000");
    private static readonly IPEndPoint Remote = IPEndPoint.Parse("140.82.112.3:443");

    [Fact]
    public void Record_SumsPerConnectionAndPerProcess()
    {
        var aggregator = new TrafficAggregator();

        aggregator.Record(Protocol.Tcp, 100, Local, Remote, 500, Direction.Sent);
        aggregator.Record(Protocol.Tcp, 100, Local, Remote, 1500, Direction.Received);
        aggregator.Record(Protocol.Tcp, 100, Local, IPEndPoint.Parse("1.1.1.1:443"), 200, Direction.Sent);

        var snapshot = aggregator.Snapshot();
        var key = TrafficKey.For(Protocol.Tcp, 100, Local, Remote);

        Assert.Equal(new TrafficCounter(500, 1500), snapshot.ByConnection[key]);
        Assert.Equal(new TrafficCounter(700, 1500), snapshot.ByProcess[100]);
        Assert.Equal(2, snapshot.ByConnection.Count);
    }

    [Fact]
    public void Record_IgnoresEmptyPackets()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Record(Protocol.Tcp, 1, Local, Remote, 0, Direction.Sent);
        Assert.Empty(aggregator.Snapshot().ByProcess);
    }

    [Fact]
    public void ConnectionTableRow_MatchesEtwTraffic()
    {
        // The connection table and ETW must agree on the key, including IPv4-mapped IPv6.
        var row = new ConnectionInfo(Protocol.Tcp, Local, Remote, "Established", 42);
        var mappedLocal = new IPEndPoint(Local.Address.MapToIPv6(), Local.Port);
        var mappedRemote = new IPEndPoint(Remote.Address.MapToIPv6(), Remote.Port);

        var aggregator = new TrafficAggregator();
        aggregator.Record(Protocol.Tcp, 42, mappedLocal, mappedRemote, 10, Direction.Sent);

        Assert.Equal(new TrafficCounter(10, 0), aggregator.Snapshot().ByConnection[row.TrafficKey]);
    }

    [Fact]
    public void UdpTraffic_IsKeyedByLocalPortRegardlessOfAddress()
    {
        // UDP sockets are usually bound to 0.0.0.0, but ETW reports the real local address.
        var socket = new ConnectionInfo(Protocol.Udp, IPEndPoint.Parse("0.0.0.0:5353"), null, "", 7);

        var aggregator = new TrafficAggregator();
        aggregator.Record(Protocol.Udp, 7, IPEndPoint.Parse("192.168.1.10:5353"), null, 80, Direction.Received);
        aggregator.Record(Protocol.Udp, 7, IPEndPoint.Parse("[fe80::1%12]:5353"), null, 99, Direction.Received);

        var snapshot = aggregator.Snapshot();
        Assert.Equal(new TrafficCounter(0, 80), snapshot.ByConnection[socket.TrafficKey]);
        Assert.Equal(new TrafficCounter(0, 179), snapshot.ByProcess[7]);
    }

    [Fact]
    public void PruneConnections_DropsIdleConnectionsButKeepsProcessTotals()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new TrafficAggregator(() => now);

        aggregator.Record(Protocol.Tcp, 1, Local, Remote, 100, Direction.Sent);
        now = now.AddMinutes(11);
        aggregator.Record(Protocol.Tcp, 1, Local, IPEndPoint.Parse("8.8.8.8:53"), 5, Direction.Sent);

        var removed = aggregator.PruneConnections(TimeSpan.FromMinutes(10));

        var snapshot = aggregator.Snapshot();
        Assert.Equal(2, removed); // one connection counter and one remote-address counter
        Assert.Single(snapshot.ByConnection);
        Assert.Single(snapshot.ByRemote);
        Assert.Equal(105, snapshot.ByProcess[1].Sent);
    }

    [Fact]
    public void ByRemote_IncludesUdpPeers()
    {
        var aggregator = new TrafficAggregator();
        var quicServer = IPEndPoint.Parse("142.250.80.46:443");
        aggregator.Record(Protocol.Udp, 9, IPEndPoint.Parse("0.0.0.0:60000"), quicServer, 1200, Direction.Received);
        aggregator.Record(Protocol.Tcp, 9, Local, new IPEndPoint(quicServer.Address.MapToIPv6(), 443), 300, Direction.Sent);

        var counter = aggregator.Snapshot().ByRemote[new RemoteKey(9, quicServer.Address)];
        Assert.Equal(new TrafficCounter(300, 1200), counter);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Record(Protocol.Tcp, 1, Local, Remote, 100, Direction.Sent);
        aggregator.Reset();

        var snapshot = aggregator.Snapshot();
        Assert.Empty(snapshot.ByConnection);
        Assert.Empty(snapshot.ByProcess);
    }

    [Fact]
    public void Record_IsThreadSafe()
    {
        var aggregator = new TrafficAggregator();
        Parallel.For(0, 10_000, _ => aggregator.Record(Protocol.Tcp, 1, Local, Remote, 1, Direction.Sent));
        Assert.Equal(10_000, aggregator.Snapshot().ByProcess[1].Sent);
    }
}
