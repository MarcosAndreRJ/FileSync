using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SyncManager.Wpf.Controls;

/// <summary>Moldura de assistente: cabeçalho, navegação lateral com estados, área principal e rodapé fixo.</summary>
public class WizardFrame : Control
{
    static WizardFrame() => DefaultStyleKeyProperty.OverrideMetadata(typeof(WizardFrame), new FrameworkPropertyMetadata(typeof(WizardFrame)));

    public static readonly DependencyProperty WizardTitleProperty = Reg<string>(nameof(WizardTitle));
    public static readonly DependencyProperty WizardSubtitleProperty = Reg<string>(nameof(WizardSubtitle));
    public static readonly DependencyProperty StepsProperty = Reg<IEnumerable>(nameof(Steps));
    public static readonly DependencyProperty WizardContentProperty = Reg<object>(nameof(WizardContent));
    public static readonly DependencyProperty FooterButtonsProperty = Reg<object>(nameof(FooterButtons));
    public static readonly DependencyProperty FooterNoteProperty = Reg<string>(nameof(FooterNote));
    public static readonly DependencyProperty CloseCommandProperty = Reg<ICommand>(nameof(CloseCommand));
    public static readonly DependencyProperty NavigateCommandProperty = Reg<ICommand>(nameof(NavigateCommand));

    private static DependencyProperty Reg<T>(string name) =>
        DependencyProperty.Register(name, typeof(T), typeof(WizardFrame));

    public string WizardTitle { get => (string)GetValue(WizardTitleProperty); set => SetValue(WizardTitleProperty, value); }
    public string WizardSubtitle { get => (string)GetValue(WizardSubtitleProperty); set => SetValue(WizardSubtitleProperty, value); }
    public IEnumerable Steps { get => (IEnumerable)GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    public object WizardContent { get => GetValue(WizardContentProperty); set => SetValue(WizardContentProperty, value); }
    public object FooterButtons { get => GetValue(FooterButtonsProperty); set => SetValue(FooterButtonsProperty, value); }
    public string FooterNote { get => (string)GetValue(FooterNoteProperty); set => SetValue(FooterNoteProperty, value); }
    public ICommand CloseCommand { get => (ICommand)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }
    public ICommand NavigateCommand { get => (ICommand)GetValue(NavigateCommandProperty); set => SetValue(NavigateCommandProperty, value); }
}

/// <summary>Posiciona a janela do assistente sobre a janela principal, atenuando o fundo (modal integrado).</summary>
public static class WizardWindow
{
    public static void Attach(Window window, Action cancel)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Loaded += (_, _) =>
        {
            if (window.Owner is { } owner && owner.IsVisible)
            {
                window.Left = owner.Left; window.Top = owner.Top;
                window.Width = owner.ActualWidth; window.Height = owner.ActualHeight;
                if (owner.WindowState == WindowState.Maximized)
                {
                    var wa = SystemParameters.WorkArea;
                    window.Left = wa.Left; window.Top = wa.Top; window.Width = wa.Width; window.Height = wa.Height;
                }
            }
            window.Dispatcher.BeginInvoke(() => window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                System.Windows.Threading.DispatcherPriority.Input);
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancel(); e.Handled = true; }
        };
    }
}
