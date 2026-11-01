using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.ViewModels;

namespace SyncManager.Wpf.Views.Pages;

/// <summary>
/// Página de Comparação com visualização hierárquica em dois painéis sincronizados.
/// </summary>
public partial class ComparisonView : UserControl
{
    public ComparisonView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private IReadOnlyList<ComparisonTreeNode> SelectedTreeNodes() =>
        ItemsGrid.SelectedItems.OfType<ComparisonTreeNode>().ToList();

    private void OnHeaderCheckAll(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.IsChecked.HasValue)
        {
            Vm.TreeManager.SetAllChecked(cb.IsChecked.Value);
        }
    }

    private void OnMarkSelectedTree(object sender, RoutedEventArgs e) =>
        Vm.SetIncludedTree(SelectedTreeNodes(), true);

    private void OnUnmarkSelectedTree(object sender, RoutedEventArgs e) =>
        Vm.SetIncludedTree(SelectedTreeNodes(), false);

    private void OnIncludeOnlySelectedTree(object sender, RoutedEventArgs e)
    {
        var sel = SelectedTreeNodes();
        if (sel.Count == 0)
        {
            MessageBox.Show("Selecione na lista os itens que deseja executar.", "Marcar somente selecionados", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Vm.IncludeOnlyTree(sel);
    }

    private void OnHideSelectedTree(object sender, RoutedEventArgs e)
    {
        var sel = SelectedTreeNodes();
        if (sel.Count > 0)
        {
            Vm.TreeManager.HideNodes(sel);
        }
    }

    private void OnRestoreHiddenTree(object sender, RoutedEventArgs e) =>
        Vm.TreeManager.RestoreHidden();

    private async void OnCopyRightTree(object sender, RoutedEventArgs e) =>
        await Safe(() => Vm.CopySelectionTreeAsync(SelectedTreeNodes(), FileSide.Source));

    private async void OnCopyLeftTree(object sender, RoutedEventArgs e) =>
        await Safe(() => Vm.CopySelectionTreeAsync(SelectedTreeNodes(), FileSide.Target));

    private async void OnDeleteSelectedTree(object sender, RoutedEventArgs e) =>
        await Safe(() => Vm.DeleteSelectionTreeAsync(SelectedTreeNodes()));

    private void OnKeepSourceTree(object sender, RoutedEventArgs e) =>
        Vm.ResolveSelectionTree(SelectedTreeNodes(), ConflictResolution.KeepSource);

    private void OnKeepTargetTree(object sender, RoutedEventArgs e) =>
        Vm.ResolveSelectionTree(SelectedTreeNodes(), ConflictResolution.KeepTarget);

    private void OnKeepBothTree(object sender, RoutedEventArgs e) =>
        Vm.ResolveSelectionTree(SelectedTreeNodes(), ConflictResolution.KeepBoth);

    private void OnIgnoreConflictTree(object sender, RoutedEventArgs e) =>
        Vm.ResolveSelectionTree(SelectedTreeNodes(), ConflictResolution.Skip);

    private async void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || !Vm.IsIdle) return;

        var selTree = SelectedTreeNodes();
        if (selTree.Count == 0) return;

        switch (e.Key)
        {
            case Key.Right:
                e.Handled = true;
                await Safe(() => Vm.CopySelectionTreeAsync(selTree, FileSide.Source));
                break;
            case Key.Left:
                e.Handled = true;
                await Safe(() => Vm.CopySelectionTreeAsync(selTree, FileSide.Target));
                break;
            case Key.Delete:
                e.Handled = true;
                await Safe(() => Vm.DeleteSelectionTreeAsync(selTree));
                break;
            case Key.Space:
                e.Handled = true;
                bool allChecked = selTree.All(n => n.IsChecked == true);
                Vm.SetIncludedTree(selTree, !allChecked);
                break;
        }
    }

    private static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPathDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files is { Length: > 0 })
            {
                var path = files[0];
                if (System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
                {
                    path = System.IO.Path.GetDirectoryName(path) ?? path;
                }
                Vm.SourcePath = path;
                e.Handled = true;
            }
        }
    }

    private void OnTargetDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files is { Length: > 0 })
            {
                var path = files[0];
                if (System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
                {
                    path = System.IO.Path.GetDirectoryName(path) ?? path;
                }
                Vm.TargetPath = path;
                e.Handled = true;
            }
        }
    }
}
