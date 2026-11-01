using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpTaskEditDialog : Window
{
    public FtpTaskEditDialog(FtpTaskEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
