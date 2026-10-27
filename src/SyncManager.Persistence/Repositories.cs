using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Persistence;

public sealed class ProfileRepository : IProfileRepository
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    private readonly Database _db;

    public ProfileRepository(Database db) => _db = db;

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string Method { get; set; } = "";
        public string DeleteStrategy { get; set; } = "";
        public string? SafetyFolderPath { get; set; }
        public string OptionsJson { get; set; } = "{}";
    }

    private static SyncProfile ToProfile(Row r)
    {
        // O JSON guarda todas as opções; as colunas são a fonte de verdade para os campos que também indexamos.
        var p = JsonSerializer.Deserialize<SyncProfile>(r.OptionsJson, Json) ?? new SyncProfile();
        p.Id = (int)r.Id;
        p.Name = r.Name;
        p.SourcePath = r.SourcePath;
        p.TargetPath = r.TargetPath;
        p.Method = Enum.Parse<SyncMethod>(r.Method);
        p.DeleteStrategy = Enum.Parse<DeleteStrategy>(r.DeleteStrategy);
        p.SafetyFolderPath = r.SafetyFolderPath;
        return p;
    }

    public async Task<IReadOnlyList<SyncProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            "SELECT Id, Name, SourcePath, TargetPath, Method, DeleteStrategy, SafetyFolderPath, OptionsJson FROM Profiles ORDER BY Name COLLATE NOCASE", cancellationToken: ct));
        return rows.Select(ToProfile).ToList();
    }

    public async Task<SyncProfile?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT Id, Name, SourcePath, TargetPath, Method, DeleteStrategy, SafetyFolderPath, OptionsJson FROM Profiles WHERE Id = @id",
            new { id }, cancellationToken: ct));
        return row is null ? null : ToProfile(row);
    }

    public async Task<bool> NameExistsAsync(string name, int excludingId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Profiles WHERE Name = @name COLLATE NOCASE AND Id <> @excludingId",
            new { name, excludingId }, cancellationToken: ct)) > 0;
    }

    public async Task<SyncProfile> SaveAsync(SyncProfile profile, CancellationToken ct = default)
    {
        var name = profile.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("O perfil precisa de um nome.");
        if (await NameExistsAsync(name, profile.Id, ct)) throw new DuplicateProfileNameException(name);

        var copy = profile.Clone();
        copy.Name = name;
        var now = DateTimeOffset.UtcNow.ToString("o");
        var args = new
        {
            copy.Id,
            Name = name,
            copy.SourcePath,
            copy.TargetPath,
            Method = copy.Method.ToString(),
            DeleteStrategy = copy.DeleteStrategy.ToString(),
            copy.SafetyFolderPath,
            OptionsJson = JsonSerializer.Serialize(copy, Json),
            Now = now
        };

        await using var conn = await _db.OpenAsync(ct);
        try
        {
            if (copy.Id == 0)
            {
                copy.Id = (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO Profiles(Name, SourcePath, TargetPath, Method, DeleteStrategy, SafetyFolderPath, OptionsJson, CreatedAt, UpdatedAt)
                    VALUES(@Name, @SourcePath, @TargetPath, @Method, @DeleteStrategy, @SafetyFolderPath, @OptionsJson, @Now, @Now);
                    SELECT last_insert_rowid();
                    """, args, cancellationToken: ct));
            }
            else
            {
                // O histórico de sincronização só vale para o par de pastas em que foi gravado. Se Source/Target mudaram,
                // mantê-lo faria o próximo sync tratar arquivos "novos" como excluídos e propagar exclusões indevidas.
                var old = await conn.QuerySingleOrDefaultAsync<PathsRow>(new CommandDefinition(
                    "SELECT SourcePath, TargetPath FROM Profiles WHERE Id = @Id", new { copy.Id }, cancellationToken: ct));
                if (old is not null && (!SamePath(old.SourcePath, copy.SourcePath) || !SamePath(old.TargetPath, copy.TargetPath)))
                    await conn.ExecuteAsync(new CommandDefinition("DELETE FROM FileSyncStates WHERE ProfileId = @Id", new { copy.Id }, cancellationToken: ct));

                var n = await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE Profiles SET Name=@Name, SourcePath=@SourcePath, TargetPath=@TargetPath, Method=@Method,
                        DeleteStrategy=@DeleteStrategy, SafetyFolderPath=@SafetyFolderPath, OptionsJson=@OptionsJson, UpdatedAt=@Now
                    WHERE Id=@Id
                    """, args, cancellationToken: ct));
                if (n == 0) throw new KeyNotFoundException($"Perfil {copy.Id} não existe mais.");
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE: corrida entre NameExists e o INSERT
        {
            throw new DuplicateProfileNameException(name);
        }
        return copy;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM Profiles WHERE Id = @id", new { id }, cancellationToken: ct));
    }

    private sealed class PathsRow
    {
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

public sealed class FileSyncStateRepository : IFileSyncStateRepository
{
    private readonly Database _db;

    public FileSyncStateRepository(Database db) => _db = db;

    private sealed class Row
    {
        public long ProfileId { get; set; }
        public string RelativePath { get; set; } = "";
        public long IsDirectory { get; set; }
        public long? LastSourceSize { get; set; }
        public long? LastTargetSize { get; set; }
        public string? LastSourceModified { get; set; }
        public string? LastTargetModified { get; set; }
        public string? LastSourceHash { get; set; }
        public string? LastTargetHash { get; set; }
        public string LastSuccessfulSyncAt { get; set; } = "";
    }

    private static DateTime? Utc(string? s) =>
        s is null ? null : DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    public async Task<IReadOnlyDictionary<string, FileSyncState>> GetAllAsync(int profileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT ProfileId, RelativePath, IsDirectory, LastSourceSize, LastTargetSize, LastSourceModified, LastTargetModified,
                   LastSourceHash, LastTargetHash, LastSuccessfulSyncAt
            FROM FileSyncStates WHERE ProfileId = @profileId
            """, new { profileId }, cancellationToken: ct));

        var map = new Dictionary<string, FileSyncState>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            map[r.RelativePath] = new FileSyncState((int)r.ProfileId, r.RelativePath, r.IsDirectory != 0, r.LastSourceSize, r.LastTargetSize,
                Utc(r.LastSourceModified), Utc(r.LastTargetModified), r.LastSourceHash, r.LastTargetHash, DateTimeOffset.Parse(r.LastSuccessfulSyncAt));
        return map;
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<FileSyncState> states, CancellationToken ct = default)
    {
        if (states.Count == 0) return;
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);   // uma transação para o lote inteiro: rápido e atômico
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO FileSyncStates(ProfileId, RelativePath, IsDirectory, LastSourceSize, LastTargetSize, LastSourceModified, LastTargetModified,
                                       LastSourceHash, LastTargetHash, LastSuccessfulSyncAt)
            VALUES(@ProfileId, @RelativePath, @IsDirectory, @LastSourceSize, @LastTargetSize, @LastSourceModified, @LastTargetModified,
                   @LastSourceHash, @LastTargetHash, @LastSuccessfulSyncAt)
            ON CONFLICT(ProfileId, RelativePath) DO UPDATE SET IsDirectory=excluded.IsDirectory, LastSourceSize=excluded.LastSourceSize,
                LastTargetSize=excluded.LastTargetSize, LastSourceModified=excluded.LastSourceModified, LastTargetModified=excluded.LastTargetModified,
                LastSourceHash=excluded.LastSourceHash, LastTargetHash=excluded.LastTargetHash, LastSuccessfulSyncAt=excluded.LastSuccessfulSyncAt
            """,
            states.Select(s => new
            {
                s.ProfileId,
                s.RelativePath,
                IsDirectory = s.IsDirectory ? 1 : 0,
                s.LastSourceSize,
                s.LastTargetSize,
                LastSourceModified = s.LastSourceModified?.ToUniversalTime().ToString("o"),
                LastTargetModified = s.LastTargetModified?.ToUniversalTime().ToString("o"),
                s.LastSourceHash,
                s.LastTargetHash,
                LastSuccessfulSyncAt = s.LastSuccessfulSyncAt.UtcDateTime.ToString("o")
            }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task DeleteManyAsync(int profileId, IReadOnlyCollection<string> relativePaths, CancellationToken ct = default)
    {
        if (relativePaths.Count == 0) return;
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM FileSyncStates WHERE ProfileId = @profileId AND RelativePath = @path COLLATE NOCASE",
            relativePaths.Select(p => new { profileId, path = p }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAllAsync(int profileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM FileSyncStates WHERE ProfileId = @profileId", new { profileId }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(int profileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM FileSyncStates WHERE ProfileId = @profileId", new { profileId }, cancellationToken: ct));
    }
}

public sealed class ExecutionHistoryRepository : IExecutionHistoryRepository
{
    private readonly Database _db;

    public ExecutionHistoryRepository(Database db) => _db = db;

    private sealed class Row
    {
        public long Id { get; set; }
        public long? ProfileId { get; set; }
        public string ProfileName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string Method { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string FinishedAt { get; set; } = "";
        public string Status { get; set; } = "";
        public int FilesCompared { get; set; }
        public int FilesCopied { get; set; }
        public int FilesUpdated { get; set; }
        public int FilesDeleted { get; set; }
        public int FilesSkipped { get; set; }
        public int Conflicts { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public long BytesTransferred { get; set; }
        public string? Message { get; set; }
        public string? SafetyFolder { get; set; }
    }

    private const string Columns =
        "Id, ProfileId, ProfileName, SourcePath, TargetPath, Method, StartedAt, FinishedAt, Status, FilesCompared, FilesCopied, " +
        "FilesUpdated, FilesDeleted, FilesSkipped, Conflicts, Errors, Warnings, BytesTransferred, Message, SafetyFolder";

    private static ExecutionHistory ToModel(Row r) => new()
    {
        Id = r.Id,
        ProfileId = (int?)r.ProfileId,
        ProfileName = r.ProfileName,
        SourcePath = r.SourcePath,
        TargetPath = r.TargetPath,
        Method = Enum.Parse<SyncMethod>(r.Method),
        StartedAt = DateTimeOffset.Parse(r.StartedAt),
        FinishedAt = DateTimeOffset.Parse(r.FinishedAt),
        Status = Enum.Parse<ExecutionStatus>(r.Status),
        FilesCompared = r.FilesCompared,
        FilesCopied = r.FilesCopied,
        FilesUpdated = r.FilesUpdated,
        FilesDeleted = r.FilesDeleted,
        FilesSkipped = r.FilesSkipped,
        Conflicts = r.Conflicts,
        Errors = r.Errors,
        Warnings = r.Warnings,
        BytesTransferred = r.BytesTransferred,
        Message = r.Message,
        SafetyFolder = r.SafetyFolder
    };

    public async Task<long> AddAsync(ExecutionHistory h, IReadOnlyList<OperationResult> results, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            INSERT INTO ExecutionHistory(ProfileId, ProfileName, SourcePath, TargetPath, Method, StartedAt, FinishedAt, Status,
                FilesCompared, FilesCopied, FilesUpdated, FilesDeleted, FilesSkipped, Conflicts, Errors, Warnings, BytesTransferred, Message, SafetyFolder)
            VALUES(@ProfileId, @ProfileName, @SourcePath, @TargetPath, @Method, @StartedAt, @FinishedAt, @Status,
                @FilesCompared, @FilesCopied, @FilesUpdated, @FilesDeleted, @FilesSkipped, @Conflicts, @Errors, @Warnings, @BytesTransferred, @Message, @SafetyFolder);
            SELECT last_insert_rowid();
            """, new
        {
            h.ProfileId, h.ProfileName, h.SourcePath, h.TargetPath,
            Method = h.Method.ToString(),
            StartedAt = h.StartedAt.UtcDateTime.ToString("o"),
            FinishedAt = h.FinishedAt.UtcDateTime.ToString("o"),
            Status = h.Status.ToString(),
            h.FilesCompared, h.FilesCopied, h.FilesUpdated, h.FilesDeleted, h.FilesSkipped, h.Conflicts, h.Errors, h.Warnings,
            h.BytesTransferred, h.Message, h.SafetyFolder
        }, tx, cancellationToken: ct));

        if (results.Count > 0)
        {
            var items = results.Select(r => new
            {
                ExecutionId = id,
                OperationType = r.Operation.Type.ToString(),
                RelativePath = r.Operation.RelativePath,
                Outcome = r.Outcome.ToString(),
                r.Message,
                r.Bytes
            });
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO ExecutionItems(ExecutionId, OperationType, RelativePath, Outcome, Message, Bytes) VALUES(@ExecutionId, @OperationType, @RelativePath, @Outcome, @Message, @Bytes)",
                items, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        h.Id = id;
        return id;
    }

    public async Task<IReadOnlyList<ExecutionHistory>> ListAsync(int limit, int? profileId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM ExecutionHistory WHERE (@profileId IS NULL OR ProfileId = @profileId) ORDER BY StartedAt DESC, Id DESC LIMIT @limit",
            new { profileId, limit }, cancellationToken: ct));
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ExecutionHistoryItem>> GetItemsAsync(long executionId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Falhas e avisos primeiro, para que o que importa apareça mesmo quando o limite corta a lista.
        var items = await conn.QueryAsync<ExecutionHistoryItem>(new CommandDefinition("""
            SELECT ExecutionId, OperationType, RelativePath, Outcome, Message, Bytes FROM ExecutionItems
            WHERE ExecutionId = @executionId
            ORDER BY CASE Outcome WHEN 'Failed' THEN 0 WHEN 'Warning' THEN 1 WHEN 'Cancelled' THEN 2 ELSE 3 END, Id
            LIMIT @limit
            """, new { executionId, limit }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task<IReadOnlyDictionary<int, ProfileLastRun>> GetLastRunsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long ProfileId, string StartedAt, string Status)>(new CommandDefinition("""
            SELECT h.ProfileId, h.StartedAt, h.Status FROM ExecutionHistory h
            WHERE h.ProfileId IS NOT NULL
              AND h.Id = (SELECT x.Id FROM ExecutionHistory x WHERE x.ProfileId = h.ProfileId ORDER BY x.StartedAt DESC, x.Id DESC LIMIT 1)
            """, cancellationToken: ct));
        return rows.ToDictionary(r => (int)r.ProfileId,
            r => new ProfileLastRun((int)r.ProfileId, DateTimeOffset.Parse(r.StartedAt), Enum.Parse<ExecutionStatus>(r.Status)));
    }
}

public sealed class ScheduleRepository : IScheduleRepository
{
    private readonly Database _db;

    public ScheduleRepository(Database db) => _db = db;

    private sealed class Row
    {
        public long Id { get; set; }
        public long ProfileId { get; set; }
        public string Kind { get; set; } = "";
        public string? TimeOfDay { get; set; }
        public long? DaysOfWeek { get; set; }
        public long? IntervalMinutes { get; set; }
        public long? DayOfMonth { get; set; }
        public long RunOnStartup { get; set; }
        public long? DelayMinutes { get; set; }
        public string? TaskName { get; set; }
        public long Enabled { get; set; }
    }

    private const string Columns = "Id, ProfileId, Kind, TimeOfDay, DaysOfWeek, IntervalMinutes, DayOfMonth, RunOnStartup, DelayMinutes, TaskName, Enabled";

    private static ScheduleDefinition ToModel(Row r) => new()
    {
        Id = (int)r.Id,
        ProfileId = (int)r.ProfileId,
        Kind = Enum.Parse<ScheduleKind>(r.Kind),
        TimeOfDay = r.TimeOfDay is null ? new TimeSpan(2, 0, 0) : TimeSpan.ParseExact(r.TimeOfDay, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture),
        Days = (WeekDays)(r.DaysOfWeek ?? 0),
        IntervalMinutes = (int)(r.IntervalMinutes ?? 360),
        DayOfMonth = (int)(r.DayOfMonth ?? 1),
        RunOnStartup = r.RunOnStartup != 0,
        DelayMinutes = (int)(r.DelayMinutes ?? 0),
        TaskName = r.TaskName,
        Enabled = r.Enabled != 0
    };

    public async Task<ScheduleDefinition?> GetByProfileAsync(int profileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Schedules WHERE ProfileId = @profileId", new { profileId }, cancellationToken: ct));
        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyDictionary<int, ScheduleDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM Schedules", cancellationToken: ct));
        return rows.Select(ToModel).ToDictionary(s => s.ProfileId);
    }

    public async Task<ScheduleDefinition> SaveAsync(ScheduleDefinition s, CancellationToken ct = default)
    {
        var errors = s.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));

        await using var conn = await _db.OpenAsync(ct);
        var args = new
        {
            s.ProfileId,
            Kind = s.Kind.ToString(),
            TimeOfDay = s.TimeOfDay.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture),
            DaysOfWeek = (int)s.Days,
            s.IntervalMinutes,
            s.DayOfMonth,
            RunOnStartup = s.RunOnStartup ? 1 : 0,
            s.DelayMinutes,
            s.TaskName,
            Enabled = s.Enabled ? 1 : 0
        };

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO Schedules(ProfileId, Kind, TimeOfDay, DaysOfWeek, IntervalMinutes, DayOfMonth, RunOnStartup, DelayMinutes, TaskName, Enabled)
            VALUES(@ProfileId, @Kind, @TimeOfDay, @DaysOfWeek, @IntervalMinutes, @DayOfMonth, @RunOnStartup, @DelayMinutes, @TaskName, @Enabled)
            ON CONFLICT(ProfileId) DO UPDATE SET Kind=excluded.Kind, TimeOfDay=excluded.TimeOfDay, DaysOfWeek=excluded.DaysOfWeek,
                IntervalMinutes=excluded.IntervalMinutes, DayOfMonth=excluded.DayOfMonth, RunOnStartup=excluded.RunOnStartup,
                DelayMinutes=excluded.DelayMinutes, TaskName=excluded.TaskName, Enabled=excluded.Enabled;
            SELECT Id FROM Schedules WHERE ProfileId = @ProfileId;
            """, args, cancellationToken: ct));
        s.Id = (int)id;
        return s;
    }

    public async Task DeleteByProfileAsync(int profileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM Schedules WHERE ProfileId = @profileId", new { profileId }, cancellationToken: ct));
    }
}

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly Database _db;

    public SettingsRepository(Database db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Value FROM Settings WHERE Key = @key", new { key }, cancellationToken: ct));
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Settings(Key, Value) VALUES(@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            new { key, value }, cancellationToken: ct));
    }
}
