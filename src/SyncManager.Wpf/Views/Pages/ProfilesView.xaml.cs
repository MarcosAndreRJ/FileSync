using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyncManager.Wpf.ViewModels;

namespace SyncManager.Wpf.Views.Pages;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // duplo clique numa linha (não no cabeçalho) edita o perfil
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement((DataGrid)sender, d) is DataGridRow &&
            DataContext is MainViewModel vm)
            vm.Profiles.EditSelected();
    }
}
