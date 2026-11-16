using System.Windows;
using System.Windows.Input;
using ScottPlot;
using TattleTail.App.ViewModels;
using TattleTail.Core;

namespace TattleTail.App;

public partial class MainWindow : Window
{
    private static readonly Color UploadColor = Color.FromHex("#E8710A");
    private static readonly Color DownloadColor = Color.FromHex("#1A73E8");

    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"TattleTail {AppVersion.Display}";
        DataContext = _viewModel;

        SetUpChart();
        _viewModel.HistoryPanel.PointsChanged += DrawChart;

        Loaded += (_, _) =>
        {
            _viewModel.Start();
            if (SmokeTest.FromCommandLine() is { } smoke)
            {
                SmokeTest.Run(this, _viewModel, smoke.ReportPath, smoke.Seconds);
            }
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void SetUpChart()
    {
        // The time range picker does the zooming, so plain mouse drags shouldn't move the chart.
        TrafficChart.UserInputProcessor.Disable();

        var plot = TrafficChart.Plot;
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            // Uploads are drawn above the line and downloads below it, so show sizes without the minus sign.
            LabelFormatter = value => value == 0 ? "0" : ByteFormatter.Format(Math.Abs(value)) + "/s",
        };
        plot.Legend.Alignment = Alignment.UpperLeft;
        TrafficChart.Refresh();
    }

    private void DrawChart()
    {
        var points = _viewModel.HistoryPanel.Points;
        var plot = TrafficChart.Plot;
        plot.Clear();

        if (points.Count > 0)
        {
            var xs = points.Select(p => p.StartUtc.ToLocalTime().ToOADate()).ToArray();
            var up = points.Select(p => p.SendRate).ToArray();
            var down = points.Select(p => -p.ReceiveRate).ToArray();

            AddArea(xs, up, UploadColor, "Upload ↑");
            AddArea(xs, down, DownloadColor, "Download ↓");

            var zero = plot.Add.HorizontalLine(0);
            zero.Color = Colors.Gray.WithAlpha(0.5);
            zero.LineWidth = 1;

            // Keep the chart symmetric-ish so a quiet direction still has room to show.
            var max = Math.Max(1024, Math.Max(up.Max(), -down.Min()) * 1.1);
            plot.Axes.SetLimits(xs[0], xs[^1], -max, max);
            plot.ShowLegend();
        }

        TrafficChart.Refresh();
    }

    private void AddArea(double[] xs, double[] ys, Color color, string label)
    {
        var line = TrafficChart.Plot.Add.Scatter(xs, ys);
        line.Color = color;
        line.LineWidth = 1.5f;
        line.MarkerSize = 0;
        line.FillY = true;
        line.FillYColor = color.WithAlpha(0.25);
        line.ConnectStyle = ConnectStyle.StepHorizontal;
        line.LegendText = label;
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        var points = _viewModel.HistoryPanel.Points;
        if (points.Count == 0)
        {
            return;
        }

        // Find the bucket under the mouse and show its numbers.
        var position = e.GetPosition(TrafficChart);
        var scale = TrafficChart.DisplayScale;
        var coordinates = TrafficChart.Plot.GetCoordinates(new Pixel(position.X * scale, position.Y * scale));
        var mouseTime = DateTime.FromOADate(coordinates.X).ToUniversalTime();

        var point = points.LastOrDefault(p => p.StartUtc <= mouseTime);
        if (point == default)
        {
            ChartReadout.Visibility = Visibility.Collapsed;
            return;
        }

        var format = _viewModel.HistoryPanel.Range switch
        {
            HistoryRange.TenMinutes => "T",
            HistoryRange.Hour or HistoryRange.Day => "t",
            _ => "g",
        };
        ChartReadoutText.Text =
            $"{point.StartUtc.ToLocalTime().ToString(format)}\n" +
            $"↑ {ByteFormatter.Format(point.SendRate)}/s\n" +
            $"↓ {ByteFormatter.Format(point.ReceiveRate)}/s";
        ChartReadout.Visibility = Visibility.Visible;
    }

    private void OnChartMouseLeave(object sender, MouseEventArgs e) => ChartReadout.Visibility = Visibility.Collapsed;

    private void OnResetClicked(object sender, RoutedEventArgs e) => _viewModel.ResetCounters();

    private void OnShowAllProgramsClicked(object sender, RoutedEventArgs e) => ProgramsGrid.SelectedItem = null;

    private async void OnClearHistoryClicked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Delete all saved traffic history? Charts and server lists will start over.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await _viewModel.ClearHistoryAsync();
        }
    }
}
