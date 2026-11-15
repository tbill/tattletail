using TattleTail.Core;

namespace TattleTail.App.ViewModels;

/// <summary>Shared traffic columns (totals and per-second rates) for both tables.</summary>
public abstract class TrafficRowBase : ObservableObject
{
    private TrafficCounter _traffic;
    private double _sendRate;
    private double _receiveRate;

    public long SentBytes => _traffic.Sent;
    public long ReceivedBytes => _traffic.Received;
    public long TotalBytes => _traffic.Total;
    public double SendRate => _sendRate;
    public double ReceiveRate => _receiveRate;
    public double TotalRate => _sendRate + _receiveRate;

    public string SentText => _traffic.Sent == 0 ? string.Empty : ByteFormatter.Format(_traffic.Sent);
    public string ReceivedText => _traffic.Received == 0 ? string.Empty : ByteFormatter.Format(_traffic.Received);
    public string SendRateText => ByteFormatter.FormatRate(_sendRate);
    public string ReceiveRateText => ByteFormatter.FormatRate(_receiveRate);

    /// <summary>Stores new totals and works out the rate since the last update.</summary>
    public void UpdateTraffic(TrafficCounter current, double elapsedSeconds)
    {
        var previous = _traffic;
        var sendRate = elapsedSeconds > 0 ? Math.Max(0, current.Sent - previous.Sent) / elapsedSeconds : 0;
        var receiveRate = elapsedSeconds > 0 ? Math.Max(0, current.Received - previous.Received) / elapsedSeconds : 0;

        if (current != previous)
        {
            _traffic = current;
            OnPropertyChanged(nameof(SentBytes));
            OnPropertyChanged(nameof(ReceivedBytes));
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(SentText));
            OnPropertyChanged(nameof(ReceivedText));
        }

        if (sendRate != _sendRate || receiveRate != _receiveRate)
        {
            _sendRate = sendRate;
            _receiveRate = receiveRate;
            OnPropertyChanged(nameof(SendRate));
            OnPropertyChanged(nameof(ReceiveRate));
            OnPropertyChanged(nameof(TotalRate));
            OnPropertyChanged(nameof(SendRateText));
            OnPropertyChanged(nameof(ReceiveRateText));
        }
    }
}
