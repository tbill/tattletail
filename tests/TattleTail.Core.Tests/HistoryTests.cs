using System.Net;
using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class TrafficDeltaCalculatorTests
{
    private static readonly ProcessInstance Chrome = new(1);

    [Fact]
    public void ReportsOnlyWhatChanged()
    {
        var ip = IPAddress.Parse("1.1.1.1");
        var calc = new TrafficDeltaCalculator();

        var first = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(Chrome, ip)] = new(100, 50) });
        var second = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(Chrome, ip)] = new(150, 50) });
        var third = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(Chrome, ip)] = new(150, 50) });

        Assert.Equal([new TrafficDelta(Chrome, ip, 100, 50)], first);
        Assert.Equal([new TrafficDelta(Chrome, ip, 50, 0)], second);
        Assert.Empty(third);
    }

    [Fact]
    public void CounterReset_CountsTheNewValue()
    {
        var ip = IPAddress.Parse("1.1.1.1");
        var calc = new TrafficDeltaCalculator();
        calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(Chrome, ip)] = new(1000, 1000) });

        var afterReset = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(Chrome, ip)] = new(10, 0) });

        Assert.Equal([new TrafficDelta(Chrome, ip, 10, 0)], afterReset);
    }
}

public sealed class TrafficHistoryTests : IDisposable
{
    private static readonly IPAddress GitHub = IPAddress.Parse("140.82.112.3");
    private static readonly IPAddress Google = IPAddress.Parse("142.250.80.46");
    private static readonly ProcessInstance Chrome = new(1);
    private static readonly ProcessInstance Other = new(2);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tattletail-test-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _store;
    private DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public TrafficHistoryTests()
    {
        _store = new HistoryStore(_dbPath);
    }

    private TrafficHistory Create(IHistoryStore? store = null, bool saveInBackground = false) => new(
        store ?? _store,
        process => process == Chrome ? @"C:\Apps\chrome.exe" : @"C:\Apps\other.exe",
        (ip, _) => ip.Equals(GitHub) ? "github.com" : ip.ToString(),
        () => _now,
        saveInBackground);

    [Fact]
    public void LiveSeries_HasOnePointPerSecondWithGapsFilled()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 1000, 4000)]);
        _now = _now.AddSeconds(2);
        history.Add([new TrafficDelta(Chrome, GitHub, 500, 0)]);

        var series = history.GetSeries(HistoryRange.TenMinutes, TrafficHistory.AllPrograms);

        Assert.Equal(600, series.Count);
        Assert.Equal(new SeriesPoint(_now.AddSeconds(-2), 1000, 4000), series[^3]);
        Assert.Equal(0, series[^2].SendRate);
        Assert.Equal(500, series[^1].SendRate);
    }

    [Fact]
    public void LiveSeries_CanBeFilteredByProgram()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 1000, 0), new TrafficDelta(Other, Google, 7, 0)]);

        var chrome = history.GetSeries(HistoryRange.TenMinutes, @"C:\Apps\chrome.exe");
        var all = history.GetSeries(HistoryRange.TenMinutes, TrafficHistory.AllPrograms);

        Assert.Equal(1000, chrome[^1].SendRate);
        Assert.Equal(1007, all[^1].SendRate);
    }

    [Fact]
    public void FinishedMinutes_AreSavedAndCombinedWithTheCurrentMinute()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 6000, 60000)]);
        _now = _now.AddMinutes(1);
        history.Add([new TrafficDelta(Chrome, GitHub, 60, 0)]); // closes 12:00, starts 12:01

        var series = history.GetSeries(HistoryRange.Hour, TrafficHistory.AllPrograms);

        Assert.Equal(60, series.Count);
        Assert.Equal(_now.AddMinutes(-1), series[^2].StartUtc);
        Assert.Equal(100, series[^2].SendRate);    // 6000 bytes / 60 s
        Assert.Equal(1000, series[^2].ReceiveRate);
        Assert.Equal(60, series[^1].SendRate);     // current minute, 1 s in
    }

    [Fact]
    public void History_SurvivesARestart()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 600, 0)]);
        history.Flush();

        _now = _now.AddMinutes(5);
        var restarted = Create();
        var series = restarted.GetSeries(HistoryRange.Hour, TrafficHistory.AllPrograms);

        Assert.Equal(10, series[^6].SendRate);
    }

    [Fact]
    public void RemoteTotals_GroupByProgramAndAddressWithNames()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 100, 1000), new TrafficDelta(Chrome, Google, 5, 5)]);
        _now = _now.AddMinutes(1);
        history.Add([new TrafficDelta(Chrome, GitHub, 1, 1), new TrafficDelta(Other, GitHub, 9, 9)]);

        var chrome = history.GetRemoteTotals(HistoryRange.Hour, @"C:\Apps\chrome.exe");
        var all = history.GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms);

        var github = Assert.Single(chrome, r => r.Ip == GitHub.ToString());
        Assert.Equal("github.com", github.Host);
        Assert.Equal(101, github.Sent);
        Assert.Equal(1001, github.Received);
        Assert.Equal(2, chrome.Count);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Prune_DeletesOldHistory()
    {
        var history = Create();
        history.Add([new TrafficDelta(Chrome, GitHub, 100, 0)]);
        history.Flush();

        _now = _now.AddDays(31);
        history.Prune(TimeSpan.FromDays(30));

        Assert.Empty(history.GetRemoteTotals(HistoryRange.Month, TrafficHistory.AllPrograms));
    }

    [Fact]
    public void RemoteTotals_UseTheNewestNameAndLastSeenMinute()
    {
        const string program = @"C:\Apps\chrome.exe";
        var minute = (long)(_now - DateTime.UnixEpoch).TotalMinutes;

        // Written out of order on purpose: the newest row must win, not the last one written.
        _store.Write([new HistoryRow(minute - 5, program, "140.82.112.3", "github.com", 10, 100)]);
        _store.Write([new HistoryRow(minute - 20, program, "140.82.112.3", "140.82.112.3", 1, 1)]);
        _store.Write([new HistoryRow(minute - 10, program, "140.82.112.3", "lb-140-82-112-3-iad.github.com", 2, 2)]);
        _store.Write([new HistoryRow(minute - 90, program, "140.82.112.3", "too-old.example", 5000, 5000)]);

        var row = Assert.Single(Create().GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms));

        Assert.Equal("github.com", row.Host);
        Assert.Equal(minute - 5, row.Minute);
        Assert.Equal(13, row.Sent);
        Assert.Equal(103, row.Received);
    }

    [Fact]
    public void FailedSave_KeepsTheMinuteAndSavesItLater()
    {
        var flaky = new FlakyStore(_store) { Failing = true };
        var history = Create(flaky);

        history.Add([new TrafficDelta(Chrome, GitHub, 600, 0)]);
        _now = _now.AddMinutes(1);
        history.Add([new TrafficDelta(Chrome, GitHub, 60, 0)]); // closes 12:00; saving it fails

        Assert.Equal("disk full", history.SaveError);
        Assert.Equal(660, Assert.Single(history.GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms)).Sent);
        Assert.Equal(10, history.GetSeries(HistoryRange.Hour, TrafficHistory.AllPrograms)[^2].SendRate);

        flaky.Failing = false;
        _now = _now.AddMinutes(1);
        history.Add([]); // closes 12:01; both minutes are saved now

        Assert.Null(history.SaveError);
        Assert.Equal(660, _store.GetRemoteTotals(0, long.MaxValue, null).Sum(r => r.Sent));
        Assert.Equal(660, Assert.Single(history.GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms)).Sent);
    }

    [Fact]
    public void FailedSaves_DoNotPileUpForever()
    {
        var history = Create(new FlakyStore(_store) { Failing = true });

        for (var i = 0; i < TrafficHistory.MaxUnsavedMinutes + 30; i++)
        {
            history.Add([new TrafficDelta(Chrome, GitHub, 1, 0)]);
            _now = _now.AddMinutes(1);
        }

        // One byte a minute: the newest unsaved minutes are kept, plus the minute still being filled.
        var day = history.GetRemoteTotals(HistoryRange.Day, TrafficHistory.AllPrograms);
        Assert.Equal(TrafficHistory.MaxUnsavedMinutes + 1, Assert.Single(day).Sent);
    }

    [Fact]
    public void BackgroundSaving_DoesNotMakeAddWaitForTheStore()
    {
        var slow = new FlakyStore(_store);
        var history = Create(slow, saveInBackground: true);
        history.Add([new TrafficDelta(Chrome, GitHub, 600, 0)]);
        _now = _now.AddMinutes(1);

        slow.Blocked.Reset();
        history.Add([new TrafficDelta(Chrome, GitHub, 60, 0)]); // would hang here if it saved on this thread
        _now = _now.AddSeconds(1);
        history.Add([new TrafficDelta(Chrome, GitHub, 1, 0)]);
        Assert.Equal(1, history.GetSeries(HistoryRange.TenMinutes, TrafficHistory.AllPrograms)[^1].SendRate);

        slow.Blocked.Set();
        Assert.True(SpinWait.SpinUntil(() => _store.GetRemoteTotals(0, long.MaxValue, null).Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(661, Assert.Single(history.GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms)).Sent);
    }

    [Fact]
    public void Clear_ForgetsUnsavedMinutesToo()
    {
        var flaky = new FlakyStore(_store) { Failing = true };
        var history = Create(flaky);
        history.Add([new TrafficDelta(Chrome, GitHub, 600, 0)]);
        _now = _now.AddMinutes(1);
        history.Add([]);

        history.Clear();

        Assert.Empty(history.GetRemoteTotals(HistoryRange.Hour, TrafficHistory.AllPrograms));
    }

    /// <summary>A store that can be told to fail or to pause, standing in for a full or busy disk.</summary>
    private sealed class FlakyStore(HistoryStore inner) : IHistoryStore
    {
        public bool Failing { get; set; }
        public ManualResetEventSlim Blocked { get; } = new(initialState: true);

        public void Write(IReadOnlyList<HistoryRow> rows)
        {
            Blocked.Wait();
            if (Failing)
            {
                throw new IOException("disk full");
            }

            inner.Write(rows);
        }

        public List<(long BucketMinute, long Sent, long Received)> GetSeries(long fromMinute, long toMinute, int bucketMinutes, string? program) =>
            inner.GetSeries(fromMinute, toMinute, bucketMinutes, program);

        public List<HistoryRow> GetRemoteTotals(long fromMinute, long toMinute, string? program) =>
            inner.GetRemoteTotals(fromMinute, toMinute, program);

        public int DeleteOlderThan(long minute) => inner.DeleteOlderThan(minute);

        public void DeleteAll() => inner.DeleteAll();
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }
}
