using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TattleTail.App.ViewModels;
using TattleTail.Core;

namespace TattleTail.App;

/// <summary>
/// <c>TattleTail.exe --smoke-test report.json [seconds]</c> opens the real window, lets it run,
/// writes what it saw to a JSON report and exits. CI uses this to check the app actually works
/// on Windows, not just that it compiles.
/// </summary>
internal static class SmokeTest
{
    public static (string ReportPath, int Seconds)? FromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--smoke-test");
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var seconds = index + 2 < args.Length && int.TryParse(args[index + 2], out var s) ? s : 20;
        return (Path.GetFullPath(args[index + 1]), seconds);
    }

    public static void Run(MainWindow window, MainViewModel viewModel, string reportPath, int seconds)
    {
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var switchedRange = false;

        timer.Tick += async (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;

            // Halfway through, switch to the hour view so the database path is exercised too.
            if (!switchedRange && elapsed > seconds / 2.0)
            {
                switchedRange = true;
                viewModel.HistoryPanel.Range = HistoryRange.Hour;
            }

            if (elapsed < seconds)
            {
                return;
            }

            timer.Stop();
            viewModel.HistoryPanel.Range = HistoryRange.TenMinutes;
            viewModel.HistoryPanel.RequestRefresh();
            await viewModel.HistoryPanel.TickAsync();

            var namedConnections = viewModel.Connections.Count(c =>
                c.RemoteIp is not null && c.RemoteText != c.RemoteIp.ToString());
            var report = new
            {
                ok = viewModel.Connections.Count > 0 && viewModel.TrafficAvailable,
                version = AppVersion.Display,
                connections = viewModel.Connections.Count,
                namedConnections,
                programs = viewModel.Programs.Count,
                programsWithTraffic = viewModel.Programs.Count(p => p.TotalBytes > 0),
                programsExited = viewModel.Programs.Count(p => !p.IsRunning),
                programsUnnamed = viewModel.Programs.Count(p => p.ProcessName == ProcessRegistry.UnknownName),
                exitedProgramNames = viewModel.Programs
                    .Where(p => !p.IsRunning && p.TotalBytes > 0).Select(p => p.ProcessName).Distinct(),
                topPrograms = viewModel.Programs
                    .OrderByDescending(p => p.TotalBytes).Take(10)
                    .Select(p => $"{p.ProcessName} (PID {p.ProcessId}{(p.IsRunning ? "" : ", exited")}, {ByteFormatter.Format(p.TotalBytes)})"),
                totalBytes = viewModel.Programs.Sum(p => p.TotalBytes),
                servers = viewModel.HistoryPanel.Servers.Count,
                groupedByDomain = viewModel.HistoryPanel.GroupByDomain,
                domainHosts = viewModel.HistoryPanel.Servers
                    .Where(s => s.IsDomain).OrderByDescending(s => s.TotalBytes).Take(5)
                    .Select(s => $"{s.Server}: {string.Join(", ", s.Children.Select(c => c.Server))}"),
                topServers = viewModel.HistoryPanel.Servers
                    .OrderByDescending(s => s.TotalBytes).Take(10)
                    .Select(s => $"{s.Server} ({ByteFormatter.Format(s.TotalBytes)}, {s.Programs})"),
                chartPoints = viewModel.HistoryPanel.Points.Count,
                chartBytesPerSecondPeak = viewModel.HistoryPanel.Points.Count == 0
                    ? 0
                    : viewModel.HistoryPanel.Points.Max(p => p.SendRate + p.ReceiveRate),
                trafficStatus = viewModel.TrafficStatus,
                namesStatus = viewModel.NamesStatus,
                historyStatus = viewModel.HistoryStatus,
                summary = viewModel.Summary,
            };

            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        };

        timer.Start();
    }

    public static void ReportCrash(Exception exception)
    {
        if (FromCommandLine() is { } smoke)
        {
            File.WriteAllText(smoke.ReportPath, JsonSerializer.Serialize(new { ok = false, error = exception.ToString() }));
            Environment.Exit(1);
        }
    }
}
