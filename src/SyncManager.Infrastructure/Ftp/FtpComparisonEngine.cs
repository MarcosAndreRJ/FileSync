using FluentFTP;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public sealed class FtpComparisonEngine : IFtpComparisonEngine
{
    private readonly IFluentFtpClientFactory _clientFactory;

    public FtpComparisonEngine(IFluentFtpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<IReadOnlyList<FtpChecklistItem>> CompareAsync(
        FtpSyncTask task,
        FtpConnectionProfile profile,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        statusProgress?.Report("Iniciando comparação e varredura de arquivos...");

        if (!Directory.Exists(task.SourceLocalPath))
        {
            throw new DirectoryNotFoundException($"Diretório de origem local não encontrado: {task.SourceLocalPath}");
        }

        var allExclusions = FtpFrameworkPresetHelper.ResolveAllExclusions(
            task.ExclusionPatterns,
            task.FrameworkPreset,
            task.SourceLocalPath);

        var pathFilter = allExclusions.Count > 0
            ? new PathFilter(allExclusions)
            : PathFilter.Empty;

        // 1. Escanear arquivos locais respeitando o escopo persistente e exclusões
        statusProgress?.Report("Escaneando arquivos locais...");
        var localFiles = ScanLocalFiles(task.SourceLocalPath, task.IncludedRelativePaths, pathFilter);

        // 2. Conectar ao FTP e escanear arquivos remotos
        statusProgress?.Report($"Conectando ao servidor FTP {profile.Host}:{profile.Port}...");
        var remoteFiles = new Dictionary<string, FtpListItem>(StringComparer.OrdinalIgnoreCase);

        var client = await _clientFactory.CreateClientAsync(profile, null, null, ct);
        try
        {
            await client.Connect(ct);
            statusProgress?.Report("Listando diretório remoto no servidor...");

            var remoteBase = NormalizeFtpPath(task.TargetRemotePath);
            if (await client.DirectoryExists(remoteBase, ct))
            {
                var listing = await client.GetListing(remoteBase, FtpListOption.Recursive | FtpListOption.Auto, ct);
                foreach (var item in listing)
                {
                    if (item.Type != FtpObjectType.File)
                        continue;

                    var rel = GetRelativeRemotePath(remoteBase, item.FullName);
                    if (!string.IsNullOrEmpty(rel))
                    {
                        remoteFiles[rel] = item;
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.Disconnect();
                client.Dispose();
            }
            catch { }
        }

        statusProgress?.Report("Comparando diferenças entre local e remoto...");

        // 3. Cruzar informações e montar o Checklist de Ações
        var checklist = new List<FtpChecklistItem>();
        var seenRemotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var remoteBasePath = NormalizeFtpPath(task.TargetRemotePath);

        foreach (var local in localFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = local.Key;
            var localInfo = local.Value;
            var localFullPath = localInfo.FullName;
            var remoteFullPath = remoteBasePath + "/" + relPath;

            if (remoteFiles.TryGetValue(relPath, out var remoteItem))
            {
                seenRemotePaths.Add(relPath);

                var localSize = localInfo.Length;
                var remoteSize = remoteItem.Size;
                var localTime = localInfo.LastWriteTimeUtc;
                var remoteTime = remoteItem.Modified != DateTime.MinValue ? remoteItem.Modified.ToUniversalTime() : (DateTime?)null;

                bool isSizeEqual = localSize == remoteSize;
                bool isTimeEqual = remoteTime.HasValue && Math.Abs((localTime - remoteTime.Value).TotalSeconds) <= 2;

                if (isSizeEqual && isTimeEqual)
                {
                    checklist.Add(new FtpChecklistItem
                    {
                        RelativePath = relPath,
                        LocalFullPath = localFullPath,
                        RemoteFullPath = remoteFullPath,
                        ProposedAction = FtpPlannedAction.Keep,
                        StatusReason = "Arquivo idêntico (tamanho e data conferem)",
                        FileSizeBytes = localSize,
                        LocalModifiedTime = localTime,
                        RemoteModifiedTime = remoteTime,
                        IsSelected = false
                    });
                }
                else
                {
                    string desc;
                    if (!isSizeEqual && isTimeEqual)
                        desc = $"Tamanho diferente (Local: {FormatBytes(localSize)}, Remoto: {FormatBytes(remoteSize)})";
                    else if (isSizeEqual && !isTimeEqual)
                        desc = $"Data diferente (Local: {localTime:g}, Remoto: {remoteTime:g})";
                    else
                        desc = "Tamanho e data diferentes";

                    checklist.Add(new FtpChecklistItem
                    {
                        RelativePath = relPath,
                        LocalFullPath = localFullPath,
                        RemoteFullPath = remoteFullPath,
                        ProposedAction = FtpPlannedAction.UpdateExisting,
                        StatusReason = desc,
                        FileSizeBytes = localSize,
                        LocalModifiedTime = localTime,
                        RemoteModifiedTime = remoteTime,
                        IsSelected = true
                    });
                }
            }
            else
            {
                checklist.Add(new FtpChecklistItem
                {
                    RelativePath = relPath,
                    LocalFullPath = localFullPath,
                    RemoteFullPath = remoteFullPath,
                    ProposedAction = FtpPlannedAction.UploadNew,
                    StatusReason = "Novo arquivo local (não existe no destino)",
                    FileSizeBytes = localInfo.Length,
                    LocalModifiedTime = localInfo.LastWriteTimeUtc,
                    RemoteModifiedTime = null,
                    IsSelected = true
                });
            }
        }

        // Se o método for MirrorSource, verificar arquivos remotos inexistentes localmente
        if (task.Method == FtpSyncMethod.MirrorSource)
        {
            foreach (var remote in remoteFiles)
            {
                if (seenRemotePaths.Contains(remote.Key))
                    continue;

                if (task.IncludedRelativePaths.Count > 0 && !IsInScope(remote.Key, task.IncludedRelativePaths))
                    continue;

                if (pathFilter.IsExcluded(remote.Key.Replace('/', '\\'), false))
                    continue;

                var remoteFullPath = remoteBasePath + "/" + remote.Key;
                var remoteTime = remote.Value.Modified != DateTime.MinValue ? remote.Value.Modified.ToUniversalTime() : (DateTime?)null;

                if (task.ProhibitRemoteDeletions)
                {
                    checklist.Add(new FtpChecklistItem
                    {
                        RelativePath = remote.Key,
                        LocalFullPath = string.Empty,
                        RemoteFullPath = remoteFullPath,
                        ProposedAction = FtpPlannedAction.Keep,
                        StatusReason = "Arquivo órfão no servidor (exclusão remota BLOQUEADA por segurança)",
                        FileSizeBytes = remote.Value.Size,
                        LocalModifiedTime = DateTime.MinValue,
                        RemoteModifiedTime = remoteTime,
                        IsSelected = false
                    });
                }
                else
                {
                    checklist.Add(new FtpChecklistItem
                    {
                        RelativePath = remote.Key,
                        LocalFullPath = string.Empty,
                        RemoteFullPath = remoteFullPath,
                        ProposedAction = FtpPlannedAction.DeleteRemote,
                        StatusReason = "Arquivo órfão no servidor (espelhamento)",
                        FileSizeBytes = remote.Value.Size,
                        LocalModifiedTime = DateTime.MinValue,
                        RemoteModifiedTime = remoteTime,
                        IsSelected = false // Por precaução, exclusões iniciam desmarcadas
                    });
                }
            }
        }

        statusProgress?.Report($"Comparação concluída: {checklist.Count} itens analisados.");
        return checklist.OrderBy(i => i.ProposedAction switch
        {
            FtpPlannedAction.UploadNew => 1,
            FtpPlannedAction.UpdateExisting => 2,
            FtpPlannedAction.DeleteRemote => 3,
            _ => 4
        }).ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, FileInfo> ScanLocalFiles(
        string rootDir,
        IReadOnlyList<string> includedPaths,
        PathFilter filter)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        if (includedPaths.Count > 0)
        {
            foreach (var inc in includedPaths)
            {
                var fullPath = Path.Combine(rootDir, inc.Replace('/', '\\'));
                if (File.Exists(fullPath))
                {
                    var rel = inc.Replace('\\', '/').TrimStart('/');
                    if (!filter.IsExcluded(rel.Replace('/', '\\'), false))
                    {
                        result[rel] = new FileInfo(fullPath);
                    }
                }
                else if (Directory.Exists(fullPath))
                {
                    ScanDirectoryRecursive(rootDir, fullPath, filter, result);
                }
            }
        }
        else
        {
            ScanDirectoryRecursive(rootDir, rootDir, filter, result);
        }

        return result;
    }

    private static void ScanDirectoryRecursive(
        string rootDir,
        string currentDir,
        PathFilter filter,
        Dictionary<string, FileInfo> result)
    {
        var dirInfo = new DirectoryInfo(currentDir);
        foreach (var file in dirInfo.GetFiles())
        {
            var relPath = Path.GetRelativePath(rootDir, file.FullName).Replace('\\', '/');
            if (!filter.IsExcluded(relPath.Replace('/', '\\'), false))
            {
                result[relPath] = file;
            }
        }

        foreach (var sub in dirInfo.GetDirectories())
        {
            var relDir = Path.GetRelativePath(rootDir, sub.FullName).Replace('\\', '/');
            if (!filter.IsExcluded(relDir.Replace('/', '\\'), true))
            {
                ScanDirectoryRecursive(rootDir, sub.FullName, filter, result);
            }
        }
    }

    private static bool IsInScope(string relativePath, IReadOnlyList<string> includedPaths)
    {
        foreach (var inc in includedPaths)
        {
            var cleanInc = inc.Replace('\\', '/').Trim('/');
            if (relativePath.Equals(cleanInc, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(cleanInc + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeFtpPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized.TrimEnd('/');
    }

    private static string GetRelativeRemotePath(string baseRemotePath, string fullRemotePath)
    {
        var b = NormalizeFtpPath(baseRemotePath);
        var f = NormalizeFtpPath(fullRemotePath);

        if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase))
        {
            var rel = f[b.Length..].TrimStart('/');
            return rel;
        }

        return f.TrimStart('/');
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {suffixes[i]}";
    }
}
