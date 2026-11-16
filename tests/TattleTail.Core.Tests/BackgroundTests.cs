using System.Xml.Linq;
using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class AutoStartTaskTests
{
    private const string Exe = @"C:\Users\R&D <team>\Tools\TattleTail.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Xml_StartsThisProgramHiddenAndElevatedAtSignIn()
    {
        var xml = AutoStartTask.BuildXml(Exe, @"PC\Bill", "S-1-5-21-1-2-3-1001");
        var task = XDocument.Parse(xml[xml.IndexOf("<Task", StringComparison.Ordinal)..]).Root!;

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Equal(Exe, task.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("--background", task.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(@"PC\Bill", task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("S-1-5-21-1-2-3-1001", task.Descendants(Ns + "Principal").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", task.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(Ns + "LogonType").Single().Value);
    }

    [Fact]
    public void Xml_DoesNotStopAfterThreeDaysOrOnBattery()
    {
        var xml = AutoStartTask.BuildXml(Exe, @"PC\Bill", "S-1-5-21-1-2-3-1001");
        var settings = XDocument.Parse(xml[xml.IndexOf("<Task", StringComparison.Ordinal)..]).Descendants(Ns + "Settings").Single();

        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
    }

    [Fact]
    public void ReadCommand_RoundTripsAwkwardPaths()
    {
        var xml = AutoStartTask.BuildXml(Exe, @"PC\Bill", "S-1-5-21-1-2-3-1001");

        var command = AutoStartTask.ReadCommand("\r\n" + xml + "\r\n");

        Assert.Equal(Exe, command);
        Assert.True(AutoStartTask.PointsAt(command, Exe.ToUpperInvariant()));
        Assert.False(AutoStartTask.PointsAt(command, @"D:\Elsewhere\TattleTail.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ERROR: The system cannot find the file specified.")]
    [InlineData("<Task><broken")]
    public void ReadCommand_IsNullForAnythingElse(string? output)
    {
        Assert.Null(AutoStartTask.ReadCommand(output));
        Assert.False(AutoStartTask.PointsAt(AutoStartTask.ReadCommand(output), Exe));
    }
}

public class NewProgramDetectorTests
{
    private DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private NewProgramDetector Create(params string[] known) => new(known, TimeSpan.FromMinutes(5), () => _now);

    [Fact]
    public void KnownPrograms_AreNotReported()
    {
        var detector = Create(@"C:\Apps\chrome.exe");

        Assert.Empty(detector.Observe([@"C:\Apps\chrome.exe", @"c:\apps\CHROME.EXE"]).Added);
    }

    [Fact]
    public void NewProgram_IsAnnouncedOnce()
    {
        var detector = Create(@"C:\Apps\chrome.exe");

        var first = detector.Observe([@"C:\Apps\chrome.exe", @"C:\Temp\odd.exe", @"C:\Temp\odd.exe"]);
        var second = detector.Observe([@"C:\Temp\odd.exe"]);

        Assert.Equal([@"C:\Temp\odd.exe"], first.Added);
        Assert.True(first.Announce);
        Assert.Empty(second.Added);
    }

    [Fact]
    public void FirstEverRun_LearnsQuietlyThenAnnounces()
    {
        var detector = Create();

        var duringLearning = detector.Observe(["chrome", "svchost"]);
        _now = _now.AddMinutes(6);
        var afterLearning = detector.Observe(["chrome", "odd"]);

        Assert.Equal(2, duringLearning.Added.Count); // still remembered...
        Assert.False(duringLearning.Announce);       // ...but not announced
        Assert.Equal(["odd"], afterLearning.Added);
        Assert.True(afterLearning.Announce);
    }

    [Fact]
    public void Forget_StartsLearningAgain()
    {
        var detector = Create("chrome");

        detector.Forget();

        Assert.True(detector.IsLearning);
        var result = detector.Observe(["chrome"]);
        Assert.Equal(["chrome"], result.Added);
        Assert.False(result.Announce);
    }

    [Fact]
    public void BlankNames_AreIgnored()
    {
        Assert.Empty(Create("chrome").Observe(["", null!]).Added);
    }
}

public sealed class KnownProgramsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tattletail-test-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _store;

    public KnownProgramsStoreTests()
    {
        _store = new HistoryStore(_dbPath);
    }

    [Fact]
    public void KnownPrograms_IncludeSavedOnesAndThoseInHistory()
    {
        _store.Write([new HistoryRow(100, @"C:\Apps\chrome.exe", "1.1.1.1", "one.one.one.one", 1, 1)]);
        _store.AddKnownPrograms([@"C:\Apps\curl.exe", @"C:\Apps\chrome.exe"], 100);
        _store.AddKnownPrograms([@"C:\Apps\curl.exe"], 200); // already known: no error, no duplicate

        var known = _store.GetKnownPrograms();

        Assert.Equal([@"C:\Apps\chrome.exe", @"C:\Apps\curl.exe"], known.Order());
    }

    [Fact]
    public void DeleteAll_ForgetsKnownPrograms()
    {
        _store.AddKnownPrograms([@"C:\Apps\curl.exe"], 100);

        _store.DeleteAll();

        Assert.Empty(_store.GetKnownPrograms());
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
