using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SyncManager.Wpf.Controls;

/// <summary>Estados visuais da área de soltar.</summary>
public enum DropState { Normal, Valid, Invalid }

/// <summary>
/// Cartão "pasta + procurar + soltar". Só comportamento de View: o caminho e os comandos vêm do ViewModel.
/// Durante o arrasto NÃO toca o disco (um compartilhamento de rede offline travaria a interface); a
/// resolução pasta/arquivo acontece só ao soltar.
/// </summary>
public partial class FolderSelector : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(FolderSelector), new PropertyMetadata(""));

    public static readonly DependencyProperty FolderPathProperty =
        DependencyProperty.Register(nameof(FolderPath), typeof(string), typeof(FolderSelector),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BrowseCommandProperty =
        DependencyProperty.Register(nameof(BrowseCommand), typeof(ICommand), typeof(FolderSelector), new PropertyMetadata(null));

    /// <summary>Executado com a pasta (string) quando algo é solto.</summary>
    public static readonly DependencyProperty DropCommandProperty =
        DependencyProperty.Register(nameof(DropCommand), typeof(ICommand), typeof(FolderSelector), new PropertyMetadata(null));

    public static readonly DependencyProperty CardStyleProperty =
        DependencyProperty.Register(nameof(CardStyle), typeof(Style), typeof(FolderSelector), new PropertyMetadata(null));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(FolderSelector), new PropertyMetadata(Brushes.Black));

    public FolderSelector() => InitializeComponent();

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string FolderPath { get => (string)GetValue(FolderPathProperty); set => SetValue(FolderPathProperty, value); }
    public ICommand? BrowseCommand { get => (ICommand?)GetValue(BrowseCommandProperty); set => SetValue(BrowseCommandProperty, value); }
    public ICommand? DropCommand { get => (ICommand?)GetValue(DropCommandProperty); set => SetValue(DropCommandProperty, value); }
    public Style? CardStyle { get => (Style?)GetValue(CardStyleProperty); set => SetValue(CardStyleProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public DropState State { get; private set; } = DropState.Normal;

    // ------------------------------------------------------------------ arrastar e soltar

    private static bool HasFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e) => OnDragOver(sender, e);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accept = HasFiles(e.Data) && IsEnabled;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetState(accept ? DropState.Valid : DropState.Invalid);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetState(DropState.Normal);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetState(DropState.Normal);
        if (HasFiles(e.Data) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            Accept(paths);
        e.Handled = true;
    }

    /// <summary>Resolve o que foi solto (pasta, ou a pasta que contém o arquivo) e entrega ao comando. Devolve a pasta aceita.</summary>
    public string? Accept(IEnumerable<string> paths)
    {
        var first = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (first is null) return null;

        // Pasta existente → ela mesma; arquivo existente → a pasta que o contém. Se não dá para saber (ex.: compartilhamento de rede
        // momentaneamente offline), decide pelo formato: com extensão parece arquivo (usa o pai), sem extensão parece pasta.
        // A validação de acesso fica a cargo do preflight, que explica claramente quando o caminho não está acessível.
        string? folder;
        if (Directory.Exists(first)) folder = first;
        else if (File.Exists(first)) folder = Path.GetDirectoryName(first);
        else folder = Path.HasExtension(first) ? Path.GetDirectoryName(first) : first;
        if (string.IsNullOrEmpty(folder)) return null;

        if (DropCommand is { } cmd && cmd.CanExecute(folder)) cmd.Execute(folder);
        return folder;
    }

    private void OnZoneClick(object sender, MouseButtonEventArgs e)
    {
        if (BrowseCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void SetState(DropState state)
    {
        State = state;
        var (stroke, fill, text) = state switch
        {
            DropState.Valid => ("PrimaryBrush", "PrimarySoftBrush", "Solte aqui"),
            DropState.Invalid => ("DangerBrush", "DangerSoftBrush", "Isto não pode ser solto aqui"),
            _ => ("TextSecondaryBrush", null, "Arraste uma pasta ou arquivo aqui ou clique para selecionar")
        };
        ZoneBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
        if (fill is null) ZoneBorder.Fill = Brushes.Transparent; else ZoneBorder.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fill);
        ZoneIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, stroke);
        ZoneText.SetResourceReference(TextBlock.ForegroundProperty, stroke);
        ZoneText.Text = text;
    }
}
