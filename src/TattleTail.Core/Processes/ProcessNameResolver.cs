using System.Diagnostics;

namespace TattleTail.Core;

public sealed record ProcessDetails(int ProcessId, string Name, string? Path);

/// <summary>
/// Looks up a process's name and executable path from its id, with a cache so we don't
/// hit the OS for every connection every second.
/// </summary>
public sealed class ProcessNameResolver
{
    private readonly Dictionary<int, ProcessDetails> _cache = new();

    public ProcessDetails Resolve(int processId)
    {
        if (_cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var details = Lookup(processId);
        _cache[processId] = details;
        return details;
    }

    /// <summary>
    /// Drops cached entries for process ids that are no longer in use, so a recycled
    /// process id gets looked up again.
    /// </summary>
    public void Retain(IReadOnlySet<int> liveProcessIds)
    {
        foreach (var pid in _cache.Keys.Where(pid => !liveProcessIds.Contains(pid)).ToList())
        {
            _cache.Remove(pid);
        }
    }

    private static ProcessDetails Lookup(int processId)
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
            return new ProcessDetails(processId, "(exited)", null);
        }
        catch (InvalidOperationException)
        {
            return new ProcessDetails(processId, "(exited)", null);
        }
    }
}
