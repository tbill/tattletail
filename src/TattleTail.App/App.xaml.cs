using System.Windows;
using System.Windows.Threading;
using TattleTail.Core;

namespace TattleTail.App;

public partial class App : Application
{
    private static readonly TimeSpan SameErrorQuietTime = TimeSpan.FromMinutes(1);

    private SingleInstance? _singleInstance;
    private MainWindow? _window;
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

        // Started by the sign-in task: stay out of the way in the system tray.
        var background = e.Args.Contains(AutoStartTask.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            // Another copy is already watching the network; show it instead of fighting over the
            // event feeds. (Unless this was a background start, which shouldn't pop anything up.)
            if (!background)
            {
                SingleInstance.ShowExistingWindow();
            }

            Shutdown();
            return;
        }

        // The window can be hidden in the tray for days, so "no window showing" must not end the app.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var window = new MainWindow();
        _window = window;
        MainWindow = window;
        _singleInstance.ShowRequested += () => Dispatcher.BeginInvoke(window.ShowFromTray);

        window.StartMonitoring();
        if (!background)
        {
            window.Show();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Signing out or shutting down: really close, don't hide in the tray.
        _window?.PrepareToExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.CleanUp();
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
