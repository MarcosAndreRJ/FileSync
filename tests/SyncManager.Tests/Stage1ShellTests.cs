using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SyncManager.Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;
using SyncManager.Wpf.Views.Pages;

namespace SyncManager.Tests;

/// <summary>Localiza elementos por nome na árvore lógica (UserControls têm namescope próprio, então Window.FindName não alcança).</summary>
internal static class UiFind
{
    public static T ByName<T>(DependencyObject root, string name) where T : FrameworkElement =>
        TryByName<T>(root, name) ?? throw new InvalidOperationException($"elemento '{name}' não encontrado");

    public static T? TryByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T t && t.Name == name) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (TryByName<T>(child, name) is { } found) return found;
        return null;
    }
}

/// <summary>Etapa 1 da refatoração visual: tema centralizado + shell com menu lateral.</summary>
public class Stage1ShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerStage1", Guid.NewGuid().ToString("N"));

    private ServiceProvider Services()
    {
        var sp = App.BuildServices(Path.Combine(_root, "db", "s1.db"), Path.Combine(_root, "logs"));
        sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
        return sp;
    }

    private static MainViewModel NewVm(ServiceProvider sp, FakeDialogs d) => new(
        sp.GetRequiredService<SyncWorkflow>(), d, sp.GetRequiredService<ILogService>(),
        sp.GetRequiredService<IProfileRepository>(), sp.GetRequiredService<SettingsService>(), sp);

    // ------------------------------------------------------------------ tema

    [Fact]
    public async Task Theme_resources_are_loaded_and_the_legacy_keys_still_resolve()
    {
        await UiThread.Run(() =>
        {
            var r = System.Windows.Application.Current.Resources;
            // chaves novas
            foreach (var key in new[] { "PrimaryBrush", "SuccessBrush", "WarningBrush", "DangerBrush", "BackgroundBrush", "SurfaceBrush",
                                        "BorderBrush", "TextPrimaryBrush", "TextSecondaryBrush", "SourceTintBrush", "TargetTintBrush" })
                Assert.True(r.Contains(key), key);
            foreach (var key in new[] { "IconSearch", "IconSettings", "IconHistory", "IconCalendar", "IconDocument", "IconHelp",
                                        "IconFolder", "IconSwap", "IconCompare", "IconProfiles", "IconCheck", "IconWarning", "IconError" })
                Assert.IsAssignableFrom<Geometry>(r[key]);
            foreach (var key in new[] { "PrimaryButton", "SuccessButton", "DangerButton", "NavButton", "PanelBorder", "Card",
                                        "SourceCard", "TargetCard", "StatCard", "PanelTitle", "PageTitle", "SidebarBorder" })
                Assert.IsType<Style>(r[key]);
            // nomes antigos usados pelas telas ainda não refatoradas
            foreach (var key in new[] { "Accent", "AccentDark", "Surface", "Background", "Border", "TextMuted", "Danger" })
                Assert.IsType<SolidColorBrush>(r[key]);

            Assert.Equal((Color)ColorConverter.ConvertFromString("#1677FF"), ((SolidColorBrush)r["PrimaryBrush"]).Color);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Status_colors_in_code_match_the_xaml_palette()
    {
        await UiThread.Run(() =>
        {
            var r = System.Windows.Application.Current.Resources;
            Color C(string key) => (Color)r[key];
            Color B(System.Windows.Media.Brush b) => ((SolidColorBrush)b).Color;

            Assert.Equal(C("TextSecondaryColor"), B(Presentation.Gray));
            Assert.Equal(C("SuccessTextColor"), B(Presentation.Green));
            Assert.Equal(C("PrimaryColor"), B(Presentation.Blue));
            Assert.Equal(C("WarningTextColor"), B(Presentation.Orange));
            Assert.Equal(C("PurpleColor"), B(Presentation.Purple));
            Assert.Equal(C("DangerColor"), B(Presentation.Red));
            Assert.Equal(C("TextPrimaryColor"), B(Presentation.Dark));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Implicit_styles_are_applied_to_standard_controls()
    {
        await UiThread.Run(async () =>
        {
            var button = new Button { Content = "x" };
            var plain = new Button { Content = "y" };
            var primary = new Button { Content = "z", Style = (Style)System.Windows.Application.Current.Resources["PrimaryButton"] };
            var window = new Window { Style = (Style)System.Windows.Application.Current.Resources["AppWindow"], Content = new StackPanel { Children = { button, plain, primary, new TextBox(), new DataGrid() } } };
            window.Show();
            await UiThread.Settle();

            Assert.Equal(((SolidColorBrush)System.Windows.Application.Current.Resources["SurfaceBrush"]).Color, ((SolidColorBrush)plain.Background).Color);
            Assert.Equal(((SolidColorBrush)System.Windows.Application.Current.Resources["PrimaryBrush"]).Color, ((SolidColorBrush)primary.Background).Color);
            Assert.Equal(((SolidColorBrush)System.Windows.Application.Current.Resources["BackgroundBrush"]).Color, ((SolidColorBrush)window.Background).Color);
            window.Close();
        });
    }

    /// <summary>Clique de verdade (inclusive o Command), via automação — RaiseEvent(Click) só chamaria handlers.</summary>
    private static void Click(Button b) =>
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(b)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();

    // ------------------------------------------------------------------ shell

    [Fact]
    public async Task Shell_hosts_the_comparison_page_with_the_same_view_model_and_sidebar_navigation()
    {
        using var sp = Services();
        var d = new FakeDialogs();

        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, d);
            var w = new MainWindow(vm);
            w.Show();
            await UiThread.Settle();
            try
            {
                // o estilo de janela precisa valer para a classe derivada (estilo implícito de Window não vale)
                Assert.Equal(((SolidColorBrush)System.Windows.Application.Current.Resources["BackgroundBrush"]).Color, ((SolidColorBrush)w.Background).Color);
                var page = UiFind.ByName<ComparisonView>(w, "ComparisonPage");
                Assert.Same(vm, page.DataContext);                               // estado único: sobrevive à navegação
                Assert.NotNull(UiFind.TryByName<DataGrid>(w, "ItemsGrid"));      // a lista existe dentro da página

                // a página atual está destacada; as demais não
                string? Tag(string name) => (string?)UiFind.ByName<Button>(w, name).Tag;
                Assert.Equal("active", Tag("NavComparison"));
                Assert.True(string.IsNullOrEmpty(Tag("NavProfiles")));

                // cada item da barra lateral abre a sua página (sem janelas extras) e fica destacado
                async Task Go(string nav, AppScreen expected, string tagged)
                {
                    Click(UiFind.ByName<Button>(w, nav));
                    await UiThread.Settle(); await Task.Delay(150); await UiThread.Settle();
                    Assert.Equal(expected, vm.CurrentScreen);
                    Assert.Equal("active", Tag(tagged));
                    foreach (var other in new[] { "NavComparison", "NavProfiles", "NavSchedules", "NavHistory", "NavLogs", "NavSettings" }.Where(n => n != tagged))
                        Assert.True(string.IsNullOrEmpty(Tag(other)), $"{other} não deveria estar ativo em {expected}");
                }
                await Go("NavProfiles", AppScreen.Profiles, "NavProfiles");
                await Go("NavSchedules", AppScreen.Schedules, "NavSchedules");
                await Go("NavHistory", AppScreen.History, "NavHistory");
                await Go("NavLogs", AppScreen.Logs, "NavLogs");
                await Go("NavSettings", AppScreen.Settings, "NavSettings");
                await Go("NavComparison", AppScreen.Comparison, "NavComparison");

                // só a página atual aparece
                Assert.Equal(Visibility.Visible, UiFind.ByName<ComparisonView>(w, "ComparisonPage").Visibility);
                Assert.Equal(Visibility.Collapsed, UiFind.ByName<SettingsView>(w, "SettingsPage").Visibility);

                Click(UiFind.ByName<Button>(w, "NavHelp"));
                await UiThread.Settle();
                Assert.Single(d.Infos);                                          // "Sobre"
                Assert.Equal(AppScreen.Comparison, vm.CurrentScreen);            // Ajuda não troca de página
            }
            finally { w.Close(); }
        });

        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Icons_render_inside_nav_buttons_and_hide_when_a_button_has_no_icon()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var w = new MainWindow(NewVm(sp, new FakeDialogs()));
            w.Show();
            await UiThread.Settle();
            try
            {
                var nav = UiFind.ByName<Button>(w, "NavSettings");
                nav.ApplyTemplate();
                var path = (System.Windows.Shapes.Path)nav.Template.FindName("icon", nav)!;
                Assert.NotNull(path.Data);
                Assert.True(path.Data.Bounds.Width > 10);                        // geometria real (não vazia)

                var noIcon = new Button { Content = "sem ícone" };
                var host = new Window { Content = noIcon };
                host.Show();
                await UiThread.Settle();
                noIcon.ApplyTemplate();
                var hidden = (System.Windows.Shapes.Path)noIcon.Template.FindName("icon", noIcon)!;
                Assert.Equal(Visibility.Collapsed, hidden.Visibility);           // o botão padrão não reserva espaço para ícone
                host.Close();
            }
            finally { w.Close(); }
        });
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>Renderização da janela populada em PNG para revisão visual (opt-in: defina SYNCMANAGER_SHOTS com a pasta de saída).</summary>
public class VisualSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerShots", Guid.NewGuid().ToString("N"));

    private static void Write(string path, string content, DateTime time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
    }

    [Fact]
    public async Task Populated_comparison_page_renders()
    {
        var sp = App.BuildServices(Path.Combine(_root, "db", "v.db"), Path.Combine(_root, "logs"));
        await sp.GetRequiredService<Database>().MigrateAsync();
        var s = Path.Combine(_root, "Pasta A"); var t = Path.Combine(_root, "Pasta B");
        var b = TestEnv.BaseTime;
        Write(Path.Combine(s, "Documentos", "doc1.txt"), "same", b); Write(Path.Combine(t, "Documentos", "doc1.txt"), "same", b);
        Write(Path.Combine(s, "Documentos", "relatorio.pdf"), "source is newer and bigger", b.AddHours(3)); Write(Path.Combine(t, "Documentos", "relatorio.pdf"), "old", b);
        Write(Path.Combine(t, "Documentos", "manual.pdf"), "only target", b.AddHours(1));
        Write(Path.Combine(s, "Projetos", "readme.md"), "only source", b.AddHours(2));
        Write(Path.Combine(s, "Imagens", "foto.jpg"), "photo", b); Write(Path.Combine(t, "Imagens", "foto.jpg"), "photo", b);
        Write(Path.Combine(s, "dados.db"), "source version!", b.AddHours(1)); Write(Path.Combine(t, "dados.db"), "target", b.AddHours(2));
        Write(Path.Combine(t, "arquivo_antigo.txt"), "old file", b);

        var dialogs = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = new MainViewModel(sp.GetRequiredService<SyncWorkflow>(), dialogs, sp.GetRequiredService<ILogService>(),
                sp.GetRequiredService<IProfileRepository>(), sp.GetRequiredService<SettingsService>(), sp);
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            vm.SourcePath = s; vm.TargetPath = t;
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.Synchronization);
            await vm.CompareAsync();
            vm.BottomExpanded = false;
            await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();

            w.UpdateLayout();
            var dir = Environment.GetEnvironmentVariable("SYNCMANAGER_SHOTS");
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(w);
            Assert.True(rtb.PixelWidth > 1000);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                await using var fs = File.Create(Path.Combine(dir, "comparison.png"));
                enc.Save(fs);
            }
            w.Close();
        });
        sp.Dispose();
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
