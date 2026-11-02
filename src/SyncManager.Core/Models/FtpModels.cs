using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

/// <summary>
/// Perfil de configuração e segurança de conexão FTP / FTPS.
/// </summary>
public class FtpConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public FtpProtocol Protocol { get; set; } = FtpProtocol.FtpsExplicit;
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Identificador da credencial salva de forma criptografada via DPAPI no Windows.
    /// Nunca guarda a senha em texto claro.
    /// </summary>
    public string CredentialIdentifier { get; set; } = string.Empty;

    public FtpTransferMode TransferMode { get; set; } = FtpTransferMode.Passive;
    public bool RequireTls12OrHigher { get; set; } = true;
    public bool CertificatePinning { get; set; } = false;
    public string? ExpectedCertificateThumbprint { get; set; }

    public string DefaultLocalPath { get; set; } = string.Empty;
    public string DefaultRemotePath { get; set; } = "/";

    public int MaxParallelConnections { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 30;
    public bool PreserveModTime { get; set; } = true; // Comandos MFMT / MDTM
    public bool SendKeepAlive { get; set; } = true;    // Comando NOOP

    public string? DefaultWebhookUrl { get; set; }
    public FtpWebhookType DefaultWebhookType { get; set; } = FtpWebhookType.GenericJson;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    public FtpConnectionProfile Clone()
    {
        return new FtpConnectionProfile
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            Protocol = Protocol,
            Username = Username,
            CredentialIdentifier = CredentialIdentifier,
            TransferMode = TransferMode,
            RequireTls12OrHigher = RequireTls12OrHigher,
            CertificatePinning = CertificatePinning,
            ExpectedCertificateThumbprint = ExpectedCertificateThumbprint,
            DefaultLocalPath = DefaultLocalPath,
            DefaultRemotePath = DefaultRemotePath,
            MaxParallelConnections = MaxParallelConnections,
            TimeoutSeconds = TimeoutSeconds,
            PreserveModTime = PreserveModTime,
            SendKeepAlive = SendKeepAlive,
            DefaultWebhookUrl = DefaultWebhookUrl,
            DefaultWebhookType = DefaultWebhookType,
            CreatedAt = CreatedAt,
            LastUsedAt = LastUsedAt
        };
    }
}

/// <summary>
/// Tarefa de sincronização FTP com escopo persistente e regras de filtragem.
/// </summary>
public class FtpSyncTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid ConnectionProfileId { get; set; }

    public string SourceLocalPath { get; set; } = string.Empty;
    public string TargetRemotePath { get; set; } = "/";
    public FtpSyncMethod Method { get; set; } = FtpSyncMethod.UpdateTarget;

    /// <summary>
    /// Escopo persistente: lista de caminhos relativos de pastas e arquivos autorizados.
    /// Pastas não presentes aqui são completamente ignoradas.
    /// </summary>
    public List<string> IncludedRelativePaths { get; set; } = [];

    /// <summary>
    /// Padrões glob para arquivos e diretórios a excluir (ex: .env*, **/.git/**, **/node_modules/**).
    /// </summary>
    public List<string> ExclusionPatterns { get; set; } =
    [
        ".env*",
        "**/.git/**",
        "**/node_modules/**",
        "**/vendor/**",
        "**/*.log",
        "**/.vs/**",
        "**/bin/**",
        "**/obj/**"
    ];

    // Travas de segurança mandatórias
    public bool RequireChecklistApproval { get; set; } = true;
    public bool BackupBeforeOverwrite { get; set; } = true;
    public bool ProhibitRemoteDeletions { get; set; } = true;

    // Recursos avançados para deploy de aplicações web e upload atômico
    public bool UseAtomicUpload { get; set; } = true; // Envia para arquivo .ftptmp e renomeia após sucesso
    public bool SafeDeploy { get => UseAtomicUpload; set => UseAtomicUpload = value; }

    // Webhooks e Notificações pós-sincronização
    public string? WebhookUrl { get; set; }
    public string? PostSyncWebhookUrl { get => WebhookUrl; set => WebhookUrl = value; }
    public FtpWebhookType WebhookType { get; set; } = FtpWebhookType.GenericJson;
    public bool NotifyOnCompletion { get; set; } = false;

    public bool EnableMaintenanceMode { get; set; } = false; // Cria arquivo de manutenção durante o deploy
    public string? MaintenanceFileName { get; set; } = ".maintenance";
    public WebFrameworkPreset FrameworkPreset { get; set; } = WebFrameworkPreset.Custom;

    public FtpComparisonStrategy ComparisonStrategy { get; set; } = FtpComparisonStrategy.SizeAndTimestamp;
    public int TimestampToleranceSeconds { get; set; } = 2;
    public double MaxRemoteDeletePercentage { get; set; } = 0.20;
    public bool RequireExplicitChecklistConfirmation { get; set; } = true;

    // Aliases para compatibilidade de visualização e binding
    public FtpSyncMethod SyncMethod { get => Method; set => Method = value; }
    public List<string> ExcludePatterns { get => ExclusionPatterns; set => ExclusionPatterns = value; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastExecutedAt { get; set; }

    public FtpSyncTask Clone()
    {
        return new FtpSyncTask
        {
            Id = Id,
            Name = Name,
            Description = Description,
            ConnectionProfileId = ConnectionProfileId,
            SourceLocalPath = SourceLocalPath,
            TargetRemotePath = TargetRemotePath,
            Method = Method,
            IncludedRelativePaths = [.. IncludedRelativePaths],
            ExclusionPatterns = [.. ExclusionPatterns],
            RequireChecklistApproval = RequireChecklistApproval,
            BackupBeforeOverwrite = BackupBeforeOverwrite,
            ProhibitRemoteDeletions = ProhibitRemoteDeletions,
            UseAtomicUpload = UseAtomicUpload,
            WebhookUrl = WebhookUrl,
            WebhookType = WebhookType,
            NotifyOnCompletion = NotifyOnCompletion,
            EnableMaintenanceMode = EnableMaintenanceMode,
            MaintenanceFileName = MaintenanceFileName,
            FrameworkPreset = FrameworkPreset,
            ComparisonStrategy = ComparisonStrategy,
            TimestampToleranceSeconds = TimestampToleranceSeconds,
            MaxRemoteDeletePercentage = MaxRemoteDeletePercentage,
            RequireExplicitChecklistConfirmation = RequireExplicitChecklistConfirmation,
            CreatedAt = CreatedAt,
            LastExecutedAt = LastExecutedAt
        };
    }
}

/// <summary>
/// Presets padronizados de exclusão para stacks e frameworks web comuns.
/// </summary>
public static class FtpExclusionPresets
{
    public static readonly IReadOnlyList<string> Laravel =
    [
        ".env",
        ".env*",
        "storage/logs/*",
        "storage/framework/cache/*",
        "storage/framework/sessions/*",
        "storage/framework/views/*",
        "vendor/*",
        "node_modules/*",
        ".git/*",
        "phpunit.xml"
    ];

    public static readonly IReadOnlyList<string> WordPress =
    [
        "wp-config.php",
        "wp-content/uploads/*",
        ".htaccess",
        ".git/*",
        "*.sql"
    ];

    public static readonly IReadOnlyList<string> NodeSpaReact =
    [
        "node_modules/*",
        ".git/*",
        ".env*",
        "dist/*",
        ".vscode/*"
    ];

    public static readonly IReadOnlyList<string> DotNetGeneric =
    [
        "bin/*",
        "obj/*",
        "*.user",
        "*.suo",
        "appsettings.Development.json",
        ".git/*"
    ];

    public static readonly IReadOnlyList<string> StaticHtml =
    [
        ".git/*",
        ".vscode/*",
        "*.psd",
        "*.ai",
        "*.sketch"
    ];

    public static List<string> GetPatterns(WebFrameworkPreset preset) => preset switch
    {
        WebFrameworkPreset.Laravel => [.. Laravel],
        WebFrameworkPreset.WordPress => [.. WordPress],
        WebFrameworkPreset.ReactViteSpa or WebFrameworkPreset.NodeJs => [.. NodeSpaReact],
        WebFrameworkPreset.AspNetCore => [.. DotNetGeneric],
        WebFrameworkPreset.StaticHtml => [.. StaticHtml],
        _ =>
        [
            ".env*",
            ".git/*",
            "node_modules/*",
            "vendor/*",
            "*.log",
            ".vs/*",
            "bin/*",
            "obj/*"
        ]
    };

    public static void ApplyPreset(FtpSyncTask task, WebFrameworkPreset preset, bool replace = false)
    {
        task.FrameworkPreset = preset;
        var newPatterns = GetPatterns(preset);
        if (replace)
        {
            task.ExclusionPatterns = [.. newPatterns];
        }
        else
        {
            foreach (var pattern in newPatterns)
            {
                if (!task.ExclusionPatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                {
                    task.ExclusionPatterns.Add(pattern);
                }
            }
        }
    }

    public static bool IsSensitiveWebFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);

        if (fileName.Equals("wp-config.php", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("storage/logs/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// Item do checklist obrigatório pré-transferência.
/// </summary>
public class FtpChecklistItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string LocalFullPath { get; set; } = string.Empty;
    public string RemoteFullPath { get; set; } = string.Empty;
    public FtpPlannedAction ProposedAction { get; set; } = FtpPlannedAction.Keep;
    public bool IsSelected { get; set; } = true;
    public long FileSizeBytes { get; set; }
    public DateTime LocalModifiedTime { get; set; }
    public DateTime? RemoteModifiedTime { get; set; }
    public string? StatusReason { get; set; }
}

/// <summary>
/// Resultado individual de cada uma das 8 etapas do diagnóstico FTP.
/// </summary>
public class FtpDiagnosticResult
{
    public FtpDiagnosticStep Step { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public FtpDiagnosticStepStatus Status { get; set; } = FtpDiagnosticStepStatus.Pending;
    public string? Details { get; set; }
    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// Registro histórico de execução para auditoria forense.
/// </summary>
public class FtpExecutionHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public FtpExecutionStatus Status { get; set; } = FtpExecutionStatus.Success;
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FailedFiles { get; set; }
    public long TotalBytesTransferred { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public List<FtpExecutionFileRecord> FilesAffected { get; set; } = [];
}

/// <summary>
/// Detalhe de cada arquivo afetado durante a transferência.
/// </summary>
public class FtpExecutionFileRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public FtpPlannedAction Action { get; set; }
    public bool Success { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
}
