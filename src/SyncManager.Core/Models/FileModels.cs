namespace SyncManager.Core.Models;

/// <summary>Item cru devolvido pelo sistema de arquivos (um nível de diretório).</summary>
public sealed record FileSystemItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc,
    FileAttributes Attributes,
    bool IsReparsePoint);

/// <summary>Item já lido pelo scanner, com caminho relativo à raiz.</summary>
public sealed record FileEntry(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc,
    FileAttributes Attributes);

public sealed record RootStatus(bool Exists, bool CanRead, bool CanWrite, string? Error)
{
    public bool IsUsable(bool requireWrite) => Exists && CanRead && (!requireWrite || CanWrite);
}
