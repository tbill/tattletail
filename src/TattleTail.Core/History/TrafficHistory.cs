using System.Net;

namespace TattleTail.Core;

/// <summary>
/// Records traffic over time and answers chart questions like "how much did Chrome
/// download each minute in the last hour?".
/// </summary>
/// <remarks>
/// Think of it as a notebook with two sections: a scratch pad holding the last ten minutes
/// second by second (in memory, for the live chart), and a ledger with one line per program,
/// server and minute (written to the <see cref="HistoryStore"/> once a minute).
/// </remarks>
public sealed class TrafficHistory
{
    public const string AllPrograms = "";
    private const int LiveSeconds = 10 * 60;

    private readonly object _gate = new();
    private readonly HistoryStore? _store;
    private readonly Func<int, string> _programFor;
    private readonly Func<IPAddress, int, string> _hostFor;
    private readonly Func<DateTime> _clock;

    // Live section: program -> (unix second -> counter). AllPrograms holds the total.
    private readonly Dictionary<string, SortedDictionary<long, TrafficCounter>> _seconds = new();

    // Ledger line being filled for the current minute.
    private readonly Dictionary<(string Program, IPAddress Ip), (int ProcessId, TrafficCounter Counter)> _pending = new();
    private long _pendingMinute = -1;

    /// <param name="store">Where finished minutes are saved; null keeps history in memory only.</param>
    /// <param name="programFor">Maps a process id to a stable program identity (e.g. exe path).</param>
    /// <param name="hostFor">Maps a remote address (and process id) to the name to store.</param>
    public TrafficHistory(HistoryStore? store, Func<int, string> programFor, Func<IPAddress, int, string> hostFor,
        Func<DateTime>? clock = null)
    {
        _store = store;
        _programFor = programFor;
        _hostFor = hostFor;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Records one second's worth of traffic. Call about once a second.</summary>
    public void Add(IReadOnlyList<TrafficDelta> deltas)
    {
        var now = _clock();
        var second = ToUnixSeconds(now);
        var minute = second / 60;

        List<HistoryRow>? finished = null;
        lock (_gate)
        {
            if (_pendingMinute >= 0 && minute != _pendingMinute)
            {
                finished = TakePendingRows();
            }

            _pendingMinute = minute;

            var total = default(TrafficCounter);
            var perProgram = new Dictionary<string, TrafficCounter>();
            foreach (var delta in deltas)
            {
                var program = _programFor(delta.ProcessId);
                var counter = new TrafficCounter(delta.Sent, delta.Received);

                perProgram[program] = Sum(perProgram.GetValueOrDefault(program), counter);
                total = Sum(total, counter);

                var key = (program, IpAddresses.Normalize(delta.Remote));
                var existing = _pending.GetValueOrDefault(key);
                _pending[key] = (delta.ProcessId, Sum(existing.Counter, counter));
            }

            AddSecond(AllPrograms, second, total);
            foreach (var (program, counter) in perProgram)
            {
                AddSecond(program, second, counter);
            }

            TrimSeconds(second - LiveSeconds);
        }

        if (finished is not null)
        {
            _store?.Write(finished);
        }
    }

    /// <summary>Writes the current partial minute to the store (call before closing).</summary>
    public void Flush()
    {
        List<HistoryRow> rows;
        lock (_gate)
        {
            rows = TakePendingRows();
        }

        _store?.Write(rows);
    }

    /// <summary>
    /// Chart points for <paramref name="range"/>, one per bucket, oldest first, with
    /// quiet periods filled in as zero.
    /// </summary>
    /// <param name="program">A program identity, or <see cref="AllPrograms"/>.</param>
    public List<SeriesPoint> GetSeries(HistoryRange range, string program)
    {
        var nowSecond = ToUnixSeconds(_clock());
        var bucket = HistoryRanges.BucketSeconds(range);
        var firstBucket = (nowSecond - (long)HistoryRanges.Duration(range).TotalSeconds) / bucket * bucket + bucket;
        var lastBucket = nowSecond / bucket * bucket;

        var totals = new Dictionary<long, TrafficCounter>();
        void AddTo(long second, TrafficCounter counter)
        {
            var b = second / bucket * bucket;
            if (b >= firstBucket && b <= lastBucket)
            {
                totals[b] = Sum(totals.GetValueOrDefault(b), counter);
            }
        }

        lock (_gate)
        {
            if (range == HistoryRange.TenMinutes)
            {
                if (_seconds.TryGetValue(program, out var seconds))
                {
                    foreach (var (second, counter) in seconds)
                    {
                        AddTo(second, counter);
                    }
                }
            }
            else
            {
                foreach (var row in PendingRows(program))
                {
                    AddTo(row.Minute * 60, new TrafficCounter(row.Sent, row.Received));
                }
            }
        }

        if (range != HistoryRange.TenMinutes && _store is not null)
        {
            var rows = _store.GetSeries(firstBucket / 60, _pendingMinute >= 0 ? _pendingMinute : nowSecond / 60 + 1,
                bucket / 60, program == AllPrograms ? null : program);
            foreach (var (bucketMinute, sent, received) in rows)
            {
                AddTo(bucketMinute * 60, new TrafficCounter(sent, received));
            }
        }

        var points = new List<SeriesPoint>();
        for (var b = firstBucket; b <= lastBucket; b += bucket)
        {
            var counter = totals.GetValueOrDefault(b);

            // The newest bucket is still filling up, so divide by the time elapsed so far.
            var seconds = b == lastBucket ? Math.Max(1, nowSecond - b + 1) : bucket;
            points.Add(new SeriesPoint(DateTime.UnixEpoch.AddSeconds(b), (double)counter.Sent / seconds,
                (double)counter.Received / seconds));
        }

        return points;
    }

    /// <summary>
    /// Totals per program and remote address over <paramref name="range"/>, including the minute
    /// in progress. <see cref="HistoryRow.Minute"/> is the last minute the address was seen.
    /// </summary>
    public List<HistoryRow> GetRemoteTotals(HistoryRange range, string program)
    {
        var nowMinute = ToUnixSeconds(_clock()) / 60;
        var fromMinute = nowMinute - (long)HistoryRanges.Duration(range).TotalMinutes + 1;

        List<HistoryRow> pending;
        long pendingMinute;
        lock (_gate)
        {
            pending = PendingRows(program).ToList();
            pendingMinute = _pendingMinute;
        }

        var stored = _store?.GetRemoteTotals(fromMinute, pendingMinute >= 0 ? pendingMinute : nowMinute + 1,
            program == AllPrograms ? null : program) ?? [];

        var merged = new Dictionary<(string Program, string Ip), HistoryRow>();
        foreach (var row in stored.Concat(pending))
        {
            var key = (row.Program, row.Ip);
            merged[key] = merged.TryGetValue(key, out var existing)
                ? existing with
                {
                    Sent = existing.Sent + row.Sent,
                    Received = existing.Received + row.Received,
                    Minute = Math.Max(existing.Minute, row.Minute),
                    Host = row.Minute >= existing.Minute ? row.Host : existing.Host,
                }
                : row;
        }

        return merged.Values.ToList();
    }

    /// <summary>Deletes stored history older than <paramref name="retention"/>.</summary>
    public void Prune(TimeSpan retention)
    {
        var cutoff = ToUnixSeconds(_clock() - retention) / 60;
        _store?.DeleteOlderThan(cutoff);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _seconds.Clear();
            _pending.Clear();
        }

        _store?.DeleteAll();
    }

    private IEnumerable<HistoryRow> PendingRows(string program) =>
        _pending
            .Where(kv => program == AllPrograms || kv.Key.Program == program)
            .Select(kv => MakeRow(_pendingMinute, kv.Key.Program, kv.Key.Ip, kv.Value.ProcessId, kv.Value.Counter));

    private List<HistoryRow> TakePendingRows()
    {
        var rows = _pending
            .Select(kv => MakeRow(_pendingMinute, kv.Key.Program, kv.Key.Ip, kv.Value.ProcessId, kv.Value.Counter))
            .ToList();
        _pending.Clear();
        return rows;
    }

    private HistoryRow MakeRow(long minute, string program, IPAddress ip, int processId, TrafficCounter counter) =>
        new(minute, program, ip.ToString(), _hostFor(ip, processId), counter.Sent, counter.Received);

    private void AddSecond(string program, long second, TrafficCounter counter)
    {
        if (!_seconds.TryGetValue(program, out var series))
        {
            series = new SortedDictionary<long, TrafficCounter>();
            _seconds[program] = series;
        }

        series[second] = Sum(series.GetValueOrDefault(second), counter);
    }

    private void TrimSeconds(long oldestToKeep)
    {
        foreach (var program in _seconds.Keys.ToList())
        {
            var series = _seconds[program];
            while (series.Count > 0 && series.First().Key < oldestToKeep)
            {
                series.Remove(series.First().Key);
            }

            if (series.Count == 0 && program != AllPrograms)
            {
                _seconds.Remove(program);
            }
        }
    }

    private static TrafficCounter Sum(TrafficCounter a, TrafficCounter b) => new(a.Sent + b.Sent, a.Received + b.Received);

    private static long ToUnixSeconds(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;
}
