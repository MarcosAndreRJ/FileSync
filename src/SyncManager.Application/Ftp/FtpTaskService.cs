using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Application.Ftp;

public interface IFtpTaskService
{
    Task<IReadOnlyList<FtpSyncTask>> GetAllAsync(CancellationToken ct = default);
    Task<FtpSyncTask?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(FtpSyncTask task, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FtpChecklistItem>> GenerateChecklistAsync(
        Guid taskId,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default);
    Task<FtpExecutionHistoryEntry> ExecuteSyncAsync(
        Guid taskId,
        IReadOnlyList<FtpChecklistItem> approvedItems,
        IProgress<FtpTransferProgressInfo>? progress = null,
        IProgress<FtpProtocolLogEntry>? logProgress = null,
        CancellationToken ct = default);
}

public sealed class FtpTaskService : IFtpTaskService
{
    private readonly IFtpTaskRepository _taskRepository;
    private readonly IFtpConnectionRepository _connectionRepository;
    private readonly IFtpComparisonEngine _comparisonEngine;
    private readonly IFtpTransferEngine _transferEngine;

    public FtpTaskService(
        IFtpTaskRepository taskRepository,
        IFtpConnectionRepository connectionRepository,
        IFtpComparisonEngine comparisonEngine,
        IFtpTransferEngine transferEngine)
    {
        _taskRepository = taskRepository;
        _connectionRepository = connectionRepository;
        _comparisonEngine = comparisonEngine;
        _transferEngine = transferEngine;
    }

    public async Task<IReadOnlyList<FtpSyncTask>> GetAllAsync(CancellationToken ct = default)
    {
        return await _taskRepository.GetAllAsync(ct);
    }

    public async Task<FtpSyncTask?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _taskRepository.GetByIdAsync(id, ct);
    }

    public async Task SaveAsync(FtpSyncTask task, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
            throw new ArgumentException("O nome da tarefa é obrigatório.", nameof(task));

        if (task.ConnectionProfileId == Guid.Empty)
            throw new ArgumentException("Um perfil de conexão FTP válido deve ser selecionado.", nameof(task));

        if (string.IsNullOrWhiteSpace(task.SourceLocalPath))
            throw new ArgumentException("O diretório local de origem é obrigatório.", nameof(task));

        if (string.IsNullOrWhiteSpace(task.TargetRemotePath))
            throw new ArgumentException("O diretório remoto de destino é obrigatório.", nameof(task));

        await _taskRepository.SaveAsync(task, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _taskRepository.DeleteAsync(id, ct);
    }

    public async Task<IReadOnlyList<FtpChecklistItem>> GenerateChecklistAsync(
        Guid taskId,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, ct)
            ?? throw new InvalidOperationException($"Tarefa de sincronização com ID '{taskId}' não encontrada.");

        var profile = await _connectionRepository.GetByIdAsync(task.ConnectionProfileId, ct)
            ?? throw new InvalidOperationException($"Perfil de conexão FTP com ID '{task.ConnectionProfileId}' não encontrado.");

        return await _comparisonEngine.CompareAsync(task, profile, statusProgress, ct);
    }

    public async Task<FtpExecutionHistoryEntry> ExecuteSyncAsync(
        Guid taskId,
        IReadOnlyList<FtpChecklistItem> approvedItems,
        IProgress<FtpTransferProgressInfo>? progress = null,
        IProgress<FtpProtocolLogEntry>? logProgress = null,
        CancellationToken ct = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, ct)
            ?? throw new InvalidOperationException($"Tarefa de sincronização com ID '{taskId}' não encontrada.");

        var profile = await _connectionRepository.GetByIdAsync(task.ConnectionProfileId, ct)
            ?? throw new InvalidOperationException($"Perfil de conexão FTP com ID '{task.ConnectionProfileId}' não encontrado.");

        // Trava de segurança: se a tarefa proibir exclusões remotas, garantir que nenhum item de exclusão seja processado
        if (task.ProhibitRemoteDeletions)
        {
            foreach (var item in approvedItems.Where(i => i.ProposedAction == FtpPlannedAction.DeleteRemote))
            {
                item.IsSelected = false;
            }
        }

        return await _transferEngine.ExecuteTransferAsync(task, profile, approvedItems, progress, logProgress, ct);
    }
}
