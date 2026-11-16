using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>One line in the "Programs" table: everything a single process has done.</summary>
public sealed class ProgramRow : TrafficRowBase
{
    private int _connectionCount;
    private bool _isRunning = true;

    public ProgramRow(ProcessInstance process, ProcessDetails details)
    {
        Process = process;
        ProcessId = details.ProcessId;
        ProcessName = details.DisplayName;
        ProcessPath = details.Path;
        ProgramKey = MakeKey(details);
    }

    /// <summary>This run of the program. Process ids get reused; this doesn't.</summary>
    public ProcessInstance Process { get; }

    /// <summary>
    /// The identity used for history: the executable path (plus, for svchost, the services
    /// inside), so a program keeps its history across restarts even though its process id changes.
    /// </summary>
    public static string MakeKey(ProcessDetails process) => ProgramIdentity.KeyFor(process);

    public string ProgramKey { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public string? ProcessPath { get; }

    public int ConnectionCount
    {
        get => _connectionCount;
        set => SetField(ref _connectionCount, value);
    }

    /// <summary>False once the program has exited (totals are kept).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    public bool Matches(string filter) =>
        ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || ProcessId.ToString().Contains(filter, StringComparison.Ordinal)
        || (ProcessPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
