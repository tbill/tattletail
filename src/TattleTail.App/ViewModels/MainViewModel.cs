using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>
/// The brains behind the main window. Once a second it reads the connection table,
/// takes a traffic snapshot, and updates the two tables.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectionTrafficRetention = TimeSpan.FromMinutes(10);

    private readonly TrafficAggregator _aggregator = new();
    private readonly EtwTrafficMonitor _monitor;
    private readonly ProcessNameResolver _resolver = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ConnectionRow> _connectionsById = new();
    private readonly Dictionary<int, ProgramRow> _programsById = new();

    private DateTime _lastSnapshotUtc = DateTime.UtcNow;
    private bool _refreshing;
    private string _filter = string.Empty;
    private string _trafficStatus = string.Empty;
    private string _summary = string.Empty;
    private bool _trafficAvailable;
    private bool _showExitedPrograms = true;

    public MainViewModel()
    {
        Connections = new ObservableCollection<ConnectionRow>();
        Programs = new ObservableCollection<ProgramRow>();

        ConnectionsView = ConfigureView(Connections, o => ((ConnectionRow)o).Matches(_filter));
        ProgramsView = ConfigureView(Programs, o =>
        {
            var row = (ProgramRow)o;
            return (_showExitedPrograms || row.IsRunning) && row.Matches(_filter);
        }, nameof(ProgramRow.IsRunning));

        _monitor = new EtwTrafficMonitor(_aggregator);
        _monitor.Faulted += ex => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            TrafficAvailable = false;
            TrafficStatus = "Traffic monitoring stopped: " + ex.Message;
        });

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ConnectionRow> Connections { get; }
    public ObservableCollection<ProgramRow> Programs { get; }
    public ICollectionView ConnectionsView { get; }
    public ICollectionView ProgramsView { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                ConnectionsView.Refresh();
                ProgramsView.Refresh();
            }
        }
    }

    public bool ShowExitedPrograms
    {
        get => _showExitedPrograms;
        set
        {
            if (SetField(ref _showExitedPrograms, value))
            {
                ProgramsView.Refresh();
            }
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

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public void Start()
    {
        try
        {
            _monitor.Start();
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

        _ = RefreshAsync();
        _timer.Start();
    }

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

        // Programs that only existed in the history disappear with it.
        foreach (var row in Programs.Where(p => !p.IsRunning).ToList())
        {
            Programs.Remove(row);
            _programsById.Remove(row.ProcessId);
        }
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
            UpdateSummary();

            _aggregator.PruneConnections(ConnectionTrafficRetention);
        }
        catch (Exception ex)
        {
            Summary = "Could not read connections: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
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
            Programs.Remove(_programsById[gone]);
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

    public void Dispose()
    {
        _timer.Stop();
        _monitor.Dispose();
    }
}
