using System.Windows;
using System.Windows.Controls;
using SyncManager.Wpf.ViewModels.Ftp;

namespace SyncManager.Wpf.Views.Ftp;

public partial class FtpTasksView : UserControl
{
    public FtpTasksView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is FtpTasksViewModel vm)
            {
                vm.RequestEditTaskDialog += editVm =>
                {
                    var dialog = new FtpTaskEditDialog(editVm)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dialog.ShowDialog();
                };

                vm.RequestChecklistDialog += checkVm =>
                {
                    var dialog = new FtpChecklistDialog(checkVm)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dialog.ShowDialog();
                };
            }
        };
    }
}
