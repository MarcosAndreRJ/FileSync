using System.Text.Json;
using Microsoft.Data.Sqlite;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Persistence;

public sealed class FtpConnectionRepository : IFtpConnectionRepository
{
    private readonly Database _db;

    public FtpConnectionRepository(Database db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FtpConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Host, Port, Protocol, Username, CredentialIdentifier,
                   TransferMode, RequireTls12OrHigher, CertificatePinning, ExpectedCertificateThumbprint,
                   DefaultLocalPath, DefaultRemotePath, MaxParallelConnections, TimeoutSeconds,
                   PreserveModTime, SendKeepAlive, CreatedAt, LastUsedAt,
                   DefaultWebhookUrl, DefaultWebhookType
            FROM FtpConnections
            ORDER BY Name COLLATE NOCASE ASC;
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FtpConnectionProfile>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapConnection(reader));
        }
        return list;
    }

    public async Task<FtpConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Host, Port, Protocol, Username, CredentialIdentifier,
                   TransferMode, RequireTls12OrHigher, CertificatePinning, ExpectedCertificateThumbprint,
                   DefaultLocalPath, DefaultRemotePath, MaxParallelConnections, TimeoutSeconds,
                   PreserveModTime, SendKeepAlive, CreatedAt, LastUsedAt,
                   DefaultWebhookUrl, DefaultWebhookType
            FROM FtpConnections
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapConnection(reader);
        }
        return null;
    }

    public async Task<FtpConnectionProfile> SaveAsync(FtpConnectionProfile profile, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FtpConnections(
                Id, Name, Host, Port, Protocol, Username, CredentialIdentifier,
                TransferMode, RequireTls12OrHigher, CertificatePinning, ExpectedCertificateThumbprint,
                DefaultLocalPath, DefaultRemotePath, MaxParallelConnections, TimeoutSeconds,
                PreserveModTime, SendKeepAlive, CreatedAt, LastUsedAt,
                DefaultWebhookUrl, DefaultWebhookType)
            VALUES(
                $id, $name, $host, $port, $protocol, $username, $credId,
                $transferMode, $tls, $pinning, $thumbprint,
                $localPath, $remotePath, $parallel, $timeout,
                $modTime, $keepAlive, $created, $lastUsed,
                $webhookUrl, $webhookType)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Host = excluded.Host,
                Port = excluded.Port,
                Protocol = excluded.Protocol,
                Username = excluded.Username,
                CredentialIdentifier = excluded.CredentialIdentifier,
                TransferMode = excluded.TransferMode,
                RequireTls12OrHigher = excluded.RequireTls12OrHigher,
                CertificatePinning = excluded.CertificatePinning,
                ExpectedCertificateThumbprint = excluded.ExpectedCertificateThumbprint,
                DefaultLocalPath = excluded.DefaultLocalPath,
                DefaultRemotePath = excluded.DefaultRemotePath,
                MaxParallelConnections = excluded.MaxParallelConnections,
                TimeoutSeconds = excluded.TimeoutSeconds,
                PreserveModTime = excluded.PreserveModTime,
                SendKeepAlive = excluded.SendKeepAlive,
                LastUsedAt = excluded.LastUsedAt,
                DefaultWebhookUrl = excluded.DefaultWebhookUrl,
                DefaultWebhookType = excluded.DefaultWebhookType;
            """;

        cmd.Parameters.AddWithValue("$id", profile.Id.ToString());
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$host", profile.Host);
        cmd.Parameters.AddWithValue("$port", profile.Port);
        cmd.Parameters.AddWithValue("$protocol", (int)profile.Protocol);
        cmd.Parameters.AddWithValue("$username", profile.Username);
        cmd.Parameters.AddWithValue("$credId", profile.CredentialIdentifier);
        cmd.Parameters.AddWithValue("$transferMode", (int)profile.TransferMode);
        cmd.Parameters.AddWithValue("$tls", profile.RequireTls12OrHigher ? 1 : 0);
        cmd.Parameters.AddWithValue("$pinning", profile.CertificatePinning ? 1 : 0);
        cmd.Parameters.AddWithValue("$thumbprint", (object?)profile.ExpectedCertificateThumbprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$localPath", profile.DefaultLocalPath);
        cmd.Parameters.AddWithValue("$remotePath", profile.DefaultRemotePath);
        cmd.Parameters.AddWithValue("$parallel", profile.MaxParallelConnections);
        cmd.Parameters.AddWithValue("$timeout", profile.TimeoutSeconds);
        cmd.Parameters.AddWithValue("$modTime", profile.PreserveModTime ? 1 : 0);
        cmd.Parameters.AddWithValue("$keepAlive", profile.SendKeepAlive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lastUsed", profile.LastUsedAt.HasValue ? profile.LastUsedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$webhookUrl", (object?)profile.DefaultWebhookUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$webhookType", (int)profile.DefaultWebhookType);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return profile;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            throw new DuplicateFtpConnectionNameException(profile.Name);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FtpConnections WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> NameExistsAsync(string name, Guid excludingId, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM FtpConnections WHERE Name = $name COLLATE NOCASE AND Id <> $id;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", excludingId.ToString());
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static FtpConnectionProfile MapConnection(SqliteDataReader r)
    {
        return new FtpConnectionProfile
        {
            Id = Guid.Parse(r.GetString(0)),
            Name = r.GetString(1),
            Host = r.GetString(2),
            Port = r.GetInt32(3),
            Protocol = (FtpProtocol)r.GetInt32(4),
            Username = r.GetString(5),
            CredentialIdentifier = r.GetString(6),
            TransferMode = (FtpTransferMode)r.GetInt32(7),
            RequireTls12OrHigher = r.GetInt32(8) == 1,
            CertificatePinning = r.GetInt32(9) == 1,
            ExpectedCertificateThumbprint = r.IsDBNull(10) ? null : r.GetString(10),
            DefaultLocalPath = r.GetString(11),
            DefaultRemotePath = r.GetString(12),
            MaxParallelConnections = r.GetInt32(13),
            TimeoutSeconds = r.GetInt32(14),
            PreserveModTime = r.GetInt32(15) == 1,
            SendKeepAlive = r.GetInt32(16) == 1,
            CreatedAt = DateTime.Parse(r.GetString(17)),
            LastUsedAt = r.IsDBNull(18) ? null : DateTime.Parse(r.GetString(18)),
            DefaultWebhookUrl = r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetString(19) : null,
            DefaultWebhookType = r.FieldCount > 20 && !r.IsDBNull(20) ? (FtpWebhookType)r.GetInt32(20) : FtpWebhookType.GenericJson
        };
    }
}

public sealed class FtpTaskRepository : IFtpTaskRepository
{
    private readonly Database _db;

    public FtpTaskRepository(Database db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FtpSyncTask>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Description, ConnectionProfileId, SourceLocalPath, TargetRemotePath,
                   Method, IncludedPathsJson, ExclusionPatternsJson, RequireChecklistApproval,
                   BackupBeforeOverwrite, ProhibitRemoteDeletions, CreatedAt, LastExecutedAt,
                   UseAtomicUpload, WebhookUrl, WebhookType, NotifyOnCompletion, FrameworkPreset,
                   EnableMaintenanceMode, MaintenanceFileName,
                   ExcludedPathsJson, ComparisonStrategy, TimestampToleranceSeconds, MaxRemoteDeletePercentage,
                   RequireExplicitChecklistConfirmation
            FROM FtpTasks
            ORDER BY Name COLLATE NOCASE ASC;
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FtpSyncTask>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapTask(reader));
        }
        return list;
    }

    public async Task<FtpSyncTask?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Description, ConnectionProfileId, SourceLocalPath, TargetRemotePath,
                   Method, IncludedPathsJson, ExclusionPatternsJson, RequireChecklistApproval,
                   BackupBeforeOverwrite, ProhibitRemoteDeletions, CreatedAt, LastExecutedAt,
                   UseAtomicUpload, WebhookUrl, WebhookType, NotifyOnCompletion, FrameworkPreset,
                   EnableMaintenanceMode, MaintenanceFileName,
                   ExcludedPathsJson, ComparisonStrategy, TimestampToleranceSeconds, MaxRemoteDeletePercentage,
                   RequireExplicitChecklistConfirmation
            FROM FtpTasks
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapTask(reader);
        }
        return null;
    }

    public async Task<IReadOnlyList<FtpSyncTask>> GetByConnectionIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Description, ConnectionProfileId, SourceLocalPath, TargetRemotePath,
                   Method, IncludedPathsJson, ExclusionPatternsJson, RequireChecklistApproval,
                   BackupBeforeOverwrite, ProhibitRemoteDeletions, CreatedAt, LastExecutedAt,
                   UseAtomicUpload, WebhookUrl, WebhookType, NotifyOnCompletion, FrameworkPreset,
                   EnableMaintenanceMode, MaintenanceFileName,
                   ExcludedPathsJson, ComparisonStrategy, TimestampToleranceSeconds, MaxRemoteDeletePercentage,
                   RequireExplicitChecklistConfirmation
            FROM FtpTasks
            WHERE ConnectionProfileId = $connId
            ORDER BY Name COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("$connId", connectionId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FtpSyncTask>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapTask(reader));
        }
        return list;
    }

    public async Task<FtpSyncTask> SaveAsync(FtpSyncTask task, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FtpTasks(
                Id, Name, Description, ConnectionProfileId, SourceLocalPath, TargetRemotePath,
                Method, IncludedPathsJson, ExclusionPatternsJson, RequireChecklistApproval,
                BackupBeforeOverwrite, ProhibitRemoteDeletions, CreatedAt, LastExecutedAt,
                UseAtomicUpload, WebhookUrl, WebhookType, NotifyOnCompletion, FrameworkPreset,
                EnableMaintenanceMode, MaintenanceFileName,
                ExcludedPathsJson, ComparisonStrategy, TimestampToleranceSeconds, MaxRemoteDeletePercentage,
                RequireExplicitChecklistConfirmation)
            VALUES(
                $id, $name, $desc, $connId, $sourcePath, $targetPath,
                $method, $included, $exclusions, $checklist,
                $backup, $prohibitDelete, $created, $lastExecuted,
                $atomic, $webhookUrl, $webhookType, $notify, $framework,
                $maintenance, $maintFile,
                $excluded, $strategy, $tolerance, $maxDelete, $explicitConfirm)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Description = excluded.Description,
                ConnectionProfileId = excluded.ConnectionProfileId,
                SourceLocalPath = excluded.SourceLocalPath,
                TargetRemotePath = excluded.TargetRemotePath,
                Method = excluded.Method,
                IncludedPathsJson = excluded.IncludedPathsJson,
                ExclusionPatternsJson = excluded.ExclusionPatternsJson,
                RequireChecklistApproval = excluded.RequireChecklistApproval,
                BackupBeforeOverwrite = excluded.BackupBeforeOverwrite,
                ProhibitRemoteDeletions = excluded.ProhibitRemoteDeletions,
                LastExecutedAt = excluded.LastExecutedAt,
                UseAtomicUpload = excluded.UseAtomicUpload,
                WebhookUrl = excluded.WebhookUrl,
                WebhookType = excluded.WebhookType,
                NotifyOnCompletion = excluded.NotifyOnCompletion,
                FrameworkPreset = excluded.FrameworkPreset,
                EnableMaintenanceMode = excluded.EnableMaintenanceMode,
                MaintenanceFileName = excluded.MaintenanceFileName,
                ExcludedPathsJson = excluded.ExcludedPathsJson,
                ComparisonStrategy = excluded.ComparisonStrategy,
                TimestampToleranceSeconds = excluded.TimestampToleranceSeconds,
                MaxRemoteDeletePercentage = excluded.MaxRemoteDeletePercentage,
                RequireExplicitChecklistConfirmation = excluded.RequireExplicitChecklistConfirmation;
            """;

        cmd.Parameters.AddWithValue("$id", task.Id.ToString());
        cmd.Parameters.AddWithValue("$name", task.Name);
        cmd.Parameters.AddWithValue("$desc", task.Description);
        cmd.Parameters.AddWithValue("$connId", task.ConnectionProfileId.ToString());
        cmd.Parameters.AddWithValue("$sourcePath", task.SourceLocalPath);
        cmd.Parameters.AddWithValue("$targetPath", task.TargetRemotePath);
        cmd.Parameters.AddWithValue("$method", (int)task.Method);
        cmd.Parameters.AddWithValue("$included", JsonSerializer.Serialize(task.IncludedRelativePaths));
        cmd.Parameters.AddWithValue("$exclusions", JsonSerializer.Serialize(task.ExclusionPatterns));
        cmd.Parameters.AddWithValue("$checklist", task.RequireChecklistApproval ? 1 : 0);
        cmd.Parameters.AddWithValue("$backup", task.BackupBeforeOverwrite ? 1 : 0);
        cmd.Parameters.AddWithValue("$prohibitDelete", task.ProhibitRemoteDeletions ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lastExecuted", task.LastExecutedAt.HasValue ? task.LastExecutedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$atomic", task.UseAtomicUpload ? 1 : 0);
        cmd.Parameters.AddWithValue("$webhookUrl", (object?)task.WebhookUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$webhookType", (int)task.WebhookType);
        cmd.Parameters.AddWithValue("$notify", task.NotifyOnCompletion ? 1 : 0);
        cmd.Parameters.AddWithValue("$framework", (int)task.FrameworkPreset);
        cmd.Parameters.AddWithValue("$maintenance", task.EnableMaintenanceMode ? 1 : 0);
        cmd.Parameters.AddWithValue("$maintFile", (object?)task.MaintenanceFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$excluded", JsonSerializer.Serialize(task.ExcludedRelativePaths));
        cmd.Parameters.AddWithValue("$strategy", (int)task.ComparisonStrategy);
        cmd.Parameters.AddWithValue("$tolerance", task.TimestampToleranceSeconds);
        cmd.Parameters.AddWithValue("$maxDelete", task.MaxRemoteDeletePercentage);
        cmd.Parameters.AddWithValue("$explicitConfirm", task.RequireExplicitChecklistConfirmation ? 1 : 0);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return task;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new DuplicateFtpTaskNameException(task.Name);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FtpTasks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> NameExistsAsync(string name, Guid excludingId, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM FtpTasks WHERE Name = $name COLLATE NOCASE AND Id <> $id;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", excludingId.ToString());
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static FtpSyncTask MapTask(SqliteDataReader r)
    {
        var includedJson = r.GetString(7);
        var exclusionsJson = r.GetString(8);

        return new FtpSyncTask
        {
            Id = Guid.Parse(r.GetString(0)),
            Name = r.GetString(1),
            Description = r.GetString(2),
            ConnectionProfileId = Guid.Parse(r.GetString(3)),
            SourceLocalPath = r.GetString(4),
            TargetRemotePath = r.GetString(5),
            Method = (FtpSyncMethod)r.GetInt32(6),
            IncludedRelativePaths = JsonSerializer.Deserialize<List<string>>(includedJson) ?? [],
            ExclusionPatterns = JsonSerializer.Deserialize<List<string>>(exclusionsJson) ?? [],
            RequireChecklistApproval = r.GetInt32(9) == 1,
            BackupBeforeOverwrite = r.GetInt32(10) == 1,
            ProhibitRemoteDeletions = r.GetInt32(11) == 1,
            CreatedAt = DateTime.Parse(r.GetString(12)),
            LastExecutedAt = r.IsDBNull(13) ? null : DateTime.Parse(r.GetString(13)),
            UseAtomicUpload = r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetInt32(14) == 1 : true,
            WebhookUrl = r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetString(15) : null,
            WebhookType = r.FieldCount > 16 && !r.IsDBNull(16) ? (FtpWebhookType)r.GetInt32(16) : FtpWebhookType.GenericJson,
            NotifyOnCompletion = r.FieldCount > 17 && !r.IsDBNull(17) && r.GetInt32(17) == 1,
            FrameworkPreset = r.FieldCount > 18 && !r.IsDBNull(18) ? (WebFrameworkPreset)r.GetInt32(18) : WebFrameworkPreset.Custom,
            EnableMaintenanceMode = r.FieldCount > 19 && !r.IsDBNull(19) && r.GetInt32(19) == 1,
            MaintenanceFileName = r.FieldCount > 20 && !r.IsDBNull(20) ? r.GetString(20) : ".maintenance",
            ExcludedRelativePaths = r.FieldCount > 21 && !r.IsDBNull(21) ? JsonSerializer.Deserialize<List<string>>(r.GetString(21)) ?? [] : [],
            ComparisonStrategy = r.FieldCount > 22 && !r.IsDBNull(22) ? (FtpComparisonStrategy)r.GetInt32(22) : FtpComparisonStrategy.SizeAndTimestamp,
            TimestampToleranceSeconds = r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetInt32(23) : 2,
            MaxRemoteDeletePercentage = r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetDouble(24) : 0.20,
            RequireExplicitChecklistConfirmation = !(r.FieldCount > 25 && !r.IsDBNull(25)) || r.GetInt32(25) == 1
        };
    }
}

public sealed class FtpHistoryRepository : IFtpHistoryRepository
{
    private readonly Database _db;

    public FtpHistoryRepository(Database db)
    {
        _db = db;
    }

    public async Task AddAsync(FtpExecutionHistoryEntry entry, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FtpHistory(
                Id, TaskId, TaskName, ConnectionName, ExecutedAt, Status,
                TotalFiles, ProcessedFiles, FailedFiles, TotalBytesTransferred,
                DurationMs, ErrorMessage, FilesAffectedJson)
            VALUES(
                $id, $taskId, $taskName, $connName, $executedAt, $status,
                $totalFiles, $processedFiles, $failedFiles, $totalBytes,
                $duration, $errorMessage, $filesAffected);
            """;

        cmd.Parameters.AddWithValue("$id", entry.Id.ToString());
        cmd.Parameters.AddWithValue("$taskId", entry.TaskId.ToString());
        cmd.Parameters.AddWithValue("$taskName", entry.TaskName);
        cmd.Parameters.AddWithValue("$connName", entry.ConnectionName);
        cmd.Parameters.AddWithValue("$executedAt", entry.ExecutedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", (int)entry.Status);
        cmd.Parameters.AddWithValue("$totalFiles", entry.TotalFiles);
        cmd.Parameters.AddWithValue("$processedFiles", entry.ProcessedFiles);
        cmd.Parameters.AddWithValue("$failedFiles", entry.FailedFiles);
        cmd.Parameters.AddWithValue("$totalBytes", entry.TotalBytesTransferred);
        cmd.Parameters.AddWithValue("$duration", (long)entry.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$filesAffected", JsonSerializer.Serialize(entry.FilesAffected));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FtpExecutionHistoryEntry>> ListAsync(int limit = 50, Guid? taskId = null, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();

        if (taskId.HasValue)
        {
            cmd.CommandText = """
                SELECT Id, TaskId, TaskName, ConnectionName, ExecutedAt, Status,
                       TotalFiles, ProcessedFiles, FailedFiles, TotalBytesTransferred,
                       DurationMs, ErrorMessage, FilesAffectedJson
                FROM FtpHistory
                WHERE TaskId = $taskId
                ORDER BY ExecutedAt DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId.Value.ToString());
        }
        else
        {
            cmd.CommandText = """
                SELECT Id, TaskId, TaskName, ConnectionName, ExecutedAt, Status,
                       TotalFiles, ProcessedFiles, FailedFiles, TotalBytesTransferred,
                       DurationMs, ErrorMessage, FilesAffectedJson
                FROM FtpHistory
                ORDER BY ExecutedAt DESC
                LIMIT $limit;
                """;
        }

        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FtpExecutionHistoryEntry>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapHistory(reader));
        }
        return list;
    }

    public async Task<FtpExecutionHistoryEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, TaskId, TaskName, ConnectionName, ExecutedAt, Status,
                   TotalFiles, ProcessedFiles, FailedFiles, TotalBytesTransferred,
                   DurationMs, ErrorMessage, FilesAffectedJson
            FROM FtpHistory
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapHistory(reader);
        }
        return null;
    }

    public async Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FtpHistory WHERE ExecutedAt < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FtpExecutionHistoryEntry MapHistory(SqliteDataReader r)
    {
        var filesJson = r.GetString(12);
        return new FtpExecutionHistoryEntry
        {
            Id = Guid.Parse(r.GetString(0)),
            TaskId = Guid.Parse(r.GetString(1)),
            TaskName = r.GetString(2),
            ConnectionName = r.GetString(3),
            ExecutedAt = DateTime.Parse(r.GetString(4)),
            Status = (FtpExecutionStatus)r.GetInt32(5),
            TotalFiles = r.GetInt32(6),
            ProcessedFiles = r.GetInt32(7),
            FailedFiles = r.GetInt32(8),
            TotalBytesTransferred = r.GetInt64(9),
            Duration = TimeSpan.FromMilliseconds(r.GetInt64(10)),
            ErrorMessage = r.IsDBNull(11) ? null : r.GetString(11),
            FilesAffected = JsonSerializer.Deserialize<List<FtpExecutionFileRecord>>(filesJson) ?? []
        };
    }
}
