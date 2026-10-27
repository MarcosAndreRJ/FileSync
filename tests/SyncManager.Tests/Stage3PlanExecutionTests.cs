using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;
using SyncManager.Wpf.Views.Pages;

namespace SyncManager.Tests;

/// <summary>Etapa 3: página "Plano de execução" e página "Execução" (progresso + lista de operações).</summary>
public class Stage3PlanExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerStage3", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    private ServiceProvider Services()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        var sp = App.BuildServices(Path.Combine(_root, "db", "s3.db"), Path.Combine(_root, "logs"));
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
    private void MirrorScenario()
    {
        Write(Source, "novo.txt", "novo");
        Write(Source, "mudou.txt", "versão nova e maior", TestEnv.BaseTime.AddHours(2));
        Write(Target, "mudou.txt", "velha", TestEnv.BaseTime);
        Write(Source, "igual.txt", "igual"); Write(Target, "igual.txt", "igual");
        Write(Target, "extra.txt", "extra");
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

    private static void Click(Button b) =>
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(b)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();

    private static async Task Prepare(MainViewModel vm, string source, string target)
    {
        vm.SourcePath = source; vm.TargetPath = target;
        vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.MirrorSourceToTarget);
        vm.ExcludeText = "";
        await vm.CompareAsync();
    }

    // ------------------------------------------------------------------ plano

    [Fact]
    public async Task Plan_page_shows_counts_columns_and_switches_visibility()
    {
        using var sp = Services();
        MirrorScenario();
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                await Prepare(vm, Source, Target);
                await UiThread.Settle();
                Assert.True(vm.IsComparisonScreen);

                Click(UiFind.ByName<Button>(w, "TabPlan"));
                await UiThread.Settle();
                Assert.True(vm.IsPlanScreen);
                Assert.Equal(Visibility.Visible, UiFind.ByName<PlanView>(w, "PlanPage").Visibility);
                Assert.Equal(Visibility.Collapsed, UiFind.ByName<ComparisonView>(w, "ComparisonPage").Visibility);
                Assert.Equal(Visibility.Collapsed, UiFind.ByName<ExecutionView>(w, "ExecutionPage").Visibility);

                var cards = vm.Plan!.Cards.ToDictionary(c => c.Label, c => c.Value);
                Assert.Equal("1", cards["Copiar"]);
                Assert.Equal("1", cards["Atualizar"]);
                Assert.Equal("1", cards["Excluir"]);
                Assert.Equal("1", cards["Ignorar"]);
                Assert.Equal("0", cards["Conflitos"]);

                var grid = UiFind.ByName<DataGrid>(w, "PlanGrid");
                Assert.Equal(new[] { "Operação", "Origem", "Destino", "Tamanho", "Status" }, grid.Columns.Select(c => c.Header?.ToString()));
                Assert.Equal(3, grid.Items.Count);                     // filtro padrão: só ações (sem SKIP)

                var copy = vm.Plan.View.Cast<PlanRow>().Single(r => r.Operation.Type == SyncOperationType.Copy);
                Assert.Equal(Path.Combine(Source, "novo.txt"), copy.From);
                Assert.Equal(Path.Combine(Target, "novo.txt"), copy.To);
                Assert.Equal("Source\\novo.txt", copy.FromDisplay);     // a grade mostra o caminho curto; o completo fica no tooltip/CSV
                Assert.Equal("Target\\novo.txt", copy.ToDisplay);
                Assert.Equal("Pendente", copy.StatusText);
                var del = vm.Plan.View.Cast<PlanRow>().Single(r => r.Operation.Type == SyncOperationType.Delete);
                Assert.Equal("", del.From);
                Assert.Equal(Path.Combine(Target, "extra.txt"), del.To);

                // vetorial, sem depender só da cor: cada linha tem ícone + texto de status
                Assert.Contains(Descendants<TextBlock>(grid), t => t.Text == "Pendente");

                Click(UiFind.ByName<Button>(w, "PlanBackButton"));
                await UiThread.Settle();
                Assert.True(vm.IsComparisonScreen);
                Assert.Equal(Visibility.Visible, UiFind.ByName<ComparisonView>(w, "ComparisonPage").Visibility);
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Plan_filter_and_search_narrow_the_list_and_the_visible_count()
    {
        using var sp = Services();
        MirrorScenario();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);
            var plan = vm.Plan!;
            Assert.Equal(3, plan.VisibleCount);

            plan.SelectedFilter = plan.Filters.First(f => f.Label == "Todas");
            Assert.Equal(4, plan.VisibleCount);
            Assert.Equal("4 de 4 operações", plan.VisibleText);

            plan.Search = "mudou";
            Assert.Equal(1, plan.VisibleCount);
            Assert.Equal("1 de 4 operações", plan.VisibleText);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Changing_the_configuration_while_on_the_plan_page_discards_it_and_returns()
    {
        using var sp = Services();
        MirrorScenario();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);
            Assert.True(vm.IsPlanScreen);

            vm.IncludeSubfolders = !vm.IncludeSubfolders;     // o plano deixou de valer
            Assert.True(vm.IsComparisonScreen);
            Assert.Null(vm.Plan);
            Assert.False(vm.ExportPlanCommand.CanExecute(null));
            await Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ exportar

    [Fact]
    public async Task Export_writes_a_csv_with_exactly_the_filtered_rows()
    {
        using var sp = Services();
        MirrorScenario();
        Write(Source, "com;ponto e virgula.txt", "x");
        var file = Path.Combine(_root, "saida", "plano.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var d = new FakeDialogs { SavePath = file };
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);
            vm.Plan!.Search = "mudou";

            vm.ExportPlanCommand.Execute(null);

            Assert.Single(d.SaveRequests);
            Assert.EndsWith(".csv", d.SaveRequests[0]);
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);      // UTF-8 com BOM: o Excel abre acentos certo
            var lines = (await File.ReadAllTextAsync(file)).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Operação;Origem;Destino;Tamanho (bytes);Status;Motivo", lines[0]);
            Assert.Equal(2, lines.Length);                                   // cabeçalho + 1 linha filtrada
            Assert.Contains("mudou.txt", lines[1]);
            Assert.Single(d.Infos);

            // nome com ";" fica entre aspas, então não quebra as colunas
            vm.Plan.Search = "ponto";
            Assert.Contains("ponto e virgula.txt\";", vm.Plan.ToCsv());
        });
    }

    [Fact]
    public async Task Export_cancelled_or_failing_does_not_throw()
    {
        using var sp = Services();
        MirrorScenario();
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);

            vm.ExportPlanCommand.Execute(null);                 // usuário cancelou o diálogo
            Assert.Empty(d.Infos); Assert.Empty(d.Errors);

            d.SavePath = Path.Combine(_root, "pasta-que-nao-existe", "x.csv");
            vm.ExportPlanCommand.Execute(null);                 // falha de E/S vira mensagem, não exceção
            Assert.Single(d.Errors);
            await Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ execução

    [Fact]
    public async Task Executing_from_the_plan_page_shows_the_execution_page_with_results()
    {
        using var sp = Services();
        MirrorScenario();
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, d);
            vm.ProfileName = "Backup diário";
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                await Prepare(vm, Source, Target);
                vm.PreviewCommand.Execute(null);
                await UiThread.Settle();

                var run = vm.ExecuteAsync();
                await run;
                await UiThread.Settle();

                Assert.True(vm.IsExecutionScreen);
                Assert.Equal(Visibility.Visible, UiFind.ByName<ExecutionView>(w, "ExecutionPage").Visibility);
                var e = vm.Execution;
                Assert.True(e.IsFinished); Assert.False(e.IsRunning);
                Assert.Equal("Backup diário", e.ProfileName);
                Assert.Equal("Concluído com sucesso", e.ResultTitle);
                Assert.Equal(100, e.Percent);
                Assert.Equal("100%", e.PercentText);
                Assert.Equal("IconCheck", e.ResultIcon);

                var ops = Assert.IsAssignableFrom<IReadOnlyList<OperationEntry>>(e.Operations);
                Assert.Contains(ops, o => o.File == "novo.txt" && o.Status == "Concluído");
                Assert.Contains(ops, o => o.File == "extra.txt" && o.Operation.StartsWith("DELETE"));
                Assert.DoesNotContain(ops, o => o.StatusKey is "error" or "running");

                Assert.True(File.Exists(Path.Combine(Target, "novo.txt")));
                Assert.False(File.Exists(Path.Combine(Target, "extra.txt")));

                // botões: terminou → Cancelar some; Voltar e logs aparecem
                Assert.Equal(Visibility.Collapsed, UiFind.ByName<Button>(w, "ExecCancelButton").Visibility);
                Assert.Equal(Visibility.Visible, UiFind.ByName<Button>(w, "ExecBackButton").Visibility);

                Click(UiFind.ByName<Button>(w, "ExecLogButton"));
                await UiThread.Settle();
                Assert.Equal(1, d.LogsOpened);

                Click(UiFind.ByName<Button>(w, "ExecBackButton"));
                await UiThread.Settle();
                Assert.True(vm.IsComparisonScreen);
                Assert.True(vm.BottomExpanded);
                Assert.Contains("Concluído com sucesso", vm.ExecutionSummary);   // o resumo textual continua disponível
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Declining_the_confirmation_keeps_the_plan_page_and_changes_nothing()
    {
        using var sp = Services();
        MirrorScenario();
        var d = new FakeDialogs { ConfirmAnswer = false };
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);

            await vm.ExecuteAsync();

            Assert.True(vm.IsPlanScreen);
            Assert.False(vm.Execution.IsRunning);
            Assert.False(File.Exists(Path.Combine(Target, "novo.txt")));
            Assert.True(File.Exists(Path.Combine(Target, "extra.txt")));
        });
    }

    [Fact]
    public async Task Cancel_button_is_available_only_while_running_and_a_cancelled_run_is_reported()
    {
        using var sp = Services();
        for (var i = 0; i < 40; i++) Write(Source, $"f{i}.bin", new string('x', 200_000));
        var d = new FakeDialogs();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                await Prepare(vm, Source, Target);
                vm.PreviewCommand.Execute(null);

                var run = vm.ExecuteAsync();
                await UiThread.Settle();
                Assert.True(vm.Execution.IsRunning || vm.Execution.IsFinished);
                if (vm.Execution.IsRunning)
                {
                    Assert.Equal(Visibility.Visible, UiFind.ByName<Button>(w, "ExecCancelButton").Visibility);
                    Assert.False(vm.BackCommand.CanExecute(null));            // não sai da página no meio da execução
                    vm.CancelCommand.Execute(null);
                }
                await run;
                await UiThread.Settle();
                Assert.True(vm.Execution.IsFinished);
                Assert.True(vm.BackCommand.CanExecute(null));
                Assert.Contains(vm.Execution.ResultTitle, new[] { "Cancelado pelo usuário", "Concluído com sucesso" });   // pode terminar antes de cancelar
            }
            finally { w.Close(); }
        });
    }

    // ------------------------------------------------------------------ apresentação do progresso (sem disco)

    [Fact]
    public async Task Progress_update_fills_files_bytes_speed_elapsed_and_remaining()
    {
        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "S", "T", Array.Empty<SyncOperation>(), 0, 0.3, null, Array.Empty<string>());
        await UiThread.Run(() =>
        {
            var e = new ExecutionViewModel();
            e.Begin("", plan);
            Assert.Equal("Perfil sem nome", e.ProfileName);
            Assert.True(e.IsRunning);

            e.Update(new ExecutionProgress("pasta\\a.txt", 10, 40, 50_000_000, 200_000_000, 10_000_000, TimeSpan.FromSeconds(5)));
            Assert.Equal("10 / 40", e.FilesText);
            Assert.Contains("MB/s", e.SpeedText);
            Assert.Equal("00:00:05", e.ElapsedText);
            Assert.Equal("00:00:15", e.RemainingText);                 // 150 MB restantes a 10 MB/s
            Assert.Equal("pasta\\a.txt", e.CurrentOperation);
            var live = Assert.IsAssignableFrom<System.Collections.ObjectModel.ObservableCollection<OperationEntry>>(e.Operations);
            Assert.Single(live);
            Assert.Equal("Em andamento", live[0].Status);

            // o mesmo arquivo em relatórios seguidos não duplica a linha
            e.Update(new ExecutionProgress("pasta\\a.txt", 10, 40, 60_000_000, 200_000_000, 10_000_000, TimeSpan.FromSeconds(6)));
            Assert.Single(live);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Remaining_time_falls_back_to_operation_rate_and_shows_a_dash_without_data()
    {
        Assert.Equal("—", ExecutionViewModel.Remaining(new ExecutionProgress(null, 0, 10, 0, 0, 0, TimeSpan.Zero)));
        // sem bytes: 5 de 10 operações em 10 s → faltam 10 s
        Assert.Equal("00:00:10", ExecutionViewModel.Remaining(new ExecutionProgress("x", 5, 10, 0, 0, 0, TimeSpan.FromSeconds(10))));
        Assert.Equal("—", ExecutionViewModel.Remaining(new ExecutionProgress("x", 10, 10, 100, 100, 50, TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Live_operations_list_is_bounded()
    {
        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "S", "T", Array.Empty<SyncOperation>(), 0, 0.3, null, Array.Empty<string>());
        await UiThread.Run(() =>
        {
            var e = new ExecutionViewModel();
            e.Begin("P", plan);
            for (var i = 0; i < 5000; i++)
                e.Update(new ExecutionProgress($"f{i}", i, 5000, 0, 0, 0, TimeSpan.FromSeconds(1)));
            var live = Assert.IsAssignableFrom<System.Collections.ObjectModel.ObservableCollection<OperationEntry>>(e.Operations);
            Assert.Equal(2000, live.Count);
            Assert.Equal("f4999", live[^1].File);
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ renderização para revisão visual (opt-in)

    [Fact]
    public async Task Plan_and_execution_pages_render()
    {
        using var sp = Services();
        MirrorScenario();
        for (var i = 0; i < 25; i++) Write(Source, $"Fotos\\img_{i:000}.jpg", new string('x', 1000 + i * 37));
        var dir = Environment.GetEnvironmentVariable("SYNCMANAGER_SHOTS");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.ProfileName = "Backup Documentos";
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            await Prepare(vm, Source, Target);
            vm.PreviewCommand.Execute(null);
            await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
            Shot(w, dir, "plan.png");

            await vm.ExecuteAsync();
            await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
            Shot(w, dir, "execution.png");
            w.Close();
        });
    }

    private static void Shot(Window w, string? dir, string name)
    {
        w.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        Assert.True(rtb.PixelWidth > 1000);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, name));
        enc.Save(fs);
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
