namespace TattleTail.Core;

/// <summary>
/// Turns running totals into "what's new since last time". Like reading an electricity
/// meter each second and writing down how much it moved.
/// </summary>
public sealed class TrafficDeltaCalculator
{
    private Dictionary<RemoteKey, TrafficCounter> _previous = new();

    public List<TrafficDelta> Next(IReadOnlyDictionary<RemoteKey, TrafficCounter> current)
    {
        var deltas = new List<TrafficDelta>();
        foreach (var (key, counter) in current)
        {
            _previous.TryGetValue(key, out var before);

            // If the counter went backwards it was reset; everything in it is new.
            var sent = counter.Sent >= before.Sent ? counter.Sent - before.Sent : counter.Sent;
            var received = counter.Received >= before.Received ? counter.Received - before.Received : counter.Received;

            if (sent > 0 || received > 0)
            {
                deltas.Add(new TrafficDelta(key.Process, key.Address, sent, received));
            }
        }

        _previous = new Dictionary<RemoteKey, TrafficCounter>(current);
        return deltas;
    }
}
