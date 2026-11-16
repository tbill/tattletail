using System.Net;

namespace TattleTail.Core;

public enum Direction
{
    Sent,
    Received,
}

/// <summary>Cumulative byte counts.</summary>
public readonly record struct TrafficCounter(long Sent, long Received)
{
    public long Total => Sent + Received;

    public TrafficCounter Add(Direction direction, long bytes) => direction == Direction.Sent
        ? this with { Sent = Sent + bytes }
        : this with { Received = Received + bytes };
}

/// <summary>Identifies "this program talking to that server address".</summary>
public readonly record struct RemoteKey(ProcessInstance Process, IPAddress Address);

/// <summary>A point-in-time copy of everything the aggregator has counted.</summary>
public sealed record TrafficSnapshot(
    IReadOnlyDictionary<TrafficKey, TrafficCounter> ByConnection,
    IReadOnlyDictionary<ProcessInstance, TrafficCounter> ByProcess,
    IReadOnlyDictionary<RemoteKey, TrafficCounter> ByRemote,
    DateTime TakenAtUtc);

/// <summary>
/// Thread-safe running totals of network traffic: per connection, per process, and per
/// process + remote address. A "process" here is a <see cref="ProcessInstance"/>, one run of a program. The ETW listener writes into it from a background thread;
/// the UI reads snapshots.
/// </summary>
public sealed class TrafficAggregator
{
    private readonly object _gate = new();
    private readonly Dictionary<TrafficKey, (TrafficCounter Counter, DateTime LastSeenUtc)> _byConnection = new();
    private readonly Dictionary<RemoteKey, (TrafficCounter Counter, DateTime LastSeenUtc)> _byRemote = new();
    private readonly Dictionary<ProcessInstance, TrafficCounter> _byProcess = new();
    private readonly Func<DateTime> _clock;

    public TrafficAggregator()
        : this(() => DateTime.UtcNow)
    {
    }

    internal TrafficAggregator(Func<DateTime> clock)
    {
        _clock = clock;
    }

    public void Record(Protocol protocol, ProcessInstance process, IPEndPoint local, IPEndPoint? remote, long bytes, Direction direction)
    {
        if (bytes <= 0)
        {
            return;
        }

        // UDP rows in the connection table have no remote side, but the remote address is
        // still useful for "which servers did this program talk to".
        var connectionKey = TrafficKey.For(protocol, process, local, protocol == Protocol.Tcp ? remote : null);
        RemoteKey? remoteKey = remote is null ? null : new RemoteKey(process, IpAddresses.Normalize(remote.Address));
        var now = _clock();

        lock (_gate)
        {
            _byConnection.TryGetValue(connectionKey, out var entry);
            _byConnection[connectionKey] = (entry.Counter.Add(direction, bytes), now);

            if (remoteKey is { } rk)
            {
                _byRemote.TryGetValue(rk, out var remoteEntry);
                _byRemote[rk] = (remoteEntry.Counter.Add(direction, bytes), now);
            }

            _byProcess.TryGetValue(process, out var processCounter);
            _byProcess[process] = processCounter.Add(direction, bytes);
        }
    }

    public TrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TrafficSnapshot(
                _byConnection.ToDictionary(kv => kv.Key, kv => kv.Value.Counter),
                new Dictionary<ProcessInstance, TrafficCounter>(_byProcess),
                _byRemote.ToDictionary(kv => kv.Key, kv => kv.Value.Counter),
                _clock());
        }
    }

    /// <summary>
    /// Forgets per-connection and per-remote counters that have had no traffic for
    /// <paramref name="maxIdle"/>, so long-running sessions don't grow without bound.
    /// Per-process totals are kept.
    /// </summary>
    public int PruneConnections(TimeSpan maxIdle)
    {
        var cutoff = _clock() - maxIdle;
        lock (_gate)
        {
            return RemoveStale(_byConnection, cutoff) + RemoveStale(_byRemote, cutoff);
        }
    }

    private static int RemoveStale<TKey>(Dictionary<TKey, (TrafficCounter Counter, DateTime LastSeenUtc)> map, DateTime cutoff)
        where TKey : notnull
    {
        var stale = map.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
        {
            map.Remove(key);
        }

        return stale.Count;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byConnection.Clear();
            _byRemote.Clear();
            _byProcess.Clear();
        }
    }
}
