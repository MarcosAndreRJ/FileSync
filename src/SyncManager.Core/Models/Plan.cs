using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

public enum FileSide { Source, Target }

/// <summary>
/// Uma operação do plano. <see cref="SourcePath"/> é o caminho de ORIGEM da operação e <see cref="TargetPath"/> o de DESTINO
/// (ou o item que será excluído/criado) — independentemente de qual lado da comparação ele esteja.
/// <see cref="From"/> e <see cref="To"/> dizem a que lado da comparação cada caminho pertence; é isso que permite
/// copiar do Target para o Source (sincronização bidirecional, setas do teclado, resolução de conflitos).
/// </summary>
public sealed record SyncOperation(
    SyncOperationType Type,
    ComparisonItem Item,
    string? SourcePath,
    string? TargetPath,
    long Bytes,
    string Reason,
    FileSide From = FileSide.Source,
    FileSide To = FileSide.Target,
    int Stage = 0,
    bool RecordsState = false)
{
    public string RelativePath => Item.RelativePath;
    public bool IsExecutable => Type is not (SyncOperationType.Skip or SyncOperationType.Conflict);
    /// <summary>Operação que altera o lado Source (exige escrita nele).</summary>
    public bool WritesSource => IsExecutable && To == FileSide.Source;
    public bool WritesTarget => IsExecutable && To == FileSide.Target;
}

/// <summary>Plano imutável do que será feito. Nada é executado até que o SyncExecutor o receba.</summary>
public sealed class SyncPlan
{
    public SyncPlan(SyncMethod method, string sourceRoot, string targetRoot, IReadOnlyList<SyncOperation> operations,
        int targetEntryCount, double massDeleteThreshold, string? blockedReason, IReadOnlyList<string> warnings,
        int sourceEntryCount = 0, IReadOnlyList<string>? staleStatePaths = null)
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTimeOffset.Now;
        Method = method;
        SourceRoot = sourceRoot;
        TargetRoot = targetRoot;
        Operations = operations;
        BlockedReason = blockedReason;
        Warnings = warnings;
        MassDeleteThreshold = massDeleteThreshold;
        TargetEntryCount = targetEntryCount;
        SourceEntryCount = sourceEntryCount;
        StaleStatePaths = staleStatePaths ?? Array.Empty<string>();

        foreach (var op in operations)
        {
            switch (op.Type)
            {
                case SyncOperationType.Copy: CopyCount++; TotalBytes += op.Bytes; break;
                case SyncOperationType.Update:
                    UpdateCount++; TotalBytes += op.Bytes;
                    if (OverwritesNewer(op)) OverwritesNewerTargetCount++;
                    break;
                case SyncOperationType.Delete:
                case SyncOperationType.DeleteDirectory:
                    DeleteCount++;
                    if (op.To == FileSide.Source) DeleteAtSourceCount++; else DeleteAtTargetCount++;
                    break;
                case SyncOperationType.CreateDirectory: CreateDirectoryCount++; break;
                case SyncOperationType.Conflict: ConflictCount++; break;
                case SyncOperationType.Skip:
                    if (op.Item.Status == ComparisonStatus.Error) ErrorCount++; else SkipCount++;
                    break;
            }
        }
    }

    /// <summary>Sobrescreve um arquivo de destino que é MAIS NOVO que a origem.</summary>
    private static bool OverwritesNewer(SyncOperation op) =>
        op.From == FileSide.Source
            ? op.Item.Status == ComparisonStatus.TargetNewer
            : op.Item.Status == ComparisonStatus.SourceNewer;

    /// <summary>Mesmo plano (raízes, avisos, bloqueio) com outra lista de operações — usado para editar o plano sem recomparar.</summary>
    public SyncPlan WithOperations(IReadOnlyList<SyncOperation> operations) =>
        new(Method, SourceRoot, TargetRoot, operations, TargetEntryCount, MassDeleteThreshold, BlockedReason, Warnings,
            SourceEntryCount, StaleStatePaths);

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public SyncMethod Method { get; }
    public string SourceRoot { get; }
    public string TargetRoot { get; }
    public IReadOnlyList<SyncOperation> Operations { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Quando preenchido, o plano não pode ser executado.</summary>
    public string? BlockedReason { get; }
    public bool IsBlocked => BlockedReason is not null;
    public double MassDeleteThreshold { get; }
    public int TargetEntryCount { get; }
    public int SourceEntryCount { get; }
    /// <summary>Caminhos que existiam no histórico de sincronização e sumiram dos dois lados (o estado deve ser apagado).</summary>
    public IReadOnlyList<string> StaleStatePaths { get; }

    public int CopyCount { get; }
    public int UpdateCount { get; }
    public int DeleteCount { get; }
    public int DeleteAtSourceCount { get; }
    public int DeleteAtTargetCount { get; }
    public int CreateDirectoryCount { get; }
    public int SkipCount { get; }
    public int ConflictCount { get; }
    public int ErrorCount { get; }
    public long TotalBytes { get; }
    public int OverwritesNewerTargetCount { get; }

    public int ExecutableCount => CopyCount + UpdateCount + DeleteCount + CreateDirectoryCount;
    public bool HasDestructiveOperations => DeleteCount > 0 || OverwritesNewerTargetCount > 0;

    /// <summary>Maior fração de exclusões sobre um dos lados (na bidirecional, exclusões podem ocorrer nos dois).</summary>
    public double DeleteRatio
    {
        get
        {
            var t = TargetEntryCount == 0 ? 0 : (double)DeleteAtTargetCount / TargetEntryCount;
            var s = SourceEntryCount == 0 ? 0 : (double)DeleteAtSourceCount / SourceEntryCount;
            return Math.Max(t, s);
        }
    }

    public bool RequiresStrongConfirmation => DeleteCount > 0 && DeleteRatio > MassDeleteThreshold;
}
