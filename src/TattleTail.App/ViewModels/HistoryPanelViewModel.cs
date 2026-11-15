using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows.Data;
using TattleTail.Core;

namespace TattleTail.App.ViewModels;

public sealed record RangeOption(HistoryRange Value, string Label);

/// <summary>
/// The lower half of the Programs tab: a traffic chart and the list of servers, for either
/// the selected program or all programs.
/// </summary>
public sealed class HistoryPanelViewModel : ObservableObject
{
    private const int SlowChartRefreshTicks = 15;
    private const int ServerRefreshTicks = 5;

    private readonly TrafficHistory _history;
    private readonly HostNameCache _names;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, ServerRow> _serversByKey = new();

    private ProgramRow? _selectedProgram;
    private HistoryRange _range;
    private AddressDisplay _display;
    private IReadOnlyList<SeriesPoint> _points = [];
    private string _totalsText = string.Empty;
    private string _filter = string.Empty;
    private bool _busy;
    private bool _refreshRequested = true;
    private int _tick;

    public HistoryPanelViewModel(TrafficHistory history, HostNameCache names, AppSettings settings)
    {
        _history = history;
        _names = names;
        _settings = settings;
        _range = settings.HistoryRange;
        _display = settings.AddressDisplay;

        Servers = new ObservableCollection<ServerRow>();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Servers);
        view.Filter = o => ((ServerRow)o).Matches(_filter);
        view.SortDescriptions.Add(new SortDescription(nameof(ServerRow.TotalBytes), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        ServersView = view;
    }

    /// <summary>Raised when <see cref="Points"/> changes, so the window can redraw the chart.</summary>
    public event Action? PointsChanged;

    public IReadOnlyList<RangeOption> Ranges { get; } = Enum.GetValues<HistoryRange>()
        .Select(r => new RangeOption(r, HistoryRanges.Describe(r)))
        .ToList();

    public ObservableCollection<ServerRow> Servers { get; }
    public ICollectionView ServersView { get; }

    public ProgramRow? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetField(ref _selectedProgram, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(HasSelection));
                RequestRefresh();
            }
        }
    }

    public bool HasSelection => _selectedProgram is not null;

    public string Title => _selectedProgram is null
        ? "All programs"
        : $"{_selectedProgram.ProcessName} (PID {_selectedProgram.ProcessId})";

    public HistoryRange Range
    {
        get => _range;
        set
        {
            if (SetField(ref _range, value))
            {
                _settings.HistoryRange = value;
                _settings.Save();
                RequestRefresh();
            }
        }
    }

    public AddressDisplay Display
    {
        get => _display;
        set
        {
            if (SetField(ref _display, value))
            {
                // The grouping changes (by name vs by address), so rebuild the list.
                Servers.Clear();
                _serversByKey.Clear();
                RequestRefresh();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                ServersView.Refresh();
            }
        }
    }

    public IReadOnlyList<SeriesPoint> Points
    {
        get => _points;
        private set
        {
            _points = value;
            PointsChanged?.Invoke();
        }
    }

    public string TotalsText
    {
        get => _totalsText;
        private set => SetField(ref _totalsText, value);
    }

    public void RequestRefresh() => _refreshRequested = true;

    /// <summary>Called once a second by the main view model.</summary>
    public async Task TickAsync()
    {
        _tick++;
        if (_busy)
        {
            return;
        }

        var forced = _refreshRequested;
        var updateChart = forced || _range == HistoryRange.TenMinutes || _tick % SlowChartRefreshTicks == 0;
        var updateServers = forced || _tick % ServerRefreshTicks == 0;
        if (!updateChart && !updateServers)
        {
            return;
        }

        _busy = true;
        _refreshRequested = false;
        try
        {
            var program = _selectedProgram?.ProgramKey ?? TrafficHistory.AllPrograms;
            var range = _range;

            // Database reads happen off the UI thread so the window never stutters.
            var (points, remotes) = await Task.Run(() => (
                updateChart ? _history.GetSeries(range, program) : null,
                updateServers ? _history.GetRemoteTotals(range, program) : null));

            if (program != (_selectedProgram?.ProgramKey ?? TrafficHistory.AllPrograms) || range != _range)
            {
                return; // the selection changed while we were reading; the next tick catches up
            }

            if (points is not null)
            {
                Points = points;
            }

            if (remotes is not null)
            {
                UpdateServers(remotes);
                TotalsText = $"{HistoryRanges.Describe(range)}:  ↑ {ByteFormatter.Format(remotes.Sum(r => r.Sent))} sent" +
                             $"   ↓ {ByteFormatter.Format(remotes.Sum(r => r.Received))} received" +
                             $"   ·  {Servers.Count} servers";
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void UpdateServers(List<HistoryRow> remotes)
    {
        var groups = remotes
            .Select(r => (Row: r, Name: BestName(r)))
            .GroupBy(x => _display switch
            {
                AddressDisplay.Names => x.Name,
                AddressDisplay.Addresses => x.Row.Ip,
                _ => x.Name + "|" + x.Row.Ip,
            });

        var seen = new HashSet<string>();
        foreach (var group in groups)
        {
            seen.Add(group.Key);
            if (!_serversByKey.TryGetValue(group.Key, out var row))
            {
                row = new ServerRow(group.Key);
                _serversByKey[group.Key] = row;
                Servers.Add(row);
            }

            var first = group.First();
            var ips = group.Select(x => x.Row.Ip).Distinct().ToList();
            var programs = group.Select(x => DisplayName(x.Row.Program)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            row.Server = _display == AddressDisplay.Names
                ? first.Name
                : AddressFormatter.Format(first.Name, first.Row.Ip, _display);
            row.Details = string.Join("\n",
                new[] { first.Name }.Concat(ips.Where(ip => ip != first.Name)).Append("Programs: " + string.Join(", ", programs)));
            row.Programs = string.Join(", ", programs);
            row.ProgramCount = programs.Count;
            row.SentBytes = group.Sum(x => x.Row.Sent);
            row.ReceivedBytes = group.Sum(x => x.Row.Received);
            row.LastSeenLocal = DateTime.UnixEpoch.AddMinutes(group.Max(x => x.Row.Minute)).ToLocalTime();
        }

        foreach (var gone in _serversByKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Servers.Remove(_serversByKey[gone]);
            _serversByKey.Remove(gone);
        }
    }

    /// <summary>
    /// The stored name, unless only the IP was known when the row was saved and we've
    /// learned a name since.
    /// </summary>
    private string BestName(HistoryRow row)
    {
        if (row.Host != row.Ip || !IPAddress.TryParse(row.Ip, out var ip))
        {
            return row.Host;
        }

        return _names.GetName(ip) ?? row.Ip;
    }

    private static string DisplayName(string programKey) =>
        programKey.Contains('\\') ? Path.GetFileNameWithoutExtension(programKey) : programKey;
}
