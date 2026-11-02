using System.Windows;
using System.Windows.Controls;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpTasksView : UserControl
{
    private FtpTasksViewModel? _subscribed;

    public FtpTasksView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as FtpTasksViewModel);
    }

    /// <summary>Inscreve os pedidos de diálogo no ViewModel atual e solta o anterior, evitando aberturas duplicadas.</summary>
    private void Attach(FtpTasksViewModel? vm)
    {
        if (ReferenceEquals(_subscribed, vm)) return;
        if (_subscribed is not null)
        {
            _subscribed.RequestEditTaskDialog -= OnRequestEditTask;
            _subscribed.RequestChecklistDialog -= OnRequestChecklist;
        }
        _subscribed = vm;
        if (vm is not null)
        {
            vm.RequestEditTaskDialog += OnRequestEditTask;
            vm.RequestChecklistDialog += OnRequestChecklist;
        }
    }

    private void OnRequestEditTask(FtpTaskEditViewModel editVm)
    {
        var dialog = new FtpTaskEditDialog(editVm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void OnRequestChecklist(FtpChecklistViewModel checkVm)
    {
        var dialog = new FtpChecklistDialog(checkVm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
