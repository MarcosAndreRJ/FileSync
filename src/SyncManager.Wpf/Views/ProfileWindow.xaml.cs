using System.Windows;
using SyncManager.Core.Models;
using SyncManager.Wpf.ViewModels;

namespace SyncManager.Wpf.Views;

public partial class ProfileWindow : Window
{
    public ProfileWindow(ProfileEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += OnVmClosed;
    }

    public SyncProfile? ResultProfile { get; private set; }
    public bool Applied { get; private set; }

    private void OnVmClosed(SyncProfile? saved)
    {
        ResultProfile = saved;
        DialogResult = saved is not null;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm && vm.IsDirty)
        {
            var res = MessageBox.Show(this, "O perfil em edição possui alterações não salvas. Deseja descartá-las?",
                "Alterações não salvas", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
        }
        DialogResult = false;
        Close();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm)
        {
            await vm.SaveAsync();
            if (!vm.HasError)
            {
                Applied = true;
                ResultProfile = vm.GetProfile();
            }
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm)
        {
            await vm.SaveAsync();
            if (!vm.HasError)
            {
                ResultProfile = vm.GetProfile();
                DialogResult = true;
                Close();
            }
        }
    }
}
