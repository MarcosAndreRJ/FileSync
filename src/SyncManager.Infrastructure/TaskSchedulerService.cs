using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Infrastructure;

/// <param name="DatabasePath">Quando informado (banco fora do padrão), a tarefa passa --db para a CLI usar o mesmo banco do aplicativo.</param>
public sealed record TaskSchedulerOptions(string CliPath, string TaskFolder = "SyncManager", string? DatabasePath = null);

/// <summary>
/// Agendamento pelo Windows Task Scheduler. Cada perfil vira a tarefa \SyncManager\Profile_{id}, que executa
/// <c>SyncManager.Cli.exe run --profile {id} --silent</c> na conta do usuário, com o app fechado.
/// Usa a API COM "Schedule.Service" por late binding (sem pacote externo) e definição de tarefa em XML.
/// </summary>
public sealed class TaskSchedulerService : ISchedulerService
{
    private const int CreateOrUpdate = 6;       // TASK_CREATE_OR_UPDATE
    private const int LogonInteractiveToken = 3; // TASK_LOGON_INTERACTIVE_TOKEN
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly TaskSchedulerOptions _options;

    public TaskSchedulerService(TaskSchedulerOptions options) => _options = options;

    public static string TaskNameFor(int profileId) => $"Profile_{profileId}";
    public string FullTaskName(int profileId) => $"\\{_options.TaskFolder}\\{TaskNameFor(profileId)}";

    // ------------------------------------------------------------------ XML da tarefa

    public static string BuildTaskXml(SyncProfile profile, ScheduleDefinition schedule, TaskSchedulerOptions options,
        string userId, DateTime now)
    {
        var errors = schedule.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));

        var triggers = new XElement(Ns + "Triggers");
        var at = now.Date + schedule.TimeOfDay;

        switch (schedule.Kind)
        {
            case ScheduleKind.Daily:
                triggers.Add(Calendar(at, new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1))));
                break;

            case ScheduleKind.Weekly:
                var days = new XElement(Ns + "DaysOfWeek");
                foreach (var (flag, name) in WeekDayNames)
                    if (schedule.Days.HasFlag(flag)) days.Add(new XElement(Ns + name));
                triggers.Add(Calendar(at, new XElement(Ns + "ScheduleByWeek", days, new XElement(Ns + "WeeksInterval", 1))));
                break;

            case ScheduleKind.Monthly:
                var months = new XElement(Ns + "Months");
                foreach (var m in CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(n => n.Length > 0))
                    months.Add(new XElement(Ns + m));
                triggers.Add(Calendar(at, new XElement(Ns + "ScheduleByMonth",
                    new XElement(Ns + "DaysOfMonth", new XElement(Ns + "Day", schedule.DayOfMonth == 0 ? "Last" : schedule.DayOfMonth.ToString(CultureInfo.InvariantCulture))),
                    months)));
                break;

            case ScheduleKind.EveryHours:
                var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
                triggers.Add(new XElement(Ns + "TimeTrigger",
                    new XElement(Ns + "Repetition",
                        new XElement(Ns + "Interval", IsoDuration(schedule.IntervalMinutes)),
                        new XElement(Ns + "StopAtDurationEnd", "false")),
                    new XElement(Ns + "StartBoundary", Iso(start)),
                    new XElement(Ns + "Enabled", "true")));
                break;
        }

        if (schedule.RunOnStartup)
        {
            var logon = new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"), new XElement(Ns + "UserId", userId));
            if (schedule.DelayMinutes > 0) logon.Add(new XElement(Ns + "Delay", IsoDuration(schedule.DelayMinutes)));
            triggers.Add(logon);
        }

        var network = PathUtil.IsUnc(profile.SourcePath.Trim()) || PathUtil.IsUnc(profile.TargetPath.Trim());
        var cliDir = Path.GetDirectoryName(options.CliPath) ?? "";

        var task = new XElement(Ns + "Task", new XAttribute("version", "1.4"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "SyncManager"),
                new XElement(Ns + "Description", $"SyncManager — perfil \"{profile.Name}\" (#{profile.Id}): {schedule.Describe()}")),
            triggers,
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userId),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", network ? "true" : "false"),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", schedule.Enabled ? "true" : "false"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT72H"),
                new XElement(Ns + "Priority", "7")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", options.CliPath),
                    new XElement(Ns + "Arguments", $"run --profile {profile.Id} --silent" + (options.DatabasePath is null ? "" : $" --db \"{options.DatabasePath}\"")),
                    new XElement(Ns + "WorkingDirectory", cliDir))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-16", null), task);
        using var sw = new Utf16StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    private sealed class Utf16StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.Unicode;
    }

    private static readonly (WeekDays Flag, string Name)[] WeekDayNames =
    {
        (WeekDays.Sunday, "Sunday"), (WeekDays.Monday, "Monday"), (WeekDays.Tuesday, "Tuesday"), (WeekDays.Wednesday, "Wednesday"),
        (WeekDays.Thursday, "Thursday"), (WeekDays.Friday, "Friday"), (WeekDays.Saturday, "Saturday")
    };

    private static XElement Calendar(DateTime start, XElement schedule) =>
        new(Ns + "CalendarTrigger",
            new XElement(Ns + "StartBoundary", Iso(start)),
            new XElement(Ns + "Enabled", "true"),
            schedule);

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static string IsoDuration(int minutes)
    {
        var d = minutes / (24 * 60);
        var h = minutes % (24 * 60) / 60;
        var m = minutes % 60;
        var sb = new StringBuilder("P");
        if (d > 0) sb.Append(d).Append('D');
        if (h > 0 || m > 0 || d == 0)
        {
            sb.Append('T');
            if (h > 0) sb.Append(h).Append('H');
            if (m > 0 || h == 0) sb.Append(m).Append('M');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ ISchedulerService

    public async Task<string> ApplyAsync(SyncProfile profile, ScheduleDefinition schedule, CancellationToken ct = default)
    {
        if (profile.Id <= 0) throw new ArgumentException("Só perfis salvos podem ser agendados.");
        if (!schedule.HasTrigger)
        {
            await RemoveAsync(profile.Id, ct);
            return "";
        }

        if (!File.Exists(_options.CliPath))
            throw new FileNotFoundException("A CLI do SyncManager não foi encontrada; ela é necessária para execuções agendadas.", _options.CliPath);

        var xml = BuildTaskXml(profile, schedule, _options, WindowsIdentity.GetCurrent().Name, DateTime.Now);
        await Task.Run(() =>
        {
            dynamic service = Connect();
            dynamic folder = GetFolder(service, create: true)!;
            folder.RegisterTask(TaskNameFor(profile.Id), xml, CreateOrUpdate, null, null, LogonInteractiveToken, null);
        }, ct);
        return FullTaskName(profile.Id);
    }

    public Task RemoveAsync(int profileId, CancellationToken ct = default) => Task.Run(() =>
    {
        dynamic service = Connect();
        dynamic? folder = GetFolder(service, create: false);
        if (folder is null) return;
        try { folder.DeleteTask(TaskNameFor(profileId), 0); }
        catch (Exception ex) when (IsNotFound(ex)) { }
    }, ct);

    public Task<ScheduledTaskInfo> GetInfoAsync(int profileId, CancellationToken ct = default) => Task.Run(() =>
    {
        dynamic service = Connect();
        dynamic? folder = GetFolder(service, create: false);
        if (folder is null) return ScheduledTaskInfo.Missing;

        try
        {
            dynamic task = folder.GetTask(TaskNameFor(profileId));
            return new ScheduledTaskInfo(
                true,
                (bool)task.Enabled,
                ToOffset((DateTime)task.NextRunTime),
                ToOffset((DateTime)task.LastRunTime),
                (int)task.LastTaskResult,
                StateText((int)task.State));
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return ScheduledTaskInfo.Missing;
        }
    }, ct);

    public Task RunNowAsync(int profileId, CancellationToken ct = default) => Task.Run(() =>
    {
        dynamic service = Connect();
        dynamic folder = GetFolder(service, create: false) ?? throw new FileNotFoundException("A tarefa agendada não existe.");
        dynamic task = folder.GetTask(TaskNameFor(profileId));
        task.Run(null);
    }, ct);

    /// <summary>Remove todas as tarefas do SyncManager e a pasta (útil na desinstalação e nos testes).</summary>
    public Task RemoveAllAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        dynamic service = Connect();
        dynamic? folder = GetFolder(service, create: false);
        if (folder is null) return;
        foreach (dynamic t in folder.GetTasks(0))
            folder.DeleteTask((string)t.Name, 0);
        dynamic root = service.GetFolder("\\");
        root.DeleteFolder(_options.TaskFolder, 0);
    }, ct);

    // ------------------------------------------------------------------ COM

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new PlatformNotSupportedException("O Agendador de Tarefas do Windows não está disponível.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private dynamic? GetFolder(dynamic service, bool create)
    {
        dynamic root = service.GetFolder("\\");
        try { return root.GetFolder(_options.TaskFolder); }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return create ? root.CreateFolder(_options.TaskFolder) : null;
        }
    }

    private static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException ||
        ex is COMException { HResult: unchecked((int)0x80070002) } ||
        ex is COMException { HResult: unchecked((int)0x80070003) };

    private static DateTimeOffset? ToOffset(DateTime d) =>
        d.Year < 2000 ? null : new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local));

    private static string StateText(int state) => state switch
    {
        1 => "Desativada",
        2 => "Na fila",
        3 => "Pronta",
        4 => "Em execução",
        _ => "Desconhecida"
    };
}
