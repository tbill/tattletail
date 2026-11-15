using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Windows.Data;
using System.Windows.Threading;
using TattleTail.Core;

namespace TattleTail.App.ViewModels;

public sealed record AddressDisplayOption(AddressDisplay Value, string Label);

/// <summary>
/// The brains behind the main window. Once a second it reads the connection table, takes a
/// traffic snapshot, records history, and updates the tables and chart.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectionTrafficRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly AppSettings _settings;
    private readonly TrafficAggregator _aggregator = new();
    private readonly EtwTrafficMonitor _trafficMonitor;
    private readonly HostNameCache _names = new();
    private readonly EtwDnsMonitor _dnsMonitor;
    private readonly ProcessNameResolver _resolver = new();
    private readonly TrafficDeltaCalculator _deltas = new();
    private readonly HistoryStore? _store;
    private readonly TrafficHistory _history;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ConnectionRow> _connectionsById = new();
    private readonly Dictionary<int, ProgramRow> _programsById = new();

    private DateTime _lastSnapshotUtc = DateTime.UtcNow;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private bool _refreshing;
    private string _filter = string.Empty;
    private string _trafficStatus = string.Empty;
    private string _namesStatus = string.Empty;
    private string _summary = string.Empty;
    private bool _trafficAvailable;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _names.ReverseLookupEnabled = _settings.ReverseDnsLookups;

        try
        {
            _store = new HistoryStore(HistoryStore.DefaultPath);
        }
        catch (Exception ex)
        {
            // Still useful without saved history: charts just cover this session.
            _namesStatus = "History won't be saved: " + ex.Message;
        }

        _history = new TrafficHistory(
            _store,
            pid => ProgramRow.MakeKey(_resolver.Resolve(pid)),
            (ip, pid) => _names.GetName(ip, pid) ?? ip.ToString());

        HistoryPanel = new HistoryPanelViewModel(_history, _names, _settings);

        Connections = new ObservableCollection<ConnectionRow>();
        Programs = new ObservableCollection<ProgramRow>();

        ConnectionsView = ConfigureView(Connections, o => ((ConnectionRow)o).Matches(_filter));
        ProgramsView = ConfigureView(Programs, o =>
        {
            var row = (ProgramRow)o;
            return (_settings.ShowExitedPrograms || row.IsRunning) && row.Matches(_filter);
        }, nameof(ProgramRow.IsRunning));

        _trafficMonitor = new EtwTrafficMonitor(_aggregator);
        _trafficMonitor.Faulted += ex => OnUiThread(() =>
        {
            TrafficAvailable = false;
            TrafficStatus = "Traffic monitoring stopped: " + ex.Message;
        });

        _dnsMonitor = new EtwDnsMonitor(_names);
        _dnsMonitor.Faulted += ex => OnUiThread(() => NamesStatus = "Website names stopped: " + ex.Message);

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ConnectionRow> Connections { get; }
    public ObservableCollection<ProgramRow> Programs { get; }
    public ICollectionView ConnectionsView { get; }
    public ICollectionView ProgramsView { get; }
    public HistoryPanelViewModel HistoryPanel { get; }

    public IReadOnlyList<AddressDisplayOption> AddressDisplayOptions { get; } =
    [
        new(AddressDisplay.Names, "Website names"),
        new(AddressDisplay.Addresses, "IP addresses"),
        new(AddressDisplay.Both, "Names and IP addresses"),
    ];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                ConnectionsView.Refresh();
                ProgramsView.Refresh();
                HistoryPanel.Filter = _filter;
            }
        }
    }

    public AddressDisplay AddressDisplay
    {
        get => _settings.AddressDisplay;
        set
        {
            if (_settings.AddressDisplay == value)
            {
                return;
            }

            _settings.AddressDisplay = value;
            _settings.Save();
            OnPropertyChanged();
            HistoryPanel.Display = value;
            UpdateRemoteNames();
        }
    }

    public bool ReverseDnsLookups
    {
        get => _settings.ReverseDnsLookups;
        set
        {
            if (_settings.ReverseDnsLookups == value)
            {
                return;
            }

            _settings.ReverseDnsLookups = value;
            _names.ReverseLookupEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowExitedPrograms
    {
        get => _settings.ShowExitedPrograms;
        set
        {
            if (_settings.ShowExitedPrograms == value)
            {
                return;
            }

            _settings.ShowExitedPrograms = value;
            _settings.Save();
            OnPropertyChanged();
            ProgramsView.Refresh();
        }
    }

    public bool TrafficAvailable
    {
        get => _trafficAvailable;
        private set => SetField(ref _trafficAvailable, value);
    }

    public string TrafficStatus
    {
        get => _trafficStatus;
        private set => SetField(ref _trafficStatus, value);
    }

    public string NamesStatus
    {
        get => _namesStatus;
        private set => SetField(ref _namesStatus, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public void Start()
    {
        try
        {
            _trafficMonitor.Start();
            TrafficAvailable = true;
            TrafficStatus = "Measuring traffic";
        }
        catch (Exception ex)
        {
            TrafficAvailable = false;
            TrafficStatus = EtwTrafficMonitor.IsElevated()
                ? "Traffic unavailable: " + ex.Message
                : "Traffic unavailable: run TattleTail as administrator";
        }

        try
        {
            _dnsMonitor.Start();
        }
        catch (Exception ex)
        {
            NamesStatus = "Website names from DNS unavailable: " + ex.Message;
        }

        _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>Sets the live totals back to zero. Saved history is kept.</summary>
    public void ResetCounters()
    {
        _aggregator.Reset();
        foreach (var row in Connections)
        {
            row.UpdateTraffic(default, 0);
        }

        foreach (var row in Programs)
        {
            row.UpdateTraffic(default, 0);
        }

        // Programs that only existed in the totals disappear with them.
        foreach (var row in Programs.Where(p => !p.IsRunning).ToList())
        {
            Programs.Remove(row);
            _programsById.Remove(row.ProcessId);
        }
    }

    /// <summary>Deletes all saved history (charts and server lists).</summary>
    public void ClearHistory()
    {
        _history.Clear();
        HistoryPanel.RequestRefresh();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var connections = await Task.Run(ConnectionTable.GetAll);
            var snapshot = _aggregator.Snapshot();
            var elapsed = (snapshot.TakenAtUtc - _lastSnapshotUtc).TotalSeconds;
            _lastSnapshotUtc = snapshot.TakenAtUtc;

            UpdateConnections(connections, snapshot, elapsed);
            UpdatePrograms(connections, snapshot, elapsed);
            UpdateRemoteNames();
            UpdateSummary();

            _history.Add(_deltas.Next(snapshot.ByRemote));
            _aggregator.PruneConnections(ConnectionTrafficRetention);
            PruneHistoryIfDue();
        }
        catch (Exception ex)
        {
            Summary = "Could not read connections: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }

        await HistoryPanel.TickAsync();
    }

    private void PruneHistoryIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < PruneInterval)
        {
            return;
        }

        _lastPruneUtc = now;
        var retention = TimeSpan.FromDays(Math.Max(1, _settings.HistoryRetentionDays));
        _ = Task.Run(() => _history.Prune(retention));
    }

    private void UpdateConnections(List<ConnectionInfo> connections, TrafficSnapshot snapshot, double elapsed)
    {
        var seen = new HashSet<string>();
        foreach (var info in connections)
        {
            var id = ConnectionRow.MakeId(info);
            if (!seen.Add(id))
            {
                continue;
            }

            snapshot.ByConnection.TryGetValue(info.TrafficKey, out var traffic);

            if (_connectionsById.TryGetValue(id, out var row))
            {
                row.State = info.State;
                row.UpdateTraffic(traffic, elapsed);
            }
            else
            {
                row = new ConnectionRow(info, _resolver.Resolve(info.ProcessId));
                row.UpdateTraffic(traffic, 0);
                _connectionsById[id] = row;
                Connections.Add(row);
            }
        }

        foreach (var gone in _connectionsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Connections.Remove(_connectionsById[gone]);
            _connectionsById.Remove(gone);
        }
    }

    private void UpdateRemoteNames()
    {
        foreach (var row in Connections)
        {
            row.UpdateRemote(LookupName(row.RemoteIp, row.ProcessId), _settings.AddressDisplay);
        }
    }

    private string? LookupName(IPAddress? ip, int processId)
    {
        if (ip is null || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return null;
        }

        return _names.GetName(ip, processId);
    }

    private void UpdatePrograms(List<ConnectionInfo> connections, TrafficSnapshot snapshot, double elapsed)
    {
        var connectionCounts = connections.GroupBy(c => c.ProcessId).ToDictionary(g => g.Key, g => g.Count());
        var pids = new HashSet<int>(connectionCounts.Keys);
        pids.UnionWith(snapshot.ByProcess.Keys);

        foreach (var pid in pids)
        {
            snapshot.ByProcess.TryGetValue(pid, out var traffic);
            connectionCounts.TryGetValue(pid, out var count);

            if (!_programsById.TryGetValue(pid, out var row))
            {
                row = new ProgramRow(_resolver.Resolve(pid));
                row.UpdateTraffic(traffic, 0);
                _programsById[pid] = row;
                Programs.Add(row);
            }
            else
            {
                row.UpdateTraffic(traffic, elapsed);
            }

            row.ConnectionCount = count;
            row.IsRunning = count > 0;
        }

        foreach (var gone in _programsById.Keys.Where(pid => !pids.Contains(pid)).ToList())
        {
            var row = _programsById[gone];
            if (HistoryPanel.SelectedProgram == row)
            {
                HistoryPanel.SelectedProgram = null;
            }

            Programs.Remove(row);
            _programsById.Remove(gone);
        }

        _resolver.Retain(pids);
    }

    private void UpdateSummary()
    {
        var tcp = Connections.Count(c => c.Protocol == "TCP");
        var udp = Connections.Count - tcp;
        var running = Programs.Count(p => p.IsRunning);
        var text = $"{tcp} TCP · {udp} UDP · {running} programs";

        if (TrafficAvailable)
        {
            var up = Programs.Sum(p => p.SendRate);
            var down = Programs.Sum(p => p.ReceiveRate);
            text += $"   ↑ {ByteFormatter.Format(up)}/s   ↓ {ByteFormatter.Format(down)}/s";
        }

        Summary = text;
    }

    private static ICollectionView ConfigureView<T>(ObservableCollection<T> source, Predicate<object> filter,
        params string[] liveFilterProperties)
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(source);
        view.Filter = filter;

        // Re-sort rows as their numbers change, using whatever column the user clicked.
        view.IsLiveSorting = true;
        if (liveFilterProperties.Length > 0)
        {
            view.IsLiveFiltering = true;
            foreach (var property in liveFilterProperties)
            {
                view.LiveFilteringProperties.Add(property);
            }
        }

        return view;
    }

    private static void OnUiThread(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _timer.Stop();
        _trafficMonitor.Dispose();
        _dnsMonitor.Dispose();

        try
        {
            _history.Flush();
        }
        catch
        {
            // Losing the last partial minute on exit is acceptable.
        }

        _store?.Dispose();
    }
}
