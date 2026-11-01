using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpConnectionEditDialog : Window
{
    private FtpConnectionEditViewModel? ViewModel => DataContext as FtpConnectionEditViewModel;

    public FtpConnectionEditDialog(FtpConnectionEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            DialogResult = true;
            Close();
        };

        viewModel.RequestDiagnosticsDialog += diagVm =>
        {
            var diagDialog = new FtpDiagnosticsDialog(diagVm)
            {
                Owner = this
            };
            diagDialog.ShowDialog();
        };

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(viewModel.Password))
            {
                UserPasswordBox.Password = viewModel.Password;
            }
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.Password = UserPasswordBox.Password;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
