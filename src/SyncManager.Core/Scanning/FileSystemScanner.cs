using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Execution;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Core.Scanning;

/// <summary>
/// Varre uma raiz recursivamente em streaming. Nunca trata falha de leitura como "pasta vazia":
/// pastas ilegíveis são registradas como <see cref="UnreliablePath"/> e a raiz inacessível aborta o scan.
/// </summary>
public sealed class FileSystemScanner : IFileSystemScanner
{
    private readonly IFileSystem _fs;

    public FileSystemScanner(IFileSystem fs) => _fs = fs;

    public Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(root, options, progress, ct), ct);

    /// <summary>Lista a pasta repetindo em erros temporários de rede; erros definitivos sobem na hora.</summary>
    private List<FileSystemItem> EnumerateWithRetry(string full, ScanOptions o, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _fs.EnumerateDirectory(full).ToList();
            }
            catch (Exception ex) when (attempt < o.TransientRetries && TransientErrors.IsTransient(ex))
            {
                ct.WaitHandle.WaitOne(o.RetryDelayMs * (attempt + 1));
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private ScanResult Scan(string root, ScanOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var entries = new List<FileEntry>();
        var unreliable = new List<UnreliablePath>();
        var filtered = 0;
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((root, ""));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (full, rel) = stack.Pop();

            List<FileSystemItem> children;
            try
            {
                children = EnumerateWithRetry(full, o, ct);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (rel.Length == 0) throw new ScanFailedException(root, ex.Message, ex);

                unreliable.Add(new UnreliablePath(rel, ComparisonStatus.Error, ex.Message));
                // Uma falha pode significar que o compartilhamento inteiro caiu: nesse caso abortar em vez de seguir.
                var status = _fs.CheckRootAsync(root, false, ct).GetAwaiter().GetResult();
                if (!status.IsUsable(false))
                    throw new ScanFailedException(root, status.Error ?? "raiz ficou indisponível durante a leitura", ex);
                continue;
            }

            foreach (var child in children)
            {
                var childRel = PathUtil.Combine(rel, child.Name);

                if (rel.Length == 0 && child.IsDirectory && o.ExcludedRootFolderNames.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (child.IsDirectory && child.IsReparsePoint)
                {
                    unreliable.Add(new UnreliablePath(childRel, ComparisonStatus.Ignored, "Link simbólico/junction não é seguido"));
                    continue;
                }

                if ((!o.IncludeHidden && child.Attributes.HasFlag(FileAttributes.Hidden)) ||
                    (!o.IncludeSystem && child.Attributes.HasFlag(FileAttributes.System)) ||
                    o.Filter.IsExcluded(childRel, child.IsDirectory))
                {
                    filtered++;
                    continue;
                }

                if (child.IsDirectory)
                {
                    if (!o.IncludeSubfolders) continue;
                    entries.Add(new FileEntry(childRel, child.FullPath, true, 0, child.ModifiedUtc, child.Attributes));
                    stack.Push((child.FullPath, childRel));
                }
                else
                {
                    entries.Add(new FileEntry(childRel, child.FullPath, false, child.Size, child.ModifiedUtc, child.Attributes));
                }

                if (entries.Count % 1000 == 0) progress?.Report(new ScanProgress(entries.Count, full));
            }
        }

        progress?.Report(new ScanProgress(entries.Count, root));
        return new ScanResult(root, entries, unreliable, filtered);
    }
}
