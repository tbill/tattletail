using System.Windows;
using TattleTail.App.ViewModels;

namespace TattleTail.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Start();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e) => _viewModel.ResetCounters();
}
