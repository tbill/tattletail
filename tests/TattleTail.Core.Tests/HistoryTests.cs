using System.Net;
using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class TrafficDeltaCalculatorTests
{
    [Fact]
    public void ReportsOnlyWhatChanged()
    {
        var ip = IPAddress.Parse("1.1.1.1");
        var calc = new TrafficDeltaCalculator();

        var first = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(1, ip)] = new(100, 50) });
        var second = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(1, ip)] = new(150, 50) });
        var third = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(1, ip)] = new(150, 50) });

        Assert.Equal([new TrafficDelta(1, ip, 100, 50)], first);
        Assert.Equal([new TrafficDelta(1, ip, 50, 0)], second);
        Assert.Empty(third);
    }

    [Fact]
    public void CounterReset_CountsTheNewValue()
    {
        var ip = IPAddress.Parse("1.1.1.1");
        var calc = new TrafficDeltaCalculator();
        calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(1, ip)] = new(1000, 1000) });

        var afterReset = calc.Next(new Dictionary<RemoteKey, TrafficCounter> { [new(1, ip)] = new(10, 0) });

        Assert.Equal([new TrafficDelta(1, ip, 10, 0)], afterReset);
    }
}

public sealed class TrafficHistoryTests : IDisposable
{
    private static readonly IPAddress GitHub = IPAddress.Parse("140.82.112.3");
    private static readonly IPAddress Google = IPAddress.Parse("142.250.80.46");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tattletail-test-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _store;
    private DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public TrafficHistoryTests()
    {
        _store = new HistoryStore(_dbPath);
    }

    private TrafficHistory Create() => new(
        _store,
        pid => pid == 1 ? @"C:\Apps\chrome.exe" : @"C:\Apps\other.exe",
        (ip, _) => ip.Equals(GitHub) ? "github.com" : ip.ToString(),
        () => _now);

    [Fact]
    public void LiveSeries_HasOnePointPerSecondWithGapsFilled()
    {
        var history = Create();
        history.Add([new TrafficDelta(1, GitHub, 1000, 4000)]);
        _now = _now.AddSeconds(2);
        history.Add([new TrafficDelta(1, GitHub, 500, 0)]);

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
        history.Add([new TrafficDelta(1, GitHub, 1000, 0), new TrafficDelta(2, Google, 7, 0)]);

        var chrome = history.GetSeries(HistoryRange.TenMinutes, @"C:\Apps\chrome.exe");
        var all = history.GetSeries(HistoryRange.TenMinutes, TrafficHistory.AllPrograms);

        Assert.Equal(1000, chrome[^1].SendRate);
        Assert.Equal(1007, all[^1].SendRate);
    }

    [Fact]
    public void FinishedMinutes_AreSavedAndCombinedWithTheCurrentMinute()
    {
        var history = Create();
        history.Add([new TrafficDelta(1, GitHub, 6000, 60000)]);
        _now = _now.AddMinutes(1);
        history.Add([new TrafficDelta(1, GitHub, 60, 0)]); // closes 12:00, starts 12:01

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
        history.Add([new TrafficDelta(1, GitHub, 600, 0)]);
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
        history.Add([new TrafficDelta(1, GitHub, 100, 1000), new TrafficDelta(1, Google, 5, 5)]);
        _now = _now.AddMinutes(1);
        history.Add([new TrafficDelta(1, GitHub, 1, 1), new TrafficDelta(2, GitHub, 9, 9)]);

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
        history.Add([new TrafficDelta(1, GitHub, 100, 0)]);
        history.Flush();

        _now = _now.AddDays(31);
        history.Prune(TimeSpan.FromDays(30));

        Assert.Empty(history.GetRemoteTotals(HistoryRange.Month, TrafficHistory.AllPrograms));
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
