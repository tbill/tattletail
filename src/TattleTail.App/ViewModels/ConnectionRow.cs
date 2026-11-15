using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>One line in the "Connections" table.</summary>
public sealed class ConnectionRow : TrafficRowBase
{
    private string _state;

    public ConnectionRow(ConnectionInfo info, ProcessDetails process)
    {
        Id = MakeId(info);
        TrafficKey = info.TrafficKey;
        ProcessName = process.Name;
        ProcessPath = process.Path;
        ProcessId = info.ProcessId;
        Protocol = info.Protocol == Core.Protocol.Tcp ? "TCP" : "UDP";
        LocalAddress = info.Local.ToString();
        RemoteAddress = info.Remote?.ToString() ?? string.Empty;
        _state = info.State;
    }

    public string Id { get; }
    public TrafficKey TrafficKey { get; }
    public string ProcessName { get; }
    public string? ProcessPath { get; }
    public int ProcessId { get; }
    public string Protocol { get; }
    public string LocalAddress { get; }
    public string RemoteAddress { get; }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public static string MakeId(ConnectionInfo info) =>
        $"{info.Protocol}|{info.ProcessId}|{info.Local}|{info.Remote}";

    public bool Matches(string filter) =>
        ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || ProcessId.ToString().Contains(filter, StringComparison.Ordinal)
        || Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || LocalAddress.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || RemoteAddress.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || State.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
