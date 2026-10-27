using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.Controls;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;

namespace SyncManager.Tests;

/// <summary>Ícone do aplicativo, execução parcial (marcar/desmarcar itens) e seleção pelos cartões do resumo.</summary>
public class Stage7SelectiveExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerStage7", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    private ServiceProvider Services()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        var sp = App.BuildServices(Path.Combine(_root, "db", "s7.db"), Path.Combine(_root, "logs"));
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

    /// <summary>Mirror: copia novo.txt, atualiza mudou.txt, exclui extra.txt, ignora igual.txt.</summary>
    private void Scenario()
    {
        Write(Source, "novo.txt", "novo");
        Write(Source, "mudou.txt", "versão nova e maior", TestEnv.BaseTime.AddHours(2));
        Write(Target, "mudou.txt", "velha");
        Write(Source, "igual.txt", "igual"); Write(Target, "igual.txt", "igual");
        Write(Target, "extra.txt", "extra");
    }

    private static async Task Prepare(MainViewModel vm, SyncMethod method = SyncMethod.MirrorSourceToTarget)
    {
        vm.SourcePath = Path.GetFullPath(vm.SourcePath);
        vm.SelectedMethod = vm.Methods.First(m => m.Method == method);
        vm.ExcludeText = "";
        await vm.CompareAsync();
    }

    private static T Card<T>(IEnumerable<T> items, Func<T, bool> pred) => items.First(pred);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    // ------------------------------------------------------------------ ícone

    [Fact]
    public async Task App_icon_is_a_multi_size_icon_used_by_windows_and_the_executable()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var uri = new Uri("pack://application:,,,/SyncManager;component/Resources/app.ico");
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(decoder.Frames.Count >= 6);
            Assert.Contains(decoder.Frames, f => f.PixelWidth == 256);
            Assert.Contains(decoder.Frames, f => f.PixelWidth == 16);

            var w = new MainWindow(NewVm(sp, new FakeDialogs()));
            Assert.NotNull(w.Icon);                          // vem do estilo AppWindow: vale para todas as janelas
            await Task.CompletedTask;
        });

        // o executável carrega o mesmo ícone (ApplicationIcon)
        var exe = typeof(App).Assembly.Location;
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        Assert.NotNull(icon);
        Assert.True(icon!.Width >= 16);
    }

    // ------------------------------------------------------------------ cartões selecionam

    [Fact]
    public async Task Clicking_a_summary_card_selects_exactly_its_items()
    {
        using var sp = Services();
        Scenario();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                vm.SourcePath = Source; vm.TargetPath = Target;
                await Prepare(vm);
                await UiThread.Settle();
                var grid = UiFind.ByName<DataGrid>(w, "ItemsGrid");

                var cards = Descendants<SummaryCard>(w).ToList();
                var sourceOnly = cards.First(c => c.Label == "Só na origem");
                Assert.True(sourceOnly.Focusable);           // tem comando: é clicável
                Assert.Contains("clique", sourceOnly.Hint);

                // clique de verdade no cartão
                sourceOnly.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await UiThread.Settle();
                var selected = grid.SelectedItems.OfType<ItemRow>().ToList();
                Assert.Equal(new[] { "novo.txt" }, selected.Select(r => r.Item.RelativePath));
                Assert.Contains("1 item(ns) selecionado(s): Só na origem", vm.StatusMessage);

                Click(cards.First(c => c.Label == "Iguais"));
                Assert.Equal(new[] { "igual.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));

                Click(cards.First(c => c.Label == "Só no destino"));
                Assert.Equal(new[] { "extra.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));

                Click(cards.First(c => c.Label == "Analisados"));
                Assert.Equal(4, grid.SelectedItems.Count);

                // um filtro que esconderia os itens é desfeito antes de selecionar
                vm.ShowErrorsCommand.Execute(null);
                await UiThread.Settle();
                Click(cards.First(c => c.Label == "Iguais"));
                Assert.Equal("Todos", vm.SelectedStatusFilter.Label);
                Assert.Single(grid.SelectedItems);

                // cartão vazio: nada selecionado e aviso
                Click(cards.First(c => c.Label == "Conflitos"));
                Assert.Empty(grid.SelectedItems);
                Assert.Contains("Nenhum item", vm.StatusMessage);

                // "Ações previstas" também selecionam
                var copiar = vm.PlanSummary.First(p => p.Label == "Copiar");
                vm.SelectRowsCommand.Execute(copiar);
                Assert.Equal(new[] { "novo.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));
                vm.SelectRowsCommand.Execute(vm.PlanSummary.First(p => p.Label == "Excluir"));
                Assert.Equal(new[] { "extra.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));
                vm.SelectRowsCommand.Execute(vm.PlanSummary.First(p => p.Label == "Atualizar"));
                Assert.Equal(new[] { "mudou.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));
                vm.SelectRowsCommand.Execute(vm.PlanSummary.First(p => p.Label == "Ignorar"));
                Assert.Equal(new[] { "igual.txt" }, grid.SelectedItems.OfType<ItemRow>().Select(r => r.Item.RelativePath));
            }
            finally { w.Close(); }
        });
    }

    private static void Click(SummaryCard c) =>
        c.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    [Fact]
    public async Task Selecting_thousands_of_rows_from_a_card_stays_fast()
    {
        using var sp = Services();
        for (var i = 0; i < 2500; i++) { Write(Source, $"i{i:0000}.txt", "same"); Write(Target, $"i{i:0000}.txt", "same"); }
        for (var i = 0; i < 2500; i++) Write(Source, $"n{i:0000}.txt", "novo");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            try
            {
                vm.SourcePath = Source; vm.TargetPath = Target;
                await Prepare(vm);
                await UiThread.Settle();
                var grid = UiFind.ByName<DataGrid>(w, "ItemsGrid");
                foreach (var label in new[] { "Iguais", "Só na origem", "Analisados" })   // metade, metade, tudo
                {
                    var sw = Stopwatch.StartNew();
                    vm.SelectRowsCommand.Execute(vm.ComparisonSummary.First(c => c.Label == label));
                    await UiThread.Settle();
                    Assert.True(sw.ElapsedMilliseconds < 8_000, $"{label}: {sw.ElapsedMilliseconds} ms");
                    Assert.Equal(label == "Analisados" ? 5000 : 2500, grid.SelectedItems.Count);
                }
            }
            finally { w.Close(); }
        });
    }

    // ------------------------------------------------------------------ execução parcial

    [Fact]
    public async Task Unchecked_items_stay_in_the_list_but_are_left_out_of_the_execution()
    {
        using var sp = Services();
        Scenario();
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            vm.SourcePath = Source; vm.TargetPath = Target;
            await Prepare(vm);

            Assert.Equal("Marcados: 3 de 3", vm.IncludedText);
            var rows = vm.RowsView!.Cast<ItemRow>().ToList();
            Assert.Equal(4, rows.Count);
            Assert.All(rows.Where(r => r.CanInclude), r => Assert.True(r.IsIncluded));
            Assert.False(rows.Single(r => r.Item.RelativePath == "igual.txt").CanInclude);   // sem ação: não há o que marcar

            // desmarca a atualização e a exclusão
            rows.Single(r => r.Item.RelativePath == "mudou.txt").IsIncluded = false;
            rows.Single(r => r.Item.RelativePath == "extra.txt").IsIncluded = false;
            Assert.Equal("Marcados: 1 de 3", vm.IncludedText);
            Assert.Equal(4, vm.RowsView!.Cast<ItemRow>().Count());                     // continuam na lista
            Assert.Equal("1", vm.PlanSummary.First(p => p.Label == "Copiar").Value);
            Assert.Equal("0", vm.PlanSummary.First(p => p.Label == "Atualizar").Value);
            Assert.Equal("0", vm.PlanSummary.First(p => p.Label == "Excluir").Value);
            Assert.Contains(vm.Notices, n => n.Contains("2 item(ns) desmarcados"));

            // a página do plano mostra só o que vai acontecer
            vm.PreviewCommand.Execute(null);
            var planned = vm.Plan!.View.Cast<PlanRow>().Select(r => r.Operation.RelativePath).ToList();
            Assert.Equal(new[] { "novo.txt" }, planned);
            vm.BackCommand.Execute(null);

            await vm.ExecuteAsync();
            Assert.True(File.Exists(Path.Combine(Target, "novo.txt")));                // marcado: executado
            Assert.Equal("velha", File.ReadAllText(Path.Combine(Target, "mudou.txt"))); // desmarcado: intacto
            Assert.True(File.Exists(Path.Combine(Target, "extra.txt")));                // desmarcado: não excluído
            var results = Assert.IsAssignableFrom<IReadOnlyList<OperationEntry>>(vm.Execution.Operations);
            Assert.DoesNotContain(results, r => r.File == "mudou.txt" && r.StatusKey == "ok");
        });
    }

    [Fact]
    public async Task Mark_all_unmark_all_and_only_selected_and_nothing_marked_disables_execute()
    {
        using var sp = Services();
        Scenario();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.SourcePath = Source; vm.TargetPath = Target;
            await Prepare(vm);
            var rows = vm.RowsView!.Cast<ItemRow>().ToList();

            vm.UnmarkAllCommand.Execute(null);
            Assert.All(rows, r => Assert.False(r.IsIncluded));
            Assert.Equal("Marcados: 0 de 3", vm.IncludedText);
            Assert.False(vm.ExecuteCommand.CanExecute(null));                           // nada marcado: nada a executar

            vm.MarkAllCommand.Execute(null);
            Assert.True(vm.ExecuteCommand.CanExecute(null));
            Assert.Equal("Marcados: 3 de 3", vm.IncludedText);

            vm.IncludeOnly(rows.Where(r => r.Item.RelativePath == "extra.txt"));
            Assert.Equal("Marcados: 1 de 3", vm.IncludedText);
            Assert.Equal("1", vm.PlanSummary.First(p => p.Label == "Excluir").Value);
            Assert.Equal("0", vm.PlanSummary.First(p => p.Label == "Copiar").Value);

            // item sem ação ignora a marca
            var igual = rows.Single(r => r.Item.RelativePath == "igual.txt");
            igual.IsIncluded = true;
            Assert.False(igual.IsIncluded);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_folder_delete_is_skipped_when_something_inside_it_is_left_out()
    {
        using var sp = Services();
        Write(Source, "fica.txt", "x");
        Write(Target, "fica.txt", "x");
        Write(Target, "velha\\a.txt", "a");
        Write(Target, "velha\\b.txt", "b");
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            vm.SourcePath = Source; vm.TargetPath = Target;
            await Prepare(vm);
            var rows = vm.RowsView!.Cast<ItemRow>().ToList();
            Assert.Contains(rows, r => r.Item.RelativePath == "velha" && r.CanInclude);   // a pasta tem exclusão própria

            rows.Single(r => r.Item.RelativePath == "velha\\b.txt").IsIncluded = false;
            await vm.ExecuteAsync();

            Assert.False(File.Exists(Path.Combine(Target, "velha", "a.txt")));            // marcado: excluído (pasta de segurança)
            Assert.True(File.Exists(Path.Combine(Target, "velha", "b.txt")));             // desmarcado: continua lá
            Assert.True(Directory.Exists(Path.Combine(Target, "velha")));                  // e a pasta não é apagada
            Assert.Empty(d.Errors);
            Assert.Contains("Concluído", vm.Execution.ResultTitle);
            Assert.Equal(0, vm.Execution.ResultText.Contains("Erros 0") ? 0 : 1);
        });
    }

    [Fact]
    public async Task Row_checkbox_in_the_grid_drives_the_view_model_and_space_toggles_the_selection()
    {
        using var sp = Services();
        Scenario();
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            try
            {
                vm.SourcePath = Source; vm.TargetPath = Target;
                await Prepare(vm);
                await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
                var grid = UiFind.ByName<DataGrid>(w, "ItemsGrid");
                Assert.Equal("Exec.", grid.Columns[0].Header);

                var boxes = Descendants<CheckBox>(grid).Where(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Executar este item").ToList();
                var visible = boxes.Where(b => b.Visibility == Visibility.Visible).ToList();
                Assert.Equal(3, visible.Count);                                  // só os itens com ação têm caixa
                Assert.All(visible, b => Assert.True(b.IsChecked));

                visible[0].IsChecked = false;                                    // o usuário desmarca na grade
                Assert.Equal(2, vm.RowsView!.Cast<ItemRow>().Count(r => r.CanInclude && r.IsIncluded));

                // Espaço alterna os selecionados
                grid.SelectedItems.Clear();
                foreach (var r in vm.RowsView!.Cast<ItemRow>().Where(r => r.CanInclude)) grid.SelectedItems.Add(r);
                grid.Focus();
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                grid.RaiseEvent(e);
                Assert.True(vm.RowsView!.Cast<ItemRow>().Where(r => r.CanInclude).All(r => r.IsIncluded));   // havia um desmarcado → marca todos
                grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.True(vm.RowsView!.Cast<ItemRow>().Where(r => r.CanInclude).All(r => !r.IsIncluded));  // todos marcados → desmarca todos
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Bidirectional_conflicts_cannot_be_checked_until_resolved_and_then_default_to_checked()
    {
        using var sp = Services();
        // mesmo arquivo alterado nos dois lados, sem histórico: conflito
        Write(Source, "c.txt", "lado A", TestEnv.BaseTime.AddHours(1));
        Write(Target, "c.txt", "lado B diferente", TestEnv.BaseTime.AddHours(2));
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.SourcePath = Source; vm.TargetPath = Target;
            await Prepare(vm, SyncMethod.Synchronization);
            var row = vm.RowsView!.Cast<ItemRow>().Single();
            Assert.True(row.IsConflict);
            Assert.False(row.CanInclude);                                          // conflito não tem ação até ser resolvido
            vm.ResolveSelection(new[] { row }, ConflictResolution.KeepSource);
            var resolved = vm.RowsView!.Cast<ItemRow>().Single();
            Assert.True(resolved.CanInclude);
            Assert.True(resolved.IsIncluded);
            Assert.True(vm.ExecuteCommand.CanExecute(null));
            await Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
