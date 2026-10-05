using Avalonia.Controls;
using SMSinspector.App.ViewModels;

namespace SMSinspector.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new DiagnosticViewModel();
        DataContext = viewModel;
        Opened += (_, _) => viewModel.Start();
        Closed += (_, _) => viewModel.Dispose();
    }
}
