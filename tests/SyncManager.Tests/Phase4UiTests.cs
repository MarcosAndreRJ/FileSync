using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.Services;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;

namespace SyncManager.Tests;

/// <summary>Teclado (← → Delete), menu de contexto, resolução de conflitos e diálogos da Fase 4, com a interface real.</summary>
public class Phase4UiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerUi4Tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    private ServiceProvider Services()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        var sp = App.BuildServices(Path.Combine(_root, "db", "ui.db"), Path.Combine(_root, "logs"));
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

    /// <summary>Dispara um PreviewKeyDown de verdade no elemento (o mesmo caminho do teclado físico).</summary>
    private static bool Press(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var waited = 0; !condition() && waited < timeoutMs; waited += 25) await Task.Delay(25);
        Assert.True(condition(), "a condição não foi atendida a tempo");
    }

    private static DataGrid Grid(Window w) => UiFind.ByName<DataGrid>(w, "ItemsGrid");

    private static void Select(Window w, MainViewModel vm, params string[] paths)
    {
        var grid = Grid(w);
        grid.SelectedItems.Clear();
        foreach (var row in vm.RowsView!.Cast<ItemRow>().Where(r => paths.Contains(r.Item.RelativePath, StringComparer.OrdinalIgnoreCase)))
            grid.SelectedItems.Add(row);
        Assert.Equal(paths.Length, grid.SelectedItems.Count);
    }

    private static async Task<(MainWindow Window, MainViewModel Vm)> OpenAsync(ServiceProvider sp, FakeDialogs d, string source, string target, SyncMethod method)
    {
        var vm = NewVm(sp, d);
        var window = new MainWindow(vm);
        window.Show();
        vm.SourcePath = source;
        vm.TargetPath = target;
        vm.SelectedMethod = vm.Methods.First(m => m.Method == method);
        await vm.CompareAsync();
        await UiThread.Settle();
        return (window, vm);
    }

    // ------------------------------------------------------------------ → e ← copiam os selecionados

    [Fact]
    public async Task Right_and_left_arrows_copy_the_selected_rows_in_the_arrow_direction()
    {
        using var sp = Services();
        Write(Source, "a.txt", "from source", TestEnv.BaseTime.AddHours(1));
        Write(Source, "untouched.txt", "stay");
        Write(Target, "b.txt", "from target", TestEnv.BaseTime.AddHours(2));
        var d = new FakeDialogs();

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);   // Augment: nunca copiaria b.txt
            try
            {
                // → : a.txt vai do Source para o Target
                Select(w, vm, "a.txt");
                Assert.True(Press(Grid(w), Key.Right));                  // tratada (não rola a grade)
                await WaitAsync(() => vm.IsIdle && File.Exists(Path.Combine(Target, "a.txt")));
                Assert.Equal("from source", File.ReadAllText(Path.Combine(Target, "a.txt")));
                Assert.False(File.Exists(Path.Combine(Target, "untouched.txt")));   // não selecionado, não copiado
                Assert.Empty(d.Confirms);                                            // copiar para lado vazio não pede confirmação

                // ← : b.txt vai do Target para o Source
                Select(w, vm, "b.txt");
                Assert.True(Press(Grid(w), Key.Left));
                await WaitAsync(() => vm.IsIdle && File.Exists(Path.Combine(Source, "b.txt")));
                Assert.Equal("from target", File.ReadAllText(Path.Combine(Source, "b.txt")));
                Assert.Equal(TestEnv.BaseTime.AddHours(2), File.GetLastWriteTimeUtc(Path.Combine(Source, "b.txt")));

                // a lista foi atualizada no lugar: as linhas copiadas agora aparecem como iguais
                var a = vm.RowsView!.Cast<ItemRow>().Single(r => r.Item.RelativePath == "a.txt");
                Assert.Equal("Igual", a.StatusText);
                Assert.Contains("Cópia", vm.StatusMessage);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Arrow_over_an_existing_file_asks_before_overwriting_and_declining_changes_nothing()
    {
        using var sp = Services();
        Write(Source, "doc.txt", "source version", TestEnv.BaseTime);
        Write(Target, "doc.txt", "NEWER target version", TestEnv.BaseTime.AddHours(3));
        var d = new FakeDialogs { ConfirmAnswer = false };

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);
            try
            {
                Select(w, vm, "doc.txt");
                Press(Grid(w), Key.Right);
                await WaitAsync(() => d.Confirms.Count == 1);
                await UiThread.Settle();
                Assert.Equal("NEWER target version", File.ReadAllText(Path.Combine(Target, "doc.txt")));   // recusado: intacto

                d.ConfirmAnswer = true;
                Press(Grid(w), Key.Right);
                await WaitAsync(() => vm.IsIdle && File.ReadAllText(Path.Combine(Target, "doc.txt")) == "source version");
                Assert.Equal(2, d.Confirms.Count);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Arrows_with_modifiers_or_without_selection_do_nothing()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        var d = new FakeDialogs();

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);
            try
            {
                Grid(w).SelectedItems.Clear();
                Press(Grid(w), Key.Right);                      // nada selecionado
                await UiThread.Settle();
                Assert.False(File.Exists(Path.Combine(Target, "a.txt")));
                Assert.Empty(d.Infos);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Arrow_with_nothing_applicable_explains_why()
    {
        using var sp = Services();
        Write(Target, "only-target.txt", "t");
        var d = new FakeDialogs();

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);
            try
            {
                Select(w, vm, "only-target.txt");
                Press(Grid(w), Key.Right);                      // o arquivo não existe no Source: não há o que copiar →
                await WaitAsync(() => d.Infos.Count == 1);
                Assert.False(File.Exists(Path.Combine(Source, "only-target.txt")));
            }
            finally { w.Close(); }
        });
    }

    // ------------------------------------------------------------------ Delete

    [Fact]
    public async Task Delete_asks_and_remove_from_list_never_touches_files_and_protects_them_from_the_run()
    {
        using var sp = Services();
        Write(Source, "keep.txt", "k");
        Write(Target, "extra1.txt", "x1"); Write(Target, "extra2.txt", "x2");
        var d = new FakeDialogs { DeleteAnswer = DeleteChoice.RemoveFromList };

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.MirrorSourceToTarget);   // Mirror apagaria os dois extras
            try
            {
                Assert.Equal(2, vm.CurrentPlan!.DeleteCount);
                Select(w, vm, "extra1.txt");
                Assert.True(Press(Grid(w), Key.Delete));
                await WaitAsync(() => d.DeleteRequests.Count == 1);
                await UiThread.Settle();

                var req = d.DeleteRequests.Single();
                Assert.Equal(1, req.Count);
                Assert.False(req.AnySource);          // só existe no Target: a janela não oferece excluir do Source
                Assert.True(req.AnyTarget);
                Assert.Contains("pasta de segurança", req.StrategyText);

                Assert.DoesNotContain(vm.RowsView!.Cast<ItemRow>(), r => r.Item.RelativePath == "extra1.txt");   // saiu da lista
                Assert.True(File.Exists(Path.Combine(Target, "extra1.txt")));                                     // arquivo intocado
                Assert.Equal(1, vm.CurrentPlan!.DeleteCount);

                // executar: o removido da lista fica protegido, o outro extra é excluído
                d.ConfirmAnswer = true;
                await vm.ExecuteAsync();
                Assert.True(File.Exists(Path.Combine(Target, "extra1.txt")));
                Assert.False(File.Exists(Path.Combine(Target, "extra2.txt")));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Delete_can_also_physically_delete_with_a_second_confirmation_through_the_safety_folder()
    {
        using var sp = Services();
        Write(Source, "both.txt", "s"); Write(Target, "both.txt", "s");
        Write(Source, "src-only.txt", "so");
        var d = new FakeDialogs { DeleteAnswer = DeleteChoice.DeleteSource, ConfirmAnswer = true };

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);
            try
            {
                Select(w, vm, "src-only.txt");
                Press(Grid(w), Key.Delete);
                await WaitAsync(() => vm.IsIdle && !File.Exists(Path.Combine(Source, "src-only.txt")));

                Assert.Contains("Excluir fisicamente", d.Confirms);                                                // segunda confirmação
                var saved = Directory.EnumerateFiles(Path.Combine(Source, ".SyncBackup"), "src-only.txt", SearchOption.AllDirectories).Single();
                Assert.Equal("so", File.ReadAllText(saved));                                                       // recuperável
                Assert.DoesNotContain(vm.RowsView!.Cast<ItemRow>(), r => r.Item.RelativePath == "src-only.txt");  // sumiu dos dois lados
                Assert.True(File.Exists(Path.Combine(Target, "both.txt")));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Cancelling_delete_or_declining_the_physical_confirmation_changes_nothing()
    {
        using var sp = Services();
        Write(Target, "x.txt", "x");
        var d = new FakeDialogs { DeleteAnswer = DeleteChoice.Cancel };

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.ReplicationAugment);
            try
            {
                Select(w, vm, "x.txt");
                Press(Grid(w), Key.Delete);
                await WaitAsync(() => d.DeleteRequests.Count == 1);
                Assert.Contains(vm.RowsView!.Cast<ItemRow>(), r => r.Item.RelativePath == "x.txt");

                d.DeleteAnswer = DeleteChoice.DeleteTarget; d.ConfirmAnswer = false;
                Press(Grid(w), Key.Delete);
                await WaitAsync(() => d.DeleteRequests.Count == 2);
                await UiThread.Settle();
                Assert.True(File.Exists(Path.Combine(Target, "x.txt")));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public async Task Delete_choice_window_returns_the_selected_option_and_disables_unavailable_ones()
    {
        await UiThread.Run(async () =>
        {
            var w = new DeleteChoiceWindow(new DeleteChoiceRequest(3, AnySource: false, AnyTarget: true, "vão para a pasta de segurança", "a.txt"));
            w.Show();
            await UiThread.Settle();
            Assert.False(((RadioButton)w.FindName("DelSource")!).IsEnabled);
            Assert.True(((RadioButton)w.FindName("DelTarget")!).IsEnabled);
            Assert.False(((RadioButton)w.FindName("DelBoth")!).IsEnabled);
            Assert.True(((RadioButton)w.FindName("RemoveOnly")!).IsChecked);     // padrão seguro: só tirar da lista

            ((RadioButton)w.FindName("DelTarget")!).IsChecked = true;
            var ok = FindButton(w, "OK");
            ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(DeleteChoice.DeleteTarget, w.Choice);

            var w2 = new DeleteChoiceWindow(new DeleteChoiceRequest(1, true, true, "x", "b.txt"));
            w2.Show();
            FindButton(w2, "Cancelar").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(DeleteChoice.Cancel, w2.Choice);
        });
    }

    private static Button FindButton(DependencyObject root, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button b && Equals(b.Content, content)) return b;
            try { return FindButton(child, content); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("botão não encontrado: " + content);
    }

    // ------------------------------------------------------------------ conflitos

    [Fact]
    public async Task Conflict_buttons_resolve_in_the_plan_and_nothing_runs_until_execute()
    {
        using var sp = Services();
        Write(Source, "clash.txt", "SOURCE", TestEnv.BaseTime.AddHours(1));
        Write(Target, "clash.txt", "TARGET!", TestEnv.BaseTime.AddHours(2));
        Write(Source, "other.txt", "o"); Write(Target, "other.txt", "o");
        var d = new FakeDialogs { ConfirmAnswer = true };

        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.Synchronization);
            try
            {
                Assert.True(vm.HasConflicts);
                Assert.Equal(1, vm.ConflictCount);

                // selecionar uma linha sem conflito avisa e não altera o plano
                Select(w, vm, "other.txt");
                vm.ResolveSelection(Grid(w).SelectedItems.OfType<ItemRow>().ToList(), ConflictResolution.KeepSource);
                Assert.Single(d.Infos);
                Assert.Equal(1, vm.ConflictCount);

                Select(w, vm, "clash.txt");
                vm.ResolveSelection(Grid(w).SelectedItems.OfType<ItemRow>().ToList(), ConflictResolution.KeepTarget);
                await UiThread.Settle();
                Assert.False(vm.HasConflicts);
                var row = vm.RowsView!.Cast<ItemRow>().Single(r => r.Item.RelativePath == "clash.txt");
                Assert.Contains("←", row.ActionText);                                          // Target → Source
                Assert.Equal("SOURCE", File.ReadAllText(Path.Combine(Source, "clash.txt")));   // ainda nada foi feito

                await vm.ExecuteAsync();
                Assert.Equal("TARGET!", File.ReadAllText(Path.Combine(Source, "clash.txt")));
            }
            finally { w.Close(); }
        });

        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Keep_both_shows_three_operations_on_one_row()
    {
        using var sp = Services();
        Write(Source, "c.txt", "S", TestEnv.BaseTime.AddHours(1));
        Write(Target, "c.txt", "T!", TestEnv.BaseTime.AddHours(2));
        var d = new FakeDialogs { ConfirmAnswer = true };

        await UiThread.Run(async () =>
        {
            var (w, vm) = await OpenAsync(sp, d, Source, Target, SyncMethod.Synchronization);
            try
            {
                Select(w, vm, "c.txt");
                vm.ResolveSelection(Grid(w).SelectedItems.OfType<ItemRow>().ToList(), ConflictResolution.KeepBoth);
                var row = vm.RowsView!.Cast<ItemRow>().Single();
                Assert.Equal(3, row.Operations.Count);
                Assert.Contains("MANTER AMBOS", row.ActionText);

                await vm.ExecuteAsync();
                Assert.Equal(2, Directory.GetFiles(Source).Length);
                Assert.Equal(2, Directory.GetFiles(Target).Length);
                Assert.Single(Directory.GetFiles(Source, "c.conflict-*.txt"));
            }
            finally { w.Close(); }
        });
    }

    // ------------------------------------------------------------------ perfis: histórico de sincronização

    [Fact]
    public async Task Saved_bidirectional_profile_remembers_history_and_it_can_be_reset_from_the_profiles_list()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a"); Write(Target, "b.txt", "b");
        var profiles = sp.GetRequiredService<IProfileRepository>();
        var states = sp.GetRequiredService<IFileSyncStateRepository>();
        var d = new FakeDialogs { ConfirmAnswer = true };
        var saved = await profiles.SaveAsync(new SyncProfile { Name = "Duas mãos", SourcePath = Source, TargetPath = Target, Method = SyncMethod.Synchronization });
        var mirror = await profiles.SaveAsync(new SyncProfile { Name = "Espelho", SourcePath = Source, TargetPath = Target, Method = SyncMethod.MirrorSourceToTarget });

        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, d);
            vm.LoadProfile(saved);
            await vm.CompareAsync();
            await vm.ExecuteAsync();
            Assert.Equal(2, await states.CountAsync(saved.Id));

            var pvm = new ProfilesViewModel(profiles, sp.GetRequiredService<IExecutionHistoryRepository>(), d,
                sp.GetRequiredService<IScheduleRepository>(), new FakeScheduler(), states,
                new ScheduleViewModel(sp.GetRequiredService<IScheduleRepository>(), new FakeScheduler(), d));
            await pvm.LoadAsync();

            pvm.Selected = pvm.Rows.Single(r => r.Name == "Espelho");
            Assert.False(pvm.ResetSyncStateCommand.CanExecute(null));            // só para perfis de sincronização
            pvm.Selected = pvm.Rows.Single(r => r.Name == "Duas mãos");
            Assert.True(pvm.ResetSyncStateCommand.CanExecute(null));

            await pvm.ResetSyncStateAsync();
            Assert.Equal(0, await states.CountAsync(saved.Id));
        });
        Assert.NotNull(mirror);
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
