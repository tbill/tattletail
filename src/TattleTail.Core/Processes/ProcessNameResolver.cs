using System.Diagnostics;

namespace TattleTail.Core;

public sealed record ProcessDetails(int ProcessId, string Name, string? Path);

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

            return new ProcessDetails(processId, process.ProcessName, path);
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
}
