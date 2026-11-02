using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public interface IFluentFtpClientFactory
{
    Task<AsyncFtpClient> CreateClientAsync(
        FtpConnectionProfile profile,
        string? explicitPassword = null,
        Action<FtpLogEntry>? logCallback = null,
        CancellationToken ct = default);
}

public sealed class FluentFtpClientFactory : IFluentFtpClientFactory
{
    private readonly IFtpCredentialStore _credentialStore;
    private readonly ILogger<FluentFtpClientFactory>? _logger;

    public FluentFtpClientFactory(IFtpCredentialStore credentialStore, ILogger<FluentFtpClientFactory>? logger = null)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task<AsyncFtpClient> CreateClientAsync(
        FtpConnectionProfile profile,
        string? explicitPassword = null,
        Action<FtpLogEntry>? logCallback = null,
        CancellationToken ct = default)
    {
        var password = explicitPassword;
        if (password == null && !string.IsNullOrWhiteSpace(profile.CredentialIdentifier))
        {
            password = await _credentialStore.GetPasswordAsync(profile.CredentialIdentifier, ct);
        }
        password ??= string.Empty;

        var client = new AsyncFtpClient(profile.Host, profile.Username, password, profile.Port);

        // Configuração de Criptografia / Protocolo
        switch (profile.Protocol)
        {
            case FtpProtocol.FtpsExplicit:
                client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                break;
            case FtpProtocol.FtpsImplicit:
                client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
                break;
            case FtpProtocol.FtpPlain:
                client.Config.EncryptionMode = FtpEncryptionMode.None;
                break;
        }

        // Modo de transferência do canal de dados
        client.Config.DataConnectionType = profile.TransferMode switch
        {
            FtpTransferMode.Active => FtpDataConnectionType.PORT,
            _ => FtpDataConnectionType.PASV
        };

        // Timeouts
        var timeoutMs = Math.Max(5, profile.TimeoutSeconds) * 1000;
        client.Config.ConnectTimeout = timeoutMs;
        client.Config.ReadTimeout = timeoutMs;
        client.Config.DataConnectionConnectTimeout = timeoutMs;
        client.Config.DataConnectionReadTimeout = timeoutMs;

        // TLS Protocols
        if (profile.RequireTls12OrHigher)
        {
            client.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        }

        // Validação e Pinning de Certificado Digital
        client.ValidateCertificate += (control, e) =>
        {
            if (profile.CertificatePinning && !string.IsNullOrWhiteSpace(profile.ExpectedCertificateThumbprint))
            {
                var serverThumbprint = e.Certificate?.GetCertHashString();
                if (serverThumbprint == null ||
                    !string.Equals(serverThumbprint, profile.ExpectedCertificateThumbprint.Replace(" ", "").Replace(":", ""), StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("Certificado rejeitado por Certificate Pinning. Esperado: {Expected}, Recebido: {Received}",
                        profile.ExpectedCertificateThumbprint, serverThumbprint);
                    e.Accept = false;
                    return;
                }
                e.Accept = true;
                return;
            }

            // Validação estrita de certificado TLS: só aceita se não houver erros na cadeia de confiança/política
            if (e.PolicyErrors == SslPolicyErrors.None)
            {
                e.Accept = true;
            }
            else
            {
                _logger?.LogWarning("Certificado TLS rejeitado por erros de política: {Errors}", e.PolicyErrors);
                e.Accept = false;
            }
        };

        // Callback de Log de Comandos e Respostas
        if (logCallback != null)
        {
            client.Config.LogToConsole = false;
            client.Config.LogHost = false;
            client.Config.LogUserName = false;
            client.Config.LogPassword = false;
            client.LegacyLogger = (level, message) => logCallback(new FtpLogEntry(level, message));
        }

        return client;
    }
}
