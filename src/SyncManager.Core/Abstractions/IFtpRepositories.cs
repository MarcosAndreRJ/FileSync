using SyncManager.Core.Models;

namespace SyncManager.Core.Abstractions;

public sealed class DuplicateFtpConnectionNameException : Exception
{
    public DuplicateFtpConnectionNameException(string name) : base($"Já existe uma conexão FTP chamada \"{name}\".") { }
}

public sealed class DuplicateFtpTaskNameException : Exception
{
    public DuplicateFtpTaskNameException(string name) : base($"Já existe uma tarefa FTP chamada \"{name}\".") { }
}

/// <summary>
/// Repositório de conexões FTP / FTPS salvas.
/// </summary>
public interface IFtpConnectionRepository
{
    Task<IReadOnlyList<FtpConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<FtpConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<FtpConnectionProfile> SaveAsync(FtpConnectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, Guid excludingId, CancellationToken ct = default);
}

/// <summary>
/// Repositório de tarefas FTP com escopo persistente.
/// </summary>
public interface IFtpTaskRepository
{
    Task<IReadOnlyList<FtpSyncTask>> GetAllAsync(CancellationToken ct = default);
    Task<FtpSyncTask?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FtpSyncTask>> GetByConnectionIdAsync(Guid connectionId, CancellationToken ct = default);
    Task<FtpSyncTask> SaveAsync(FtpSyncTask task, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, Guid excludingId, CancellationToken ct = default);
}

/// <summary>
/// Repositório para histórico e auditoria forense de execuções FTP.
/// </summary>
public interface IFtpHistoryRepository
{
    Task AddAsync(FtpExecutionHistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<FtpExecutionHistoryEntry>> ListAsync(int limit = 50, Guid? taskId = null, CancellationToken ct = default);
    Task<FtpExecutionHistoryEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>
/// Cofre seguro para armazenamento de senhas de conexões FTP usando Windows DPAPI.
/// </summary>
public interface IFtpCredentialStore
{
    Task SavePasswordAsync(string credentialIdentifier, string password, CancellationToken ct = default);
    Task<string?> GetPasswordAsync(string credentialIdentifier, CancellationToken ct = default);
    Task DeletePasswordAsync(string credentialIdentifier, CancellationToken ct = default);
}
