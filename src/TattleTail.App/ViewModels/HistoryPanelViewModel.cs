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
    /// <summary>
    /// Seconds between chart refreshes. The longer the range, the less one more second changes
    /// the picture and the more history there is to read, so long ranges refresh less often.
    /// </summary>
    private static int ChartRefreshTicks(HistoryRange range) => range switch
    {
        HistoryRange.TenMinutes => 1,
        HistoryRange.Hour or HistoryRange.Day => 15,
        _ => 60,
    };

    /// <summary>Seconds between refreshes of the servers list.</summary>
    private static int ServerRefreshTicks(HistoryRange range) => range switch
    {
        HistoryRange.TenMinutes or HistoryRange.Hour => 5,
        HistoryRange.Day => 15,
        _ => 60,
    };

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

    /// <summary>Show one row per domain (e.g. "github.com") with its hosts inside.</summary>
    public bool GroupByDomain
    {
        get => _settings.GroupServersByDomain;
        set
        {
            if (_settings.GroupServersByDomain == value)
            {
                return;
            }

            _settings.GroupServersByDomain = value;
            _settings.Save();
            OnPropertyChanged();
            Servers.Clear();
            _serversByKey.Clear();
            RequestRefresh();
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
        var updateChart = forced || _tick % ChartRefreshTicks(_range) == 0;
        var updateServers = forced || _tick % ServerRefreshTicks(_range) == 0;
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
                             (GroupByDomain ? $"   ·  {Servers.Count} domains" : $"   ·  {Servers.Count} servers");
            }
        }
        catch (Exception ex)
        {
            // This runs every second, so say what's wrong in place rather than in a pop-up.
            TotalsText = "Could not read history: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Everything we know about one host (or one address, depending on the display mode).</summary>
    private sealed record HostSummary(
        string Key, string Name, string Domain, string Display, List<string> Ips, List<string> Programs,
        long Sent, long Received, long LastMinute);

    private void UpdateServers(List<HistoryRow> remotes)
    {
        var hosts = remotes
            .Select(r => (Row: r, Name: BestName(r)))
            .GroupBy(x => _display switch
            {
                AddressDisplay.Names => x.Name,
                AddressDisplay.Addresses => x.Row.Ip,
                _ => x.Name + "|" + x.Row.Ip,
            })
            .Select(g =>
            {
                var first = g.First();
                return new HostSummary(
                    g.Key,
                    first.Name,
                    PublicSuffixList.Default.GetDomain(first.Name),
                    _display == AddressDisplay.Names ? first.Name : AddressFormatter.Format(first.Name, first.Row.Ip, _display),
                    g.Select(x => x.Row.Ip).Distinct().ToList(),
                    g.Select(x => DisplayName(x.Row.Program)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    g.Sum(x => x.Row.Sent),
                    g.Sum(x => x.Row.Received),
                    g.Max(x => x.Row.Minute));
            })
            .ToList();

        if (!GroupByDomain)
        {
            Sync(Servers, _serversByKey, hosts, h => h.Key, (key, _) => new ServerRow(key), (row, host) => Fill(row, host));
            return;
        }

        var domains = hosts.GroupBy(h => h.Domain).ToList();
        Sync(Servers, _serversByKey, domains, d => "domain:" + d.Key, (key, _) => new ServerRow(key, isDomain: true),
            (row, domain) =>
            {
                var children = domain.ToList();
                var programs = children.SelectMany(h => h.Programs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                row.Server = domain.Key;
                row.Details = $"{children.Count} host(s):\n" +
                              string.Join("\n", children.OrderByDescending(h => h.Sent + h.Received).Take(15).Select(h => "  " + h.Display)) +
                              (children.Count > 15 ? "\n  …" : string.Empty) +
                              "\nPrograms: " + string.Join(", ", programs);
                row.Programs = string.Join(", ", programs);
                row.ProgramCount = programs.Count;
                row.SentBytes = children.Sum(h => h.Sent);
                row.ReceivedBytes = children.Sum(h => h.Received);
                row.LastSeenLocal = ToLocal(children.Max(h => h.LastMinute));

                var childMap = row.Children.ToDictionary(c => c.Key);
                Sync(row.Children, childMap, children, h => h.Key, (key, _) => new ServerRow(key), (child, host) => Fill(child, host));
                row.NotifyChildrenChanged();
            });
    }

    private static void Fill(ServerRow row, HostSummary host)
    {
        row.Server = host.Display;
        row.Details = string.Join("\n",
            new[] { host.Name }.Concat(host.Ips.Where(ip => ip != host.Name)).Append("Programs: " + string.Join(", ", host.Programs)));
        row.Programs = string.Join(", ", host.Programs);
        row.ProgramCount = host.Programs.Count;
        row.SentBytes = host.Sent;
        row.ReceivedBytes = host.Received;
        row.LastSeenLocal = ToLocal(host.LastMinute);
    }

    /// <summary>
    /// Makes <paramref name="rows"/> match <paramref name="items"/>: updates rows that exist, adds new
    /// ones and removes ones that are gone. Updating in place keeps the user's scroll position and selection.
    /// </summary>
    private static void Sync<T>(ObservableCollection<ServerRow> rows, Dictionary<string, ServerRow> byKey, IEnumerable<T> items,
        Func<T, string> keyOf, Func<string, T, ServerRow> create, Action<ServerRow, T> fill)
    {
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            var key = keyOf(item);
            seen.Add(key);
            if (!byKey.TryGetValue(key, out var row))
            {
                row = create(key, item);
                byKey[key] = row;
                rows.Add(row);
            }

            fill(row, item);
        }

        foreach (var gone in byKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            rows.Remove(byKey[gone]);
            byKey.Remove(gone);
        }
    }

    private static DateTime ToLocal(long unixMinute) => DateTime.UnixEpoch.AddMinutes(unixMinute).ToLocalTime();

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

    private static string DisplayName(string programKey) => ProgramIdentity.DisplayName(programKey);
}
