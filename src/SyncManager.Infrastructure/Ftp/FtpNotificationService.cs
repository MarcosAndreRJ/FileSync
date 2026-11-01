using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public sealed class FtpNotificationService : IFtpNotificationService
{
    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly HttpClient _httpClient;

    public FtpNotificationService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public async Task SendNotificationAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry executionResult,
        CancellationToken ct = default)
    {
        // Determinar URL e Tipo do webhook (prioridade para a Tarefa, fallback para o Perfil)
        var webhookUrl = !string.IsNullOrWhiteSpace(task.WebhookUrl)
            ? task.WebhookUrl.Trim()
            : profile.DefaultWebhookUrl?.Trim();

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        var webhookType = !string.IsNullOrWhiteSpace(task.WebhookUrl)
            ? task.WebhookType
            : profile.DefaultWebhookType;

        var payload = BuildPayload(webhookType, task, profile, executionResult);
        await PostPayloadAsync(webhookUrl, payload, ct);
    }

    public async Task SendTestWebhookAsync(
        string webhookUrl,
        FtpWebhookType webhookType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("A URL do webhook não pode ser vazia.", nameof(webhookUrl));

        var dummyTask = new FtpSyncTask
        {
            Name = "Teste de Webhook",
            SourceLocalPath = "C:\\Local\\Projeto",
            TargetRemotePath = "/public_html"
        };

        var dummyProfile = new FtpConnectionProfile
        {
            Name = "Servidor de Teste",
            Host = "ftp.exemplo.com",
            Protocol = FtpProtocol.FtpsExplicit
        };

        var dummyResult = new FtpExecutionHistoryEntry
        {
            Status = FtpExecutionStatus.Success,
            TotalFiles = 5,
            ProcessedFiles = 5,
            FailedFiles = 0,
            TotalBytesTransferred = 1024 * 512,
            Duration = TimeSpan.FromSeconds(2.4),
            ExecutedAt = DateTime.UtcNow
        };

        var payload = BuildPayload(webhookType, dummyTask, dummyProfile, dummyResult, isTest: true);
        await PostPayloadAsync(webhookUrl.Trim(), payload, ct);
    }

    private async Task PostPayloadAsync(string url, string jsonPayload, CancellationToken ct)
    {
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FileSync-NotificationService", "1.0"));

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    internal static string BuildPayload(
        FtpWebhookType webhookType,
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry result,
        bool isTest = false)
    {
        var statusStr = result.Status switch
        {
            FtpExecutionStatus.Success => "Sucesso",
            FtpExecutionStatus.Warning => "Avisos",
            FtpExecutionStatus.Failed => "Falha",
            FtpExecutionStatus.Cancelled => "Cancelado",
            _ => result.Status.ToString()
        };

        var sizeStr = FormatBytes(result.TotalBytesTransferred);
        var durationStr = result.Duration.ToString(@"hh\:mm\:ss");
        var titlePrefix = isTest ? "[TESTE] " : string.Empty;

        return webhookType switch
        {
            FtpWebhookType.Discord => BuildDiscordPayload(titlePrefix, statusStr, sizeStr, durationStr, task, profile, result),
            FtpWebhookType.Slack => BuildSlackPayload(titlePrefix, statusStr, sizeStr, durationStr, task, profile, result),
            FtpWebhookType.MicrosoftTeams => BuildTeamsPayload(titlePrefix, statusStr, sizeStr, durationStr, task, profile, result),
            _ => BuildGenericJsonPayload(isTest, statusStr, sizeStr, durationStr, task, profile, result)
        };
    }

    private static string BuildDiscordPayload(
        string prefix,
        string statusStr,
        string sizeStr,
        string durationStr,
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry result)
    {
        int color = result.Status switch
        {
            FtpExecutionStatus.Success => 0x2ecc71, // Verde
            FtpExecutionStatus.Warning => 0xe67e22, // Laranja
            _ => 0xe74c3c // Vermelho
        };

        var embed = new
        {
            username = "FileSync",
            embeds = new[]
            {
                new
                {
                    title = $"{prefix}Sincronização FTP Finalizada",
                    description = $"Tarefa **{task.Name}** executada no servidor **{profile.Name}** com status **{statusStr}**.",
                    color = color,
                    fields = new[]
                    {
                        new { name = "Status", value = statusStr, @inline = true },
                        new { name = "Processados / Falhas", value = $"{result.ProcessedFiles} / {result.FailedFiles}", @inline = true },
                        new { name = "Dados Transferidos", value = sizeStr, @inline = true },
                        new { name = "Duração", value = durationStr, @inline = true },
                        new { name = "Host", value = profile.Host, @inline = true },
                        new { name = "Destino Remoto", value = task.TargetRemotePath, @inline = true }
                    },
                    timestamp = DateTime.UtcNow.ToString("O")
                }
            }
        };

        return JsonSerializer.Serialize(embed);
    }

    private static string BuildSlackPayload(
        string prefix,
        string statusStr,
        string sizeStr,
        string durationStr,
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry result)
    {
        string color = result.Status switch
        {
            FtpExecutionStatus.Success => "#2eb886",
            FtpExecutionStatus.Warning => "#daa038",
            _ => "#a30200"
        };

        var slack = new
        {
            text = $"{prefix}FileSync: Tarefa '{task.Name}' finalizada com status *{statusStr}*",
            attachments = new[]
            {
                new
                {
                    color = color,
                    title = "Detalhes da Execução",
                    fields = new[]
                    {
                        new { title = "Tarefa", value = task.Name, @short = true },
                        new { title = "Conexão", value = $"{profile.Name} ({profile.Host})", @short = true },
                        new { title = "Status", value = statusStr, @short = true },
                        new { title = "Arquivos", value = $"{result.ProcessedFiles} processados / {result.FailedFiles} falhas", @short = true },
                        new { title = "Transferido", value = sizeStr, @short = true },
                        new { title = "Duração", value = durationStr, @short = true }
                    },
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            }
        };

        return JsonSerializer.Serialize(slack);
    }

    private static string BuildTeamsPayload(
        string prefix,
        string statusStr,
        string sizeStr,
        string durationStr,
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry result)
    {
        string themeColor = result.Status switch
        {
            FtpExecutionStatus.Success => "2ecc71",
            FtpExecutionStatus.Warning => "e67e22",
            _ => "e74c3c"
        };

        var teams = new
        {
            type = "MessageCard",
            context = "http://schema.org/extensions",
            themeColor = themeColor,
            summary = $"{prefix}Sincronização FTP: {task.Name}",
            sections = new[]
            {
                new
                {
                    activityTitle = $"{prefix}FileSync: Sincronização FTP Finalizada",
                    activitySubtitle = $"Tarefa: {task.Name} | Servidor: {profile.Name}",
                    facts = new[]
                    {
                        new { name = "Status", value = statusStr },
                        new { name = "Arquivos Processados", value = result.ProcessedFiles.ToString() },
                        new { name = "Falhas", value = result.FailedFiles.ToString() },
                        new { name = "Volume Transferido", value = sizeStr },
                        new { name = "Duração", value = durationStr },
                        new { name = "Destino", value = task.TargetRemotePath }
                    },
                    markdown = true
                }
            }
        };

        return JsonSerializer.Serialize(teams);
    }

    private static string BuildGenericJsonPayload(
        bool isTest,
        string statusStr,
        string sizeStr,
        string durationStr,
        FtpSyncTask task,
        FtpConnectionProfile profile,
        FtpExecutionHistoryEntry result)
    {
        var generic = new
        {
            @event = isTest ? "test_webhook" : "ftp_sync_completed",
            timestamp = DateTime.UtcNow.ToString("O"),
            task = new
            {
                id = task.Id,
                name = task.Name,
                sourcePath = task.SourceLocalPath,
                targetPath = task.TargetRemotePath
            },
            connection = new
            {
                id = profile.Id,
                name = profile.Name,
                host = profile.Host,
                protocol = profile.Protocol.ToString()
            },
            execution = new
            {
                status = statusStr,
                totalFiles = result.TotalFiles,
                processedFiles = result.ProcessedFiles,
                failedFiles = result.FailedFiles,
                bytesTransferred = result.TotalBytesTransferred,
                durationSeconds = result.Duration.TotalSeconds,
                errorMessage = result.ErrorMessage
            }
        };

        return JsonSerializer.Serialize(generic, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
