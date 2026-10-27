using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SyncManager.Wpf.Controls;

/// <summary>
/// Indicador do resumo da comparação. O ícone é indicado pela CHAVE do recurso (ex.: "IconCheck") e resolvido aqui,
/// na View — assim o ViewModel não precisa acessar recursos de interface.
/// </summary>
public partial class SummaryCard : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SummaryCard), new PropertyMetadata("", (d, _) => ((SummaryCard)d).UpdateHint()));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SummaryCard), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(nameof(IconKey), typeof(string), typeof(SummaryCard),
            new PropertyMetadata(null, (d, e) => ((SummaryCard)d).ApplyIcon((string?)e.NewValue)));

    /// <summary>Clicar no cartão (ou Enter/Espaço com foco) executa o comando com <see cref="CommandParameter"/>.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(System.Windows.Input.ICommand), typeof(SummaryCard),
            new PropertyMetadata(null, (d, e) => ((SummaryCard)d).OnCommandChanged()));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(SummaryCard), new PropertyMetadata(null));

    public SummaryCard()
    {
        InitializeComponent();
        MouseLeftButtonUp += (_, e) => { if (Run()) e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && Run()) e.Handled = true; };
    }

    public System.Windows.Input.ICommand? Command { get => (System.Windows.Input.ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); private set => SetValue(HintProperty, value); }

    private void UpdateHint() => Hint = Command is null ? Label : Label + " — clique para selecionar esses itens";

    private void OnCommandChanged()
    {
        var clickable = Command is not null;
        Cursor = clickable ? System.Windows.Input.Cursors.Hand : null;
        Focusable = clickable;
        UpdateHint();
    }

    private bool Run()
    {
        if (Command is not { } cmd || !cmd.CanExecute(CommandParameter)) return false;
        cmd.Execute(CommandParameter);
        return true;
    }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public string? IconKey { get => (string?)GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }

    private void ApplyIcon(string? key)
    {
        if (string.IsNullOrEmpty(key)) { IconPath.Visibility = Visibility.Collapsed; return; }
        IconPath.Visibility = Visibility.Visible;
        IconPath.SetResourceReference(System.Windows.Shapes.Path.DataProperty, key);
    }
}
