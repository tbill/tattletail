using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>One line in the "Programs" table: everything a single process has done.</summary>
public sealed class ProgramRow : TrafficRowBase
{
    private int _connectionCount;
    private bool _isRunning = true;

    public ProgramRow(ProcessDetails process)
    {
        ProcessId = process.ProcessId;
        ProcessName = process.Name;
        ProcessPath = process.Path;
    }

    public int ProcessId { get; }
    public string ProcessName { get; }
    public string? ProcessPath { get; }

    public int ConnectionCount
    {
        get => _connectionCount;
        set => SetField(ref _connectionCount, value);
    }

    /// <summary>False once the program has no open sockets left (totals are kept).</summary>
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
