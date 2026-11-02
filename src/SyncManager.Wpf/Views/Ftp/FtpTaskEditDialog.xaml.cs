using System.Windows;
using SyncManager.Wpf.Controls;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

/// <summary>Assistente guiado de tarefa FTP em seis etapas, exibido como modal sobre a janela principal.</summary>
public partial class FtpTaskEditDialog : Window
{
    public FtpTaskEditDialog(FtpTaskEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        void End(bool result)
        {
            try { DialogResult = result; } catch (InvalidOperationException) { /* exibida sem ShowDialog */ }
            Close();
        }

        Action onClose = () => End(true);
        Action onCancel = () => End(false);
        Action<FtpRemotePickerViewModel> onPicker = picker =>
        {
            var dialog = new FtpBrowserDialog(picker) { Owner = this };
            dialog.ShowDialog();
        };
        viewModel.RequestClose += onClose;
        viewModel.RequestCancel += onCancel;
        viewModel.RequestRemotePicker += onPicker;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= onClose;
            viewModel.RequestCancel -= onCancel;
            viewModel.RequestRemotePicker -= onPicker;
        };

        WizardWindow.Attach(this, () => viewModel.CancelCommand.Execute(null));
    }
}
