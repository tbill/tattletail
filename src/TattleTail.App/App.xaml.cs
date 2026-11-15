using System.Windows;
using System.Windows.Threading;

namespace TattleTail.App;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "TattleTail error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
