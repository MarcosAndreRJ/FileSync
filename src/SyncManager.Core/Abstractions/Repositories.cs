using SyncManager.Core.Models;

namespace SyncManager.Core.Abstractions;

public sealed class DuplicateProfileNameException : Exception
{
    public DuplicateProfileNameException(string name) : base($"Já existe um perfil chamado \"{name}\".") { }
}

public interface IProfileRepository
{
    Task<IReadOnlyList<SyncProfile>> GetAllAsync(CancellationToken ct = default);
    Task<SyncProfile?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Insere (Id = 0) ou atualiza. Devolve o perfil com o Id definitivo.</summary>
    Task<SyncProfile> SaveAsync(SyncProfile profile, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, int excludingId, CancellationToken ct = default);
}

public interface IExecutionHistoryRepository
{
    Task<long> AddAsync(ExecutionHistory history, IReadOnlyList<OperationResult> results, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionHistory>> ListAsync(int limit, int? profileId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionHistoryItem>> GetItemsAsync(long executionId, int limit, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, ProfileLastRun>> GetLastRunsAsync(CancellationToken ct = default);
}

public interface IFileSyncStateRepository
{
    /// <summary>Todo o histórico do perfil, indexado pelo caminho relativo (sem diferenciar maiúsculas).</summary>
    Task<IReadOnlyDictionary<string, FileSyncState>> GetAllAsync(int profileId, CancellationToken ct = default);
    Task UpsertManyAsync(IReadOnlyCollection<FileSyncState> states, CancellationToken ct = default);
    Task DeleteManyAsync(int profileId, IReadOnlyCollection<string> relativePaths, CancellationToken ct = default);
    Task DeleteAllAsync(int profileId, CancellationToken ct = default);
    Task<int> CountAsync(int profileId, CancellationToken ct = default);
}

public interface IScheduleRepository
{
    Task<ScheduleDefinition?> GetByProfileAsync(int profileId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, ScheduleDefinition>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Insere ou atualiza o agendamento do perfil (no máximo um por perfil).</summary>
    Task<ScheduleDefinition> SaveAsync(ScheduleDefinition schedule, CancellationToken ct = default);
    Task DeleteByProfileAsync(int profileId, CancellationToken ct = default);
}

/// <summary>Materializa agendamentos no agendador do sistema operacional.</summary>
public interface ISchedulerService
{
    /// <summary>Cria/atualiza a tarefa do perfil. Devolve o nome da tarefa. Sem gatilhos, remove a tarefa.</summary>
    Task<string> ApplyAsync(SyncProfile profile, ScheduleDefinition schedule, CancellationToken ct = default);
    Task RemoveAsync(int profileId, CancellationToken ct = default);
    Task<ScheduledTaskInfo> GetInfoAsync(int profileId, CancellationToken ct = default);
    /// <summary>Dispara a tarefa agora, exatamente como o agendador faria.</summary>
    Task RunNowAsync(int profileId, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
