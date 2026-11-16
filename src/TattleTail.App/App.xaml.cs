using System.Windows;
using System.Windows.Threading;

namespace TattleTail.App;

public partial class App : Application
{
    private static readonly TimeSpan SameErrorQuietTime = TimeSpan.FromMinutes(1);

    private SingleInstance? _singleInstance;
    private bool _showingError;
    private string? _lastError;
    private DateTime _lastErrorShownUtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                SmokeTest.ReportCrash(ex);
            }
        };

        base.OnStartup(e);

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            // Another copy is already watching the network; show it instead of fighting over the event feeds.
            SingleInstance.ActivateExistingWindow();
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SmokeTest.ReportCrash(e.Exception);
        e.Handled = true;

        // A message box keeps the app running underneath it, so an error that repeats (say, on
        // the once-a-second refresh) would otherwise stack up a new box every second. Show one
        // at a time, and don't nag about the same error straight after it was dismissed.
        var message = e.Exception.Message;
        var now = DateTime.UtcNow;
        if (_showingError || (message == _lastError && now - _lastErrorShownUtc < SameErrorQuietTime))
        {
            return;
        }

        _showingError = true;
        try
        {
            MessageBox.Show(message, "TattleTail error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            _lastError = message;
            _lastErrorShownUtc = DateTime.UtcNow;
        }
    }
}
