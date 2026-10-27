namespace SyncManager.Core.Models;

/// <summary>
/// Retrato de um item no momento da última sincronização bem-sucedida do perfil. É o "ancestral comum" da
/// sincronização bidirecional: compará-lo com cada lado diz quem mudou desde então (um, os dois ou nenhum).
/// </summary>
public sealed record FileSyncState(
    int ProfileId,
    string RelativePath,
    bool IsDirectory,
    long? LastSourceSize,
    long? LastTargetSize,
    DateTime? LastSourceModified,
    DateTime? LastTargetModified,
    string? LastSourceHash,
    string? LastTargetHash,
    DateTimeOffset LastSuccessfulSyncAt);
