using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMSinspector.App.ViewModels;

namespace SMSinspector.App;

public partial class MainWindow : Window
{
    private readonly DiagnosticViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _viewModel;
        Opened += async (_, _) => await _viewModel.StartAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void OnChooseDecompFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Root of your doldecomp/sms clone",
            AllowMultiple = false,
        });

        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
        {
            await _viewModel.ChooseDecompFolderAsync(path);
        }
    }
}
