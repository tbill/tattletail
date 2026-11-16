using System.Diagnostics;

namespace TattleTail.Core;

/// <param name="Service">
/// For the Windows service host (svchost.exe): the services running inside it, e.g. "Windows Update".
/// </param>
public sealed record ProcessDetails(int ProcessId, string Name, string? Path, string? Service = null)
{
    /// <summary>The name to show: "chrome", or "svchost (Windows Update)".</summary>
    public string DisplayName => Service is null ? Name : $"{Name} ({Service})";
}

/// <summary>Asks the operating system for a process's name and executable path.</summary>
public static class ProcessNameResolver
{
    /// <summary>Details of the process running under <paramref name="processId"/>, or null if there is none.</summary>
    public static ProcessDetails? Lookup(int processId)
    {
        switch (processId)
        {
            case 0:
                return new ProcessDetails(0, "System Idle Process", null);
            case 4:
                return new ProcessDetails(4, "System", null);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // Protected or 32/64-bit mismatched processes don't expose their modules.
            }

            return new ProcessDetails(processId, process.ProcessName, path, ServicesIn(processId, process.ProcessName));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>For svchost.exe, a label naming the services inside it; otherwise null.</summary>
    private static string? ServicesIn(int processId, string processName)
    {
        if (!OperatingSystem.IsWindows() || !ProgramIdentity.IsServiceHost(processName))
        {
            return null;
        }

        try
        {
            return ProgramIdentity.ServiceLabel(ServiceTable.GetForProcess(processId).Select(s => s.DisplayName));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // plain "svchost" is still better than nothing
        }
    }
}
