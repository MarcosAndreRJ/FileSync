using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Abstractions;

/// <summary>Única porta de acesso ao disco usada pelo motor. Permite testar falhas sem depender do disco real.</summary>
public interface IFileSystem
{
    Task<RootStatus> CheckRootAsync(string path, bool requireWrite, CancellationToken ct);
    IEnumerable<FileSystemItem> EnumerateDirectory(string path);
    FileSystemItem? GetItem(string path);
    void CreateDirectory(string path);
    /// <summary>Copia o conteúdo criando o destino novo (falha se já existir).</summary>
    Task CopyFileAsync(string source, string destination, IProgress<long>? progress, CancellationToken ct);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void DeleteEmptyDirectory(string path);
    void SendToRecycleBin(string path);
    Task<string> ComputeHashAsync(string path, CancellationToken ct);
    void SetModifiedUtc(string path, DateTime utc);
    void SetAttributes(string path, FileAttributes attributes);
}

public interface IFileSystemScanner
{
    Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct);
}

public interface IFileComparer
{
    Task<ComparisonItem> CompareAsync(string relativePath, FileEntry? source, FileEntry? target,
        CompareOptions options, CancellationToken ct);

    /// <summary>
    /// O item mudou desde o último estado sincronizado? Barato (só metadados) sempre que possível; lê o conteúdo
    /// (hash) apenas quando o tamanho é igual, a data mudou e há hash guardado para comparar.
    /// </summary>
    Task<bool> HasChangedAsync(FileEntry entry, long? baseSize, DateTime? baseModified, string? baseHash,
        CompareOptions options, CancellationToken ct);
}

public interface ISyncPlanner
{
    /// <param name="knownSyncStates">Quantos itens o histórico de sincronização do perfil tem (0 = primeira sincronização).</param>
    SyncPlan Build(ComparisonResult result, SyncProfile profile, int knownSyncStates = 0);

    /// <summary>Decide a operação de UM item (usado ao atualizar itens individuais sem replanejar tudo).</summary>
    SyncOperation PlanItem(ComparisonItem item, SyncProfile profile);
}

public interface ISyncExecutor
{
    Task<ExecutionResult> ExecuteAsync(SyncPlan plan, SyncProfile profile,
        IProgress<ExecutionProgress>? progress, CancellationToken ct);
}

public interface ILogService
{
    string LogDirectory { get; }
    event Action<LogEntry>? EntryWritten;
    void Write(LogEntry entry);
}

public static class LogServiceExtensions
{
    public static void Info(this ILogService log, string evt, string? detail = null) =>
        log.Write(new LogEntry(DateTimeOffset.Now, LogSeverity.Info, evt, detail));

    public static void Warning(this ILogService log, string evt, string? detail = null) =>
        log.Write(new LogEntry(DateTimeOffset.Now, LogSeverity.Warning, evt, detail));

    public static void Error(this ILogService log, string evt, string? detail = null) =>
        log.Write(new LogEntry(DateTimeOffset.Now, LogSeverity.Error, evt, detail));
}
