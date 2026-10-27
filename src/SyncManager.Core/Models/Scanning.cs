using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;

namespace SyncManager.Core.Models;

public sealed record ScanOptions(
    bool IncludeSubfolders,
    bool IncludeHidden,
    bool IncludeSystem,
    PathFilter Filter,
    IReadOnlyCollection<string> ExcludedRootFolderNames,
    int TransientRetries = 0,
    int RetryDelayMs = 500);

/// <summary>Caminho que não pôde ser lido com confiabilidade (erro) ou foi deliberadamente ignorado (link).</summary>
public sealed record UnreliablePath(string RelativePath, ComparisonStatus Status, string Message);

public sealed record ScanResult(
    string Root,
    IReadOnlyList<FileEntry> Entries,
    IReadOnlyList<UnreliablePath> Unreliable,
    int FilteredCount);

public sealed record ScanProgress(int ItemsFound, string CurrentDirectory);

public sealed class ScanFailedException : IOException
{
    public ScanFailedException(string root, string message, Exception? inner = null)
        : base($"Não foi possível ler '{root}': {message}", inner) { }
}
