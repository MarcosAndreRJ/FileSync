using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.ViewModels;

namespace SyncManager.Wpf.Views.Pages;

/// <summary>
/// Página "Comparação". Só comportamento de View: arrastar e soltar, teclas na lista e rolagem do log.
/// Toda regra continua no MainViewModel.
/// </summary>
public partial class ComparisonView : UserControl
{
    private MainViewModel? _subscribed;

    public ComparisonView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Hook()
    {
        if (_subscribed is not null) { _subscribed.LogLines.CollectionChanged -= OnLogChanged; _subscribed.SelectRowsRequested -= OnSelectRows; }
        _subscribed = DataContext as MainViewModel;
        if (_subscribed is not null) { _subscribed.LogLines.CollectionChanged += OnLogChanged; _subscribed.SelectRowsRequested += OnSelectRows; }
    }

    private bool _scrollPending;

    // Uma rolagem por lote de linhas, e só com o log visível: rolar a cada linha (ou uma lista oculta) força layout reentrante.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !LogList.IsVisible || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (LogList.IsVisible && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    // ------------------------------------------------------------------ teclado na lista: ← → Delete

    private IReadOnlyList<ItemRow> Selected() => ItemsGrid.SelectedItems.OfType<ItemRow>().ToList();

    /// <summary>
    /// →  copia os itens selecionados do Source para o Target; ←  do Target para o Source; Delete remove da lista
    /// (perguntando se também exclui fisicamente). Tudo passa pelo plano/executor: a interface nunca copia nem apaga diretamente.
    /// </summary>
    private async void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || !Vm.IsIdle) return;

        switch (e.Key)
        {
            case Key.Right:
                e.Handled = true;
                await Safe(() => Vm.CopySelectionAsync(Selected(), FileSide.Source));
                break;
            case Key.Left:
                e.Handled = true;
                await Safe(() => Vm.CopySelectionAsync(Selected(), FileSide.Target));
                break;
            case Key.Delete:
                e.Handled = true;
                await Safe(() => Vm.DeleteSelectionAsync(Selected()));
                break;
            case Key.Space:
                e.Handled = true;
                ToggleSelectedIncluded();
                break;
        }
    }

    private async void OnCopyRight(object sender, RoutedEventArgs e) => await Safe(() => Vm.CopySelectionAsync(Selected(), FileSide.Source));
    private async void OnCopyLeft(object sender, RoutedEventArgs e) => await Safe(() => Vm.CopySelectionAsync(Selected(), FileSide.Target));
    private async void OnDeleteSelected(object sender, RoutedEventArgs e) => await Safe(() => Vm.DeleteSelectionAsync(Selected()));

    private void OnKeepSource(object sender, RoutedEventArgs e) => Vm.ResolveSelection(Selected(), ConflictResolution.KeepSource);
    private void OnKeepTarget(object sender, RoutedEventArgs e) => Vm.ResolveSelection(Selected(), ConflictResolution.KeepTarget);
    private void OnKeepBoth(object sender, RoutedEventArgs e) => Vm.ResolveSelection(Selected(), ConflictResolution.KeepBoth);
    private void OnIgnoreConflict(object sender, RoutedEventArgs e) => Vm.ResolveSelection(Selected(), ConflictResolution.Skip);

    // ------------------------------------------------------------------ marcar / desmarcar o que será executado e seleção por cartão

    private void OnMarkSelected(object sender, RoutedEventArgs e) => Vm.SetIncluded(Selected(), true);
    private void OnUnmarkSelected(object sender, RoutedEventArgs e) => Vm.SetIncluded(Selected(), false);

    private void OnIncludeOnlySelected(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) { MessageBox.Show("Selecione na lista os itens que deseja executar.", "Marcar somente os selecionados", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Vm.IncludeOnly(selected);
    }

    /// <summary>Espaço alterna a marca dos selecionados: se todos já estão marcados, desmarca; senão marca todos.</summary>
    private void ToggleSelectedIncluded()
    {
        var rows = Selected().Where(r => r.CanInclude).ToList();
        if (rows.Count > 0) Vm.SetIncluded(rows, !rows.All(r => r.IsIncluded));
    }

    /// <summary>Clique num item de "Ações previstas" (Copiar, Atualizar...): seleciona os itens dele, como os cartões.</summary>
    private void OnPlanItemClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SummaryEntry entry && Vm.SelectRowsCommand.CanExecute(entry))
            Vm.SelectRowsCommand.Execute(entry);
    }

    /// <summary>
    /// Seleciona na grade exatamente as linhas pedidas. A grade do WPF é lenta para adicionar milhares de itens um a um,
    /// então escolhe o caminho mais curto: "selecionar tudo e tirar o que sobra" quando a maioria é selecionada.
    /// </summary>
    private void OnSelectRows(IReadOnlyList<ItemRow> rows)
    {
        var grid = ItemsGrid;
        grid.UnselectAll();
        if (rows.Count == 0) return;

        var total = grid.Items.Count;
        if (rows.Count >= total) grid.SelectAll();
        else if (rows.Count <= total - rows.Count)
            foreach (var r in rows) grid.SelectedItems.Add(r);
        else
        {
            grid.SelectAll();
            var keep = new HashSet<ItemRow>(rows);
            foreach (var r in grid.Items.OfType<ItemRow>().Where(r => !keep.Contains(r)).ToList())
                grid.SelectedItems.Remove(r);
        }

        grid.ScrollIntoView(rows[0]);
        grid.Focus();
    }

    /// <summary>Handlers async void: uma exceção aqui derrubaria o aplicativo, então ela é mostrada em vez de propagada.</summary>
    private static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ painel "Opções avançadas"

    /// <summary>Botão "Mais opções...": só abre/fecha o painel de opções (comportamento puramente visual).</summary>
    private void OnToggleOptions(object sender, RoutedEventArgs e) =>
        OptionsExpander.IsExpanded = !OptionsExpander.IsExpanded;
}
