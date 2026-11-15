using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>One line in the "Servers" list: a website or address and how much was exchanged with it.</summary>
public sealed class ServerRow : ObservableObject
{
    private string _server = string.Empty;
    private string _details = string.Empty;
    private string _programs = string.Empty;
    private int _programCount;
    private long _sent;
    private long _received;
    private DateTime _lastSeenLocal;

    public ServerRow(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public string Server { get => _server; set => SetField(ref _server, value); }

    /// <summary>Tooltip: name, every IP address and every program.</summary>
    public string Details { get => _details; set => SetField(ref _details, value); }

    public string Programs { get => _programs; set => SetField(ref _programs, value); }
    public int ProgramCount { get => _programCount; set => SetField(ref _programCount, value); }

    public long SentBytes
    {
        get => _sent;
        set
        {
            if (SetField(ref _sent, value))
            {
                OnPropertyChanged(nameof(SentText));
                OnPropertyChanged(nameof(TotalBytes));
            }
        }
    }

    public long ReceivedBytes
    {
        get => _received;
        set
        {
            if (SetField(ref _received, value))
            {
                OnPropertyChanged(nameof(ReceivedText));
                OnPropertyChanged(nameof(TotalBytes));
            }
        }
    }

    public long TotalBytes => _sent + _received;
    public string SentText => _sent == 0 ? string.Empty : ByteFormatter.Format(_sent);
    public string ReceivedText => _received == 0 ? string.Empty : ByteFormatter.Format(_received);

    public DateTime LastSeenLocal
    {
        get => _lastSeenLocal;
        set
        {
            if (SetField(ref _lastSeenLocal, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string LastSeenText => _lastSeenLocal.Date == DateTime.Today
        ? _lastSeenLocal.ToString("t")
        : _lastSeenLocal.ToString("g");

    public bool Matches(string filter) =>
        Server.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Details.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
