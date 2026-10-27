namespace SyncManager.Core.Enums;

public enum ComparisonStatus
{
    Identical,
    SourceOnly,
    TargetOnly,
    SourceNewer,
    TargetNewer,
    DifferentSize,
    DifferentContent,
    Conflict,
    Error,
    Ignored
}

public enum SyncOperationType
{
    CreateDirectory,
    Copy,
    Update,
    Delete,
    DeleteDirectory,
    Skip,
    Conflict
}

public enum SyncMethod
{
    /// <summary>Bidirecional (Fase 4).</summary>
    Synchronization,
    /// <summary>Target fica igual ao Source; extras do Target são removidos.</summary>
    MirrorSourceToTarget,
    /// <summary>Copia só o que falta no Target; nunca atualiza nem remove.</summary>
    ReplicationAugment,
    /// <summary>Atualiza no Target apenas arquivos existentes nos dois lados quando o Source é mais novo.</summary>
    ReplicationRefresh,
    /// <summary>Copia arquivos novos e atualiza os modificados; nunca remove.</summary>
    ReplicationUpdate
}

public enum ExecutionStatus
{
    Success,
    SuccessWithWarnings,
    ConflictsUnresolved,
    Failed,
    Cancelled
}

public enum ConflictResolution
{
    None,
    KeepSource,
    KeepTarget,
    KeepBoth,
    Skip
}

public enum DeleteStrategy
{
    Permanent,
    RecycleBin,
    SafetyFolder
}

public enum OperationOutcome
{
    Succeeded,
    Skipped,
    Warning,
    Failed,
    Cancelled
}

public enum LogSeverity
{
    Info,
    Warning,
    Error
}
