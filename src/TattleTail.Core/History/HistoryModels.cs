using System.Net;

namespace TattleTail.Core;

/// <summary>Bytes a program exchanged with one remote address since the previous second.</summary>
public readonly record struct TrafficDelta(ProcessInstance Process, IPAddress Remote, long Sent, long Received);

/// <summary>
/// One stored row of history: bytes a program exchanged with one remote address during one minute.
/// </summary>
/// <param name="Minute">Minutes since the Unix epoch (UTC).</param>
/// <param name="Program">Program identity: its executable path, or its name if the path is unknown.</param>
/// <param name="Ip">Remote IP address.</param>
/// <param name="Host">Best known name for the address when the row was written, or the IP itself.</param>
public sealed record HistoryRow(long Minute, string Program, string Ip, string Host, long Sent, long Received);

/// <summary>Traffic in one time bucket of a chart. Rates are in bytes per second.</summary>
public readonly record struct SeriesPoint(DateTime StartUtc, double SendRate, double ReceiveRate);

public enum HistoryRange
{
    TenMinutes,
    Hour,
    Day,
    Week,
    Month,
}

public static class HistoryRanges
{
    public static TimeSpan Duration(HistoryRange range) => range switch
    {
        HistoryRange.TenMinutes => TimeSpan.FromMinutes(10),
        HistoryRange.Hour => TimeSpan.FromHours(1),
        HistoryRange.Day => TimeSpan.FromDays(1),
        HistoryRange.Week => TimeSpan.FromDays(7),
        HistoryRange.Month => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    /// <summary>How much time each bar/point on the chart covers.</summary>
    public static int BucketSeconds(HistoryRange range) => range switch
    {
        HistoryRange.TenMinutes => 1,
        HistoryRange.Hour => 60,
        HistoryRange.Day => 10 * 60,
        HistoryRange.Week => 60 * 60,
        HistoryRange.Month => 6 * 60 * 60,
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    public static string Describe(HistoryRange range) => range switch
    {
        HistoryRange.TenMinutes => "Last 10 minutes",
        HistoryRange.Hour => "Last hour",
        HistoryRange.Day => "Last 24 hours",
        HistoryRange.Week => "Last 7 days",
        HistoryRange.Month => "Last 30 days",
        _ => range.ToString(),
    };
}
