using System.Globalization;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

public sealed record KindOption(ScheduleKind Kind, string Label);
public sealed record DayOfMonthOption(int Value, string Label);

/// <summary>Edita o agendamento de um perfil e o materializa como tarefa do Windows Task Scheduler.</summary>
public sealed class ScheduleViewModel : ObservableObject
{
    private readonly IScheduleRepository _schedules;
    private readonly ISchedulerService _scheduler;
    private readonly IDialogService _dialogs;

    private SyncProfile _profile = new();
    private ScheduleDefinition _existing = new();
    private KindOption _kind;
    private string _time = "02:00";
    private WeekDays _days = WeekDays.Weekdays;
    private int _hours = 6;
    private DayOfMonthOption _dayOfMonth;
    private bool _runOnStartup;
    private int _delay;
    private bool _enabled = true;
    private string _taskStatus = "";
    private bool _hasSavedSchedule;

    public ScheduleViewModel(IScheduleRepository schedules, ISchedulerService scheduler, IDialogService dialogs)
    {
        _schedules = schedules;
        _scheduler = scheduler;
        _dialogs = dialogs;

        Kinds = new[]
        {
            new KindOption(ScheduleKind.Daily, "Todos os dias"),
            new KindOption(ScheduleKind.Weekly, "Dias da semana selecionados"),
            new KindOption(ScheduleKind.EveryHours, "A cada X horas"),
            new KindOption(ScheduleKind.Monthly, "Mensalmente"),
            new KindOption(ScheduleKind.None, "Sem horário recorrente (somente ao entrar no Windows)")
        };
        _kind = Kinds[0];

        DaysOfMonth = Enumerable.Range(1, 28).Select(d => new DayOfMonthOption(d, $"Dia {d}"))
            .Append(new DayOfMonthOption(0, "Último dia do mês")).ToArray();
        _dayOfMonth = DaysOfMonth[0];

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => _hasSavedSchedule);
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => _hasSavedSchedule);
        PresetWeekdaysCommand = new RelayCommand(() => Days = WeekDays.Weekdays);
        PresetAllCommand = new RelayCommand(() => Days = WeekDays.All);
    }

    public IReadOnlyList<KindOption> Kinds { get; }
    public IReadOnlyList<DayOfMonthOption> DaysOfMonth { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand RunNowCommand { get; }
    public RelayCommand PresetWeekdaysCommand { get; }
    public RelayCommand PresetAllCommand { get; }
    /// <summary>Agendamento salvo ou removido (a página que o hospeda atualiza a lista).</summary>
    public event Action? ScheduleChanged;

    public string Title => $"Agendamento — {_profile.Name}";
    public string ProfileSummary => $"{_profile.SourcePath}   →   {_profile.TargetPath}";

    public KindOption SelectedKind
    {
        get => _kind;
        set { if (value is not null && SetProperty(ref _kind, value)) { OnPropertyChanged(nameof(ShowTime)); OnPropertyChanged(nameof(ShowWeekly)); OnPropertyChanged(nameof(ShowInterval)); OnPropertyChanged(nameof(ShowMonthly)); Changed(); } }
    }

    public bool ShowTime => _kind.Kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly;
    public bool ShowWeekly => _kind.Kind == ScheduleKind.Weekly;
    public bool ShowInterval => _kind.Kind == ScheduleKind.EveryHours;
    public bool ShowMonthly => _kind.Kind == ScheduleKind.Monthly;

    public string Time { get => _time; set { if (SetProperty(ref _time, value)) Changed(); } }
    public int Hours { get => _hours; set { if (SetProperty(ref _hours, value)) Changed(); } }
    public DayOfMonthOption SelectedDayOfMonth { get => _dayOfMonth; set { if (value is not null && SetProperty(ref _dayOfMonth, value)) Changed(); } }
    public bool RunOnStartup { get => _runOnStartup; set { if (SetProperty(ref _runOnStartup, value)) Changed(); } }
    public int DelayMinutes { get => _delay; set { if (SetProperty(ref _delay, value)) Changed(); } }
    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) Changed(); } }
    public string TaskStatus { get => _taskStatus; private set => SetProperty(ref _taskStatus, value); }
    private string _message = "";
    /// <summary>Resultado da última ação (salvar/remover), para a tela mostrar sem diálogo.</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool HasProfile => _profile.Id > 0;

    public WeekDays Days
    {
        get => _days;
        set
        {
            if (!SetProperty(ref _days, value)) return;
            foreach (var n in new[] { nameof(Sunday), nameof(Monday), nameof(Tuesday), nameof(Wednesday), nameof(Thursday), nameof(Friday), nameof(Saturday) })
                OnPropertyChanged(n);
            Changed();
        }
    }

    private bool Day(WeekDays d) => _days.HasFlag(d);
    private void SetDay(WeekDays d, bool on) => Days = on ? _days | d : _days & ~d;
    public bool Sunday { get => Day(WeekDays.Sunday); set => SetDay(WeekDays.Sunday, value); }
    public bool Monday { get => Day(WeekDays.Monday); set => SetDay(WeekDays.Monday, value); }
    public bool Tuesday { get => Day(WeekDays.Tuesday); set => SetDay(WeekDays.Tuesday, value); }
    public bool Wednesday { get => Day(WeekDays.Wednesday); set => SetDay(WeekDays.Wednesday, value); }
    public bool Thursday { get => Day(WeekDays.Thursday); set => SetDay(WeekDays.Thursday, value); }
    public bool Friday { get => Day(WeekDays.Friday); set => SetDay(WeekDays.Friday, value); }
    public bool Saturday { get => Day(WeekDays.Saturday); set => SetDay(WeekDays.Saturday, value); }

    /// <summary>Resumo em linguagem natural do que será agendado (ou o motivo de não ser válido).</summary>
    public string Preview
    {
        get
        {
            var (schedule, error) = Build();
            return error is not null ? "⚠ " + error : schedule!.Describe();
        }
    }

    private void Changed() => OnPropertyChanged(nameof(Preview));

    public async Task LoadAsync(SyncProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ProfileSummary));

        var saved = await _schedules.GetByProfileAsync(profile.Id);
        _hasSavedSchedule = saved is not null;
        _existing = saved ?? new ScheduleDefinition { ProfileId = profile.Id };

        _kind = Kinds.First(k => k.Kind == _existing.Kind);
        _time = _existing.TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _days = _existing.Days == WeekDays.None ? WeekDays.Weekdays : _existing.Days;
        _hours = Math.Max(1, _existing.IntervalMinutes / 60);
        _dayOfMonth = DaysOfMonth.FirstOrDefault(d => d.Value == _existing.DayOfMonth) ?? DaysOfMonth[0];
        _runOnStartup = _existing.RunOnStartup;
        _delay = _existing.DelayMinutes;
        _enabled = _existing.Enabled;
        _message = "";
        OnPropertyChanged(string.Empty); // atualiza todos os bindings

        await RefreshStatusAsync();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private (ScheduleDefinition? Schedule, string? Error) Build()
    {
        if (!TimeSpan.TryParseExact(_time.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time) ||
            time >= TimeSpan.FromHours(24))
            return (null, "Informe o horário no formato HH:mm (ex.: 18:30).");

        var s = new ScheduleDefinition
        {
            Id = _existing.Id,
            ProfileId = _profile.Id,
            Kind = _kind.Kind,
            TimeOfDay = time,
            Days = _days,
            IntervalMinutes = _hours * 60,
            DayOfMonth = _dayOfMonth.Value,
            RunOnStartup = _runOnStartup,
            DelayMinutes = _delay,
            Enabled = _enabled,
            TaskName = _existing.TaskName
        };
        var errors = s.Validate();
        return errors.Count > 0 ? (null, string.Join(" ", errors)) : (s, null);
    }

    public async Task SaveAsync()
    {
        var (schedule, error) = Build();
        if (schedule is null)
        {
            _dialogs.ShowError("Agendamento", error!);
            return;
        }

        try
        {
            // Primeiro o Agendador do Windows (pode falhar); só então grava no banco, para nunca ficar "agendado" sem tarefa.
            schedule.TaskName = await _scheduler.ApplyAsync(_profile, schedule);
            _existing = await _schedules.SaveAsync(schedule);
            _hasSavedSchedule = true;
            await RefreshStatusAsync();
            Message = "Agendamento salvo e registrado no Agendador de Tarefas.";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            ScheduleChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível agendar", ex.Message);
        }
    }

    public async Task RemoveAsync()
    {
        if (!_dialogs.Confirm("Remover agendamento", $"Remover o agendamento do perfil \"{_profile.Name}\"?\nO perfil e o histórico são mantidos.", true))
            return;
        try
        {
            await _scheduler.RemoveAsync(_profile.Id);
            await _schedules.DeleteByProfileAsync(_profile.Id);
            _hasSavedSchedule = false;
            await RefreshStatusAsync();
            Message = "Agendamento removido.";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            ScheduleChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível remover o agendamento", ex.Message);
        }
    }

    private async Task RunNowAsync()
    {
        try
        {
            await _scheduler.RunNowAsync(_profile.Id);
            TaskStatus = "Tarefa disparada agora pelo Agendador do Windows. Veja o resultado no Histórico.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível executar a tarefa", ex.Message);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (!_hasSavedSchedule) { TaskStatus = "Este perfil ainda não tem agendamento."; return; }
        try
        {
            var info = await _scheduler.GetInfoAsync(_profile.Id);
            TaskStatus = !info.Exists
                ? "⚠ A tarefa não existe mais no Agendador do Windows (foi removida fora do SyncManager). Salve para recriá-la."
                : $"Tarefa: {info.State}" +
                  (info.NextRun is { } n ? $"  |  próxima execução: {n.LocalDateTime:yyyy-MM-dd HH:mm}" : "") +
                  (info.LastRun is { } l ? $"  |  última: {l.LocalDateTime:yyyy-MM-dd HH:mm} (resultado {info.LastResult})" : "");
        }
        catch (Exception ex)
        {
            TaskStatus = "Não foi possível consultar o Agendador do Windows: " + ex.Message;
        }
    }
}
