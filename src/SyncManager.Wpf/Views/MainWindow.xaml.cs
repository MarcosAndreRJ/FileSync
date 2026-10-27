using System.Windows;
using SyncManager.Wpf.ViewModels;

namespace SyncManager.Wpf.Views;

/// <summary>
/// Shell do aplicativo: menu, barra lateral e a página atual (hoje só "Comparação").
/// O DataContext continua sendo o MainViewModel (singleton), então o estado da comparação sobrevive à navegação.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
