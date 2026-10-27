using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Comparison;
using SyncManager.Core.Enums;
using SyncManager.Core.Execution;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;
using SyncManager.Core.Scanning;
using SyncManager.Infrastructure;
using SyncManager.Persistence;

namespace SyncManager.Tests;

/// <summary>Pastas temporárias reais (source/target) + motor completo montado à mão.</summary>
public sealed class TestEnv : IDisposable
{
    public static readonly DateTime BaseTime = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SyncManagerTests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public string Target => Path.Combine(Root, "target");
    public IFileSystem Fs { get; }
    public FileLogService Log { get; }
    public SyncWorkflow Workflow { get; }
    public Database Db { get; }
    public ProfileRepository Profiles { get; }
    public ExecutionHistoryRepository History { get; }
    public SettingsService Settings { get; }
    public IProfileLockProvider Locks { get; }

    public TestEnv(Func<IFileSystem, IFileSystem>? wrapFs = null)
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        Fs = wrapFs?.Invoke(new PhysicalFileSystem()) ?? new PhysicalFileSystem();
        Log = new FileLogService(null, Path.Combine(Root, "logs"));
        Db = new Database(Path.Combine(Root, "db", "test.db"));
        Db.MigrateAsync().GetAwaiter().GetResult();
        Profiles = new ProfileRepository(Db);
        History = new ExecutionHistoryRepository(Db);
        Settings = new SettingsService(new SettingsRepository(Db));
        Locks = new ProfileLockProvider(Root);
        Workflow = new SyncWorkflow(
            new FileSystemScanner(Fs),
            new DirectoryComparer(new FileComparer(Fs)),
            new SyncPlanner(),
            new SyncExecutor(Fs, Log, TimeProvider.System),
            new PreflightValidator(Fs),
            Log, History, Settings, Locks, new FileSyncStateRepository(Db), Fs);
    }

    public SyncProfile Profile(SyncMethod method = SyncMethod.MirrorSourceToTarget) => new()
    {
        Name = "teste",
        SourcePath = Source,
        TargetPath = Target,
        Method = method,
        MaxParallelFileOperations = 1,
        TransientRetryCount = 0   // testes de arquivo bloqueado não devem esperar retentativas
    };

    public string Write(string root, string rel, string content, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc ?? BaseTime);
        return path;
    }

    public string S(string rel, string content, DateTime? t = null) => Write(Source, rel, content, t);
    public string T(string rel, string content, DateTime? t = null) => Write(Target, rel, content, t);

    public Task<CompareOutcome> CompareAsync(SyncProfile p, CancellationToken ct = default) =>
        Workflow.CompareAsync(p, null, ct);

    public static ComparisonStatus StatusOf(CompareOutcome o, string rel) =>
        o.Comparison.Items.Single(i => i.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase)).Status;

    public static SyncOperationType OpOf(CompareOutcome o, string rel) =>
        o.Plan.Operations.Single(i => i.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase)).Type;

    public void Dispose()
    {
        Log.Dispose();
        Database.ReleaseFileLocks();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { /* limpeza de teste é melhor esforço */ }
    }
}

/// <summary>Decorator que injeta falhas sobre o sistema de arquivos real.</summary>
public sealed class FaultyFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    public FaultyFileSystem(IFileSystem inner) => _inner = inner;

    public bool Offline { get; set; }
    /// <summary>Chamado depois que a cópia do arquivo terminou (útil para alterar o Source "durante" a cópia).</summary>
    public Action<string>? AfterCopy { get; set; }
    /// <summary>As próximas N cópias falham com erro de rede temporário (ERROR_NETNAME_DELETED) e depois voltam ao normal.</summary>
    public int TransientCopyFailures { get; set; }
    public int CopyAttempts { get; private set; }

    private void EnsureOnline()
    {
        if (Offline) throw new IOException("O caminho de rede especificado não existe. (simulado)");
    }

    public Task<RootStatus> CheckRootAsync(string path, bool requireWrite, CancellationToken ct) =>
        Offline ? Task.FromResult(new RootStatus(false, false, false, "offline (simulado)")) : _inner.CheckRootAsync(path, requireWrite, ct);

    public IEnumerable<FileSystemItem> EnumerateDirectory(string path) { EnsureOnline(); return _inner.EnumerateDirectory(path); }
    public FileSystemItem? GetItem(string path) { EnsureOnline(); return _inner.GetItem(path); }
    public void CreateDirectory(string path) { EnsureOnline(); _inner.CreateDirectory(path); }

    public async Task CopyFileAsync(string source, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        EnsureOnline();
        CopyAttempts++;
        if (TransientCopyFailures > 0)
        {
            TransientCopyFailures--;
            throw new IOException("O nome de rede especificado não está mais disponível. (simulado)", unchecked((int)0x80070040));
        }
        await _inner.CopyFileAsync(source, destination, progress, ct);
        AfterCopy?.Invoke(source);
    }

    public void MoveFile(string source, string destination, bool overwrite) { EnsureOnline(); _inner.MoveFile(source, destination, overwrite); }
    public void DeleteFile(string path) { EnsureOnline(); _inner.DeleteFile(path); }
    public void DeleteEmptyDirectory(string path) { EnsureOnline(); _inner.DeleteEmptyDirectory(path); }
    public void SendToRecycleBin(string path) { EnsureOnline(); _inner.SendToRecycleBin(path); }
    public Task<string> ComputeHashAsync(string path, CancellationToken ct) { EnsureOnline(); return _inner.ComputeHashAsync(path, ct); }
    public void SetModifiedUtc(string path, DateTime utc) { EnsureOnline(); _inner.SetModifiedUtc(path, utc); }
    public void SetAttributes(string path, FileAttributes attributes) { EnsureOnline(); _inner.SetAttributes(path, attributes); }
}
