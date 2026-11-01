using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;

namespace SyncManager.Application.Ftp;

public interface IFtpHistoryService
{
    Task<IReadOnlyList<FtpExecutionHistoryEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<FtpExecutionHistoryEntry>> GetByTaskIdAsync(Guid taskId, int limit = 50, CancellationToken ct = default);
    Task<FtpExecutionHistoryEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task ClearAsync(DateTime cutoff, CancellationToken ct = default);
}

public sealed class FtpHistoryService : IFtpHistoryService
{
    private readonly IFtpHistoryRepository _repository;

    public FtpHistoryService(IFtpHistoryRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<FtpExecutionHistoryEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        return await _repository.ListAsync(limit, null, ct);
    }

    public async Task<IReadOnlyList<FtpExecutionHistoryEntry>> GetByTaskIdAsync(Guid taskId, int limit = 50, CancellationToken ct = default)
    {
        return await _repository.ListAsync(limit, taskId, ct);
    }

    public async Task<FtpExecutionHistoryEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _repository.GetByIdAsync(id, ct);
    }

    public async Task ClearAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await _repository.DeleteOlderThanAsync(cutoff, ct);
    }
}
