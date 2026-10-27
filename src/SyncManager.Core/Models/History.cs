using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

public sealed class ExecutionHistory
{
    public long Id { get; set; }
    /// <summary>Null para execuções manuais sem perfil salvo. Sem chave estrangeira: o histórico sobrevive à exclusão do perfil.</summary>
    public int? ProfileId { get; set; }
    public string ProfileName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public SyncMethod Method { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public ExecutionStatus Status { get; set; }
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
    public TimeSpan Duration => FinishedAt - StartedAt;

    public static ExecutionHistory From(SyncProfile profile, SyncPlan plan, ExecutionResult r) => new()
    {
        ProfileId = profile.Id > 0 ? profile.Id : null,
        ProfileName = profile.Id > 0 ? profile.Name : "(comparação manual)",
        SourcePath = plan.SourceRoot,
        TargetPath = plan.TargetRoot,
        Method = plan.Method,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        Status = r.Status,
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
}

public sealed record ExecutionHistoryItem(long ExecutionId, string OperationType, string RelativePath, string Outcome, string? Message, long Bytes);

public sealed record ProfileLastRun(int ProfileId, DateTimeOffset StartedAt, ExecutionStatus Status);
