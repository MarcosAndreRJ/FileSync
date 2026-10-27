using System.Collections.ObjectModel;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

public enum ProfilesAction { None, New, Edit, Compare, Execute, Schedule }

public sealed record ProfilesRequest(ProfilesAction Action, SyncProfile? Profile);

public sealed class ProfileRow
{
    public ProfileRow(SyncProfile profile, ProfileLastRun? last, ScheduleDefinition? schedule = null, ScheduledTaskInfo? task = null)
    {
        Profile = profile;
        ScheduleText = schedule is null ? "Não agendado" : schedule.Describe();
        NextRunText = schedule is null ? "—"
            : task is null ? "indisponível"
            : !task.Exists ? "⚠ tarefa ausente"
            : !task.Enabled ? "desativada"
            : task.NextRun is { } next ? next.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "ao entrar no Windows";
        LastRunText = last is null ? "Nunca executado" : last.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        StatusText = last is null ? "—" : Presentation.StatusName(last.Status);
        StatusBrush = last?.Status switch
        {
            null => Presentation.Gray,
            ExecutionStatus.Success => Presentation.Green,
            ExecutionStatus.Failed => Presentation.Red,
            _ => Presentation.Orange
        };
    }

    public SyncProfile Profile { get; }
    public string Name => Profile.Name;
    public string Source => Profile.SourcePath;
    public string Target => Profile.TargetPath;
    public string MethodText => Presentation.MethodName(Profile.Method);
    public string LastRunText { get; }
    public string StatusText { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
    public string NextRunText { get; }
    public string ScheduleText { get; }
    /// <summary>Resumo das opções principais, para o painel de detalhes.</summary>
    public string OptionsText =>
        string.Join("  •  ", new[]
        {
            Profile.IncludeSubfolders ? "Com subpastas" : "Sem subpastas",
            Profile.VerifyContentByHash ? "Conteúdo por SHA-256" : "Tamanho e data",
            Profile.DeleteStrategy switch { DeleteStrategy.SafetyFolder => "Exclusão: pasta de segurança", DeleteStrategy.RecycleBin => "Exclusão: Lixeira", _ => "Exclusão: permanente" },
            $"Filtros: {Profile.ExcludePatterns.Count} excluir / {Profile.IncludePatterns.Count} incluir"
        });
}

public sealed class ProfilesViewModel : ObservableObject
{
    private readonly IProfileRepository _profiles;
    private readonly IExecutionHistoryRepository _history;
    private readonly IDialogService _dialogs;
    private readonly IScheduleRepository _schedules;
    private readonly ISchedulerService _scheduler;
    private readonly IFileSyncStateRepository _syncStates;
    private ProfileRow? _selected;
    private string _message = "";

    public ProfilesViewModel(IProfileRepository profiles, IExecutionHistoryRepository history, IDialogService dialogs,
        IScheduleRepository schedules, ISchedulerService scheduler, IFileSyncStateRepository syncStates, ScheduleViewModel schedule)
    {
        _profiles = profiles;
        _history = history;
        _dialogs = dialogs;
        _schedules = schedules;
        _scheduler = scheduler;
        _syncStates = syncStates;
        Schedule = schedule;
        Schedule.ScheduleChanged += () => _ = LoadAsync();
        ResetSyncStateCommand = new AsyncRelayCommand(ResetSyncStateAsync, () => Selected is { Profile.Method: SyncMethod.Synchronization });

        ScheduleCommand = new AsyncRelayCommand(EditScheduleAsync, () => Selected is not null);
        NewCommand = new RelayCommand(() => Raise(ProfilesAction.New));
        EditCommand = new RelayCommand(() => Raise(ProfilesAction.Edit), () => Selected is not null);
        CompareCommand = new RelayCommand(() => Raise(ProfilesAction.Compare), () => Selected is not null);
        ExecuteCommand = new RelayCommand(() => Raise(ProfilesAction.Execute), () => Selected is not null);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => Selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null);
    }

    public ObservableCollection<ProfileRow> Rows { get; } = new();
    /// <summary>Editar, comparar, executar, agendar ou criar: quem navega (MainViewModel) decide o que fazer.</summary>
    public event Action<ProfilesRequest>? ActionRequested;
    /// <summary>Editor de agendamento da linha selecionada (página Agendamentos). Só carrega quando <see cref="TrackSchedule"/>.</summary>
    public ScheduleViewModel Schedule { get; }
    public bool TrackSchedule { get; set; }

    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand ExecuteCommand { get; }
    public AsyncRelayCommand ScheduleCommand { get; }
    public AsyncRelayCommand ResetSyncStateCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    public bool IsEmpty => Rows.Count == 0;

    public ProfileRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            if (TrackSchedule && value is not null) _ = Schedule.LoadAsync(value.Profile);
        }
    }
    public bool HasSelection => _selected is not null;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public async Task LoadAsync()
    {
        var keep = Selected?.Profile.Id;
        var all = await _profiles.GetAllAsync();
        var last = await _history.GetLastRunsAsync();
        var schedules = await _schedules.GetAllAsync();
        Rows.Clear();
        foreach (var p in all)
        {
            schedules.TryGetValue(p.Id, out var schedule);
            ScheduledTaskInfo? task = null;
            if (schedule is not null)
            {
                try { task = await _scheduler.GetInfoAsync(p.Id); }
                catch { /* Agendador indisponível: a linha mostra "indisponível" */ }
            }
            Rows.Add(new ProfileRow(p, last.GetValueOrDefault(p.Id), schedule, task));
        }
        Selected = Rows.FirstOrDefault(r => r.Profile.Id == keep) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        Message = all.Count == 0 ? "Nenhum perfil salvo ainda. Use \"Novo\" para criar o primeiro." : $"{all.Count} perfil(is).";
    }

    private void Raise(ProfilesAction action) => ActionRequested?.Invoke(new ProfilesRequest(action, Selected?.Profile));

    public void EditSelected() { if (Selected is not null) Raise(ProfilesAction.Edit); }

    /// <summary>Esquece o histórico da última sincronização: a próxima bidirecional será tratada como a primeira (diferenças viram conflitos).</summary>
    public async Task ResetSyncStateAsync()
    {
        var p = Selected!.Profile;
        if (!_dialogs.Confirm("Zerar histórico de sincronização",
                $"Esquecer o histórico da última sincronização do perfil \"{p.Name}\"?\n\n" +
                "Na próxima execução bidirecional, arquivos diferentes nos dois lados serão tratados como CONFLITOS " +
                "e nenhuma exclusão será propagada. Nenhum arquivo é alterado agora.", true))
            return;
        await _syncStates.DeleteAllAsync(p.Id);
        Message = $"Histórico de sincronização de \"{p.Name}\" zerado.";
    }

    public Task EditScheduleAsync() { Raise(ProfilesAction.Schedule); return Task.CompletedTask; }

    public async Task DuplicateAsync()
    {
        var source = Selected!.Profile;
        var copy = source.Clone();
        copy.Id = 0;
        var baseName = source.Name + " (cópia)";
        var name = baseName;
        for (var i = 2; await _profiles.NameExistsAsync(name, 0); i++) name = $"{baseName} {i}";
        copy.Name = name;

        try
        {
            var saved = await _profiles.SaveAsync(copy);
            await LoadAsync();
            Selected = Rows.FirstOrDefault(r => r.Profile.Id == saved.Id);
            Message = $"Perfil duplicado como \"{saved.Name}\".";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível duplicar", ex.Message);
        }
    }

    public async Task DeleteAsync()
    {
        var p = Selected!.Profile;
        if (!_dialogs.Confirm("Excluir perfil",
                $"Excluir o perfil \"{p.Name}\"?\n\nO histórico de execuções dele é mantido. Os arquivos das pastas não são afetados.", true))
            return;
        try
        {
            // A tarefa agendada precisa sair junto, senão ela continuaria chamando a CLI para um perfil que não existe mais.
            var hadSchedule = await _schedules.GetByProfileAsync(p.Id) is not null;
            var taskError = "";
            if (hadSchedule)
            {
                try { await _scheduler.RemoveAsync(p.Id); }
                catch (Exception ex) { taskError = ex.Message; }
            }

            await _profiles.DeleteAsync(p.Id);
            await LoadAsync();
            Message = $"Perfil \"{p.Name}\" excluído.";
            if (taskError.Length > 0)
                _dialogs.ShowError("Tarefa agendada não removida",
                    $"O perfil foi excluído, mas a tarefa \\SyncManager\\Profile_{p.Id} não pôde ser removida do Agendador de Tarefas:\n{taskError}\n\nRemova-a manualmente.");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível excluir", ex.Message);
        }
    }
}
