using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SyncManager.Wpf.Views.Pages;

public partial class LogsView : UserControl
{
    private bool _scrollPending;

    public LogsView()
    {
        InitializeComponent();
        // acompanha o fim do log enquanto chegam linhas (só comportamento de tela)
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.MainViewModel old) old.LogLines.CollectionChanged -= OnLines;
            if (e.NewValue is ViewModels.MainViewModel vm) vm.LogLines.CollectionChanged += OnLines;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScrollToEnd(); };
    }

    // Uma rolagem por lote (não por linha) e só com a página visível: rolar uma lista oculta ou a cada linha força layout reentrante.
    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !IsVisible || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ScrollToEnd);
    }

    private void ScrollToEnd()
    {
        _scrollPending = false;
        if (IsVisible && SessionLog.Items.Count > 0)
            SessionLog.ScrollIntoView(SessionLog.Items[^1]);
    }
}
