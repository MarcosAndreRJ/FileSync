using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpChecklistDialog : Window
{
    public FtpChecklistDialog(FtpChecklistViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () => Close();

        viewModel.RequestExecutionDialog += execVm =>
        {
            var dialog = new FtpExecutionDialog(execVm)
            {
                Owner = this
            };
            dialog.ShowDialog();
        };
    }
}
