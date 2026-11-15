using System.Reflection;

namespace TattleTail.App;

/// <summary>The version people see, e.g. "26-11.R2.0", or "dev build a870b4d" for non-release builds.</summary>
internal static class AppVersion
{
    public static string Display { get; } = Describe(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Describe(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return "dev build";
        }

        // The SDK appends "+<git commit>" to the informational version for non-release builds.
        var parts = informational.Split('+', 2);
        var version = parts[0];
        var commit = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : null;

        return version == "dev"
            ? commit is null ? "dev build" : $"dev build {commit}"
            : version;
    }
}
