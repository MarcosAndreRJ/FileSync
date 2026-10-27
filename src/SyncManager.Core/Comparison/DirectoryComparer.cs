using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Comparison;

/// <summary>Cruza os resultados de scan de Source e Target e produz o <see cref="ComparisonResult"/>.</summary>
public sealed class DirectoryComparer
{
    private readonly IFileComparer _comparer;

    public DirectoryComparer(IFileComparer comparer) => _comparer = comparer;

    /// <param name="states">Histórico da última sincronização (só na bidirecional). Null = método sem histórico.</param>
    public async Task<ComparisonResult> CompareAsync(ScanResult source, ScanResult target, CompareOptions options,
        IProgress<int>? progress, CancellationToken ct, IReadOnlyDictionary<string, FileSyncState>? states = null)
    {
        var s = source.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var t = target.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        // Caminhos ilegíveis (ou links ignorados) de qualquer lado contaminam tudo que está abaixo deles,
        // nos dois lados: nunca planejamos cópia/exclusão sobre algo que não conseguimos ler por inteiro.
        var unreliable = new Dictionary<string, UnreliablePath>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in source.Unreliable.Concat(target.Unreliable))
        {
            if (!unreliable.TryGetValue(u.RelativePath, out var existing) ||
                (existing.Status == ComparisonStatus.Ignored && u.Status == ComparisonStatus.Error))
                unreliable[u.RelativePath] = u;
        }

        var keys = new HashSet<string>(s.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(t.Keys);
        keys.UnionWith(unreliable.Keys);
        var ordered = keys.ToList();
        ordered.Sort(StringComparer.OrdinalIgnoreCase);

        // Conflitos de tipo (arquivo × pasta) são decididos só pelos metadados; conhecê-los antes permite
        // calcular os itens de forma independente (e, portanto, em paralelo).
        var typeConflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in ordered)
            if (s.TryGetValue(rel, out var se) && t.TryGetValue(rel, out var te) && se.IsDirectory != te.IsDirectory)
                typeConflicts.Add(rel);

        var items = new ComparisonItem[ordered.Count];
        var done = 0;

        async ValueTask ComputeAsync(int i, CancellationToken token)
        {
            var rel = ordered[i];
            s.TryGetValue(rel, out var se);
            t.TryGetValue(rel, out var te);
            var state = states is not null && states.TryGetValue(rel, out var st) ? st : null;

            var u = FindUnreliable(rel, unreliable);
            ComparisonItem item;
            if (u is not null)
                item = FileComparer.Build(rel, se, te, options, u.Status, u.Message);
            else if (IsInsideAny(rel, typeConflicts))
                item = FileComparer.Build(rel, se, te, options, ComparisonStatus.Ignored, "Está dentro de um item em conflito; nada será feito aqui");
            else
                item = await CompareOneAsync(rel, se, te, options, state, states is not null, token);

            items[i] = item;
            var n = Interlocked.Increment(ref done);
            if (n % 200 == 0) progress?.Report(n);
        }

        // Leitura de conteúdo (hash) é o único custo alto: só aí vale paralelizar; sem hash o laço simples é mais barato.
        if (options.VerifyContentByHash && options.MaxDegreeOfParallelism > 1)
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Count),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = ct },
                async (i, token) => await ComputeAsync(i, token));
        }
        else
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ComputeAsync(i, ct);
            }
        }

        // Itens do histórico que sumiram dos dois lados (e não estão sob um caminho ilegível) são obsoletos.
        List<string>? stale = null;
        if (states is not null)
        {
            foreach (var key in states.Keys)
            {
                if (s.ContainsKey(key) || t.ContainsKey(key)) continue;
                if (FindUnreliable(key, unreliable) is not null) continue;
                (stale ??= new List<string>()).Add(key);
            }
        }

        progress?.Report(done);
        return new ComparisonResult(options.SourceRoot, options.TargetRoot, items, source.Entries.Count, target.Entries.Count, stale);
    }

    /// <summary>Compara um único item (e, se houver histórico, marca quem mudou desde a última sincronização).</summary>
    public async Task<ComparisonItem> CompareOneAsync(string rel, FileEntry? s, FileEntry? t, CompareOptions options,
        FileSyncState? state, bool trackState, CancellationToken ct)
    {
        // Atalho de desempenho: nos dois lados tamanho+data iguais ao estado guardado e hashes guardados iguais
        // → idêntico sem ler o conteúdo (evita rehash de arquivos que não mudaram).
        if (state is not null && s is { IsDirectory: false } && t is { IsDirectory: false } && options.VerifyContentByHash &&
            state.LastSourceHash is not null && string.Equals(state.LastSourceHash, state.LastTargetHash, StringComparison.OrdinalIgnoreCase) &&
            FileComparer.MatchesBase(s, state.LastSourceSize, state.LastSourceModified, options.TimestampTolerance) &&
            FileComparer.MatchesBase(t, state.LastTargetSize, state.LastTargetModified, options.TimestampTolerance))
        {
            return FileComparer.Build(rel, s, t, options, ComparisonStatus.Identical, null) with
            {
                HasSyncState = true, SourceChanged = false, TargetChanged = false
            };
        }

        var item = await _comparer.CompareAsync(rel, s, t, options, ct);
        if (!trackState || item.Status is ComparisonStatus.Error or ComparisonStatus.Ignored) return item;

        try
        {
            bool? sourceChanged = null, targetChanged = null;
            if (state is not null)
            {
                if (s is not null) sourceChanged = await _comparer.HasChangedAsync(s, state.LastSourceSize, state.LastSourceModified, state.LastSourceHash, options, ct);
                if (t is not null) targetChanged = await _comparer.HasChangedAsync(t, state.LastTargetSize, state.LastTargetModified, state.LastTargetHash, options, ct);
            }
            return item with { HasSyncState = state is not null, SourceChanged = sourceChanged, TargetChanged = targetChanged };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item with { Status = ComparisonStatus.Error, Message = ex.Message };
        }
    }

    private static bool IsInsideAny(string rel, HashSet<string> set)
    {
        if (set.Count == 0) return false;
        var cur = rel;
        while (true)
        {
            var idx = cur.LastIndexOf('\\');
            if (idx < 0) return false;
            cur = cur[..idx];
            if (set.Contains(cur)) return true;
        }
    }

    private static UnreliablePath? FindUnreliable(string rel, Dictionary<string, UnreliablePath> map)
    {
        if (map.Count == 0) return null;
        var cur = rel;
        while (true)
        {
            if (map.TryGetValue(cur, out var u)) return u;
            var idx = cur.LastIndexOf('\\');
            if (idx < 0) return null;
            cur = cur[..idx];
        }
    }
}
