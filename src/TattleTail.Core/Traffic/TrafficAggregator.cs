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

/// <summary>A point-in-time copy of everything the aggregator has counted.</summary>
public sealed record TrafficSnapshot(
    IReadOnlyDictionary<TrafficKey, TrafficCounter> ByConnection,
    IReadOnlyDictionary<int, TrafficCounter> ByProcess,
    DateTime TakenAtUtc);

/// <summary>
/// Thread-safe running totals of network traffic, per connection and per process.
/// The ETW listener writes into it from a background thread; the UI reads snapshots.
/// </summary>
public sealed class TrafficAggregator
{
    private readonly object _gate = new();
    private readonly Dictionary<TrafficKey, (TrafficCounter Counter, DateTime LastSeenUtc)> _byConnection = new();
    private readonly Dictionary<int, TrafficCounter> _byProcess = new();
    private readonly Func<DateTime> _clock;

    public TrafficAggregator()
        : this(() => DateTime.UtcNow)
    {
    }

    internal TrafficAggregator(Func<DateTime> clock)
    {
        _clock = clock;
    }

    public void Record(Protocol protocol, int processId, IPEndPoint local, IPEndPoint? remote, long bytes, Direction direction)
    {
        if (bytes <= 0)
        {
            return;
        }

        var key = TrafficKey.For(protocol, processId, local, remote);
        var now = _clock();

        lock (_gate)
        {
            _byConnection.TryGetValue(key, out var entry);
            _byConnection[key] = (entry.Counter.Add(direction, bytes), now);

            _byProcess.TryGetValue(processId, out var processCounter);
            _byProcess[processId] = processCounter.Add(direction, bytes);
        }
    }

    public TrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TrafficSnapshot(
                _byConnection.ToDictionary(kv => kv.Key, kv => kv.Value.Counter),
                new Dictionary<int, TrafficCounter>(_byProcess),
                _clock());
        }
    }

    /// <summary>
    /// Forgets per-connection counters that have had no traffic for <paramref name="maxIdle"/>,
    /// so long-running sessions don't grow without bound. Per-process totals are kept.
    /// </summary>
    public int PruneConnections(TimeSpan maxIdle)
    {
        var cutoff = _clock() - maxIdle;
        lock (_gate)
        {
            var stale = _byConnection.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                _byConnection.Remove(key);
            }

            return stale.Count;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byConnection.Clear();
            _byProcess.Clear();
        }
    }
}
