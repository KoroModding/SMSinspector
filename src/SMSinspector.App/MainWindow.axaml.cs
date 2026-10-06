using Avalonia.Controls;
using Avalonia.Input;
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

    private void OnShowClass(object? sender, RoutedEventArgs e) => _viewModel.ShowClass();

    private void OnSaveReport(object? sender, RoutedEventArgs e) => _viewModel.SaveReport();

    private async void OnRunNameExtractor(object? sender, RoutedEventArgs e) => await _viewModel.RunNameExtractorAsync();

    private void OnSaveNamesReport(object? sender, RoutedEventArgs e) => _viewModel.SaveNamesReport();

    private void OnClassQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.ShowClass();
        }
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
