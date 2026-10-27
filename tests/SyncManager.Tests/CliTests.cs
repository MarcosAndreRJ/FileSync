using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Cli;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Infrastructure;
using SyncManager.Persistence;

namespace SyncManager.Tests;

internal sealed class FakeNotifier : INotificationService
{
    public List<(string Title, string Message, NotificationLevel Level)> Sent { get; } = new();
    public void Notify(string title, string message, NotificationLevel level) => Sent.Add((title, message, level));
}

/// <summary>Ambiente completo da CLI: banco e logs temporários, pastas source/target e um perfil salvo.</summary>
internal sealed class CliEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SyncManagerCliTests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public string Target => Path.Combine(Root, "target");
    public ServiceProvider Services { get; }
    public FakeNotifier Notifier { get; } = new();
    public SyncProfile Profile { get; private set; } = null!;

    public CliEnv()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        Services = new ServiceCollection()
            .AddSyncManagerEngine(Path.Combine(Root, "db", "cli.db"), Path.Combine(Root, "logs"))
            .BuildServiceProvider();
        Services.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
    }

    public async Task<CliEnv> WithProfileAsync(Action<SyncProfile>? configure = null)
    {
        var p = new SyncProfile { Name = "Backup Financeiro", SourcePath = Source, TargetPath = Target, MaxParallelFileOperations = 1, TransientRetryCount = 0 };
        configure?.Invoke(p);
        Profile = await Services.GetRequiredService<IProfileRepository>().SaveAsync(p);
        return this;
    }

    public void S(string rel, string content) => Write(Source, rel, content);
    public void T(string rel, string content) => Write(Target, rel, content);

    private static void Write(string root, string rel, string content)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, TestEnv.BaseTime);
    }

    public async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var app = new CliApp(Services, o, e, Notifier);
        var code = await app.RunAsync(args, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    public Task<IReadOnlyList<ExecutionHistory>> HistoryAsync() =>
        Services.GetRequiredService<IExecutionHistoryRepository>().ListAsync(20);

    public void Dispose()
    {
        Services.Dispose();
        Database.ReleaseFileLocks();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { }
    }
}

public class CliTests
{
    [Fact]
    public async Task Success_exits_0_runs_by_id_and_by_name_and_records_history()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("a.txt", "a"); env.S(@"d\b.txt", "b");

        var (code, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString());
        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal("a", File.ReadAllText(Path.Combine(env.Target, "a.txt")));

        env.S("c.txt", "c");
        var (code2, _, _) = await env.RunAsync("run", "--profile", "backup financeiro"); // nome, sem diferenciar maiúsculas
        Assert.Equal(0, code2);
        Assert.True(File.Exists(Path.Combine(env.Target, "c.txt")));

        var history = await env.HistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(env.Profile.Id, h.ProfileId));
    }

    [Fact]
    public async Task Silent_mode_prints_nothing()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("a.txt", "a");
        var (code, stdout, stderr) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal(0, code);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.True(File.Exists(Path.Combine(env.Target, "a.txt")));
        // ...mas o log em arquivo registra tudo
        var log = string.Concat(Directory.EnumerateFiles(Path.Combine(env.Root, "logs")).Select(f => { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(fs).ReadToEnd(); }));
        Assert.Contains("CLI RUN", log);
        Assert.Contains("PROFILE FINISHED", log);
    }

    [Fact]
    public async Task Interactive_mode_shows_progress_and_result()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("a.txt", "a");
        var (_, stdout, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString());
        Assert.Contains("Plano: copiar 1", stdout);
        Assert.Contains("Resultado: Success", stdout);
    }

    [Fact]
    public async Task Locked_file_exits_1_success_with_warnings()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("ok.txt", "ok");
        env.S("locked.bin", "locked");
        using var hold = new FileStream(Path.Combine(env.Source, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        var (code, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal((int)ExitCode.SuccessWithWarnings, code);
        Assert.True(File.Exists(Path.Combine(env.Target, "ok.txt")));
    }

    [Fact]
    public async Task Unresolved_conflict_exits_3_and_leaves_it_untouched()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("x", "file on source");
        env.T(@"x\inner.txt", "inside a folder on target");

        var (code, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal((int)ExitCode.ConflictsUnresolved, code);
        Assert.True(File.Exists(Path.Combine(env.Target, "x", "inner.txt")));
    }

    [Fact]
    public async Task Unavailable_target_exits_2_and_changes_nothing()
    {
        using var env = await new CliEnv().WithProfileAsync(p => p.TargetPath = Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N")));
        env.S("a.txt", "a");
        var (code, _, err) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString());
        Assert.Equal((int)ExitCode.Error, code);
        Assert.Contains("Target inacess", err);
        Assert.Empty(await env.HistoryAsync());
    }

    [Fact]
    public async Task Unknown_profile_exits_4()
    {
        using var env = await new CliEnv().WithProfileAsync();
        var (code, _, err) = await env.RunAsync("run", "--profile", "999");
        Assert.Equal((int)ExitCode.ProfileNotFound, code);
        Assert.Contains("não encontrado", err);
        Assert.Equal(4, (await env.RunAsync("run", "--profile", "fantasma", "--silent")).Code);
    }

    [Fact]
    public async Task Profile_already_running_exits_5_and_changes_nothing()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("a.txt", "a");
        using (env.Services.GetRequiredService<IProfileLockProvider>().TryAcquire(env.Profile.Id))
        {
            var (code, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
            Assert.Equal((int)ExitCode.AlreadyRunning, code);
            Assert.False(File.Exists(Path.Combine(env.Target, "a.txt")));
        }
        Assert.Equal(0, (await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent")).Code); // liberado
    }

    [Fact]
    public async Task Dry_run_compares_but_never_touches_files_or_history()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("new.txt", "n"); env.T("extra.txt", "x"); env.T("keep.txt", "k"); env.S("keep.txt", "k");
        var (code, stdout, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("dry-run", stdout);
        Assert.Contains("new.txt", stdout);
        Assert.False(File.Exists(Path.Combine(env.Target, "new.txt")));
        Assert.True(File.Exists(Path.Combine(env.Target, "extra.txt")));
        Assert.Empty(await env.HistoryAsync());
    }

    [Fact]
    public async Task Unattended_mass_delete_is_refused_unless_explicitly_allowed()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("only.txt", "o");
        for (var i = 0; i < 9; i++) env.T($"extra{i}.txt", "x"); // 9 de 10 itens do Target seriam excluídos

        var (code, _, err) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal((int)ExitCode.Error, code);
        Assert.Contains("abortada por segurança", err);
        Assert.Equal(9, Directory.EnumerateFiles(env.Target, "extra*").Count());
        Assert.False(File.Exists(Path.Combine(env.Target, "only.txt")));

        var (code2, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent", "--allow-mass-delete");
        Assert.Equal(0, code2);
        Assert.Empty(Directory.EnumerateFiles(env.Target, "extra*"));
        Assert.NotNull(Assert.Single(await env.HistoryAsync()).SafetyFolder); // e ainda recuperável
    }

    [Fact]
    public async Task Empty_source_blocks_the_mirror_and_exits_2()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.T("precious.txt", "keep me");
        var (code, _, err) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal(2, code);
        Assert.Contains("bloqueado", err);
        Assert.True(File.Exists(Path.Combine(env.Target, "precious.txt")));
    }

    [Fact]
    public async Task Bad_arguments_exit_2_and_help_exits_0()
    {
        using var env = await new CliEnv().WithProfileAsync();
        Assert.Equal(2, (await env.RunAsync("run")).Code);
        Assert.Equal(2, (await env.RunAsync("run", "--profile")).Code);
        Assert.Equal(2, (await env.RunAsync("run", "--profile", "1", "--xyz")).Code);
        Assert.Equal(2, (await env.RunAsync("dance")).Code);
        var (code, stdout, _) = await env.RunAsync("help");
        Assert.Equal(0, code);
        Assert.Contains("Códigos de saída", stdout);
        Assert.Equal(0, (await env.RunAsync("--help")).Code);
    }

    [Fact]
    public async Task List_shows_profiles_with_their_schedule()
    {
        using var env = await new CliEnv().WithProfileAsync();
        await env.Services.GetRequiredService<IScheduleRepository>().SaveAsync(new ScheduleDefinition
        {
            ProfileId = env.Profile.Id, Kind = ScheduleKind.Weekly, Days = WeekDays.Weekdays, TimeOfDay = new TimeSpan(18, 30, 0)
        });
        var (code, stdout, _) = await env.RunAsync("list");
        Assert.Equal(0, code);
        Assert.Contains("Backup Financeiro", stdout);
        Assert.Contains("Segunda a sexta às 18:30", stdout);
    }

    // ---------------------------------------------------------------- notificações

    [Theory]
    [InlineData(NotifyMode.OnProblems, false, 0)]   // sucesso: sem notificação
    [InlineData(NotifyMode.Always, false, 1)]
    [InlineData(NotifyMode.Never, true, 0)]
    [InlineData(NotifyMode.OnProblems, true, 1)]    // aviso: notifica
    public async Task Silent_runs_notify_according_to_settings(NotifyMode mode, bool withWarning, int expected)
    {
        using var env = await new CliEnv().WithProfileAsync();
        await env.Services.GetRequiredService<SettingsService>().SaveAsync(new AppSettings { NotifyMode = mode });
        env.S("ok.txt", "ok");
        FileStream? hold = null;
        if (withWarning)
        {
            env.S("locked.bin", "x");
            hold = new FileStream(Path.Combine(env.Source, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        (int Code, string Out, string Err) r;
        using (hold)
            r = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.True(r.Code == (withWarning ? 1 : 0), $"exit={r.Code} err={r.Err}");

        Assert.Equal(expected, env.Notifier.Sent.Count);
        if (expected == 1 && withWarning) Assert.Equal(NotificationLevel.Warning, env.Notifier.Sent[0].Level);
    }

    [Fact]
    public async Task No_notify_flag_and_interactive_runs_never_notify()
    {
        using var env = await new CliEnv().WithProfileAsync();
        await env.Services.GetRequiredService<SettingsService>().SaveAsync(new AppSettings { NotifyMode = NotifyMode.Always });
        env.S("a.txt", "a");
        await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent", "--no-notify");
        await env.RunAsync("run", "--profile", env.Profile.Id.ToString()); // interativo: o usuário está vendo o console
        Assert.Empty(env.Notifier.Sent);
    }

    [Fact]
    public async Task Failures_before_execution_also_notify_when_silent()
    {
        using var env = await new CliEnv().WithProfileAsync(p => p.TargetPath = Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N")));
        await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        var n = Assert.Single(env.Notifier.Sent);
        Assert.Equal(NotificationLevel.Error, n.Level);
        Assert.Contains("FALHOU", n.Title);
    }

    [Fact]
    public async Task A_notifier_that_throws_never_breaks_the_run()
    {
        using var env = await new CliEnv().WithProfileAsync();
        await env.Services.GetRequiredService<SettingsService>().SaveAsync(new AppSettings { NotifyMode = NotifyMode.Always });
        env.S("a.txt", "a");
        var app = new CliApp(env.Services, TextWriter.Null, TextWriter.Null, new ThrowingNotifier());
        Assert.Equal(0, await app.RunAsync(new[] { "run", "--profile", env.Profile.Id.ToString(), "--silent" }, default));
    }

    private sealed class ThrowingNotifier : INotificationService
    {
        public void Notify(string title, string message, NotificationLevel level) => throw new InvalidOperationException("sem área de trabalho");
    }

    [Theory]
    [InlineData(ExecutionStatus.Success, NotifyMode.OnProblems, false)]
    [InlineData(ExecutionStatus.Cancelled, NotifyMode.OnProblems, true)]
    [InlineData(ExecutionStatus.Failed, NotifyMode.OnProblems, true)]
    [InlineData(ExecutionStatus.ConflictsUnresolved, NotifyMode.OnProblems, true)]
    [InlineData(ExecutionStatus.Failed, NotifyMode.Never, false)]
    [InlineData(ExecutionStatus.Success, NotifyMode.Always, true)]
    public void Notification_policy(ExecutionStatus status, NotifyMode mode, bool expected) =>
        Assert.Equal(expected, NotificationPolicy.ShouldNotify(mode, status));

    [Theory]
    [InlineData(ExecutionStatus.Success, 0)]
    [InlineData(ExecutionStatus.SuccessWithWarnings, 1)]
    [InlineData(ExecutionStatus.Failed, 2)]
    [InlineData(ExecutionStatus.ConflictsUnresolved, 3)]
    [InlineData(ExecutionStatus.Cancelled, 6)]
    public void Exit_code_mapping(ExecutionStatus status, int expected) =>
        Assert.Equal(expected, (int)ExitCodes.From(status));
}

/// <summary>
/// Ponta a ponta REAL: perfil no banco → tarefa registrada no Agendador do Windows → o Windows executa o
/// SyncManager.Cli.exe → arquivos copiados e histórico gravado. Usa banco e pasta de tarefas temporários.
/// </summary>
public class ScheduledRunEndToEndTests
{
    [Fact]
    public async Task Windows_task_scheduler_runs_the_cli_and_the_profile_executes()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("report.txt", "scheduled run worked");
        env.S(@"sub\data.bin", "more");

        var cli = Path.Combine(AppContext.BaseDirectory, "SyncManager.Cli.exe");
        Assert.True(File.Exists(cli), "SyncManager.Cli.exe deveria estar na pasta de saída dos testes.");

        var db = Path.Combine(env.Root, "db", "cli.db");
        using var scheduler = new ScopedScheduler(new TaskSchedulerOptions(cli, "SyncManager.Tests.E2E." + Guid.NewGuid().ToString("N")[..8], db));
        var svc = scheduler.Service;

        Database.ReleaseFileLocks();
        await svc.ApplyAsync(env.Profile, new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = DateTime.Now.AddHours(6).TimeOfDay });
        await svc.RunNowAsync(env.Profile.Id);

        // o Windows inicia a CLI em outro processo; espera o histórico aparecer
        IReadOnlyList<ExecutionHistory> history = Array.Empty<ExecutionHistory>();
        for (var i = 0; i < 120 && history.Count == 0; i++)
        {
            await Task.Delay(250);
            history = await env.HistoryAsync();
        }

        Assert.Single(history);
        Assert.Equal(ExecutionStatus.Success, history[0].Status);
        Assert.Equal(env.Profile.Id, history[0].ProfileId);
        Assert.Equal("scheduled run worked", File.ReadAllText(Path.Combine(env.Target, "report.txt")));
        Assert.True(File.Exists(Path.Combine(env.Target, "sub", "data.bin")));

        // o resultado da tarefa no Windows é o código de saída da CLI
        ScheduledTaskInfo info = ScheduledTaskInfo.Missing;
        for (var i = 0; i < 40; i++)
        {
            info = await svc.GetInfoAsync(env.Profile.Id);
            if (info.State != "Em execução" && info.LastResult == 0) break;
            await Task.Delay(250);
        }
        Assert.Equal(0, info.LastResult);
    }

    private sealed class ScopedScheduler : IDisposable
    {
        public TaskSchedulerService Service { get; }
        public ScopedScheduler(TaskSchedulerOptions o) => Service = new TaskSchedulerService(o);
        public void Dispose() { try { Service.RemoveAllAsync().GetAwaiter().GetResult(); } catch { } }
    }
}

/// <summary>Executa o SyncManager.Cli.exe de verdade, como o Task Scheduler faria.</summary>
public class CliProcessTests
{
    private static string CliExe
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "SyncManager.Cli.exe");
            if (File.Exists(local)) return local;
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\SyncManager.Cli\bin\Debug\net10.0-windows\SyncManager.Cli.exe"));
            return File.Exists(repo) ? repo : throw new FileNotFoundException("SyncManager.Cli.exe não foi compilado.");
        }
    }

    private static (int Code, string Out) Run(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(CliExe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout);
    }

    [Fact]
    public async Task The_real_executable_follows_the_exit_code_contract()
    {
        using var env = await new CliEnv().WithProfileAsync();
        env.S("a.txt", "from the real exe");
        var db = Path.Combine(env.Root, "db", "cli.db");
        env.Services.GetRequiredService<Database>(); // garante o banco criado e migrado
        Database.ReleaseFileLocks();

        var ok = Run($"run --profile {env.Profile.Id} --silent --db \"{db}\"");
        Assert.Equal(0, ok.Code);
        Assert.Equal("", ok.Out.Trim());
        Assert.Equal("from the real exe", File.ReadAllText(Path.Combine(env.Target, "a.txt")));

        Assert.Equal(4, Run($"run --profile 12345 --silent --db \"{db}\"").Code);
        Assert.Equal(2, Run($"run --bogus --db \"{db}\"").Code);

        var help = Run("help");
        Assert.Equal(0, help.Code);
        Assert.Contains("SyncManager.Cli", help.Out);
    }
}
