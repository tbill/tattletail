using System.Xml.Linq;

namespace TattleTail.Core;

/// <summary>
/// Describes the Windows scheduled task that starts TattleTail when the user signs in.
/// </summary>
/// <remarks>
/// TattleTail needs administrator rights, and Windows won't start such a program from the usual
/// "run at startup" list without asking for permission every time. A scheduled task marked
/// "run with highest privileges" is the standing permission slip: approved once when it is
/// created, then honoured at every sign-in without a prompt.
/// </remarks>
public static class AutoStartTask
{
    public const string TaskName = "TattleTail";

    /// <summary>Command-line switch that starts TattleTail hidden in the system tray.</summary>
    public const string BackgroundArgument = "--background";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The task definition, in the XML form <c>schtasks /Create /XML</c> accepts.</summary>
    /// <param name="exePath">Full path of TattleTail.exe.</param>
    /// <param name="userName">Account that signs in, e.g. <c>PC\Bill</c>.</param>
    /// <param name="userSid">That account's security identifier, e.g. <c>S-1-5-21-…</c>.</param>
    public static string BuildXml(string exePath, string userName, string userSid)
    {
        // Elements are in the order Task Scheduler itself writes them.
        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Starts TattleTail in the system tray when you sign in, so traffic history has no gaps.")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", userName))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    // Run in the signed-in user's own desktop session (so the tray icon shows), elevated.
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                // The defaults would refuse to start on battery and stop when unplugged.
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                // The default limit would close TattleTail after three days. PT0S means no limit.
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                // The default (7) runs tasks at below-normal priority; 4 is normal, like any app.
                new XElement(Ns + "Priority", "4")),
            new XElement(Ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", exePath),
                    new XElement(Ns + "Arguments", BackgroundArgument))));

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + task;
    }

    /// <summary>
    /// The program a task definition starts (as printed by <c>schtasks /Query /XML</c>), or null
    /// if the text isn't a task definition.
    /// </summary>
    public static string? ReadCommand(string? taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml))
        {
            return null;
        }

        try
        {
            // schtasks may print a line or two before the XML itself.
            var start = taskXml.IndexOf('<');
            var command = start < 0 ? null : XDocument.Parse(taskXml[start..]).Descendants(Ns + "Command").FirstOrDefault()?.Value;
            return string.IsNullOrWhiteSpace(command) ? null : command.Trim().Trim('"');
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>True if the registered task starts this copy of TattleTail (not one that has since been moved).</summary>
    public static bool PointsAt(string? registeredCommand, string exePath) =>
        registeredCommand is not null && string.Equals(registeredCommand, exePath, StringComparison.OrdinalIgnoreCase);
}
