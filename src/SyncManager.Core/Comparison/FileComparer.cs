using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Comparison;

/// <summary>
/// Decide o status de um par de itens: existência → tipo → tamanho → data (com tolerância) → hash (opcional).
/// O hash só é calculado quando tamanho e data não bastam para decidir.
/// </summary>
public sealed class FileComparer : IFileComparer
{
    private readonly IFileSystem _fs;

    public FileComparer(IFileSystem fs) => _fs = fs;

    public async Task<ComparisonItem> CompareAsync(string relativePath, FileEntry? s, FileEntry? t,
        CompareOptions o, CancellationToken ct)
    {
        ComparisonStatus status;
        string? message = null;

        if (s is null && t is null) throw new ArgumentException("Pelo menos um lado deve existir.");
        if (t is null) status = ComparisonStatus.SourceOnly;
        else if (s is null) status = ComparisonStatus.TargetOnly;
        else if (s.IsDirectory != t.IsDirectory)
        {
            status = ComparisonStatus.Conflict;
            message = s.IsDirectory ? "Source é pasta e Target é arquivo" : "Source é arquivo e Target é pasta";
        }
        else if (s.IsDirectory) status = ComparisonStatus.Identical;
        else
        {
            try
            {
                (status, message) = await CompareFilesAsync(s, t, o, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status = ComparisonStatus.Error;
                message = ex.Message;
            }
        }

        return Build(relativePath, s, t, o, status, message);
    }

    public async Task<bool> HasChangedAsync(FileEntry entry, long? baseSize, DateTime? baseModified, string? baseHash,
        CompareOptions o, CancellationToken ct)
    {
        if (entry.IsDirectory) return false;                           // pasta não "muda": só existe ou não
        if (baseSize is null || baseModified is null) return true;
        if (entry.Size != baseSize) return true;
        if (MatchesBase(entry, baseSize, baseModified, o.TimestampTolerance)) return false;

        // Mesmo tamanho, data diferente: só o conteúdo decide — e só se houver hash guardado para comparar.
        if (o.VerifyContentByHash && baseHash is not null)
        {
            var hash = await _fs.ComputeHashAsync(entry.FullPath, ct);
            return !string.Equals(hash, baseHash, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>Tamanho e data iguais aos do estado guardado (dentro da tolerância) — sem ler o disco.</summary>
    public static bool MatchesBase(FileEntry entry, long? baseSize, DateTime? baseModified, TimeSpan tolerance) =>
        baseSize is not null && baseModified is not null && entry.Size == baseSize &&
        (entry.ModifiedUtc - baseModified.Value).Duration() <= tolerance;

    public static ComparisonItem Build(string relativePath, FileEntry? s, FileEntry? t, CompareOptions o,
        ComparisonStatus status, string? message) =>
        new(relativePath,
            Path.Combine(o.SourceRoot, relativePath),
            Path.Combine(o.TargetRoot, relativePath),
            (s ?? t)?.IsDirectory ?? false,
            s is not null, t is not null,
            s is { IsDirectory: false } ? s.Size : null,
            t is { IsDirectory: false } ? t.Size : null,
            s?.ModifiedUtc, t?.ModifiedUtc,
            status, message);

    private async Task<(ComparisonStatus, string?)> CompareFilesAsync(FileEntry s, FileEntry t, CompareOptions o, CancellationToken ct)
    {
        var diff = s.ModifiedUtc - t.ModifiedUtc;
        var sameTime = diff.Duration() <= o.TimestampTolerance;
        var newer = diff > TimeSpan.Zero ? ComparisonStatus.SourceNewer : ComparisonStatus.TargetNewer;

        if (s.Size != t.Size)
            return sameTime ? (ComparisonStatus.DifferentSize, "Tamanhos diferentes com a mesma data") : (newer, null);

        if (o.VerifyContentByHash)
        {
            var hs = _fs.ComputeHashAsync(s.FullPath, ct);
            var ht = _fs.ComputeHashAsync(t.FullPath, ct);
            var hashes = await Task.WhenAll(hs, ht);
            if (hashes[0] == hashes[1]) return (ComparisonStatus.Identical, null);
            return sameTime ? (ComparisonStatus.DifferentContent, "Mesmo tamanho e data, conteúdo diferente (SHA-256)") : (newer, null);
        }

        return sameTime ? (ComparisonStatus.Identical, null) : (newer, null);
    }
}
