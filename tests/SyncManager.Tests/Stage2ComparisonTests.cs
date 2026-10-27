using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.Controls;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;
using SyncManager.Wpf.Views.Pages;

namespace SyncManager.Tests;

/// <summary>Etapa 2: página de Comparação (cartões Source/Target, trocar lados, abas, resumo, lista).</summary>
public class Stage2ComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerStage2", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    private ServiceProvider Services()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        var sp = App.BuildServices(Path.Combine(_root, "db", "s2.db"), Path.Combine(_root, "logs"));
        sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
        return sp;
    }

    private static MainViewModel NewVm(ServiceProvider sp, FakeDialogs d) => new(
        sp.GetRequiredService<SyncWorkflow>(), d, sp.GetRequiredService<ILogService>(),
        sp.GetRequiredService<IProfileRepository>(), sp.GetRequiredService<SettingsService>(), sp);

    private static void Write(string root, string rel, string content, DateTime? modified = null)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified ?? TestEnv.BaseTime);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    // ------------------------------------------------------------------ cartões Source/Target + trocar lados

    [Fact]
    public async Task Folder_selectors_are_two_way_bound_to_the_view_model()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                var source = UiFind.ByName<FolderSelector>(w, "SourceSelector");
                var target = UiFind.ByName<FolderSelector>(w, "TargetSelector");

                vm.SourcePath = @"C:\Projetos\Documentos";                       // ViewModel → tela
                await UiThread.Settle();
                Assert.Equal(@"C:\Projetos\Documentos", source.FolderPath);
                Assert.Equal(@"C:\Projetos\Documentos", UiFind.ByName<TextBox>(source, "PathBox").Text);

                UiFind.ByName<TextBox>(target, "PathBox").Text = @"D:\Backup";      // tela → ViewModel (digitando)
                await UiThread.Settle();
                Assert.Equal(@"D:\Backup", vm.TargetPath);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Swap_button_exchanges_source_and_target_and_discards_the_stale_plan()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            vm.SourcePath = Source; vm.TargetPath = Target;
            await vm.CompareAsync();
            Assert.True(vm.HasResults);

            Assert.True(vm.SwapSidesCommand.CanExecute(null));
            vm.SwapSidesCommand.Execute(null);

            Assert.Equal(Target, vm.SourcePath);
            Assert.Equal(Source, vm.TargetPath);
            Assert.False(vm.HasResults);                                         // a direção mudou: o plano antigo não vale mais
            Assert.Contains("Comparar novamente", vm.StatusMessage);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Swap_and_drop_commands_do_nothing_while_busy()
    {
        using var sp = Services();
        for (var i = 0; i < 300; i++) Write(Source, $"d{i % 10}\\f{i}.txt", "x" + i);
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.SourcePath = Source; vm.TargetPath = Target;
            var compare = vm.CompareAsync();                                     // deixa ocupado
            if (!vm.IsIdle)
            {
                Assert.False(vm.SwapSidesCommand.CanExecute(null));
                vm.DropSourceCommand.Execute(@"C:\outra");
                Assert.Equal(Source, vm.SourcePath);
            }
            await compare;
        });
    }

    [Fact]
    public async Task Dropping_a_folder_or_a_file_fills_the_path_and_empty_drops_are_ignored()
    {
        using var sp = Services();
        Write(Source, "sub\\arquivo.txt", "x");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var selector = new FolderSelector { DropCommand = vm.DropSourceCommand };

            Assert.Equal(Source, selector.Accept(new[] { Source }));                        // pasta local
            Assert.Equal(Source, vm.SourcePath);

            var file = Path.Combine(Source, "sub", "arquivo.txt");
            Assert.Equal(Path.Combine(Source, "sub"), selector.Accept(new[] { file }));     // arquivo → pasta que o contém
            Assert.Equal(Path.Combine(Source, "sub"), vm.SourcePath);

            Assert.Equal(@"\\192.168.0.5\Arquivos", selector.Accept(new[] { @"\\192.168.0.5\Arquivos" }));            // rede (mesmo offline)
            Assert.Equal(@"\\SERVIDOR\Financeiro\pasta", selector.Accept(new[] { @"\\SERVIDOR\Financeiro\pasta" }));
            Assert.Equal(@"\\SERVIDOR\Financeiro", selector.Accept(new[] { @"\\SERVIDOR\Financeiro\relatorio.pdf" })); // arquivo de rede → pasta
            Assert.Null(selector.Accept(Array.Empty<string>()));
            Assert.Null(selector.Accept(new[] { "" }));
            Assert.Equal(DropState.Normal, selector.State);
            await Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ abas, contagens e resumo

    [Fact]
    public async Task Tabs_are_quick_filters_with_counts_and_include_bidirectional_conflicts()
    {
        using var sp = Services();
        Write(Source, "same.txt", "s"); Write(Target, "same.txt", "s");
        Write(Source, "clash.txt", "source", TestEnv.BaseTime.AddHours(1)); Write(Target, "clash.txt", "target!", TestEnv.BaseTime.AddHours(2));
        Write(Source, "only.txt", "o");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.SourcePath = Source; vm.TargetPath = Target;
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.Synchronization);
            await vm.CompareAsync();

            Assert.Equal("Conflitos (1)", vm.ConflictsTabText);                  // conflito da bidirecional (status é "mais novo")
            Assert.Equal("Erros (0)", vm.ErrorsTabText);
            Assert.Equal("active", vm.AllTabTag);
            Assert.Equal("", vm.ConflictsTabTag);
            Assert.Equal(3, vm.VisibleCount);

            vm.ShowConflictsCommand.Execute(null);
            Assert.Equal("active", vm.ConflictsTabTag);
            Assert.Equal("", vm.AllTabTag);
            Assert.Equal(1, vm.VisibleCount);
            Assert.Equal("clash.txt", vm.RowsView!.Cast<ItemRow>().Single().Item.RelativePath);

            vm.ShowErrorsCommand.Execute(null);
            Assert.Equal(0, vm.VisibleCount);

            vm.ShowAllCommand.Execute(null);
            Assert.Equal(3, vm.VisibleCount);

            // o combo de filtro e as abas ficam sincronizados
            vm.SelectedStatusFilter = vm.StatusFilters.First(f => f.Label == "Iguais");
            Assert.Equal("", vm.AllTabTag);
            Assert.Equal(1, vm.VisibleCount);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Summary_cards_and_planned_actions_reflect_the_comparison()
    {
        using var sp = Services();
        Write(Source, "same.txt", "s"); Write(Target, "same.txt", "s");
        Write(Source, "new1.txt", "1"); Write(Source, "new2.txt", "2");
        Write(Target, "extra.txt", "e");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            Assert.False(vm.HasResults);
            Assert.True(vm.NoResults);
            Assert.Empty(vm.ComparisonSummary);

            vm.SourcePath = Source; vm.TargetPath = Target;                      // Mirror (padrão)
            await vm.CompareAsync();

            Assert.True(vm.HasResults);
            Assert.False(vm.NoResults);
            string V(string label) => vm.ComparisonSummary.Single(c => c.Label == label).Value;
            Assert.Equal("4", V("Analisados"));
            Assert.Equal("1", V("Iguais"));
            Assert.Equal("2", V("Só na origem"));
            Assert.Equal("1", V("Só no destino"));
            Assert.Equal("0", V("Erros"));
            Assert.All(vm.ComparisonSummary, c => Assert.False(string.IsNullOrEmpty(c.IconKey)));   // todo cartão tem ícone (não depende só de cor)

            string P(string label) => vm.PlanSummary.Single(c => c.Label == label).Value;
            Assert.Equal("2", P("Copiar"));
            Assert.Equal("0", P("Atualizar"));
            Assert.Equal("1", P("Excluir"));
            Assert.Equal("1", P("Ignorar"));
            Assert.Contains("Dados a transferir", vm.PlanSummaryText);
            Assert.Equal("4 itens (3 diferenças)", vm.ItemsCountText);

            vm.SourcePath = Source + "x";                                        // mudar a configuração descarta tudo
            Assert.False(vm.HasResults);
            Assert.Empty(vm.ComparisonSummary);
            Assert.Empty(vm.PlanSummary);
            Assert.Equal("Conflitos (0)", vm.ConflictsTabText);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Log_panel_starts_collapsed_and_opens_after_an_execution()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs { ConfirmAnswer = true });
            vm.SourcePath = Source; vm.TargetPath = Target;
            Assert.False(vm.BottomExpanded);
            await vm.CompareAsync();
            await vm.ExecuteAsync();
            Assert.True(vm.BottomExpanded);
            Assert.Contains("Copiados: 1", vm.ExecutionSummary);
        });
    }

    // ------------------------------------------------------------------ a tela em si

    [Fact]
    public async Task Grid_keeps_virtualization_and_shows_vector_status_icons_with_text()
    {
        using var sp = Services();
        Write(Source, "only-source.txt", "a");
        Write(Target, "only-target.txt", "b");
        Write(Source, "same.txt", "s"); Write(Target, "same.txt", "s");
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm) { Width = 1366, Height = 800 };
            w.Show();
            vm.SourcePath = Source; vm.TargetPath = Target;
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.ReplicationAugment);
            await vm.CompareAsync();
            await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            try
            {
                var grid = UiFind.ByName<DataGrid>(w, "ItemsGrid");
                // desempenho: virtualização intacta
                Assert.True(grid.EnableRowVirtualization);
                Assert.True(grid.EnableColumnVirtualization);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));

                // colunas na ordem pedida
                Assert.Equal(new[] { "Exec.", "Status", "Ação", "Caminho relativo", "Tam. Source", "Tam. Target", "Modificado (Source)", "Modificado (Target)" },
                    grid.Columns.Select(c => c.Header?.ToString()).ToArray());

                // cada linha tem ícone vetorial + texto do status (não depende só da cor)
                var resources = System.Windows.Application.Current.Resources;
                var expected = new Dictionary<string, string>
                {
                    ["only-source.txt"] = "IconArrowRight", ["only-target.txt"] = "IconArrowLeft", ["same.txt"] = "IconCheck"
                };
                foreach (ItemRow row in vm.RowsView!)
                {
                    var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
                    Assert.NotNull(container);
                    var icon = Descendants<System.Windows.Shapes.Path>(container).First(p => p.Width == 15);
                    Assert.Equal(((Geometry)resources[expected[row.Item.RelativePath]]).Bounds, icon.Data.Bounds);
                    Assert.Contains(Descendants<TextBlock>(container), t => t.Text == row.StatusText);
                }
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task More_options_button_toggles_the_advanced_panel_and_the_plan_tab_opens_the_plan()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                var expander = UiFind.ByName<Expander>(w, "OptionsExpander");
                Assert.False(expander.IsExpanded);
                var more = Descendants<Button>(w).First(b => Equals(b.Content, "Mais opções..."));
                Click(more);
                await UiThread.Settle();                                          // o Invoke da automação é assíncrono
                Assert.True(expander.IsExpanded);
                Click(more);
                await UiThread.Settle();
                Assert.False(expander.IsExpanded);

                vm.SourcePath = Source; vm.TargetPath = Target;
                await vm.CompareAsync();
                await UiThread.Settle();                                          // deixa o WPF reavaliar CanExecute dos comandos
                Click(UiFind.ByName<Button>(w, "TabPlan"));                       // a aba "Plano de execução" abre o plano
                await UiThread.Settle();
                Assert.True(vm.IsPlanScreen);
            }
            finally { w.Close(); }
        });
    }

    private static void Click(Button b) =>
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(b)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
