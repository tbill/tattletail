using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using TattleTail.Core;

namespace TattleTail.App;

/// <summary>
/// Turns "start TattleTail when I sign in" on and off by creating or deleting a Windows
/// scheduled task (see <see cref="AutoStartTask"/> for why a task and not the startup folder).
/// </summary>
internal static class AutoStart
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Can't tell where TattleTail.exe is.");

    /// <summary>The program the task currently starts, or null if there is no task.</summary>
    public static string? RegisteredCommand()
    {
        var (exitCode, output, _) = RunSchtasks("/Query", "/TN", AutoStartTask.TaskName, "/XML");
        return exitCode == 0 ? AutoStartTask.ReadCommand(output) : null;
    }

    /// <summary>
    /// Whether TattleTail starts at sign-in. If the task points at an old location (the exe was
    /// moved), it is re-pointed at this copy.
    /// </summary>
    public static bool IsEnabled()
    {
        var command = RegisteredCommand();
        if (command is null)
        {
            return false;
        }

        if (!AutoStartTask.PointsAt(command, ExePath))
        {
            Enable();
        }

        return true;
    }

    public static void Enable()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var xml = AutoStartTask.BuildXml(ExePath, identity.Name,
            identity.User?.Value ?? throw new InvalidOperationException("Can't tell which Windows account this is."));

        var file = Path.Combine(Path.GetTempPath(), $"tattletail-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // UTF-16, as the XML header says
            var (exitCode, output, error) = RunSchtasks("/Create", "/TN", AutoStartTask.TaskName, "/XML", file, "/F");
            if (exitCode != 0)
            {
                throw new InvalidOperationException(FirstLine(error, output, $"schtasks failed with code {exitCode}"));
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable()
    {
        if (RegisteredCommand() is null)
        {
            return;
        }

        var (exitCode, output, error) = RunSchtasks("/Delete", "/TN", AutoStartTask.TaskName, "/F");
        if (exitCode != 0)
        {
            throw new InvalidOperationException(FirstLine(error, output, $"schtasks failed with code {exitCode}"));
        }
    }

    /// <summary>
    /// Creates the task, reads it back and removes it again, leaving things as they were.
    /// Used by the smoke test to prove the task definition is one Windows accepts.
    /// </summary>
    public static string SelfTest()
    {
        try
        {
            var before = RegisteredCommand();
            Enable();
            var during = RegisteredCommand();
            Disable();
            var after = RegisteredCommand();
            if (before is not null)
            {
                Enable();
            }

            if (!AutoStartTask.PointsAt(during, ExePath))
            {
                return $"task was created but starts '{during}' instead of '{ExePath}'";
            }

            return after is null ? "ok" : "task was not removed";
        }
        catch (Exception ex)
        {
            return "failed: " + ex.Message;
        }
    }

    private static (int ExitCode, string Output, string Error) RunSchtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        // Read both streams while waiting, so a full pipe can't stall schtasks.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill();
            throw new TimeoutException("Windows Task Scheduler did not answer.");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static string FirstLine(params string[] candidates) =>
        candidates.Select(c => c.Trim()).First(c => c.Length > 0).Split('\n')[0].Trim();
}
