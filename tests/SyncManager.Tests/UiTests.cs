using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.Services;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace SyncManager.Tests;

/// <summary>Uma única thread STA com Dispatcher para todos os testes de UI (o WPF só admite um Application por processo).</summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Lazy = new(Start);
    public static readonly List<string> BindingErrors = new();

    private sealed class Listener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { lock (BindingErrors) BindingErrors.Add(message ?? ""); }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();   // carrega os recursos do App.xaml (estilos usados pelas janelas)
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is Window w)
                {
                    w.ShowActivated = false;
                    w.ShowInTaskbar = false;
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -32000;
                    w.Top = -32000;
                }
            }));
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new Listener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static async Task Run(Func<Task> body)
    {
        try
        {
            await Lazy.Value.InvokeAsync(body).Task.Unwrap();
        }
        finally
        {
            await Lazy.Value.InvokeAsync(() =>
            {
                if (System.Windows.Application.Current is { } a)
                {
                    foreach (Window w in a.Windows.OfType<Window>().ToArray())
                    {
                        try { w.Close(); } catch { }
                    }
                }
            });
        }
    }

    /// <summary>Deixa o WPF processar layout/binding pendentes.</summary>
    public static Task Settle() => Lazy.Value.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<string> Confirms { get; } = new();

    public string? BrowseFolder(string? initialPath, string title) => null;
    public List<string> SaveRequests { get; } = new();
    public string? SavePath { get; set; }
    public string? AskSaveFile(string title, string suggestedName, string filter) { SaveRequests.Add(suggestedName); return SavePath; }
    public bool Confirm(string title, string message, bool isWarning) { Confirms.Add(title); return ConfirmAnswer; }
    public void ShowError(string title, string message) => Errors.Add(title + ": " + message);
    public void ShowInfo(string title, string message) => Infos.Add(title);
    public DeleteChoice DeleteAnswer { get; set; } = DeleteChoice.Cancel;
    public List<DeleteChoiceRequest> DeleteRequests { get; } = new();
    public List<string> Infos { get; } = new();
    public DeleteChoice AskDeleteChoice(DeleteChoiceRequest request) { DeleteRequests.Add(request); return DeleteAnswer; }
    public int LogsOpened { get; private set; }
    public void OpenFolder(string path) { }
    public void OpenLogFolder() => LogsOpened++;
}

internal sealed class FakeScheduler : SyncManager.Core.Abstractions.ISchedulerService
{
    public Dictionary<int, (SyncProfile Profile, ScheduleDefinition Schedule)> Applied { get; } = new();
    public List<int> Removed { get; } = new();
    public List<int> RunNowCalls { get; } = new();
    public bool FailApply { get; set; }

    public Task<string> ApplyAsync(SyncProfile profile, ScheduleDefinition schedule, CancellationToken ct = default)
    {
        if (FailApply) throw new InvalidOperationException("falha simulada do Agendador");
        Applied[profile.Id] = (profile, schedule);
        return Task.FromResult($"\\SyncManager\\Profile_{profile.Id}");
    }

    public Task RemoveAsync(int profileId, CancellationToken ct = default) { Removed.Add(profileId); Applied.Remove(profileId); return Task.CompletedTask; }
    public Task RunNowAsync(int profileId, CancellationToken ct = default) { RunNowCalls.Add(profileId); return Task.CompletedTask; }

    public Task<ScheduledTaskInfo> GetInfoAsync(int profileId, CancellationToken ct = default) =>
        Task.FromResult(Applied.TryGetValue(profileId, out var a)
            ? new ScheduledTaskInfo(true, a.Schedule.Enabled, new DateTimeOffset(2030, 1, 2, 3, 4, 0, TimeSpan.Zero), null, null, "Pronta")
            : ScheduledTaskInfo.Missing);
}

public class UiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerUiTests", Guid.NewGuid().ToString("N"));

    private ServiceProvider Services()
    {
        var sp = App.BuildServices(Path.Combine(_root, "db", "ui.db"), Path.Combine(_root, "logs"));
        sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
        return sp;
    }

    private (string Source, string Target) Folders()
    {
        var s = Path.Combine(_root, "source"); var t = Path.Combine(_root, "target");
        Directory.CreateDirectory(s); Directory.CreateDirectory(t);
        return (s, t);
    }

    private static MainViewModel NewVm(ServiceProvider sp, FakeDialogs dialogs) => new(
        sp.GetRequiredService<SyncWorkflow>(), dialogs, sp.GetRequiredService<SyncManager.Core.Abstractions.ILogService>(),
        sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>(), sp.GetRequiredService<SettingsService>(), sp);

    [Fact]
    public async Task All_windows_load_without_xaml_or_binding_errors()
    {
        using var sp = Services();
        var (s, t) = Folders();
        await File.WriteAllTextAsync(Path.Combine(s, "a.txt"), "x");
        var profiles = sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>();
        await profiles.SaveAsync(new SyncProfile { Name = "P1", SourcePath = s, TargetPath = t });

        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();

            var main = sp.GetRequiredService<MainWindow>();
            main.Show();
            var vm = sp.GetRequiredService<MainViewModel>();
            vm.SourcePath = s; vm.TargetPath = t;
            await vm.CompareAsync();
            await UiThread.Settle();
            vm.SelectedStatusFilter = vm.StatusFilters[2];
            vm.Search = "a.txt";
            await UiThread.Settle();

            var plan = vm.CurrentPlan!;
            vm.PreviewCommand.Execute(null); await UiThread.Settle(); Assert.True(vm.IsPlanScreen); vm.BackCommand.Execute(null); await UiThread.Settle();

            // todas as páginas do shell carregam sem erro de XAML/binding
            await vm.ShowProfilesAsync(); await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            Assert.Single(vm.Profiles.Rows);
            await vm.OpenEditorAsync(vm.Profiles.Rows[0].Profile);
            foreach (var sec in Enum.GetValues<EditorSection>()) { vm.Editor!.Section = sec; await UiThread.Settle(); }
            vm.Editor!.CancelCommand.Execute(null); await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            await vm.ShowSchedulesAsync(); await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            await vm.ShowHistoryAsync(); await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            vm.ShowLogsCommand.Execute(null); await UiThread.Settle();
            await vm.ShowSettingsAsync(); await UiThread.Settle(); await Task.Delay(200); await UiThread.Settle();
            Assert.Equal(4, vm.Settings.MaxParallelLocal);

            main.Close();
        });

        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Profile_flow_save_load_compare_execute_and_history()
    {
        using var sp = Services();
        var (s, t) = Folders();
        await File.WriteAllTextAsync(Path.Combine(s, "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(t, "extra.txt"), "extra");
        var dialogs = new FakeDialogs();
        var profiles = sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>();
        var history = sp.GetRequiredService<SyncManager.Core.Abstractions.IExecutionHistoryRepository>();

        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, dialogs);

            // salvar sem nome é recusado
            vm.SourcePath = s; vm.TargetPath = t;
            await vm.SaveProfileAsync();
            Assert.Single(dialogs.Errors);
            Assert.Empty(await profiles.GetAllAsync());

            // salvar com nome
            vm.ProfileName = "Backup teste";
            vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncMethod.MirrorSourceToTarget);
            vm.ExcludeText = "*.tmp\r\n*.bak";
            vm.ToleranceSeconds = 5;
            await vm.SaveProfileAsync();
            Assert.True(vm.WindowTitle.Contains("Backup teste") && !vm.IsDirty);
            var saved = Assert.Single(await profiles.GetAllAsync());
            Assert.Equal(new[] { "*.tmp", "*.bak" }, saved.ExcludePatterns);
            Assert.Equal(5, saved.TimestampToleranceSeconds);

            // alterar marca como não salvo; abrir outro perfil pede confirmação
            vm.IncludeHidden = false;
            Assert.True(vm.IsDirty);

            // "Executar" a partir do gerenciador: carrega, compara, mostra o plano e só então confirma/executa
            await vm.HandleProfileRequestAsync(new ProfilesRequest(ProfilesAction.Execute, saved));

            Assert.True(vm.IsPlanScreen);                       // o plano aparece como página; nada foi executado ainda
            Assert.NotNull(vm.Plan);
            Assert.False(File.Exists(Path.Combine(t, "a.txt")));
            await vm.ExecuteAsync();                            // EXECUTAR na página do plano
            Assert.True(vm.IsExecutionScreen);
            Assert.True(vm.Execution.IsFinished);
            Assert.Contains("Confirmar execução", dialogs.Confirms);
            Assert.True(File.Exists(Path.Combine(t, "a.txt")));
            Assert.False(File.Exists(Path.Combine(t, "extra.txt")));
            Assert.Contains("Concluído com sucesso", vm.ExecutionSummary);
        });

        var h = Assert.Single(await history.ListAsync(10));
        Assert.Equal("Backup teste", h.ProfileName);
        Assert.Equal(1, h.FilesCopied);
        Assert.Equal(1, h.FilesDeleted);
        Assert.True((await history.GetLastRunsAsync()).ContainsKey(h.ProfileId!.Value));
    }

    [Fact]
    public async Task Declining_the_confirmation_changes_nothing()
    {
        using var sp = Services();
        var (s, t) = Folders();
        await File.WriteAllTextAsync(Path.Combine(s, "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(t, "extra.txt"), "extra");
        var dialogs = new FakeDialogs { ConfirmAnswer = false };

        await UiThread.Run(async () =>
        {
            var vm = NewVm(sp, dialogs);
            vm.SourcePath = s; vm.TargetPath = t;
            await vm.CompareAsync();
            await vm.ExecuteAsync();
        });

        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        Assert.True(File.Exists(Path.Combine(t, "extra.txt")));
        Assert.Empty(await sp.GetRequiredService<SyncManager.Core.Abstractions.IExecutionHistoryRepository>().ListAsync(10));
    }

    [Fact]
    public async Task Profiles_window_duplicate_and_delete()
    {
        using var sp = Services();
        var dialogs = new FakeDialogs();
        var repo = sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>();
        await repo.SaveAsync(new SyncProfile { Name = "Origem", SourcePath = "a", TargetPath = "b" });

        await UiThread.Run(async () =>
        {
            var vm = new ProfilesViewModel(repo, sp.GetRequiredService<SyncManager.Core.Abstractions.IExecutionHistoryRepository>(), dialogs,
                sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>(), new FakeScheduler(), sp.GetRequiredService<SyncManager.Core.Abstractions.IFileSyncStateRepository>(),
                new ScheduleViewModel(sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>(), new FakeScheduler(), dialogs));
            await vm.LoadAsync();
            Assert.Single(vm.Rows);

            await vm.DuplicateAsync();                                   // duplica "Origem" → "Origem (cópia)"
            vm.Selected = vm.Rows.First(r => r.Name == "Origem");
            await vm.DuplicateAsync();                                   // nome já usado → "Origem (cópia) 2"
            Assert.Equal(new[] { "Origem", "Origem (cópia)", "Origem (cópia) 2" }, vm.Rows.Select(r => r.Name).OrderBy(n => n.Length).ThenBy(n => n).ToArray());

            vm.Selected = vm.Rows.First(r => r.Name == "Origem (cópia)");
            await vm.DeleteAsync();
            Assert.Equal(2, vm.Rows.Count);
        });
    }

    // ------------------------------------------------------------------ agendamento (Fase 3)

    private static async Task<(SyncProfile Profile, ScheduleViewModel Vm, FakeScheduler Scheduler, FakeDialogs Dialogs)> NewScheduleVm(ServiceProvider sp)
    {
        var profile = await sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>()
            .SaveAsync(new SyncProfile { Name = "Backup Financeiro", SourcePath = @"C:\Financeiro", TargetPath = @"\\192.168.0.10\Backup" });
        var scheduler = new FakeScheduler();
        var dialogs = new FakeDialogs();
        var vm = new ScheduleViewModel(sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>(), scheduler, dialogs);
        return (profile, vm, scheduler, dialogs);
    }

    [Fact]
    public async Task Schedule_window_weekdays_at_1830_is_applied_to_the_scheduler_then_persisted()
    {
        using var sp = Services();
        var (profile, vm, scheduler, dialogs) = await NewScheduleVm(sp);
        var repo = sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>();

        await UiThread.Run(async () =>
        {
            await vm.LoadAsync(profile);
            Assert.Contains("ainda não tem agendamento", vm.TaskStatus);

            vm.SelectedKind = vm.Kinds.First(k => k.Kind == ScheduleKind.Weekly);
            vm.Time = "18:30";
            vm.PresetWeekdaysCommand.Execute(null);
            Assert.Equal("Segunda a sexta às 18:30", vm.Preview);

            vm.RunOnStartup = true;
            vm.DelayMinutes = 5;
            Assert.Equal("Segunda a sexta às 18:30 + Ao entrar no Windows (após 5 min)", vm.Preview);

            await vm.SaveAsync();
        });

        Assert.Empty(dialogs.Errors);
        var (appliedProfile, applied) = scheduler.Applied[profile.Id];
        Assert.Equal(profile.Id, appliedProfile.Id);
        Assert.Equal(ScheduleKind.Weekly, applied.Kind);
        Assert.Equal(new TimeSpan(18, 30, 0), applied.TimeOfDay);
        Assert.Equal(WeekDays.Weekdays, applied.Days);

        var saved = (await repo.GetByProfileAsync(profile.Id))!;
        Assert.Equal(@"\SyncManager\Profile_" + profile.Id, saved.TaskName);
        Assert.True(saved.RunOnStartup);
        Assert.Equal(5, saved.DelayMinutes);

        // reabrir mostra o que foi salvo
        var vm2 = new ScheduleViewModel(repo, scheduler, dialogs);
        await UiThread.Run(async () =>
        {
            await vm2.LoadAsync(profile);
            Assert.Equal(ScheduleKind.Weekly, vm2.SelectedKind.Kind);
            Assert.Equal("18:30", vm2.Time);
            Assert.True(vm2.Friday && vm2.Monday && !vm2.Sunday);
            Assert.Contains("próxima execução: 2030", vm2.TaskStatus);
        });
    }

    [Fact]
    public async Task Schedule_window_rejects_invalid_input_without_touching_the_scheduler()
    {
        using var sp = Services();
        var (profile, vm, scheduler, dialogs) = await NewScheduleVm(sp);

        await UiThread.Run(async () =>
        {
            await vm.LoadAsync(profile);
            vm.Time = "25:99";
            Assert.StartsWith("⚠", vm.Preview);
            await vm.SaveAsync();

            vm.Time = "10:00";
            vm.SelectedKind = vm.Kinds.First(k => k.Kind == ScheduleKind.Weekly);
            vm.Days = WeekDays.None;
            Assert.StartsWith("⚠", vm.Preview);
            await vm.SaveAsync();
        });

        Assert.Equal(2, dialogs.Errors.Count);
        Assert.Empty(scheduler.Applied);
        Assert.Null(await sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>().GetByProfileAsync(profile.Id));
    }

    [Fact]
    public async Task Scheduler_failure_leaves_no_schedule_saved_in_the_database()
    {
        using var sp = Services();
        var (profile, vm, scheduler, dialogs) = await NewScheduleVm(sp);
        scheduler.FailApply = true;

        await UiThread.Run(async () =>
        {
            await vm.LoadAsync(profile);
            await vm.SaveAsync();
        });

        Assert.Contains(dialogs.Errors, e => e.Contains("falha simulada"));
        Assert.Null(await sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>().GetByProfileAsync(profile.Id));
    }

    [Fact]
    public async Task Removing_a_schedule_removes_the_task_and_the_row()
    {
        using var sp = Services();
        var (profile, vm, scheduler, dialogs) = await NewScheduleVm(sp);
        var repo = sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>();

        await UiThread.Run(async () =>
        {
            await vm.LoadAsync(profile);
            await vm.SaveAsync();
            Assert.NotNull(await repo.GetByProfileAsync(profile.Id));
            await vm.RemoveAsync();
        });

        Assert.Contains(profile.Id, scheduler.Removed);
        Assert.Null(await repo.GetByProfileAsync(profile.Id));
    }

    [Fact]
    public async Task Profiles_list_shows_schedule_and_next_run_and_delete_removes_the_task()
    {
        using var sp = Services();
        var dialogs = new FakeDialogs();
        var scheduler = new FakeScheduler();
        var profiles = sp.GetRequiredService<SyncManager.Core.Abstractions.IProfileRepository>();
        var schedules = sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>();
        var p1 = await profiles.SaveAsync(new SyncProfile { Name = "Agendado", SourcePath = "a", TargetPath = "b" });
        await profiles.SaveAsync(new SyncProfile { Name = "Manual", SourcePath = "a", TargetPath = "b" });
        var sched = new ScheduleDefinition { ProfileId = p1.Id, Kind = ScheduleKind.Weekly, Days = WeekDays.Sunday, TimeOfDay = new TimeSpan(23, 0, 0) };
        await scheduler.ApplyAsync(p1, sched);
        await schedules.SaveAsync(sched);

        await UiThread.Run(async () =>
        {
            var vm = new ProfilesViewModel(profiles, sp.GetRequiredService<SyncManager.Core.Abstractions.IExecutionHistoryRepository>(), dialogs, schedules, scheduler, sp.GetRequiredService<SyncManager.Core.Abstractions.IFileSyncStateRepository>(),
                new ScheduleViewModel(schedules, scheduler, dialogs));
            await vm.LoadAsync();

            var scheduled = vm.Rows.Single(r => r.Name == "Agendado");
            Assert.Equal("Todo domingo às 23:00", scheduled.ScheduleText);
            Assert.StartsWith("2030-01-02", scheduled.NextRunText);
            var manual = vm.Rows.Single(r => r.Name == "Manual");
            Assert.Equal("Não agendado", manual.ScheduleText);
            Assert.Equal("—", manual.NextRunText);

            // "Agendar" pede a página Agendamentos com o perfil selecionado
            ProfilesRequest? asked = null;
            vm.ActionRequested += r => asked = r;
            vm.Selected = manual;
            await vm.EditScheduleAsync();
            Assert.Equal(ProfilesAction.Schedule, asked!.Action);
            Assert.Equal("Manual", asked.Profile!.Name);

            vm.Selected = scheduled;
            await vm.DeleteAsync();
        });

        Assert.Contains(p1.Id, scheduler.Removed);
        Assert.Null(await profiles.GetAsync(p1.Id));
        Assert.Null(await schedules.GetByProfileAsync(p1.Id)); // linha removida em cascata
    }

    [Fact]
    public async Task Schedule_window_loads_without_binding_errors()
    {
        using var sp = Services();
        var (profile, _, scheduler, dialogs) = await NewScheduleVm(sp);

        await UiThread.Run(async () =>
        {
            lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
            var vm = new ScheduleViewModel(sp.GetRequiredService<SyncManager.Core.Abstractions.IScheduleRepository>(), scheduler, dialogs);
            var w = new Window { Content = new SyncManager.Wpf.Views.Pages.ScheduleEditorView { DataContext = vm }, Width = 520, Height = 700 };
            w.Show();
            await vm.LoadAsync(profile);
            await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
            foreach (var kind in vm.Kinds) { vm.SelectedKind = kind; await UiThread.Settle(); }
            vm.RunOnStartup = true;
            await UiThread.Settle();
            w.Close();
        });

        lock (UiThread.BindingErrors) Assert.True(UiThread.BindingErrors.Count == 0, string.Join("\n", UiThread.BindingErrors));
    }

    [Fact]
    public async Task Settings_window_persists_the_notification_mode()
    {
        using var sp = Services();
        var dialogs = new FakeDialogs();
        var settings = sp.GetRequiredService<SettingsService>();

        await UiThread.Run(async () =>
        {
            var vm = new SettingsViewModel(settings, dialogs);
            await vm.LoadAsync();
            Assert.Equal(NotifyMode.OnProblems, vm.SelectedNotify.Mode);
            vm.SelectedNotify = vm.NotifyOptions.First(o => o.Mode == NotifyMode.Always);
            await vm.SaveAsync();
        });

        Assert.Equal(NotifyMode.Always, (await settings.LoadAsync()).NotifyMode);
    }

    public void Dispose()
    {
        Database.ReleaseFileLocks();
        try { Directory.Delete(_root, true); } catch { }
    }
}
