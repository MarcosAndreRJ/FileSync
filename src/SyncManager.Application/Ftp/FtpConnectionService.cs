using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;

namespace SyncManager.Application.Ftp;

public interface IFtpConnectionService
{
    Task<IReadOnlyList<FtpConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<FtpConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(FtpConnectionProfile profile, string? password = null, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<string> GetPasswordAsync(Guid id, CancellationToken ct = default);
}

public sealed class FtpConnectionService : IFtpConnectionService
{
    private readonly IFtpConnectionRepository _repository;
    private readonly IFtpCredentialStore _credentialStore;

    public FtpConnectionService(
        IFtpConnectionRepository repository,
        IFtpCredentialStore credentialStore)
    {
        _repository = repository;
        _credentialStore = credentialStore;
    }

    public async Task<IReadOnlyList<FtpConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        return await _repository.GetAllAsync(ct);
    }

    public async Task<FtpConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _repository.GetByIdAsync(id, ct);
    }

    public async Task SaveAsync(FtpConnectionProfile profile, string? password = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("O nome do perfil é obrigatório.", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new ArgumentException("O endereço do host é obrigatório.", nameof(profile));

        if (profile.Port <= 0 || profile.Port > 65535)
            throw new ArgumentOutOfRangeException(nameof(profile), "A porta deve estar entre 1 e 65535.");

        // Salvar senha protegida caso tenha sido fornecida
        if (password != null)
        {
            var targetName = $"FileSync_Ftp_{profile.Id}";
            await _credentialStore.SavePasswordAsync(targetName, password, ct);
            profile.CredentialIdentifier = targetName;
        }

        await _repository.SaveAsync(profile, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(existing.CredentialIdentifier))
            {
                await _credentialStore.DeletePasswordAsync(existing.CredentialIdentifier, ct);
            }
            await _repository.DeleteAsync(id, ct);
        }
    }

    public async Task<string> GetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct);
        if (existing == null || string.IsNullOrEmpty(existing.CredentialIdentifier))
            return string.Empty;

        return await _credentialStore.GetPasswordAsync(existing.CredentialIdentifier, ct) ?? string.Empty;
    }
}
