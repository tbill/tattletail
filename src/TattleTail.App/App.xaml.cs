using System.Windows;
using System.Windows.Threading;

namespace TattleTail.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;

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
        MessageBox.Show(e.Exception.Message, "TattleTail error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
