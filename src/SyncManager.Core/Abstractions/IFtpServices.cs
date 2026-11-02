using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Abstractions;

/// <summary>
/// Item do sistema de arquivos remoto para navegação em árvore.
/// </summary>
public class FtpRemoteItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime? ModifiedTime { get; set; }
}

/// <summary>
/// Serviço de diagnóstico e teste de conexão em 8 etapas.
/// </summary>
public interface IFtpDiagnosticService
{
    IAsyncEnumerable<FtpDiagnosticResult> RunDiagnosticsAsync(
        FtpConnectionProfile profile,
        string password,
        CancellationToken ct = default);
}

/// <summary>
/// Navegador de diretórios remotos para seleção de pastas.
/// </summary>
public interface IFtpDirectoryBrowser
{
    Task<IReadOnlyList<FtpRemoteItem>> ListDirectoryAsync(
        FtpConnectionProfile profile,
        string remotePath,
        CancellationToken ct = default,
        string? explicitPassword = null);
}

/// <summary>
/// Evento de progresso detalhado durante a sincronização FTP.
/// </summary>
public class FtpTransferProgressInfo
{
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public int PendingFiles { get; set; }
    public int FailedFiles { get; set; }
    public int IgnoredFiles { get; set; }

    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public double ProgressPercentage => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100.0 : 0.0;

    public double TransferRateBytesPerSec { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    public string CurrentFileRelativePath { get; set; } = string.Empty;
    public long CurrentFileTotalBytes { get; set; }
    public long CurrentFileTransferredBytes { get; set; }
    public double CurrentFileProgressPercentage => CurrentFileTotalBytes > 0 ? (double)CurrentFileTransferredBytes / CurrentFileTotalBytes * 100.0 : 0.0;

    public string CurrentOperationDescription { get; set; } = string.Empty;
}

/// <summary>
/// Log de comando de protocolo FTP para visualização no console.
/// </summary>
public class FtpProtocolLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsCommand { get; set; } // true = enviado pelo cliente, false = resposta do servidor
    public string Direction => IsCommand ? "CMD" : "RESP";
    public string Message { get; set; } = string.Empty;

    public FtpProtocolLogEntry() { }

    public FtpProtocolLogEntry(string message, bool isCommand = false)
    {
        Message = message;
        IsCommand = isCommand;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Motor de comparação entre diretório local e servidor FTP com escopo persistente.
/// </summary>
public interface IFtpComparisonEngine
{
    Task<IReadOnlyList<FtpChecklistItem>> CompareAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Motor de execução de transferências FTP com monitoramento e emissão de logs em tempo real.
/// </summary>
public interface IFtpTransferEngine
{
    Task<FtpExecutionHistoryEntry> ExecuteTransferAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        IReadOnlyList<FtpChecklistItem> approvedItems,
        IProgress<FtpTransferProgressInfo>? progress = null,
        IProgress<FtpProtocolLogEntry>? logProgress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Serviço de envio de notificações via Webhook (Discord, Slack, Teams, Generic JSON).
/// </summary>
public interface IFtpNotificationService
{
    Task SendNotificationAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry executionResult,
        CancellationToken ct = default);

    Task SendTestWebhookAsync(
        string webhookUrl,
        FtpWebhookType webhookType,
        CancellationToken ct = default);
}
