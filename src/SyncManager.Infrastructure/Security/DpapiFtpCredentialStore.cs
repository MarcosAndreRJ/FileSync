using System.Security.Cryptography;
using System.Text;
using SyncManager.Core.Abstractions;

namespace SyncManager.Infrastructure.Security;

/// <summary>
/// Armazena credenciais no disco local criptografadas com a Windows Data Protection API (DPAPI).
/// A chave criptográfica é derivada da conta do usuário atual do Windows e protegida pelo SO.
/// </summary>
public sealed class DpapiFtpCredentialStore : IFtpCredentialStore
{
    private readonly string _credentialsDirectory;
    private static readonly byte[] Entropy = "FileSync.Ftp.Security.Salt.2026"u8.ToArray();

    public DpapiFtpCredentialStore(string? baseDirectory = null)
    {
        baseDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncManager",
            "Vault");

        _credentialsDirectory = baseDirectory;
        Directory.CreateDirectory(_credentialsDirectory);
    }

    private string GetFilePath(string credentialIdentifier)
    {
        var sanitized = string.Join("_", credentialIdentifier.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_credentialsDirectory, $"{sanitized}.cred");
    }

    public Task SavePasswordAsync(string credentialIdentifier, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialIdentifier))
            throw new ArgumentException("Identificador de credencial não pode ser vazio.", nameof(credentialIdentifier));

        var plainBytes = Encoding.UTF8.GetBytes(password);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

        var filePath = GetFilePath(credentialIdentifier);
        File.WriteAllBytes(filePath, cipherBytes);

        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(string credentialIdentifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialIdentifier))
            return Task.FromResult<string?>(null);

        var filePath = GetFilePath(credentialIdentifier);
        if (!File.Exists(filePath))
            return Task.FromResult<string?>(null);

        try
        {
            var cipherBytes = File.ReadAllBytes(filePath);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            var password = Encoding.UTF8.GetString(plainBytes);
            return Task.FromResult<string?>(password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task DeletePasswordAsync(string credentialIdentifier, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(credentialIdentifier))
        {
            var filePath = GetFilePath(credentialIdentifier);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        return Task.CompletedTask;
    }
}
