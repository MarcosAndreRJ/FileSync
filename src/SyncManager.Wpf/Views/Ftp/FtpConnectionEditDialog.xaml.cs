using System.ComponentModel;
using System.Windows;
using SyncManager.Wpf.Controls;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

/// <summary>Assistente guiado de conexão FTP (Geral, Segurança, Testar conexão) exibido como modal sobre a janela principal.</summary>
public partial class FtpConnectionEditDialog : Window
{
    private FtpConnectionEditViewModel? ViewModel => DataContext as FtpConnectionEditViewModel;
    private bool _syncingPassword;

    public FtpConnectionEditDialog(FtpConnectionEditViewModel viewModel)
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
        Action<FtpRemotePickerViewModel> onBrowser = picker =>
        {
            var dialog = new FtpBrowserDialog(picker) { Owner = this };
            dialog.ShowDialog();
        };
        viewModel.RequestClose += onClose;
        viewModel.RequestCancel += onCancel;
        viewModel.RequestBrowser += onBrowser;

        // a senha do cofre é carregada de forma assíncrona: reflete no PasswordBox quando chegar
        PropertyChangedEventHandler onChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(FtpConnectionEditViewModel.Password)) PushPasswordToBox();
        };
        viewModel.PropertyChanged += onChanged;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= onClose;
            viewModel.RequestCancel -= onCancel;
            viewModel.RequestBrowser -= onBrowser;
            viewModel.PropertyChanged -= onChanged;
        };

        WizardWindow.Attach(this, () => viewModel.CancelCommand.Execute(null));
        Loaded += (_, _) => PushPasswordToBox();
    }

    private void PushPasswordToBox()
    {
        if (ViewModel is null || UserPasswordBox.Password == ViewModel.Password) return;
        _syncingPassword = true;
        try { UserPasswordBox.Password = ViewModel.Password; }
        finally { _syncingPassword = false; }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword || ViewModel is null) return;
        ViewModel.Password = UserPasswordBox.Password;
    }
}
