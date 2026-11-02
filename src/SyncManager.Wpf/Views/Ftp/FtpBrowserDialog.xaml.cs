using System.Windows;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

/// <summary>Navegador FTP (origem local + diretórios remotos reais); também serve de seletor de caminho remoto.</summary>
public partial class FtpBrowserDialog : Window
{
    private readonly FtpRemotePickerViewModel _vm;

    public FtpBrowserDialog(FtpRemotePickerViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        Action<bool> onClose = result =>
        {
            try { DialogResult = result; } catch (InvalidOperationException) { }
            Close();
        };
        viewModel.RequestClose += onClose;
        Closed += (_, _) => viewModel.RequestClose -= onClose;

        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnRemoteSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedNode = e.NewValue as FsNode;
    }
}
