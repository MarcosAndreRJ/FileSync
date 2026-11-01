using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpExecutionDialog : Window
{
    private readonly FtpExecutionViewModel _viewModel;

    public FtpExecutionDialog(FtpExecutionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.RequestClose += () => Close();

        _viewModel.RequestScrollToBottom += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                ExecutionLogScrollViewer.ScrollToEnd();
            });
        };

        Loaded += async (_, _) =>
        {
            if (_viewModel.IsRunning) return;
            await _viewModel.StartTransferAsync();
        };
    }
}
