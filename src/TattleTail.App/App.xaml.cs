using System.Windows;
using System.Windows.Threading;

namespace TattleTail.App;

public partial class App : Application
{
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
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SmokeTest.ReportCrash(e.Exception);
        MessageBox.Show(e.Exception.Message, "TattleTail error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
