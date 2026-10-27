using System.Windows;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.Views;

/// <summary>Pergunta o que fazer ao apertar Delete: só tirar da lista, ou também excluir fisicamente (e de qual lado).</summary>
public partial class DeleteChoiceWindow : Window
{
    public DeleteChoice Choice { get; private set; } = DeleteChoice.Cancel;

    public DeleteChoiceWindow(DeleteChoiceRequest request)
    {
        InitializeComponent();
        Heading.Text = request.Count == 1
            ? "O que fazer com o item selecionado?"
            : $"O que fazer com os {request.Count:N0} itens selecionados?";
        Sample.Text = request.Count == 1 ? request.Sample : $"{request.Sample}  e mais {request.Count - 1:N0}";

        DelSource.IsEnabled = request.AnySource;
        DelTarget.IsEnabled = request.AnyTarget;
        DelBoth.IsEnabled = request.AnySource && request.AnyTarget;
        StrategyNote.Text = $"A exclusão física é feita pelo plano (com confirmação): os arquivos {request.StrategyText}.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Choice = RemoveOnly.IsChecked == true ? DeleteChoice.RemoveFromList
               : DelSource.IsChecked == true ? DeleteChoice.DeleteSource
               : DelTarget.IsChecked == true ? DeleteChoice.DeleteTarget
               : DeleteChoice.DeleteBoth;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.Cancel;
        Close();
    }
}
