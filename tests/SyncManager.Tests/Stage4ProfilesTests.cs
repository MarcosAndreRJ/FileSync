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

/// <summary>Etapas 4 e 5: página Perfis, editor de perfil, Filtros, Agendamentos, Histórico, Configurações e Logs.</summary>
public class Stage4ProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerStage4", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    private ServiceProvider Services()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        var sp = App.BuildServices(Path.Combine(_root, "db", "s4.db"), Path.Combine(_root, "logs"));
        sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
        return sp;
    }

    private static MainViewModel NewVm(ServiceProvider sp, FakeDialogs d) => new(
        sp.GetRequiredService<SyncWorkflow>(), d, sp.GetRequiredService<ILogService>(),
        sp.GetRequiredService<IProfileRepository>(), sp.GetRequiredService<SettingsService>(), sp);

    private static void Write(string root, string rel, string content)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, TestEnv.BaseTime);
    }

    private static void Click(Button b) =>
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(b)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();

    private Task<SyncProfile> SaveProfile(ServiceProvider sp, string name, Action<SyncProfile>? tweak = null)
    {
        var p = new SyncProfile { Name = name, SourcePath = Source, TargetPath = Target, Method = SyncMethod.MirrorSourceToTarget };
        tweak?.Invoke(p);
        return sp.GetRequiredService<IProfileRepository>().SaveAsync(p);
    }

    // ------------------------------------------------------------------ página Perfis

    [Fact]
    public async Task Profiles_page_lists_shows_details_and_routes_actions()
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        var saved = await SaveProfile(sp, "Documentos");
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                Click(UiFind.ByName<Button>(w, "NavProfiles"));
                await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
                Assert.True(vm.IsProfilesScreen);
                Assert.Equal(Visibility.Visible, UiFind.ByName<ProfilesView>(w, "ProfilesPage").Visibility);
                var grid = UiFind.ByName<DataGrid>(w, "ProfilesGrid");
                Assert.Single(grid.Items);
                Assert.Equal(Visibility.Visible, UiFind.ByName<Border>(w, "DetailsPanel").Visibility);
                Assert.Equal("Documentos", vm.Profiles.Selected!.Name);
                Assert.Contains("Com subpastas", vm.Profiles.Selected.OptionsText);

                // Comparar: carrega o perfil na tela de comparação e compara
                Click(UiFind.ByName<Button>(w, "CompareProfileButton"));
                await UiThread.Settle(); await Task.Delay(500); await UiThread.Settle();
                Assert.True(vm.IsComparisonScreen);
                Assert.Equal(Source, vm.SourcePath);
                Assert.NotNull(vm.CurrentPlan);

                // Executar agora: compara e abre a página do plano, sem executar ainda
                await vm.ShowProfilesAsync();
                Click(UiFind.ByName<Button>(w, "RunProfileButton"));
                await UiThread.Settle(); await Task.Delay(500); await UiThread.Settle();
                Assert.True(vm.IsPlanScreen);
                Assert.False(File.Exists(Path.Combine(Target, "a.txt")));

                // Editar: abre o editor com os dados do perfil
                await vm.ShowProfilesAsync();
                Click(UiFind.ByName<Button>(w, "EditProfileButton"));
                await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
                Assert.True(vm.IsEditorScreen);
                Assert.Equal("Documentos", vm.Editor!.Name);
                Assert.Equal(saved.Id, vm.Profiles.Selected!.Profile.Id);
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Empty_profiles_page_shows_the_empty_state()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await vm.ShowProfilesAsync();
            Assert.True(vm.Profiles.IsEmpty);
            Assert.False(vm.Profiles.HasSelection);
            await Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ editor

    [Fact]
    public async Task New_profile_is_created_in_the_editor_with_default_filters_and_saved_to_the_database()
    {
        using var sp = Services();
        var repo = sp.GetRequiredService<IProfileRepository>();
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            await vm.HandleProfileRequestAsync(new ProfilesRequest(ProfilesAction.New, null));

            Assert.True(vm.IsEditorScreen);
            var e = vm.Editor!;
            Assert.True(e.IsNew);
            Assert.Equal("Novo perfil", e.Title);
            Assert.Contains(e.Excludes, x => x.Text == "*.tmp");             // padrão das Configurações
            Assert.True(e.CannotSchedule);

            // sem nome / sem pastas: erro na tela, nada salvo
            await e.SaveAsync();
            Assert.True(e.HasError);
            Assert.Empty(await repo.GetAllAsync());
            e.Name = "Fotos";
            await e.SaveAsync();
            Assert.Contains("pastas", e.Error);

            e.Source = Source; e.Target = Target;
            e.SelectedMethod = e.Methods.First(m => m.Method == SyncMethod.Synchronization);
            e.SelectedDeleteOption = e.DeleteOptions.First(o => o.Strategy == DeleteStrategy.Permanent);
            e.IncludeSubfolders = false;
            e.VerifyContentByHash = true;
            e.ToleranceSeconds = 5;
            e.FilterCaseSensitive = true;
            e.FilterWildcards = false;
            e.SafetyFolder = Path.Combine(_root, "seguro");
            e.MassDeletePercent = 50;
            e.MaxParallel = 3; e.RetryCount = 4; e.RetryDelayMs = 2500;
            e.NewInclude = "*.jpg"; e.AddIncludeCommand.Execute(null);
            Assert.Equal("", e.NewInclude);
            await e.SaveAsync();

            var p = Assert.Single(await repo.GetAllAsync());
            Assert.Equal("Fotos", p.Name);
            Assert.Equal(SyncMethod.Synchronization, p.Method);
            Assert.Equal(DeleteStrategy.Permanent, p.DeleteStrategy);
            Assert.False(p.IncludeSubfolders);
            Assert.True(p.VerifyContentByHash);
            Assert.Equal(5, p.TimestampToleranceSeconds);
            Assert.True(p.FilterCaseSensitive);
            Assert.False(p.FilterWildcards);
            Assert.Equal(Path.Combine(_root, "seguro"), p.SafetyFolderPath);
            Assert.Equal(0.5, p.MassDeleteThreshold, 3);
            Assert.Equal(3, p.MaxParallelFileOperations);
            Assert.Equal(4, p.TransientRetryCount);
            Assert.Equal(2500, p.TransientRetryDelayMs);
            Assert.Equal(new[] { "*.jpg" }, p.IncludePatterns);
            Assert.Contains("*.tmp", p.ExcludePatterns);

            // salvar volta para a lista de perfis
            await UiThread.Settle(); await Task.Delay(200);
            Assert.True(vm.IsProfilesScreen);
            Assert.Single(vm.Profiles.Rows);
        });
    }

    [Fact]
    public async Task Editing_an_existing_profile_round_trips_every_field_and_keeps_the_id()
    {
        using var sp = Services();
        var repo = sp.GetRequiredService<IProfileRepository>();
        var saved = await SaveProfile(sp, "Completo", p =>
        {
            p.IncludeHidden = false; p.IncludeSystem = true; p.PreserveAttributes = false; p.PreserveTimestamps = false;
            p.ContinueOnError = false; p.ConfirmDeletes = false; p.BackupBeforeOverwrite = false; p.VerifyCopyByHash = true;
            p.ExcludePatterns = new List<string> { "a", "b" }; p.IncludePatterns = new List<string> { "c" };
            p.TransientRetryCount = 7; p.TransientRetryDelayMs = 333; p.MaxParallelFileOperations = 6; p.MassDeleteThreshold = 0.1;
            p.FilterCaseSensitive = true;
        });
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await vm.OpenEditorAsync(saved);
            var e = vm.Editor!;
            Assert.False(e.IsDirty);
            Assert.False(e.IncludeHidden); Assert.True(e.IncludeSystem); Assert.False(e.ContinueOnError); Assert.True(e.VerifyCopyByHash);
            Assert.Equal(new[] { "a", "b" }, e.Excludes.Select(x => x.Text));
            Assert.Equal(10, e.MassDeletePercent);
            Assert.False(e.CannotSchedule);

            e.Name = "Completo 2";
            Assert.True(e.IsDirty);
            await e.SaveAsync();

            var p = (await repo.GetAsync(saved.Id))!;
            Assert.Equal("Completo 2", p.Name);
            Assert.False(p.IncludeHidden); Assert.True(p.IncludeSystem); Assert.False(p.PreserveAttributes); Assert.False(p.PreserveTimestamps);
            Assert.False(p.ContinueOnError); Assert.False(p.ConfirmDeletes); Assert.False(p.BackupBeforeOverwrite); Assert.True(p.VerifyCopyByHash);
            Assert.Equal(7, p.TransientRetryCount); Assert.Equal(333, p.TransientRetryDelayMs); Assert.Equal(6, p.MaxParallelFileOperations);
            Assert.Equal(0.1, p.MassDeleteThreshold, 3);
            Assert.True(p.FilterCaseSensitive);
            Assert.Equal(new[] { "c" }, p.IncludePatterns);
        });
    }

    [Fact]
    public async Task Duplicate_name_is_reported_inside_the_editor()
    {
        using var sp = Services();
        await SaveProfile(sp, "Repetido");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await vm.HandleProfileRequestAsync(new ProfilesRequest(ProfilesAction.New, null));
            var e = vm.Editor!;
            e.Name = "repetido"; e.Source = Source; e.Target = Target;
            await e.SaveAsync();
            Assert.True(e.HasError);
            Assert.True(vm.IsEditorScreen);               // continua no editor para corrigir
            Assert.Single(await sp.GetRequiredService<IProfileRepository>().GetAllAsync());
        });
    }

    [Fact]
    public async Task Leaving_the_editor_with_changes_asks_first_and_cancel_discards_without_asking()
    {
        using var sp = Services();
        var saved = await SaveProfile(sp, "Edita");
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs { ConfirmAnswer = false };
            var vm = NewVm(sp, d);
            await vm.OpenEditorAsync(saved);
            vm.Editor!.Name = "Mudou";

            await vm.ShowSettingsAsync();                   // navegar embora pergunta; "Não" mantém o editor
            Assert.True(vm.IsEditorScreen);
            Assert.Contains("Alterações não salvas", d.Confirms);

            d.ConfirmAnswer = true;
            await vm.ShowSettingsAsync();                   // "Sim" descarta
            Assert.True(vm.IsSettingsScreen);
            Assert.Equal("Edita", (await sp.GetRequiredService<IProfileRepository>().GetAsync(saved.Id))!.Name);

            // Cancelar não pergunta
            d.Confirms.Clear();
            await vm.OpenEditorAsync(saved);
            vm.Editor!.Name = "Outra";
            vm.Editor.CancelCommand.Execute(null);
            await UiThread.Settle(); await Task.Delay(200);
            Assert.True(vm.IsProfilesScreen);
            Assert.Empty(d.Confirms);
            Assert.Equal("Edita", (await sp.GetRequiredService<IProfileRepository>().GetAsync(saved.Id))!.Name);
        });
    }

    [Fact]
    public async Task Filters_add_dedupe_edit_and_remove()
    {
        using var sp = Services();
        var saved = await SaveProfile(sp, "Filtros", p => p.ExcludePatterns = new List<string> { "*.log" });
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await vm.OpenEditorAsync(saved, EditorSection.Filters);
            var e = vm.Editor!;
            Assert.True(e.IsFilters);

            e.NewExclude = "  node_modules\\  "; e.AddExcludeCommand.Execute(null);
            e.NewExclude = "NODE_MODULES\\"; e.AddExcludeCommand.Execute(null);   // duplicado (sem diferenciar maiúsculas)
            e.NewExclude = "   "; e.AddExcludeCommand.Execute(null);               // vazio não entra
            Assert.Equal(new[] { "*.log", "node_modules\\" }, e.Excludes.Select(x => x.Text));

            e.Excludes[0].Text = "*.bak";                                          // edição direto na linha
            Assert.True(e.IsDirty);
            e.RemoveFilterCommand.Execute(e.Excludes[1]);
            Assert.Equal(new[] { "*.bak" }, e.Excludes.Select(x => x.Text));

            await e.SaveAsync();
            Assert.Equal(new[] { "*.bak" }, (await sp.GetRequiredService<IProfileRepository>().GetAsync(saved.Id))!.ExcludePatterns);
        });
    }

    [Fact]
    public async Task Test_connections_reports_each_side()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var e = sp.GetRequiredService<ProfileEditorViewModel>();
            await e.LoadAsync(new SyncProfile { Name = "C", SourcePath = Source, TargetPath = Path.Combine(_root, "nao-existe") });
            await e.TestConnectionsAsync();
            Assert.StartsWith("Acessível", e.SourceTestText);
            Assert.StartsWith("Inacessível", e.TargetTestText);

            e.Target = Target; e.Source = "";
            await e.TestConnectionsAsync();
            Assert.StartsWith("Informe", e.SourceTestText);
            Assert.StartsWith("Acessível (leitura e escrita)", e.TargetTestText);
        });
    }

    [Fact]
    public async Task Saving_in_the_editor_refreshes_the_profile_loaded_on_the_comparison_screen()
    {
        using var sp = Services();
        var saved = await SaveProfile(sp, "Ativo");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.LoadProfile(saved);
            await vm.OpenEditorAsync(saved);
            vm.Editor!.SelectedMethod = vm.Editor.Methods.First(m => m.Method == SyncMethod.ReplicationAugment);
            await vm.Editor.SaveAsync();
            await UiThread.Settle(); await Task.Delay(200);
            Assert.Equal(SyncMethod.ReplicationAugment, vm.SelectedMethod.Method);
        });
    }

    [Fact]
    public async Task Saving_from_the_comparison_screen_keeps_fields_only_the_editor_shows()
    {
        using var sp = Services();
        var saved = await SaveProfile(sp, "Preserva", p => { p.TransientRetryCount = 9; p.FilterCaseSensitive = true; p.FilterWildcards = false; });
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            vm.LoadProfile(saved);
            vm.ToleranceSeconds = 7;
            await vm.SaveProfileAsync();
            var p = (await sp.GetRequiredService<IProfileRepository>().GetAsync(saved.Id))!;
            Assert.Equal(7, p.TimestampToleranceSeconds);
            Assert.Equal(9, p.TransientRetryCount);
            Assert.True(p.FilterCaseSensitive);
            Assert.False(p.FilterWildcards);
        });
    }

    [Fact]
    public async Task Editor_page_renders_every_section_without_binding_errors()
    {
        using var sp = Services();
        var saved = await SaveProfile(sp, "Visual");
        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            try
            {
                await vm.OpenEditorAsync(saved);
                await UiThread.Settle();
                foreach (var name in new[] { "SecMethod", "SecFilters", "SecSchedule", "SecAdvanced", "SecGeneral" })
                {
                    Click(UiFind.ByName<Button>(w, name));
                    await UiThread.Settle(); await Task.Delay(100); await UiThread.Settle();
                }
                Assert.True(vm.Editor!.IsGeneral);
                Assert.Equal("active", (string)UiFind.ByName<Button>(w, "SecGeneral").Tag);
                Click(UiFind.ByName<Button>(w, "TestConnectionsButton"));
                await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
                Assert.StartsWith("Acessível", vm.Editor.SourceTestText);
            }
            finally { w.Close(); }
        });
        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    // ------------------------------------------------------------------ agendamentos

    [Fact]
    public async Task Schedules_page_edits_the_schedule_of_the_selected_profile()
    {
        using var sp = Services();
        var a = await SaveProfile(sp, "A");
        await SaveProfile(sp, "B");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            await vm.ShowSchedulesAsync();
            Assert.True(vm.IsSchedulesScreen);
            Assert.Equal(2, vm.Profiles.Rows.Count);
            Assert.True(vm.Profiles.TrackSchedule);

            vm.Profiles.Selected = vm.Profiles.Rows.Single(r => r.Name == "A");
            await Task.Delay(300);
            Assert.Equal("Agendamento — A", vm.Profiles.Schedule.Title);
            Assert.Equal(5, vm.Profiles.Schedule.Kinds.Count);
            Assert.Equal(a.Id, vm.Profiles.Selected.Profile.Id);
            await Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ histórico

    [Fact]
    public async Task History_page_lists_executions_and_rerun_opens_the_plan_of_that_profile()
    {
        using var sp = Services();
        Write(Source, "x.txt", "x");
        var saved = await SaveProfile(sp, "Rodado");
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            vm.LoadProfile(saved);
            await vm.CompareAsync();
            await vm.ExecuteAsync();
            Write(Source, "y.txt", "y");

            await vm.ShowHistoryAsync();
            Assert.True(vm.IsHistoryScreen);
            var row = Assert.Single(vm.History.Rows);
            Assert.Equal("Rodado", row.ProfileName);
            Assert.True(vm.History.RerunCommand.CanExecute(null));

            vm.History.RerunCommand.Execute(null);                     // "Executar novamente"
            await Task.Delay(800); await UiThread.Settle();
            Assert.True(vm.IsPlanScreen);                               // compara de novo e mostra o plano; nada executado
            Assert.False(File.Exists(Path.Combine(Target, "y.txt")));
        });
    }

    [Fact]
    public async Task Rerun_of_a_deleted_profile_explains_instead_of_failing()
    {
        using var sp = Services();
        Write(Source, "x.txt", "x");
        var saved = await SaveProfile(sp, "Some");
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            vm.LoadProfile(saved);
            await vm.CompareAsync(); await vm.ExecuteAsync();
            await sp.GetRequiredService<IProfileRepository>().DeleteAsync(saved.Id);
            await vm.ShowHistoryAsync();
            vm.History.RerunCommand.Execute(null);
            await Task.Delay(300); await UiThread.Settle();
            Assert.Contains(d.Errors, e => e.Contains("não existe mais"));
        });
    }

    // ------------------------------------------------------------------ configurações

    [Fact]
    public async Task Settings_page_persists_new_options_and_confirm_before_execute_is_honoured()
    {
        using var sp = Services();
        Write(Source, "n.txt", "n");
        var settings = sp.GetRequiredService<SettingsService>();
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            await vm.ShowSettingsAsync();
            var s = vm.Settings;
            Assert.True(s.ConfirmBeforeExecute);
            Assert.False(s.StartMinimized);

            // navegação por categorias
            foreach (var c in Enum.GetValues<SettingsCategory>()) { s.SelectCategoryCommand.Execute(c.ToString()); Assert.Equal(c, s.Category); }

            s.ConfirmBeforeExecute = false; s.StartMinimized = true; s.MaxParallelLocal = 99; s.LogRetentionDays = 12;
            await s.SaveAsync();
            Assert.Equal("Configurações salvas.", s.Message);
            Assert.Equal(32, s.MaxParallelLocal);                      // a tela mostra o valor normalizado
            var loaded = await settings.LoadAsync();
            Assert.False(loaded.ConfirmBeforeExecute); Assert.True(loaded.StartMinimized); Assert.Equal(12, loaded.LogRetentionDays);

            // sem exclusões no plano e "confirmar antes de executar" desligado: executa sem perguntar
            await Task.Delay(200);
            vm.SourcePath = Source; vm.TargetPath = Target;
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.ReplicationAugment);
            await vm.CompareAsync();
            await vm.ExecuteAsync();
            Assert.DoesNotContain("Confirmar execução", d.Confirms);
            Assert.True(File.Exists(Path.Combine(Target, "n.txt")));

            s.RestoreDefaultsCommand.Execute(null);
            Assert.True(s.ConfirmBeforeExecute);
            Assert.Contains("Salvar", s.Message);
            Assert.False((await settings.LoadAsync()).ConfirmBeforeExecute);   // restaurar só muda a tela até salvar
        });
    }

    [Fact]
    public async Task Destructive_plans_still_ask_even_when_confirm_before_execute_is_off()
    {
        using var sp = Services();
        Write(Source, "n.txt", "n"); Write(Target, "extra.txt", "e");
        await sp.GetRequiredService<SettingsService>().SaveAsync(new AppSettings { ConfirmBeforeExecute = false });
        await UiThread.Run(async () =>
        {
            var d = new FakeDialogs();
            var vm = NewVm(sp, d);
            await vm.ApplyDefaultsAsync();
            vm.SourcePath = Source; vm.TargetPath = Target;
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.MirrorSourceToTarget);
            await vm.CompareAsync();
            await vm.ExecuteAsync();
            Assert.Contains("Confirmar execução", d.Confirms);        // o perfil manda nas exclusões (ConfirmDeletes padrão = ligado)
        });
    }

    // ------------------------------------------------------------------ logs

    [Fact]
    public async Task Logs_page_shows_the_session_log()
    {
        using var sp = Services();
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm);
            w.Show();
            try
            {
                Click(UiFind.ByName<Button>(w, "NavLogs"));
                await UiThread.Settle();
                Assert.True(vm.IsLogsScreen);
                sp.GetRequiredService<ILogService>().Info("TESTE LOG", "linha da página de logs");
                await Task.Delay(700); await UiThread.Settle();
                Assert.Contains(vm.LogLines, l => l.Contains("TESTE LOG"));
                Assert.Equal(Visibility.Visible, UiFind.ByName<LogsView>(w, "LogsPage").Visibility);
            }
            finally { w.Close(); }
        });
    }

    // ------------------------------------------------------------------ renderização para revisão visual (opt-in)

    [Theory]
    [InlineData("profiles")]
    [InlineData("editor-general")]
    [InlineData("editor-method")]
    [InlineData("editor-filters")]
    [InlineData("editor-schedule")]
    [InlineData("editor-advanced")]
    [InlineData("schedules")]
    [InlineData("history")]
    [InlineData("settings")]
    [InlineData("logs")]
    public async Task Page_renders(string page)
    {
        using var sp = Services();
        Write(Source, "a.txt", "a");
        var p1 = await SaveProfile(sp, "Backup Documentos", p => { p.ExcludePatterns = new List<string> { "*.tmp", "node_modules\\" }; p.IncludePatterns = new List<string> { "*.docx" }; });
        await SaveProfile(sp, "Sincronizar Fotos", p => p.Method = SyncMethod.Synchronization);
        var dir = Environment.GetEnvironmentVariable("SYNCMANAGER_SHOTS");
        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, new FakeDialogs());
            var w = new MainWindow(vm) { Width = 1366, Height = 820 };
            w.Show();
            try
            {
                vm.LoadProfile(p1);
                await vm.CompareAsync(); await vm.ExecuteAsync();          // gera histórico e log
                switch (page)
                {
                    case "profiles": await vm.ShowProfilesAsync(); break;
                    case "schedules": await vm.ShowSchedulesAsync(); break;
                    case "history": await vm.ShowHistoryAsync(); break;
                    case "settings": await vm.ShowSettingsAsync(); break;
                    case "logs": vm.ShowLogsCommand.Execute(null); break;
                    default:
                        var section = page switch { "editor-method" => EditorSection.Method, "editor-filters" => EditorSection.Filters, "editor-schedule" => EditorSection.Schedule, "editor-advanced" => EditorSection.Advanced, _ => EditorSection.General };
                        await vm.OpenEditorAsync(p1, section);
                        break;
                }
                await UiThread.Settle(); await Task.Delay(400); await UiThread.Settle();
                w.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(w);
                Assert.True(rtb.PixelWidth > 1000);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, page + ".png"));
                    enc.Save(fs);
                }
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
