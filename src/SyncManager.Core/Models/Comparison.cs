using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

public sealed record CompareOptions(
    string SourceRoot,
    string TargetRoot,
    TimeSpan TimestampTolerance,
    bool VerifyContentByHash,
    /// <summary>Quantos pares são comparados em paralelo quando há leitura de conteúdo (hash).</summary>
    int MaxDegreeOfParallelism = 4);

public sealed record ComparisonItem(
    string RelativePath,
    string SourcePath,
    string TargetPath,
    bool IsDirectory,
    bool SourceExists,
    bool TargetExists,
    long? SourceSize,
    long? TargetSize,
    DateTime? SourceModified,
    DateTime? TargetModified,
    ComparisonStatus Status,
    string? Message = null,
    // Preenchidos só na sincronização bidirecional, a partir do FileSyncState da última sincronização:
    bool HasSyncState = false,
    bool? SourceChanged = null,
    bool? TargetChanged = null);

public sealed class ComparisonResult
{
    private readonly int[] _counts = new int[Enum.GetValues<ComparisonStatus>().Length];

    public ComparisonResult(string sourceRoot, string targetRoot, IReadOnlyList<ComparisonItem> items,
        int sourceEntryCount, int targetEntryCount, IReadOnlyList<string>? staleStatePaths = null)
    {
        StaleStatePaths = staleStatePaths ?? Array.Empty<string>();
        SourceRoot = sourceRoot;
        TargetRoot = targetRoot;
        Items = items;
        SourceEntryCount = sourceEntryCount;
        TargetEntryCount = targetEntryCount;
        foreach (var i in items) _counts[(int)i.Status]++;
    }

    public string SourceRoot { get; }
    public string TargetRoot { get; }
    public IReadOnlyList<ComparisonItem> Items { get; }
    public int SourceEntryCount { get; }
    public int TargetEntryCount { get; }
    /// <summary>Itens do histórico de sincronização que não existem mais em nenhum lado.</summary>
    public IReadOnlyList<string> StaleStatePaths { get; }

    public int Count(ComparisonStatus s) => _counts[(int)s];
    public int Analyzed => Items.Count;
    public int Different => Count(ComparisonStatus.DifferentSize) + Count(ComparisonStatus.DifferentContent);
}
