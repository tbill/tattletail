namespace TattleTail.Core;

/// <summary>
/// How a program is identified in saved history, in the known-programs list and on screen.
/// </summary>
/// <remarks>
/// The identity is the executable's path, so a program keeps its history across restarts even
/// though its process id changes. Every <c>svchost.exe</c> has the same path, so for those the
/// services inside are added: Windows Update and the DNS client each get their own history.
/// The two parts are joined with '|', which can't appear in a Windows path.
/// </remarks>
public static class ProgramIdentity
{
    public const string ServiceHostName = "svchost";
    private const char Separator = '|';
    private const int MaxServicesInLabel = 3;

    /// <summary>True for the generic Windows service host, whose own name says nothing about what it does.</summary>
    public static bool IsServiceHost(string? processName) =>
        string.Equals(processName, ServiceHostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One short label for the services in a process: "Windows Update", or for a shared host
    /// "DHCP Client, DNS Client, Network Store Interface Service +4 more". Null if there are none.
    /// </summary>
    public static string? ServiceLabel(IEnumerable<string> displayNames)
    {
        // Sorted, so the same set of services always gives the same label (it's part of the identity).
        var names = displayNames
            .Select(n => n.Trim().Replace(Separator, '/'))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var label = string.Join(", ", names.Take(MaxServicesInLabel));
        return names.Count > MaxServicesInLabel ? $"{label} +{names.Count - MaxServicesInLabel} more" : label;
    }

    /// <summary>The identity stored in history for <paramref name="process"/>.</summary>
    public static string KeyFor(ProcessDetails process)
    {
        var program = process.Path ?? process.Name;
        return process.Service is null ? program : program + Separator + process.Service;
    }

    /// <summary>
    /// A short name for a stored identity: "chrome" for <c>C:\…\chrome.exe</c>,
    /// "svchost (Windows Update)" for <c>C:\…\svchost.exe|Windows Update</c>.
    /// </summary>
    public static string DisplayName(string key)
    {
        var split = key.IndexOf(Separator);
        var program = split < 0 ? key : key[..split];
        var service = split < 0 ? null : key[(split + 1)..];

        // Done by hand rather than with Path, so it behaves the same wherever the code runs.
        var slash = program.LastIndexOf('\\');
        if (slash >= 0)
        {
            program = program[(slash + 1)..];
            var dot = program.LastIndexOf('.');
            if (dot > 0)
            {
                program = program[..dot];
            }
        }

        return string.IsNullOrEmpty(service) ? program : $"{program} ({service})";
    }
}
