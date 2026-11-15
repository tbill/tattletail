using System.Net;
using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>One line in the "Connections" table.</summary>
public sealed class ConnectionRow : TrafficRowBase
{
    private string _state;
    private string? _remoteName;
    private AddressDisplay _display;

    public ConnectionRow(ConnectionInfo info, ProcessDetails process)
    {
        Id = MakeId(info);
        TrafficKey = info.TrafficKey;
        ProcessName = process.Name;
        ProcessPath = process.Path;
        ProcessId = info.ProcessId;
        Protocol = info.Protocol == Core.Protocol.Tcp ? "TCP" : "UDP";
        LocalAddress = info.Local.ToString();
        RemoteIp = info.Remote is null ? null : IpAddresses.Normalize(info.Remote.Address);
        RemotePort = info.Remote?.Port ?? 0;
        _state = info.State;
    }

    public string Id { get; }
    public TrafficKey TrafficKey { get; }
    public string ProcessName { get; }
    public string? ProcessPath { get; }
    public int ProcessId { get; }
    public string Protocol { get; }
    public string LocalAddress { get; }
    public IPAddress? RemoteIp { get; }
    public int RemotePort { get; }

    /// <summary>The remote server, shown as a name, an IP address or both (per the user's choice).</summary>
    public string RemoteText => RemoteIp is null ? string.Empty : AddressFormatter.Format(_remoteName, RemoteIp.ToString(), _display);

    /// <summary>Tooltip with every detail we know about the remote side.</summary>
    public string? RemoteDetails => RemoteIp is null
        ? null
        : _remoteName is null || _remoteName == RemoteIp.ToString() ? RemoteIp.ToString() : $"{_remoteName}\n{RemoteIp}";

    /// <summary>The website's domain, e.g. "github.com" for "api.github.com".</summary>
    public string RemoteDomain => RemoteIp is null
        ? string.Empty
        : PublicSuffixList.Default.GetDomain(_remoteName ?? RemoteIp.ToString());

    public string RemotePortText => RemotePort == 0 ? string.Empty : RemotePort.ToString();

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public void UpdateRemote(string? name, AddressDisplay display)
    {
        if (name == _remoteName && display == _display)
        {
            return;
        }

        _remoteName = name;
        _display = display;
        OnPropertyChanged(nameof(RemoteText));
        OnPropertyChanged(nameof(RemoteDetails));
        OnPropertyChanged(nameof(RemoteDomain));
    }

    public static string MakeId(ConnectionInfo info) =>
        $"{info.Protocol}|{info.ProcessId}|{info.Local}|{info.Remote}";

    public bool Matches(string filter) =>
        ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || ProcessId.ToString().Contains(filter, StringComparison.Ordinal)
        || Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || LocalAddress.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (RemoteIp?.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (_remoteName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || RemotePortText.Contains(filter, StringComparison.Ordinal)
        || State.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
