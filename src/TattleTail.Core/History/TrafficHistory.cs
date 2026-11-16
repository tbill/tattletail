using System.Net;

namespace TattleTail.Core;

/// <summary>
/// Records traffic over time and answers chart questions like "how much did Chrome
/// download each minute in the last hour?".
/// </summary>
/// <remarks>
/// Think of it as a notebook with two sections: a scratch pad holding the last ten minutes
/// second by second (in memory, for the live chart), and a ledger with one line per program,
/// server and minute (written to the <see cref="IHistoryStore"/> once a minute).
/// <para>A finished minute waits in a tray until it is safely in the store. If saving fails it
/// stays in the tray, still shows on the charts, and is tried again a minute later.</para>
/// </remarks>
public sealed class TrafficHistory
{
    public const string AllPrograms = "";
    private const int LiveSeconds = 10 * 60;

    /// <summary>How many finished minutes to hold on to while the store can't be written.</summary>
    internal const int MaxUnsavedMinutes = 60;

    // _gate guards the in-memory state and is only ever held briefly. _ioGate is held around
    // store reads and writes so that a finished minute is seen exactly once: in the tray or
    // in the store, never both and never neither. Always take _ioGate first.
    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly IHistoryStore? _store;
    private readonly Func<ProcessInstance, string> _programFor;
    private readonly Func<IPAddress, ProcessInstance, string> _hostFor;
    private readonly Func<DateTime> _clock;
    private readonly bool _saveInBackground;

    // The tray: finished minutes not yet in the store, oldest first.
    private readonly List<List<HistoryRow>> _unsaved = new();
    private volatile string? _saveError;

    // Live section: program -> (unix second -> counter). AllPrograms holds the total.
    private readonly Dictionary<string, SortedDictionary<long, TrafficCounter>> _seconds = new();

    // Ledger line being filled for the current minute.
    private readonly Dictionary<(string Program, IPAddress Ip), (ProcessInstance Process, TrafficCounter Counter)> _pending = new();
    private long _pendingMinute = -1;

    /// <param name="store">Where finished minutes are saved; null keeps history in memory only.</param>
    /// <param name="programFor">Maps a running program to a stable identity (e.g. exe path).</param>
    /// <param name="hostFor">Maps a remote address (and the program talking to it) to the name to store.</param>
    /// <param name="saveInBackground">
    /// Save finished minutes on a background thread, so <see cref="Add"/> never waits for the disk.
    /// </param>
    public TrafficHistory(IHistoryStore? store, Func<ProcessInstance, string> programFor,
        Func<IPAddress, ProcessInstance, string> hostFor, Func<DateTime>? clock = null, bool saveInBackground = false)
    {
        _saveInBackground = saveInBackground;
        _store = store;
        _programFor = programFor;
        _hostFor = hostFor;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Why the last attempt to save history failed, or null if saving is working.</summary>
    public string? SaveError => _saveError;

    /// <summary>Records one second's worth of traffic. Call about once a second.</summary>
    public void Add(IReadOnlyList<TrafficDelta> deltas)
    {
        var now = _clock();
        var second = ToUnixSeconds(now);
        var minute = second / 60;

        var minuteFinished = false;
        lock (_gate)
        {
            if (_pendingMinute >= 0 && minute != _pendingMinute)
            {
                MovePendingToUnsaved();
                minuteFinished = _unsaved.Count > 0;
            }

            _pendingMinute = minute;

            var total = default(TrafficCounter);
            var perProgram = new Dictionary<string, TrafficCounter>();
            foreach (var delta in deltas)
            {
                var program = _programFor(delta.Process);
                var counter = new TrafficCounter(delta.Sent, delta.Received);

                perProgram[program] = Sum(perProgram.GetValueOrDefault(program), counter);
                total = Sum(total, counter);

                var key = (program, IpAddresses.Normalize(delta.Remote));
                var existing = _pending.GetValueOrDefault(key);
                _pending[key] = (delta.Process, Sum(existing.Counter, counter));
            }

            AddSecond(AllPrograms, second, total);
            foreach (var (program, counter) in perProgram)
            {
                AddSecond(program, second, counter);
            }

            TrimSeconds(second - LiveSeconds);
        }

        // Once a minute, which also makes it the retry after a failed save.
        if (minuteFinished)
        {
            if (_saveInBackground)
            {
                _ = Task.Run(SaveUnsaved);
            }
            else
            {
                SaveUnsaved();
            }
        }
    }

    /// <summary>Writes everything, including the current partial minute, to the store (call before closing).</summary>
    public void Flush()
    {
        lock (_ioGate)
        {
            lock (_gate)
            {
                MovePendingToUnsaved();
            }

            SaveUnsaved();
        }
    }

    private void SaveUnsaved()
    {
        lock (_ioGate)
        {
            while (true)
            {
                List<HistoryRow> batch;
                lock (_gate)
                {
                    if (_unsaved.Count == 0)
                    {
                        break;
                    }

                    batch = _unsaved[0];
                }

                try
                {
                    _store!.Write(batch);
                }
                catch (Exception ex)
                {
                    // Keep the minute in the tray; the next finished minute tries again.
                    _saveError = ex.Message;
                    return;
                }

                lock (_gate)
                {
                    _unsaved.Remove(batch);
                }
            }

            _saveError = null;
        }
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

        if (range == HistoryRange.TenMinutes)
        {
            // The live chart is answered from memory alone, so it never waits for the disk.
            lock (_gate)
            {
                if (_seconds.TryGetValue(program, out var seconds))
                {
                    foreach (var (second, counter) in seconds)
                    {
                        AddTo(second, counter);
                    }
                }
            }
        }
        else
        {
            lock (_ioGate)
            {
                long pendingMinute;
                lock (_gate)
                {
                    pendingMinute = _pendingMinute;
                    foreach (var row in PendingRows(program).Concat(UnsavedRows(program)))
                    {
                        AddTo(row.Minute * 60, new TrafficCounter(row.Sent, row.Received));
                    }
                }

                if (_store is not null)
                {
                    var rows = _store.GetSeries(firstBucket / 60, pendingMinute >= 0 ? pendingMinute : nowSecond / 60 + 1,
                        bucket / 60, program == AllPrograms ? null : program);
                    foreach (var (bucketMinute, sent, received) in rows)
                    {
                        AddTo(bucketMinute * 60, new TrafficCounter(sent, received));
                    }
                }
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
        List<HistoryRow> stored;
        lock (_ioGate)
        {
            long pendingMinute;
            lock (_gate)
            {
                pending = UnsavedRows(program).Where(r => r.Minute >= fromMinute).Concat(PendingRows(program)).ToList();
                pendingMinute = _pendingMinute;
            }

            stored = _store?.GetRemoteTotals(fromMinute, pendingMinute >= 0 ? pendingMinute : nowMinute + 1,
                program == AllPrograms ? null : program) ?? [];
        }

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
        lock (_ioGate)
        {
            lock (_gate)
            {
                _seconds.Clear();
                _pending.Clear();
                _unsaved.Clear();
            }

            _store?.DeleteAll();
        }
    }

    private IEnumerable<HistoryRow> PendingRows(string program) =>
        _pending
            .Where(kv => program == AllPrograms || kv.Key.Program == program)
            .Select(kv => MakeRow(_pendingMinute, kv.Key.Program, kv.Key.Ip, kv.Value.Process, kv.Value.Counter));

    private IEnumerable<HistoryRow> UnsavedRows(string program) =>
        _unsaved.SelectMany(batch => batch).Where(row => program == AllPrograms || row.Program == program);

    /// <summary>Closes the minute being filled and puts it in the tray to be saved.</summary>
    private void MovePendingToUnsaved()
    {
        var rows = _pending
            .Select(kv => MakeRow(_pendingMinute, kv.Key.Program, kv.Key.Ip, kv.Value.Process, kv.Value.Counter))
            .ToList();
        _pending.Clear();

        // Without a store there is nowhere to save to, so there is nothing to wait for.
        if (rows.Count == 0 || _store is null)
        {
            return;
        }

        _unsaved.Add(rows);
        if (_unsaved.Count > MaxUnsavedMinutes)
        {
            _unsaved.RemoveAt(0);
        }
    }

    private HistoryRow MakeRow(long minute, string program, IPAddress ip, ProcessInstance process, TrafficCounter counter) =>
        new(minute, program, ip.ToString(), _hostFor(ip, process), counter.Sent, counter.Received);

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
