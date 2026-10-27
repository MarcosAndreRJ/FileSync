using System.Xml.Linq;
using SyncManager.Core.Models;
using SyncManager.Infrastructure;

namespace SyncManager.Tests;

public class ScheduleModelTests
{
    [Theory]
    [InlineData(ScheduleKind.Daily, WeekDays.None, 360, 1, false, 0, true, "Todos os dias às 02:00")]
    [InlineData(ScheduleKind.Weekly, WeekDays.Weekdays, 360, 1, false, 0, true, "Segunda a sexta às 02:00")]
    [InlineData(ScheduleKind.Weekly, WeekDays.Sunday, 360, 1, false, 0, true, "Todo domingo às 02:00")]
    [InlineData(ScheduleKind.Weekly, WeekDays.Monday, 360, 1, false, 0, true, "Toda segunda-feira às 02:00")]
    [InlineData(ScheduleKind.Weekly, WeekDays.Monday | WeekDays.Wednesday, 360, 1, false, 0, true, "seg, qua às 02:00")]
    [InlineData(ScheduleKind.Weekly, WeekDays.All, 360, 1, false, 0, true, "Todos os dias às 02:00")]
    [InlineData(ScheduleKind.EveryHours, WeekDays.None, 360, 1, false, 0, true, "A cada 6 horas")]
    [InlineData(ScheduleKind.EveryHours, WeekDays.None, 60, 1, false, 0, true, "A cada hora")]
    [InlineData(ScheduleKind.EveryHours, WeekDays.None, 90, 1, false, 0, true, "A cada 90 minutos")]
    [InlineData(ScheduleKind.Monthly, WeekDays.None, 360, 15, false, 0, true, "Todo dia 15 do mês às 02:00")]
    [InlineData(ScheduleKind.Monthly, WeekDays.None, 360, 0, false, 0, true, "No último dia do mês às 02:00")]
    [InlineData(ScheduleKind.None, WeekDays.None, 360, 1, true, 0, true, "Ao entrar no Windows")]
    [InlineData(ScheduleKind.None, WeekDays.None, 360, 1, true, 5, true, "Ao entrar no Windows (após 5 min)")]
    [InlineData(ScheduleKind.Daily, WeekDays.None, 360, 1, true, 10, true, "Todos os dias às 02:00 + Ao entrar no Windows (após 10 min)")]
    [InlineData(ScheduleKind.Daily, WeekDays.None, 360, 1, false, 0, false, "Todos os dias às 02:00 (desativado)")]
    [InlineData(ScheduleKind.None, WeekDays.None, 360, 1, false, 0, true, "Sem agendamento")]
    public void Describe_reads_naturally(ScheduleKind kind, WeekDays days, int interval, int dom, bool startup, int delay, bool enabled, string expected)
    {
        var s = new ScheduleDefinition { Kind = kind, Days = days, IntervalMinutes = interval, DayOfMonth = dom, RunOnStartup = startup, DelayMinutes = delay, Enabled = enabled };
        Assert.Equal(expected, s.Describe());
    }

    [Fact]
    public void Validate_rejects_nonsense()
    {
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.None }.Validate());
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.Weekly, Days = WeekDays.None }.Validate());
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.EveryHours, IntervalMinutes = 1 }.Validate());
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.Monthly, DayOfMonth = 40 }.Validate());
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = TimeSpan.FromHours(25) }.Validate());
        Assert.NotEmpty(new ScheduleDefinition { Kind = ScheduleKind.Daily, RunOnStartup = true, DelayMinutes = -1 }.Validate());
        Assert.Empty(new ScheduleDefinition { Kind = ScheduleKind.None, RunOnStartup = true }.Validate());
        Assert.Empty(new ScheduleDefinition { Kind = ScheduleKind.Monthly, DayOfMonth = 0 }.Validate());
    }
}

public class TaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly TaskSchedulerOptions Options = new(@"C:\Program Files\Sync Manager\SyncManager.Cli.exe");
    private static readonly DateTime Now = new(2026, 9, 30, 14, 35, 40);

    private static XDocument Build(ScheduleDefinition s, SyncProfile? p = null) =>
        XDocument.Parse(TaskSchedulerService.BuildTaskXml(p ?? new SyncProfile { Id = 15, Name = "Backup", SourcePath = @"C:\a", TargetPath = @"D:\b" }, s, Options, @"PC\user", Now));

    [Fact]
    public void Action_runs_the_cli_silently_for_the_profile()
    {
        var x = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily });
        var exec = x.Descendants(Ns + "Exec").Single();
        Assert.Equal(Options.CliPath, exec.Element(Ns + "Command")!.Value);
        Assert.Equal("run --profile 15 --silent", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal(@"C:\Program Files\Sync Manager", exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void Custom_database_is_passed_to_the_cli_so_the_task_uses_the_same_data()
    {
        var withDb = new TaskSchedulerOptions(Options.CliPath, "SyncManager", @"D:\Dados Sync\my.db");
        var xml = XDocument.Parse(TaskSchedulerService.BuildTaskXml(
            new SyncProfile { Id = 15, Name = "x", SourcePath = "a", TargetPath = "b" },
            new ScheduleDefinition { Kind = ScheduleKind.Daily }, withDb, @"PC\user", Now));
        Assert.Equal("run --profile 15 --silent --db \"D:\\Dados Sync\\my.db\"",
            xml.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void Safe_task_settings()
    {
        var s = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily }).Descendants(Ns + "Settings").Single();
        Assert.Equal("IgnoreNew", s.Element(Ns + "MultipleInstancesPolicy")!.Value);      // nunca duas instâncias
        Assert.Equal("true", s.Element(Ns + "StartWhenAvailable")!.Value);                // recupera execução perdida
        Assert.Equal("false", s.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", s.Element(Ns + "RunOnlyIfNetworkAvailable")!.Value);        // perfil local
        var principal = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily }).Descendants(Ns + "Principal").Single();
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);        // sem elevação
    }

    [Fact]
    public void Network_profiles_wait_for_the_network()
    {
        var p = new SyncProfile { Id = 3, Name = "Rede", SourcePath = @"C:\a", TargetPath = @"\\192.168.0.10\Backup" };
        var s = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily }, p).Descendants(Ns + "Settings").Single();
        Assert.Equal("true", s.Element(Ns + "RunOnlyIfNetworkAvailable")!.Value);
    }

    [Fact]
    public void Daily_trigger()
    {
        var t = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(2, 0, 0) }).Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("2026-09-30T02:00:00", t.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("1", t.Descendants(Ns + "DaysInterval").Single().Value);
    }

    [Fact]
    public void Weekday_trigger_lists_exactly_the_selected_days()
    {
        var t = Build(new ScheduleDefinition { Kind = ScheduleKind.Weekly, Days = WeekDays.Weekdays, TimeOfDay = new TimeSpan(18, 30, 0) })
            .Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("2026-09-30T18:30:00", t.Element(Ns + "StartBoundary")!.Value);
        var days = t.Descendants(Ns + "DaysOfWeek").Single().Elements().Select(e => e.Name.LocalName).ToArray();
        Assert.Equal(new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }, days);
    }

    [Fact]
    public void Sunday_only_trigger()
    {
        var t = Build(new ScheduleDefinition { Kind = ScheduleKind.Weekly, Days = WeekDays.Sunday, TimeOfDay = new TimeSpan(23, 0, 0) });
        Assert.Equal("Sunday", t.Descendants(Ns + "DaysOfWeek").Single().Elements().Single().Name.LocalName);
    }

    [Fact]
    public void Monthly_trigger_with_day_and_with_last_day()
    {
        var day = Build(new ScheduleDefinition { Kind = ScheduleKind.Monthly, DayOfMonth = 15 });
        Assert.Equal("15", day.Descendants(Ns + "Day").Single().Value);
        Assert.Equal(12, day.Descendants(Ns + "Months").Single().Elements().Count());

        var last = Build(new ScheduleDefinition { Kind = ScheduleKind.Monthly, DayOfMonth = 0 });
        Assert.Equal("Last", last.Descendants(Ns + "Day").Single().Value);
    }

    [Theory]
    [InlineData(360, "PT6H")]
    [InlineData(60, "PT1H")]
    [InlineData(90, "PT1H30M")]
    [InlineData(30, "PT30M")]
    [InlineData(1440, "P1D")]
    [InlineData(1500, "P1DT1H")]
    public void Interval_trigger_uses_iso_durations(int minutes, string expected)
    {
        var t = Build(new ScheduleDefinition { Kind = ScheduleKind.EveryHours, IntervalMinutes = minutes }).Descendants(Ns + "TimeTrigger").Single();
        Assert.Equal(expected, t.Descendants(Ns + "Interval").Single().Value);
        Assert.Equal("2026-09-30T14:35:00", t.Element(Ns + "StartBoundary")!.Value);
    }

    [Fact]
    public void Logon_trigger_with_delay_and_combined_with_a_recurrence()
    {
        var x = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily, RunOnStartup = true, DelayMinutes = 10 });
        Assert.Single(x.Descendants(Ns + "CalendarTrigger"));
        var logon = x.Descendants(Ns + "LogonTrigger").Single();
        Assert.Equal("PT10M", logon.Element(Ns + "Delay")!.Value);
        Assert.Equal(@"PC\user", logon.Element(Ns + "UserId")!.Value);

        var only = Build(new ScheduleDefinition { Kind = ScheduleKind.None, RunOnStartup = true });
        Assert.Empty(only.Descendants(Ns + "CalendarTrigger"));
        Assert.Null(only.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "Delay"));
    }

    [Fact]
    public void Disabled_schedule_creates_a_disabled_task()
    {
        var x = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily, Enabled = false });
        Assert.Equal("false", x.Descendants(Ns + "Settings").Single().Element(Ns + "Enabled")!.Value);
    }

    [Fact]
    public void Special_characters_are_escaped_and_xml_stays_valid()
    {
        var p = new SyncProfile { Id = 9, Name = "R&D <backup> \"x\"", SourcePath = @"C:\a", TargetPath = @"D:\b" };
        var x = Build(new ScheduleDefinition { Kind = ScheduleKind.Daily }, p);
        Assert.Contains("R&D <backup>", x.Descendants(Ns + "Description").Single().Value);
    }

    [Fact]
    public void Invalid_schedule_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(new ScheduleDefinition { Kind = ScheduleKind.Weekly, Days = WeekDays.None }));
    }
}

/// <summary>Registra tarefas DE VERDADE no Agendador de Tarefas (pasta temporária própria, removida ao final).</summary>
public class TaskSchedulerIntegrationTests : IDisposable
{
    private readonly TaskSchedulerService _service;
    // Comando inofensivo: nenhuma tarefa de teste deve, se disparar, abrir janela nem mexer em arquivos.
    private static readonly string Harmless = Path.Combine(Environment.SystemDirectory, "whoami.exe");

    public TaskSchedulerIntegrationTests() =>
        _service = new TaskSchedulerService(new TaskSchedulerOptions(Harmless, "SyncManager.Tests." + Guid.NewGuid().ToString("N")[..8]));

    private static SyncProfile Profile(int id) => new() { Id = id, Name = "teste " + id, SourcePath = @"C:\a", TargetPath = @"D:\b" };

    private static TimeSpan LaterToday() => DateTime.Now.AddHours(6).TimeOfDay;

    [Theory]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    [InlineData(ScheduleKind.Monthly)]
    [InlineData(ScheduleKind.EveryHours)]
    public async Task Registers_reads_and_removes_a_real_task(ScheduleKind kind)
    {
        var s = new ScheduleDefinition { Kind = kind, Days = WeekDays.All, TimeOfDay = LaterToday(), IntervalMinutes = 360, DayOfMonth = 0 };

        Assert.Equal(ScheduledTaskInfo.Missing, await _service.GetInfoAsync(101));
        var name = await _service.ApplyAsync(Profile(101), s);
        Assert.EndsWith(@"\Profile_101", name);

        var info = await _service.GetInfoAsync(101);
        Assert.True(info.Exists);
        Assert.True(info.Enabled);
        Assert.NotNull(info.NextRun);
        Assert.True(info.NextRun > DateTimeOffset.Now.AddMinutes(-1));

        await _service.RemoveAsync(101);
        Assert.False((await _service.GetInfoAsync(101)).Exists);
        await _service.RemoveAsync(101); // remover de novo é inofensivo
    }

    [Fact]
    public async Task Logon_trigger_only_has_no_next_run_but_exists()
    {
        await _service.ApplyAsync(Profile(102), new ScheduleDefinition { Kind = ScheduleKind.None, RunOnStartup = true, DelayMinutes = 3 });
        var info = await _service.GetInfoAsync(102);
        Assert.True(info.Exists);
        Assert.Null(info.NextRun);
    }

    [Fact]
    public async Task Update_replaces_the_task_and_disabled_flag_is_honored()
    {
        var s = new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = LaterToday() };
        await _service.ApplyAsync(Profile(103), s);
        Assert.True((await _service.GetInfoAsync(103)).Enabled);

        s.Enabled = false;
        await _service.ApplyAsync(Profile(103), s);
        var info = await _service.GetInfoAsync(103);
        Assert.True(info.Exists);
        Assert.False(info.Enabled);
    }

    [Fact]
    public async Task Applying_a_schedule_without_triggers_removes_the_task()
    {
        await _service.ApplyAsync(Profile(104), new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = LaterToday() });
        Assert.True((await _service.GetInfoAsync(104)).Exists);
        await _service.ApplyAsync(Profile(104), new ScheduleDefinition { Kind = ScheduleKind.None, RunOnStartup = false });
        Assert.False((await _service.GetInfoAsync(104)).Exists);
    }

    [Fact]
    public async Task Run_now_triggers_the_task()
    {
        await _service.ApplyAsync(Profile(105), new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = LaterToday() });
        await _service.RunNowAsync(105);

        ScheduledTaskInfo info = ScheduledTaskInfo.Missing;
        for (var i = 0; i < 40; i++)
        {
            info = await _service.GetInfoAsync(105);
            if (info.LastRun is { } lr && lr > DateTimeOffset.Now.AddMinutes(-2)) break;
            await Task.Delay(250);
        }
        Assert.NotNull(info.LastRun);
        Assert.True(info.LastRun > DateTimeOffset.Now.AddMinutes(-2));
    }

    [Fact]
    public async Task Missing_cli_is_reported_clearly()
    {
        var broken = new TaskSchedulerService(new TaskSchedulerOptions(@"C:\nao\existe\SyncManager.Cli.exe", "SyncManager.Tests.none"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            broken.ApplyAsync(Profile(106), new ScheduleDefinition { Kind = ScheduleKind.Daily }));
    }

    [Fact]
    public async Task Only_saved_profiles_can_be_scheduled()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyAsync(Profile(0), new ScheduleDefinition { Kind = ScheduleKind.Daily }));
    }

    public void Dispose()
    {
        try { _service.RemoveAllAsync().GetAwaiter().GetResult(); } catch { }
    }
}
