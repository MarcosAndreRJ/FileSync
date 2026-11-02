using System.Windows;
using System.Windows.Controls;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpConnectionsView : UserControl
{
    private FtpConnectionsViewModel? _subscribed;

    public FtpConnectionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as FtpConnectionsViewModel);
    }

    /// <summary>Inscreve os pedidos de diálogo no ViewModel atual e solta o anterior, evitando aberturas duplicadas.</summary>
    private void Attach(FtpConnectionsViewModel? vm)
    {
        if (ReferenceEquals(_subscribed, vm)) return;
        if (_subscribed is not null)
        {
            _subscribed.RequestEditConnectionDialog -= OnRequestEditConnection;
            _subscribed.RequestDiagnosticsDialog -= OnRequestDiagnostics;
            _subscribed.RequestBrowserDialog -= OnRequestBrowser;
        }
        _subscribed = vm;
        if (vm is not null)
        {
            vm.RequestEditConnectionDialog += OnRequestEditConnection;
            vm.RequestDiagnosticsDialog += OnRequestDiagnostics;
            vm.RequestBrowserDialog += OnRequestBrowser;
        }
    }

    private void OnRequestEditConnection(FtpConnectionEditViewModel editVm)
    {
        var dialog = new FtpConnectionEditDialog(editVm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void OnRequestBrowser(FtpRemotePickerViewModel browserVm)
    {
        var dialog = new FtpBrowserDialog(browserVm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void OnRequestDiagnostics(FtpDiagnosticsViewModel diagVm)
    {
        var dialog = new FtpDiagnosticsDialog(diagVm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
