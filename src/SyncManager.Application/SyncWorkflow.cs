using SyncManager.Core.Abstractions;
using SyncManager.Core.Comparison;
using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Application;

public sealed class SyncValidationException : Exception
{
    public SyncValidationException(IReadOnlyList<string> errors) : base(string.Join(Environment.NewLine, errors)) =>
        Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Resultado de Compare + Plan. Carrega o perfil "congelado" usado, para que a execução use o mesmo.</summary>
public sealed record CompareOutcome(SyncProfile Profile, ComparisonResult Comparison, SyncPlan Plan);

/// <summary>
/// Orquestra COMPARE → PLAN e EXECUTE. Ponto de entrada único para a UI e para a CLI:
/// nenhum chamador fala diretamente com o sistema de arquivos.
/// </summary>
public sealed class SyncWorkflow
{
    public const string SafetyFolderName = ".SyncBackup";

    private readonly IFileSystemScanner _scanner;
    private readonly DirectoryComparer _comparer;
    private readonly ISyncPlanner _planner;
    private readonly ISyncExecutor _executor;
    private readonly PreflightValidator _preflight;
    private readonly ILogService _log;
    private readonly IExecutionHistoryRepository _history;
    private readonly SettingsService _settings;
    private readonly IProfileLockProvider _locks;
    private readonly IFileSyncStateRepository _states;
    private readonly IFileSystem _fs;

    public SyncWorkflow(IFileSystemScanner scanner, DirectoryComparer comparer, ISyncPlanner planner,
        ISyncExecutor executor, PreflightValidator preflight, ILogService log,
        IExecutionHistoryRepository history, SettingsService settings, IProfileLockProvider locks,
        IFileSyncStateRepository states, IFileSystem fs)
    {
        _scanner = scanner;
        _comparer = comparer;
        _planner = planner;
        _executor = executor;
        _preflight = preflight;
        _log = log;
        _history = history;
        _settings = settings;
        _locks = locks;
        _states = states;
        _fs = fs;
    }

    // ------------------------------------------------------------------ COMPARE → PLAN

    public async Task<CompareOutcome> CompareAsync(SyncProfile profileInput, IProgress<string>? status, CancellationToken ct)
    {
        var profile = profileInput.Clone();
        if (profile.MaxParallelFileOperations <= 0)
        {
            var app = await _settings.LoadAsync(ct);
            var network = PathUtil.IsUnc(profile.SourcePath.Trim()) || PathUtil.IsUnc(profile.TargetPath.Trim());
            profile.MaxParallelFileOperations = network ? app.MaxParallelNetwork : app.MaxParallelLocal;
        }

        var check = await _preflight.ValidateAsync(profile, requireWrite: false, ct);
        if (!check.IsValid)
        {
            foreach (var e in check.Errors) _log.Error("PREFLIGHT", e);
            throw new SyncValidationException(check.Errors);
        }

        var source = PathUtil.NormalizeRoot(profile.SourcePath);
        var target = PathUtil.NormalizeRoot(profile.TargetPath);
        _log.Info("COMPARE START", $"{source} ⇄ {target} | {profile.Method}");

        var scanOptions = new ScanOptions(profile.IncludeSubfolders, profile.IncludeHidden, profile.IncludeSystem,
            new PathFilter(profile.ExcludePatterns, profile.IncludePatterns, profile.FilterCaseSensitive, profile.FilterWildcards), new[] { SafetyFolderName },
            profile.TransientRetryCount, Math.Max(1, profile.TransientRetryDelayMs));

        status?.Report("Lendo Source e Target...");
        var progress = new SyncedProgress<ScanProgress>(p => status?.Report($"Lendo... {p.ItemsFound:N0} itens ({p.CurrentDirectory})"));

        var sourceTask = _scanner.ScanAsync(source, scanOptions, progress, ct);
        var targetTask = _scanner.ScanAsync(target, scanOptions, progress, ct);
        var scans = await Task.WhenAll(sourceTask, targetTask);

        var states = await LoadStatesAsync(profile, ct);
        var options = BuildCompareOptions(profile, source, target);

        status?.Report("Comparando...");
        var comparison = await _comparer.CompareAsync(scans[0], scans[1], options,
            new SyncedProgress<int>(n => status?.Report($"Comparando... {n:N0} itens")), ct, states);

        var plan = _planner.Build(comparison, profile, states?.Count ?? 0);

        _log.Info("COMPARE FINISHED",
            $"analisados {comparison.Analyzed:N0}, copiar {plan.CopyCount}, atualizar {plan.UpdateCount}, excluir {plan.DeleteCount}, " +
            $"ignorar {plan.SkipCount}, conflitos {plan.ConflictCount}, erros {plan.ErrorCount}");
        if (plan.IsBlocked) _log.Warning("PLAN BLOCKED", plan.BlockedReason);
        return new CompareOutcome(profile, comparison, plan);
    }

    private static CompareOptions BuildCompareOptions(SyncProfile profile, string source, string target) =>
        new(source, target, TimeSpan.FromSeconds(Math.Max(0, profile.TimestampToleranceSeconds)),
            profile.VerifyContentByHash, Math.Clamp(profile.MaxParallelFileOperations, 1, 32));

    /// <summary>Histórico da última sincronização — só existe para a bidirecional. Perfil não salvo = sem histórico.</summary>
    private async Task<IReadOnlyDictionary<string, FileSyncState>?> LoadStatesAsync(SyncProfile profile, CancellationToken ct)
    {
        if (profile.Method != SyncMethod.Synchronization) return null;
        if (profile.Id <= 0) return new Dictionary<string, FileSyncState>(StringComparer.OrdinalIgnoreCase);
        return await _states.GetAllAsync(profile.Id, ct);
    }

    // ------------------------------------------------------------------ atualizar itens individuais

    /// <summary>
    /// Relê do disco só os itens indicados (por exemplo, depois de uma cópia manual) e atualiza comparação e plano,
    /// preservando tudo que o usuário já decidiu para os demais itens. Bem mais barato que recomparar as pastas inteiras.
    /// </summary>
    public async Task<CompareOutcome> RefreshItemsAsync(CompareOutcome outcome, IEnumerable<string> relativePaths, CancellationToken ct)
    {
        var profile = outcome.Profile;
        var paths = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        var options = BuildCompareOptions(profile, outcome.Comparison.SourceRoot, outcome.Comparison.TargetRoot);
        var states = await LoadStatesAsync(profile, ct);

        var oldItems = outcome.Comparison.Items.ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
        var newItems = new Dictionary<string, ComparisonItem>(StringComparer.OrdinalIgnoreCase);
        var sourceCount = outcome.Comparison.SourceEntryCount;
        var targetCount = outcome.Comparison.TargetEntryCount;

        foreach (var rel in paths)
        {
            ct.ThrowIfCancellationRequested();
            var s = Stat(options.SourceRoot, rel);
            var t = Stat(options.TargetRoot, rel);

            oldItems.TryGetValue(rel, out var old);
            sourceCount += (s is null ? 0 : 1) - (old?.SourceExists == true ? 1 : 0);
            targetCount += (t is null ? 0 : 1) - (old?.TargetExists == true ? 1 : 0);

            if (s is null && t is null) { oldItems.Remove(rel); continue; }
            FileSyncState? state = null;
            states?.TryGetValue(rel, out state);
            newItems[rel] = await _comparer.CompareOneAsync(rel, s, t, options, state, states is not null, ct);
        }

        var items = oldItems.Values.Where(i => !newItems.ContainsKey(i.RelativePath)).Concat(newItems.Values)
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var comparison = new ComparisonResult(outcome.Comparison.SourceRoot, outcome.Comparison.TargetRoot, items,
            Math.Max(0, sourceCount), Math.Max(0, targetCount), outcome.Comparison.StaleStatePaths);

        var ops = outcome.Plan.Operations.Where(o => !paths.Contains(o.RelativePath)).ToList();
        ops.AddRange(newItems.Values.Select(i => _planner.PlanItem(i, profile)));
        ops.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        var plan = outcome.Plan.WithOperations(ops);
        return outcome with { Comparison = comparison, Plan = plan };
    }

    private FileEntry? Stat(string root, string rel)
    {
        try
        {
            var full = Path.Combine(root, rel);
            var item = _fs.GetItem(full);
            return item is null ? null : new FileEntry(rel, full, item.IsDirectory, item.IsDirectory ? 0 : item.Size, item.ModifiedUtc, item.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ EXECUTE

    /// <summary>Executa o plano e grava o histórico. Falha ao gravar o histórico nunca invalida a execução já feita.</summary>
    public async Task<ExecutionResult> ExecuteAsync(CompareOutcome outcome, IProgress<ExecutionProgress>? progress, CancellationToken ct)
    {
        // Perfis salvos não podem rodar em duplicidade (ex.: tarefa agendada + interface ao mesmo tempo).
        using var profileLock = outcome.Profile.Id > 0
            ? _locks.TryAcquire(outcome.Profile.Id) ?? throw new ProfileBusyException(outcome.Profile.Name)
            : null;

        var result = await _executor.ExecuteAsync(outcome.Plan, outcome.Profile, progress, ct);

        try
        {
            await RecordSyncStatesAsync(outcome, result);
        }
        catch (Exception ex)
        {
            _log.Warning("SYNC STATE", "Não foi possível gravar o histórico de sincronização (o próximo sync pode apontar conflitos): " + ex.Message);
        }

        try
        {
            var record = ExecutionHistory.From(outcome.Profile, outcome.Plan, result);
            // Sem CancellationToken: o histórico de uma execução cancelada também deve ser gravado.
            await _history.AddAsync(record, result.Results, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warning("HISTORY", "Não foi possível gravar o histórico: " + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// Depois de uma execução bidirecional, guarda o retrato de cada item que agora está sincronizado — é o que permite,
    /// no próximo sync, saber quem mudou. Itens que falharam ou deram aviso NÃO entram (continuam "não sincronizados").
    /// </summary>
    private async Task RecordSyncStatesAsync(CompareOutcome outcome, ExecutionResult result)
    {
        var plan = outcome.Plan;
        var profile = outcome.Profile;
        if (plan.Method != SyncMethod.Synchronization || profile.Id <= 0) return;
        if (result.Status == ExecutionStatus.Failed && result.Results.Count == 0) return;   // nada rodou (ex.: destino offline)

        var failed = new HashSet<string>(result.Results.Where(r => r.Outcome != OperationOutcome.Succeeded)
            .Select(r => r.Operation.RelativePath), StringComparer.OrdinalIgnoreCase);
        var synced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in result.Results.Where(r => r.Outcome == OperationOutcome.Succeeded))
        {
            switch (r.Operation.Type)
            {
                case SyncOperationType.Copy or SyncOperationType.Update or SyncOperationType.CreateDirectory:
                    synced.Add(r.Operation.RelativePath); break;
                case SyncOperationType.Delete or SyncOperationType.DeleteDirectory:
                    removed.Add(r.Operation.RelativePath); break;
            }
        }
        foreach (var op in plan.Operations.Where(o => o.RecordsState && o.Type == SyncOperationType.Skip))
            synced.Add(op.RelativePath);
        foreach (var stale in plan.StaleStatePaths) removed.Add(stale);

        synced.ExceptWith(failed);
        removed.ExceptWith(failed);
        removed.ExceptWith(synced);

        var now = DateTimeOffset.UtcNow;
        var upserts = new List<FileSyncState>(synced.Count);
        foreach (var rel in synced)
        {
            var state = await BuildStateAsync(profile, plan, rel, now);
            if (state is not null) upserts.Add(state);
        }

        await _states.UpsertManyAsync(upserts);
        await _states.DeleteManyAsync(profile.Id, removed.ToList());
        if (upserts.Count + removed.Count > 0)
            _log.Info("SYNC STATE", $"histórico atualizado: {upserts.Count} gravados, {removed.Count} removidos");
    }

    private async Task<FileSyncState?> BuildStateAsync(SyncProfile profile, SyncPlan plan, string rel, DateTimeOffset now)
    {
        try
        {
            var sp = Path.Combine(plan.SourceRoot, rel);
            var tp = Path.Combine(plan.TargetRoot, rel);
            var s = _fs.GetItem(sp);
            var t = _fs.GetItem(tp);
            if (s is null || t is null || s.IsDirectory != t.IsDirectory) return null;   // só registra o que existe igual dos dois lados

            string? sh = null, th = null;
            if (!s.IsDirectory && profile.VerifyContentByHash)
            {
                try
                {
                    sh = await _fs.ComputeHashAsync(sp, CancellationToken.None);
                    th = await _fs.ComputeHashAsync(tp, CancellationToken.None);
                }
                catch (IOException) { sh = th = null; }   // sem hash o estado ainda vale (tamanho + data)
            }

            return new FileSyncState(profile.Id, rel, s.IsDirectory,
                s.IsDirectory ? null : s.Size, t.IsDirectory ? null : t.Size,
                s.IsDirectory ? null : s.ModifiedUtc, t.IsDirectory ? null : t.ModifiedUtc, sh, th, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Esquece o histórico de sincronização do perfil: a próxima bidirecional será tratada como a primeira.</summary>
    public Task ResetSyncStateAsync(int profileId, CancellationToken ct = default) => _states.DeleteAllAsync(profileId, ct);

    /// <summary>IProgress que invoca o callback diretamente (o consumidor faz o marshalling, se precisar).</summary>
    private sealed class SyncedProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public SyncedProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}
