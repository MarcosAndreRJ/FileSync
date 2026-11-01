using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpDiagnosticsDialog : Window
{
    private readonly FtpDiagnosticsViewModel _viewModel;

    public FtpDiagnosticsDialog(FtpDiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.RequestClose += () => Close();

        _viewModel.RequestScrollToBottom += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                LogScrollViewer.ScrollToEnd();
            });
        };

        Loaded += async (_, _) =>
        {
            if (_viewModel.StartCommand.CanExecute(null))
            {
                await _viewModel.RunDiagnosticsAsync();
            }
        };
    }
}
