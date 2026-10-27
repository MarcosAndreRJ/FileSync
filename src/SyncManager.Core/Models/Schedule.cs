using System.Globalization;

namespace SyncManager.Core.Models;

public enum ScheduleKind { None, Daily, Weekly, EveryHours, Monthly }

[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday
}

/// <summary>Agendamento de um perfil. Um perfil tem no máximo um; ele é materializado como uma tarefa do Windows Task Scheduler.</summary>
public sealed class ScheduleDefinition
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;
    public TimeSpan TimeOfDay { get; set; } = new(2, 0, 0);
    public WeekDays Days { get; set; } = WeekDays.Weekdays;
    /// <summary>Usado por <see cref="ScheduleKind.EveryHours"/>.</summary>
    public int IntervalMinutes { get; set; } = 360;
    /// <summary>1–31, ou 0 para o último dia do mês. Usado por <see cref="ScheduleKind.Monthly"/>.</summary>
    public int DayOfMonth { get; set; } = 1;
    /// <summary>Executa quando o usuário entra no Windows (gatilho "ao fazer logon" da tarefa).</summary>
    public bool RunOnStartup { get; set; }
    /// <summary>Atraso, em minutos, depois do logon para executar automaticamente.</summary>
    public int DelayMinutes { get; set; }
    public string? TaskName { get; set; }
    public bool Enabled { get; set; } = true;

    public bool HasTrigger => Kind != ScheduleKind.None || RunOnStartup;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!HasTrigger) errors.Add("Escolha uma recorrência ou marque a execução ao iniciar.");
        if (TimeOfDay < TimeSpan.Zero || TimeOfDay >= TimeSpan.FromHours(24)) errors.Add("Horário inválido.");
        if (Kind == ScheduleKind.Weekly && Days == WeekDays.None) errors.Add("Selecione pelo menos um dia da semana.");
        if (Kind == ScheduleKind.EveryHours && (IntervalMinutes < 5 || IntervalMinutes > 7 * 24 * 60))
            errors.Add("O intervalo deve ficar entre 5 minutos e 7 dias.");
        if (Kind == ScheduleKind.Monthly && (DayOfMonth < 0 || DayOfMonth > 31)) errors.Add("Dia do mês inválido.");
        if (DelayMinutes < 0 || DelayMinutes > 24 * 60) errors.Add("O atraso deve ficar entre 0 e 1440 minutos.");
        return errors;
    }

    private static readonly string[] DayNames =
        { "domingo", "segunda-feira", "terça-feira", "quarta-feira", "quinta-feira", "sexta-feira", "sábado" };
    private static readonly string[] DayShort = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };

    /// <summary>Texto curto para listas ("Segunda a sexta às 18:30").</summary>
    public string Describe()
    {
        var parts = new List<string>();
        var at = TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        switch (Kind)
        {
            case ScheduleKind.Daily:
                parts.Add($"Todos os dias às {at}");
                break;
            case ScheduleKind.Weekly:
                parts.Add(DescribeDays() + $" às {at}");
                break;
            case ScheduleKind.EveryHours:
                parts.Add(IntervalMinutes % 60 == 0
                    ? (IntervalMinutes == 60 ? "A cada hora" : $"A cada {IntervalMinutes / 60} horas")
                    : $"A cada {IntervalMinutes} minutos");
                break;
            case ScheduleKind.Monthly:
                parts.Add(DayOfMonth == 0 ? $"No último dia do mês às {at}" : $"Todo dia {DayOfMonth} do mês às {at}");
                break;
        }

        if (RunOnStartup)
            parts.Add(DelayMinutes > 0 ? $"Ao entrar no Windows (após {DelayMinutes} min)" : "Ao entrar no Windows");

        var text = parts.Count == 0 ? "Sem agendamento" : string.Join(" + ", parts);
        return parts.Count > 0 && !Enabled ? text + " (desativado)" : text;
    }

    private string DescribeDays()
    {
        if (Days == WeekDays.All) return "Todos os dias";
        if (Days == WeekDays.Weekdays) return "Segunda a sexta";
        var idx = Enumerable.Range(0, 7).Where(i => Days.HasFlag((WeekDays)(1 << i))).ToList();
        if (idx.Count == 1)
        {
            var i = idx[0];
            return (i is 0 or 6 ? "Todo " : "Toda ") + DayNames[i];
        }
        return string.Join(", ", idx.Select(i => DayShort[i]));
    }
}

public sealed record ScheduledTaskInfo(bool Exists, bool Enabled, DateTimeOffset? NextRun, DateTimeOffset? LastRun, int? LastResult, string State)
{
    public static readonly ScheduledTaskInfo Missing = new(false, false, null, null, null, "Não registrada");
}
