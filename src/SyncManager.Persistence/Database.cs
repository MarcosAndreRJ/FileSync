using Microsoft.Data.Sqlite;

namespace SyncManager.Persistence;

/// <summary>Abre conexões SQLite e garante que o esquema esteja atualizado (migrations versionadas).</summary>
public sealed class Database
{
    private static readonly (int Version, string Name, string Sql)[] Migrations =
    {
        (1, "Esquema inicial", """
            CREATE TABLE Profiles(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                Method TEXT NOT NULL,
                DeleteStrategy TEXT NOT NULL,
                SafetyFolderPath TEXT NULL,
                OptionsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL);

            CREATE TABLE Schedules(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL REFERENCES Profiles(Id) ON DELETE CASCADE,
                Kind TEXT NOT NULL,
                TimeOfDay TEXT NULL,
                DaysOfWeek INTEGER NULL,
                IntervalMinutes INTEGER NULL,
                DayOfMonth INTEGER NULL,
                RunOnStartup INTEGER NOT NULL DEFAULT 0,
                DelayMinutes INTEGER NULL,
                TaskName TEXT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1);

            CREATE TABLE ExecutionHistory(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NULL,
                ProfileName TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                Method TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT NOT NULL,
                Status TEXT NOT NULL,
                FilesCompared INTEGER NOT NULL,
                FilesCopied INTEGER NOT NULL,
                FilesUpdated INTEGER NOT NULL,
                FilesDeleted INTEGER NOT NULL,
                FilesSkipped INTEGER NOT NULL,
                Conflicts INTEGER NOT NULL,
                Errors INTEGER NOT NULL,
                Warnings INTEGER NOT NULL,
                BytesTransferred INTEGER NOT NULL,
                Message TEXT NULL,
                SafetyFolder TEXT NULL);
            CREATE INDEX IX_ExecutionHistory_Profile ON ExecutionHistory(ProfileId, StartedAt DESC);
            CREATE INDEX IX_ExecutionHistory_Started ON ExecutionHistory(StartedAt DESC);

            CREATE TABLE ExecutionItems(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ExecutionId INTEGER NOT NULL REFERENCES ExecutionHistory(Id) ON DELETE CASCADE,
                OperationType TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                Message TEXT NULL,
                Bytes INTEGER NOT NULL);
            CREATE INDEX IX_ExecutionItems_Execution ON ExecutionItems(ExecutionId);

            CREATE TABLE FileSyncStates(
                ProfileId INTEGER NOT NULL REFERENCES Profiles(Id) ON DELETE CASCADE,
                RelativePath TEXT NOT NULL,
                LastSourceSize INTEGER NULL,
                LastTargetSize INTEGER NULL,
                LastSourceModified TEXT NULL,
                LastTargetModified TEXT NULL,
                LastSourceHash TEXT NULL,
                LastTargetHash TEXT NULL,
                LastSuccessfulSyncAt TEXT NOT NULL,
                PRIMARY KEY(ProfileId, RelativePath));

            CREATE TABLE Settings(
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL);
            """),
        (2, "Um agendamento por perfil", """
            CREATE UNIQUE INDEX UX_Schedules_Profile ON Schedules(ProfileId);
            """),
        (3, "Pastas no histórico de sincronização", """
            ALTER TABLE FileSyncStates ADD COLUMN IsDirectory INTEGER NOT NULL DEFAULT 0;
            """)
    };

    private readonly string _connectionString;

    public Database(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncManager", "syncmanager.db");

    public string DatabasePath { get; }
    public static int LatestVersion => Migrations[^1].Version;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>Cria o banco se preciso e aplica as migrations pendentes, cada uma em sua transação.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var conn = await OpenAsync(ct);
        await Exec(conn, null, "PRAGMA journal_mode = WAL;", ct);
        await Exec(conn, null, "CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAt TEXT NOT NULL);", ct);

        long current;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
            current = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        foreach (var (version, name, sql) in Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await Exec(conn, tx, sql, ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO SchemaVersion(Version, Name, AppliedAt) VALUES($v, $n, $t);";
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static async Task Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static void ReleaseFileLocks() => SqliteConnection.ClearAllPools();
}
