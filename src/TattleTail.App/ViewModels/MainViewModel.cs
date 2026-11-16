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
    private readonly ProcessRegistry _processes = new();
    private readonly TrafficDeltaCalculator _deltas = new();
    private readonly HistoryStore? _store;
    private readonly TrafficHistory _history;
    private readonly NewProgramDetector _newPrograms;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ConnectionRow> _connectionsById = new();
    private readonly Dictionary<ProcessInstance, ProgramRow> _programsById = new();

    private DateTime _lastSnapshotUtc = DateTime.UtcNow;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private bool _refreshing;
    private string _filter = string.Empty;
    private string _trafficStatus = string.Empty;
    private string _namesStatus = string.Empty;
    private string _historyStatus = string.Empty;
    private string? _historyUnavailable;
    private string _trayText = "TattleTail";
    private bool _startWithWindows;
    private bool _startWithWindowsReady;
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
            _historyUnavailable = "History won't be saved: " + ex.Message;
            _historyStatus = _historyUnavailable;
        }

        _history = new TrafficHistory(
            _store,
            process => ProgramRow.MakeKey(_processes.GetDetails(process)),
            (ip, process) => _names.GetName(ip, _processes.GetDetails(process).ProcessId) ?? ip.ToString(),
            saveInBackground: true);

        _newPrograms = new NewProgramDetector(LoadKnownPrograms());

        HistoryPanel = new HistoryPanelViewModel(_history, _names, _settings);

        Connections = new ObservableCollection<ConnectionRow>();
        Programs = new ObservableCollection<ProgramRow>();

        ConnectionsView = ConfigureView(Connections, o => ((ConnectionRow)o).Matches(_filter));
        ProgramsView = ConfigureView(Programs, o =>
        {
            var row = (ProgramRow)o;
            return (_settings.ShowExitedPrograms || row.IsRunning) && row.Matches(_filter);
        }, nameof(ProgramRow.IsRunning));

        _trafficMonitor = new EtwTrafficMonitor(_aggregator, _processes);
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

    public string HistoryStatus
    {
        get => _historyStatus;
        private set => SetField(ref _historyStatus, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    /// <summary>Raised with the display names of programs that just used the network for the first time.</summary>
    public event Action<IReadOnlyList<string>>? NewProgramsDetected;

    /// <summary>Text for the tray icon's tooltip: the app name and current speeds.</summary>
    public string TrayText
    {
        get => _trayText;
        private set => SetField(ref _trayText, value);
    }

    /// <summary>Start TattleTail (hidden in the tray) when the user signs in to Windows.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows != value && _startWithWindowsReady)
            {
                _ = SetStartWithWindowsAsync(value);
            }
        }
    }

    /// <summary>False while Windows is being asked about, or told to change, the sign-in task.</summary>
    public bool StartWithWindowsReady
    {
        get => _startWithWindowsReady;
        private set => SetField(ref _startWithWindowsReady, value);
    }

    public bool KeepRunningInTray
    {
        get => _settings.KeepRunningInTray;
        set
        {
            if (_settings.KeepRunningInTray != value)
            {
                _settings.KeepRunningInTray = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool AlertOnNewPrograms
    {
        get => _settings.AlertOnNewPrograms;
        set
        {
            if (_settings.AlertOnNewPrograms != value)
            {
                _settings.AlertOnNewPrograms = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>True the first time it is asked, ever: whether to explain that closing only hides the window.</summary>
    public bool TakeTrayHint()
    {
        if (_settings.TrayHintShown)
        {
            return false;
        }

        _settings.TrayHintShown = true;
        _settings.Save();
        return true;
    }

    private async Task SetStartWithWindowsAsync(bool enable)
    {
        StartWithWindowsReady = false;
        try
        {
            await Task.Run(enable ? AutoStart.Enable : AutoStart.Disable);
            _startWithWindows = enable;
        }
        catch (Exception ex)
        {
            NamesStatus = "Could not change Start with Windows: " + ex.Message;
        }

        // Either the new value, or (after a failure) back to the old one.
        OnPropertyChanged(nameof(StartWithWindows));
        StartWithWindowsReady = true;
    }

    private async Task LoadStartWithWindowsAsync()
    {
        try
        {
            _startWithWindows = await Task.Run(AutoStart.IsEnabled);
        }
        catch
        {
            // Task Scheduler didn't answer; show it as off and let the user try the switch.
        }

        OnPropertyChanged(nameof(StartWithWindows));
        StartWithWindowsReady = true;
    }

    private List<string> LoadKnownPrograms()
    {
        try
        {
            return _store?.GetKnownPrograms() ?? [];
        }
        catch
        {
            return [];
        }
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
        _ = LoadStartWithWindowsAsync();
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
            _programsById.Remove(row.Process);
        }
    }

    /// <summary>Deletes all saved history (charts and server lists).</summary>
    public async Task ClearHistoryAsync()
    {
        try
        {
            // Off the UI thread: deleting waits its turn behind any history read in progress.
            await Task.Run(_history.Clear);
            _newPrograms.Forget();
        }
        catch (Exception ex)
        {
            HistoryStatus = "Could not clear history: " + ex.Message;
        }

        HistoryPanel.RequestRefresh();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        // This runs every second, so nothing in here may throw: an error that reached the
        // window would pop up again every second. Each step reports its own trouble in the
        // status bar and the remaining steps still run.
        _refreshing = true;
        try
        {
            List<ConnectionInfo>? connections = null;
            try
            {
                connections = await Task.Run(ConnectionTable.GetAll);
            }
            catch (Exception ex)
            {
                Summary = "Could not read connections: " + ex.Message;
            }

            var snapshot = _aggregator.Snapshot();
            var elapsed = (snapshot.TakenAtUtc - _lastSnapshotUtc).TotalSeconds;
            _lastSnapshotUtc = snapshot.TakenAtUtc;

            if (connections is not null)
            {
                try
                {
                    var programs = AssignPrograms(connections);
                    UpdateConnections(programs, snapshot, elapsed);
                    UpdatePrograms(programs, snapshot, elapsed);
                    UpdateRemoteNames();
                    UpdateSummary();
                }
                catch (Exception ex)
                {
                    Summary = "Could not update the tables: " + ex.Message;
                }
            }

            try
            {
                var deltas = _deltas.Next(snapshot.ByRemote);
                _history.Add(deltas);
                CheckForNewPrograms(deltas);
                _aggregator.PruneConnections(ConnectionTrafficRetention);
                PruneHistoryIfDue();
                UpdateHistoryStatus();
            }
            catch (Exception ex)
            {
                HistoryStatus = "Could not record history: " + ex.Message;
            }
        }
        finally
        {
            _refreshing = false;
        }

        await HistoryPanel.TickAsync();
    }

    /// <summary>Notes, and if wanted announces, programs using the network for the first time.</summary>
    private void CheckForNewPrograms(List<TrafficDelta> deltas)
    {
        // Talking to yourself (localhost) or shouting into the room (broadcast, multicast) isn't
        // "using the network" in the sense people care about here.
        var programs = deltas
            .Where(d => IpAddresses.SpecialName(d.Remote) is null)
            .Select(d => d.Process)
            .Distinct()
            .Select(_processes.GetDetails)
            .Where(d => d.Name != ProcessRegistry.UnknownName)
            .Select(d => (Key: ProgramRow.MakeKey(d), Name: d.DisplayName))
            .ToList();
        if (programs.Count == 0)
        {
            return;
        }

        var result = _newPrograms.Observe(programs.Select(p => p.Key));
        if (result.Added.Count == 0)
        {
            return;
        }

        if (_store is { } store)
        {
            var minute = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMinutes;
            _ = Task.Run(() =>
            {
                try
                {
                    store.AddKnownPrograms(result.Added, minute);
                }
                catch
                {
                    // Worst case the program is announced again after a restart.
                }
            });
        }

        if (result.Announce && _settings.AlertOnNewPrograms)
        {
            var added = new HashSet<string>(result.Added, StringComparer.OrdinalIgnoreCase);
            var names = programs.Where(p => added.Contains(p.Key)).Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            NewProgramsDetected?.Invoke(names);
        }
    }

    private void UpdateHistoryStatus()
    {
        HistoryStatus = _historyUnavailable
            ?? (_history.SaveError is { } error ? "History isn't being saved (will keep trying): " + error : string.Empty);
    }

    /// <summary>Pairs every socket with the program that owns it right now.</summary>
    private List<(ConnectionInfo Info, ProcessInstance Process)> AssignPrograms(List<ConnectionInfo> connections)
    {
        // Normally the kernel tells us when programs start and exit. Without that feed, the
        // best clue that a process id has changed hands is that it stopped owning any sockets.
        if (!TrafficAvailable)
        {
            _processes.MarkExitedExcept(connections.Select(c => c.ProcessId).ToHashSet());
        }

        return connections.Select(c => (c, _processes.GetInstance(c.ProcessId))).ToList();
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

    private void UpdateConnections(List<(ConnectionInfo Info, ProcessInstance Process)> connections,
        TrafficSnapshot snapshot, double elapsed)
    {
        var seen = new HashSet<string>();
        foreach (var (info, process) in connections)
        {
            var id = ConnectionRow.MakeId(info, process);
            if (!seen.Add(id))
            {
                continue;
            }

            snapshot.ByConnection.TryGetValue(info.TrafficKeyFor(process), out var traffic);

            if (_connectionsById.TryGetValue(id, out var row))
            {
                row.State = info.State;
                row.UpdateTraffic(traffic, elapsed);
            }
            else
            {
                row = new ConnectionRow(info, process, _processes.GetDetails(process));
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

    private void UpdatePrograms(List<(ConnectionInfo Info, ProcessInstance Process)> connections,
        TrafficSnapshot snapshot, double elapsed)
    {
        var connectionCounts = connections.GroupBy(c => c.Process).ToDictionary(g => g.Key, g => g.Count());
        var programs = new HashSet<ProcessInstance>(connectionCounts.Keys);
        programs.UnionWith(snapshot.ByProcess.Keys);

        foreach (var process in programs)
        {
            snapshot.ByProcess.TryGetValue(process, out var traffic);
            connectionCounts.TryGetValue(process, out var count);

            if (!_programsById.TryGetValue(process, out var row))
            {
                row = new ProgramRow(process, _processes.GetDetails(process));
                row.UpdateTraffic(traffic, 0);
                _programsById[process] = row;
                Programs.Add(row);
            }
            else
            {
                row.UpdateTraffic(traffic, elapsed);
            }

            row.ConnectionCount = count;
            row.IsRunning = !_processes.HasExited(process);
        }

        foreach (var gone in _programsById.Keys.Where(process => !programs.Contains(process)).ToList())
        {
            var row = _programsById[gone];
            if (HistoryPanel.SelectedProgram == row)
            {
                HistoryPanel.SelectedProgram = null;
            }

            Programs.Remove(row);
            _programsById.Remove(gone);
        }

        _processes.Retain(programs);
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
        TrayText = TrafficAvailable
            ? $"TattleTail\n↑ {ByteFormatter.Format(Programs.Sum(p => p.SendRate))}/s   ↓ {ByteFormatter.Format(Programs.Sum(p => p.ReceiveRate))}/s"
            : "TattleTail";
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
