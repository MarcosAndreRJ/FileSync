using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;

namespace SyncManager.Core.Execution;

/// <summary>O Source mudou (ou sumiu) entre a comparação e a cópia; nada foi gravado no destino final.</summary>
public sealed class SourceChangedException : IOException
{
    public SourceChangedException(string message) : base(message) { }
}

public sealed record SafeCopyOptions(
    bool PreserveTimestamps,
    bool PreserveAttributes,
    bool VerifyHash,
    Func<CancellationToken, Task>? BeforeReplace = null);

internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public InlineProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

/// <summary>
/// Cópia segura: grava em arquivo temporário ao lado do destino, valida (tamanho, estabilidade do Source e
/// opcionalmente hash) e só então substitui o destino. O temporário é sempre removido em caso de falha.
/// </summary>
public sealed class SafeFileCopier
{
    private readonly IFileSystem _fs;

    public SafeFileCopier(IFileSystem fs) => _fs = fs;

    public async Task<long> CopyAsync(string sourcePath, string targetPath, FileSystemItem expectedSource,
        SafeCopyOptions options, IProgress<long>? progress, CancellationToken ct)
    {
        var before = _fs.GetItem(sourcePath) ?? throw new FileNotFoundException("Source desapareceu antes da cópia.", sourcePath);
        if (before.Size != expectedSource.Size || before.ModifiedUtc != expectedSource.ModifiedUtc)
            throw new SourceChangedException("Source foi alterado antes do início da cópia.");

        var dir = Path.GetDirectoryName(targetPath)!;
        _fs.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".synctmp");

        try
        {
            await _fs.CopyFileAsync(sourcePath, temp, progress, ct);

            var after = _fs.GetItem(sourcePath) ?? throw new SourceChangedException("Source desapareceu durante a cópia.");
            if (after.Size != before.Size || after.ModifiedUtc != before.ModifiedUtc)
                throw new SourceChangedException("Source foi alterado durante a cópia.");

            var copied = _fs.GetItem(temp);
            if (copied is null || copied.Size != before.Size)
                throw new IOException($"Cópia incompleta: esperado {before.Size} bytes, obtido {copied?.Size ?? 0}.");

            if (options.VerifyHash)
            {
                var hs = _fs.ComputeHashAsync(sourcePath, ct);
                var ht = _fs.ComputeHashAsync(temp, ct);
                var h = await Task.WhenAll(hs, ht);
                if (h[0] != h[1]) throw new IOException("O hash SHA-256 da cópia não confere com o Source.");
            }

            if (options.PreserveTimestamps) _fs.SetModifiedUtc(temp, before.ModifiedUtc);
            var keep = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
            _fs.SetAttributes(temp, options.PreserveAttributes ? before.Attributes & keep : FileAttributes.Normal);

            ct.ThrowIfCancellationRequested();
            if (options.BeforeReplace is not null) await options.BeforeReplace(ct);

            _fs.MoveFile(temp, targetPath, overwrite: true);
            return before.Size;
        }
        finally
        {
            try { if (_fs.GetItem(temp) is not null) _fs.DeleteFile(temp); }
            catch { /* melhor esforço: o temporário órfão é inofensivo e identificável (.synctmp) */ }
        }
    }
}
