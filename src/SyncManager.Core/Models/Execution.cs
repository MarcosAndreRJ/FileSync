using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

public sealed record ExecutionProgress(
    string? CurrentFile,
    int CompletedOperations,
    int TotalOperations,
    long BytesProcessed,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes > 0 && TotalOperations > 0
        ? Math.Min(1.0, ((double)CompletedOperations / TotalOperations + (double)BytesProcessed / TotalBytes) / 2)
        : (TotalOperations > 0 ? (double)CompletedOperations / TotalOperations : 1.0);
}

public sealed record OperationResult(SyncOperation Operation, OperationOutcome Outcome, string? Message, long Bytes);

public sealed class ExecutionResult
{
    public required ExecutionStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public string? Message { get; init; }
    public string? SafetyFolder { get; init; }
    public int FilesCompared { get; init; }
    public int FilesCopied { get; init; }
    public int FilesUpdated { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesSkipped { get; init; }
    public int Conflicts { get; init; }
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public int NotExecuted { get; init; }
    public long BytesTransferred { get; init; }
    public IReadOnlyList<OperationResult> Results { get; init; } = Array.Empty<OperationResult>();
    public TimeSpan Duration => FinishedAt - StartedAt;
}

public sealed record LogEntry(DateTimeOffset Time, LogSeverity Severity, string Event, string? Detail);
