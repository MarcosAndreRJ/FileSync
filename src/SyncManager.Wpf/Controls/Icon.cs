using System.Windows;
using System.Windows.Media;

namespace SyncManager.Wpf.Controls;

/// <summary>
/// Propriedades anexadas para botões com ícone vetorial, sem precisar de um controle novo:
/// <c>ctl:Icon.Data="{StaticResource IconSearch}"</c> define o desenho e <c>ctl:Icon.Size</c> o tamanho (padrão 18).
/// </summary>
public static class Icon
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.RegisterAttached("Size", typeof(double), typeof(Icon), new PropertyMetadata(18d));

    /// <summary>
    /// Em um <see cref="System.Windows.Shapes.Path"/>: <c>ctl:Icon.Key="{Binding IconKey}"</c> escolhe o desenho pela CHAVE
    /// do recurso (ex.: "IconCheck"). Útil em itens gerados por ViewModel, que conhecem só o nome do ícone e não devem
    /// acessar recursos de interface.
    /// </summary>
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(Icon), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Shapes.Path path) return;
        if (e.NewValue is not string { Length: > 0 } key) { path.Visibility = Visibility.Collapsed; return; }
        path.Visibility = Visibility.Visible;
        path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, key);
    }

    public static Geometry? GetData(DependencyObject d) => (Geometry?)d.GetValue(DataProperty);
    public static void SetData(DependencyObject d, Geometry? value) => d.SetValue(DataProperty, value);
    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);
}
