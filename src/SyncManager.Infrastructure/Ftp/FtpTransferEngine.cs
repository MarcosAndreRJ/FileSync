using System.Diagnostics;
using FluentFTP;
using FluentFTP.Exceptions;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public sealed class FtpTransferEngine : IFtpTransferEngine
{
    private readonly IFluentFtpClientFactory _clientFactory;
    private readonly IFtpHistoryRepository _historyRepository;
    private readonly IFtpTaskRepository _taskRepository;
    private readonly IFtpConnectionRepository _connectionRepository;
    private readonly IFtpNotificationService? _notificationService;

    public FtpTransferEngine(
        IFluentFtpClientFactory clientFactory,
        IFtpHistoryRepository historyRepository,
        IFtpTaskRepository taskRepository,
        IFtpConnectionRepository connectionRepository,
        IFtpNotificationService? notificationService = null)
    {
        _clientFactory = clientFactory;
        _historyRepository = historyRepository;
        _taskRepository = taskRepository;
        _connectionRepository = connectionRepository;
        _notificationService = notificationService;
    }

    public async Task<FtpExecutionHistoryEntry> ExecuteTransferAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        IReadOnlyList<FtpChecklistItem> approvedItems,
        IProgress<FtpTransferProgressInfo>? progress = null,
        IProgress<FtpProtocolLogEntry>? logProgress = null,
        CancellationToken ct = default)
    {
        var itemsToProcess = approvedItems
            .Where(i => i.IsSelected && i.ProposedAction != FtpPlannedAction.Keep && i.ProposedAction != FtpPlannedAction.IgnoreManual)
            .ToList();

        var totalFiles = itemsToProcess.Count;
        var totalBytes = itemsToProcess
            .Where(i => i.ProposedAction is FtpPlannedAction.UploadNew or FtpPlannedAction.UpdateExisting)
            .Sum(i => i.FileSizeBytes);

        var progressInfo = new FtpTransferProgressInfo
        {
            TotalFiles = totalFiles,
            PendingFiles = totalFiles,
            TotalBytes = totalBytes
        };
        progress?.Report(progressInfo);

        var affectedFiles = new List<FtpExecutionFileRecord>();
        var totalSw = Stopwatch.StartNew();
        var rateSw = Stopwatch.StartNew();

        long transferredBytes = 0;
        int completedFiles = 0;
        int failedFiles = 0;

        AsyncFtpClient? client = null;
        string? globalErrorMessage = null;
        string? maintenancePath = null;

        try
        {
            client = await _clientFactory.CreateClientAsync(
                profile,
                null,
                logEntry =>
                {
                    logProgress?.Report(new FtpProtocolLogEntry
                    {
                        Timestamp = DateTime.Now,
                        IsCommand = logEntry.Message.StartsWith(">") || logEntry.Message.StartsWith("Command:"),
                        Message = logEntry.Message
                    });
                },
                ct);

            await client.Connect(ct);

            var remoteBase = NormalizeFtpPath(task.TargetRemotePath);

            // 1. Ativar Modo de Manutencao se configurado
            if (task.EnableMaintenanceMode && !string.IsNullOrWhiteSpace(task.MaintenanceFileName))
            {
                try
                {
                    maintenancePath = remoteBase + "/" + task.MaintenanceFileName.TrimStart('/');
                    logProgress?.Report(new FtpProtocolLogEntry
                    {
                        Timestamp = DateTime.Now,
                        IsCommand = true,
                        Message = $"Ativando modo de manutencao criando '{maintenancePath}'..."
                    });
                    var mntBytes = System.Text.Encoding.UTF8.GetBytes($"Maintenance mode enabled by FileSync at {DateTime.UtcNow:O}");
                    await client.UploadBytes(mntBytes, maintenancePath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
                }
                catch (Exception ex)
                {
                    logProgress?.Report(new FtpProtocolLogEntry
                    {
                        Timestamp = DateTime.Now,
                        IsCommand = false,
                        Message = $"Aviso: Nao foi possivel ativar modo de manutencao: {ex.Message}"
                    });
                }
            }

            foreach (var item in itemsToProcess)
            {
                ct.ThrowIfCancellationRequested();

                var fileRecord = new FtpExecutionFileRecord
                {
                    RelativePath = item.RelativePath,
                    Action = item.ProposedAction,
                    Bytes = item.FileSizeBytes
                };

                var itemSw = Stopwatch.StartNew();
                try
                {
                    var remoteFilePath = remoteBase + "/" + item.RelativePath.TrimStart('/');

                    progressInfo.CurrentFileRelativePath = item.RelativePath;
                    progressInfo.CurrentFileTotalBytes = item.FileSizeBytes;
                    progressInfo.CurrentFileTransferredBytes = 0;
                    progressInfo.CurrentOperationDescription = item.ProposedAction switch
                    {
                        FtpPlannedAction.UploadNew => $"Enviando novo arquivo: {item.RelativePath}",
                        FtpPlannedAction.UpdateExisting => $"Atualizando arquivo: {item.RelativePath}",
                        FtpPlannedAction.DeleteRemote => $"Excluindo arquivo remoto: {item.RelativePath}",
                        _ => "Processando..."
                    };
                    progress?.Report(progressInfo);

                    if (item.ProposedAction is FtpPlannedAction.UploadNew or FtpPlannedAction.UpdateExisting)
                    {
                        var localFilePath = Path.Combine(task.SourceLocalPath, item.RelativePath.Replace('/', '\\'));
                        if (!File.Exists(localFilePath))
                        {
                            throw new FileNotFoundException($"Arquivo local de origem nao encontrado: {localFilePath}");
                        }

                        // Backup antes de sobrescrever se configurado
                        if (task.BackupBeforeOverwrite && item.ProposedAction == FtpPlannedAction.UpdateExisting)
                        {
                            await CreateRemoteBackupAsync(client, remoteFilePath, ct);
                        }

                        long fileBytesSoFar = 0;
                        var ftpProgress = new Progress<FtpProgress>(p =>
                        {
                            var delta = p.TransferredBytes - fileBytesSoFar;
                            if (delta > 0)
                            {
                                Interlocked.Add(ref transferredBytes, delta);
                                fileBytesSoFar = p.TransferredBytes;
                            }

                            progressInfo.CurrentFileTransferredBytes = p.TransferredBytes;
                            progressInfo.TransferredBytes = transferredBytes;

                            // Atualizar taxa e tempo estimado a cada 250ms
                            if (rateSw.ElapsedMilliseconds >= 250)
                            {
                                var totalSec = totalSw.Elapsed.TotalSeconds;
                                if (totalSec > 0)
                                {
                                    var rate = transferredBytes / totalSec;
                                    progressInfo.TransferRateBytesPerSec = rate;
                                    var remainingBytes = Math.Max(0, totalBytes - transferredBytes);
                                    if (rate > 0)
                                    {
                                        progressInfo.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / rate);
                                    }
                                }
                                rateSw.Restart();
                            }

                            progress?.Report(progressInfo);
                        });

                    // Se Upload Atomico (Safe Deploy) estiver ativado, envia com extensao temporaria e depois renomeia
                    string? tempRemotePathToClean = null;
                    try
                    {
                        if (task.UseAtomicUpload)
                        {
                            var tempRemotePath = remoteFilePath + ".ftptmp";
                            tempRemotePathToClean = tempRemotePath;

                            var tempStatus = await client.UploadFile(
                                localFilePath,
                                tempRemotePath,
                                FtpRemoteExists.Overwrite,
                                createRemoteDir: true,
                                FtpVerify.None,
                                ftpProgress,
                                ct);

                            if (tempStatus == FtpStatus.Failed)
                            {
                                throw new IOException($"Falha no upload atomico temporario para '{item.RelativePath}'");
                            }

                            // Renomear arquivo temporario para o destino final
                            if (await client.FileExists(remoteFilePath, ct))
                            {
                                await client.DeleteFile(remoteFilePath, ct);
                            }
                            await client.Rename(tempRemotePath, remoteFilePath, ct);
                            tempRemotePathToClean = null;
                        }
                        else
                        {
                            var status = await client.UploadFile(
                                localFilePath,
                                remoteFilePath,
                                FtpRemoteExists.Overwrite,
                                createRemoteDir: true,
                                FtpVerify.None,
                                ftpProgress,
                                ct);

                            if (status == FtpStatus.Failed)
                            {
                                throw new IOException($"Falha reportada pelo FluentFTP ao enviar: {item.RelativePath}");
                            }
                        }

                        // Preservar timestamp se configurado
                        if (profile.PreserveModTime)
                        {
                            try
                            {
                                var localTime = File.GetLastWriteTimeUtc(localFilePath);
                                await client.SetModifiedTime(remoteFilePath, localTime, ct);
                            }
                            catch
                            {
                                // Servidores que nao suportam MFMT/MDTM podem falhar silenciosamente aqui
                            }
                        }

                        completedFiles++;
                        fileRecord.Success = true;
                    }
                    catch
                    {
                        if (tempRemotePathToClean != null && client != null && client.IsConnected)
                        {
                            try
                            {
                                if (await client.FileExists(tempRemotePathToClean, CancellationToken.None))
                                {
                                    await client.DeleteFile(tempRemotePathToClean, CancellationToken.None);
                                }
                            }
                            catch { }
                        }
                        throw;
                    }
                    }
                    else if (item.ProposedAction == FtpPlannedAction.DeleteRemote)
                    {
                        if (task.ProhibitRemoteDeletions)
                        {
                            fileRecord.Success = false;
                            fileRecord.Error = "Exclusão remota bloqueada pelas configurações de segurança da tarefa.";
                            failedFiles++;
                        }
                        else
                        {
                            await client.DeleteFile(remoteFilePath, ct);
                            completedFiles++;
                            fileRecord.Success = true;
                        }
                    }

                    progressInfo.CompletedFiles = completedFiles;
                    progressInfo.FailedFiles = failedFiles;
                    progressInfo.PendingFiles = Math.Max(0, totalFiles - (completedFiles + failedFiles));
                    progress?.Report(progressInfo);
                }
                catch (Exception ex)
                {
                    failedFiles++;
                    fileRecord.Success = false;
                    fileRecord.Error = ex.Message;

                    progressInfo.FailedFiles = failedFiles;
                    progressInfo.PendingFiles = Math.Max(0, totalFiles - (completedFiles + failedFiles));
                    progress?.Report(progressInfo);

                    logProgress?.Report(new FtpProtocolLogEntry
                    {
                        Timestamp = DateTime.Now,
                        IsCommand = false,
                        Message = $"ERRO ao processar '{item.RelativePath}': {ex.Message}"
                    });
                }
                finally
                {
                    itemSw.Stop();
                    affectedFiles.Add(fileRecord);
                }
            }
        }
        catch (Exception ex)
        {
            globalErrorMessage = ex.Message;
            logProgress?.Report(new FtpProtocolLogEntry
            {
                Timestamp = DateTime.Now,
                IsCommand = false,
                Message = $"FALHA CRÍTICA NA TRANSFERÊNCIA: {ex.Message}"
            });
        }
        finally
        {
            totalSw.Stop();
            try
            {
                if (maintenancePath != null && client != null && client.IsConnected)
                {
                    try
                    {
                        logProgress?.Report(new FtpProtocolLogEntry
                        {
                            Timestamp = DateTime.Now,
                            IsCommand = true,
                            Message = $"Desativando modo de manutenção removendo '{maintenancePath}'..."
                        });
                        if (await client.FileExists(maintenancePath, CancellationToken.None))
                        {
                            await client.DeleteFile(maintenancePath, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        logProgress?.Report(new FtpProtocolLogEntry
                        {
                            Timestamp = DateTime.Now,
                            IsCommand = false,
                            Message = $"Aviso ao desativar modo de manutenção: {ex.Message}"
                        });
                    }
                }

                if (client != null && client.IsConnected)
                {
                    await client.Disconnect();
                }
                client?.Dispose();
            }
            catch { }
        }

        // Determinar status final da execução
        FtpExecutionStatus finalStatus;
        if (globalErrorMessage != null)
        {
            finalStatus = FtpExecutionStatus.Failed;
        }
        else if (failedFiles == 0)
        {
            finalStatus = FtpExecutionStatus.Success;
        }
        else if (completedFiles > 0)
        {
            finalStatus = FtpExecutionStatus.Warning;
        }
        else
        {
            finalStatus = FtpExecutionStatus.Failed;
        }

        var historyEntry = new FtpExecutionHistoryEntry
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            TaskName = task.Name,
            ConnectionName = profile.Name,
            ExecutedAt = DateTime.UtcNow,
            Status = finalStatus,
            TotalFiles = totalFiles,
            ProcessedFiles = completedFiles,
            FailedFiles = failedFiles,
            TotalBytesTransferred = transferredBytes,
            Duration = totalSw.Elapsed,
            ErrorMessage = globalErrorMessage,
            FilesAffected = affectedFiles
        };

        // Salvar histórico no banco
        try
        {
            await _historyRepository.AddAsync(historyEntry, default);

            // Atualizar data de última execução da tarefa e conexão
            task.LastExecutedAt = DateTime.UtcNow;
            await _taskRepository.SaveAsync(task, default);

            profile.LastUsedAt = DateTime.UtcNow;
            await _connectionRepository.SaveAsync(profile, default);
        }
        catch (Exception ex)
        {
            logProgress?.Report(new FtpProtocolLogEntry
            {
                Timestamp = DateTime.Now,
                IsCommand = false,
                Message = $"Aviso ao persistir histórico: {ex.Message}"
            });
        }

        // Enviar notificação de webhook se configurado
        if (_notificationService != null && (task.NotifyOnCompletion || !string.IsNullOrWhiteSpace(task.WebhookUrl) || !string.IsNullOrWhiteSpace(profile.DefaultWebhookUrl)))
        {
            try
            {
                logProgress?.Report(new FtpProtocolLogEntry
                {
                    Timestamp = DateTime.Now,
                    IsCommand = false,
                    Message = "Enviando notificação de webhook..."
                });
                await _notificationService.SendNotificationAsync(task, profile, historyEntry, default);
                logProgress?.Report(new FtpProtocolLogEntry
                {
                    Timestamp = DateTime.Now,
                    IsCommand = false,
                    Message = "Notificação de webhook enviada com sucesso."
                });
            }
            catch (Exception ex)
            {
                logProgress?.Report(new FtpProtocolLogEntry
                {
                    Timestamp = DateTime.Now,
                    IsCommand = false,
                    Message = $"Aviso ao enviar webhook: {ex.Message}"
                });
            }
        }

        progressInfo.CompletedFiles = completedFiles;
        progressInfo.FailedFiles = failedFiles;
        progressInfo.PendingFiles = 0;
        progressInfo.CurrentOperationDescription = finalStatus switch
        {
            FtpExecutionStatus.Success => "Transferência concluída com sucesso!",
            FtpExecutionStatus.Warning => $"Transferência concluída com {failedFiles} falha(s).",
            _ => "Transferência finalizada com erro."
        };
        progress?.Report(progressInfo);

        return historyEntry;
    }

    private static async Task CreateRemoteBackupAsync(AsyncFtpClient client, string remoteFilePath, CancellationToken ct)
    {
        try
        {
            if (await client.FileExists(remoteFilePath, ct))
            {
                var dir = GetParentDirectory(remoteFilePath);
                var filename = Path.GetFileName(remoteFilePath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var backupDir = $"{dir}/_backups";
                var backupPath = $"{backupDir}/{filename}.{timestamp}.bak";

                if (!await client.DirectoryExists(backupDir, ct))
                {
                    await client.CreateDirectory(backupDir, ct);
                }

                await client.Rename(remoteFilePath, backupPath, ct);
            }
        }
        catch
        {
            // Não abortar sincronização caso haja restrição ao criar diretório de backup
        }
    }

    private static string NormalizeFtpPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized.TrimEnd('/');
    }

    private static string GetParentDirectory(string ftpPath)
    {
        var idx = ftpPath.LastIndexOf('/');
        if (idx <= 0) return "/";
        return ftpPath[..idx];
    }
}
