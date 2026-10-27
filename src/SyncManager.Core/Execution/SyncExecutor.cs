using System.Collections.Concurrent;
using System.Diagnostics;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Core.Execution;

/// <summary>
/// Único componente que altera arquivos. Executa somente o que está no <see cref="SyncPlan"/>, revalidando
/// cada operação imediatamente antes de executá-la e isolando falhas por arquivo.
/// </summary>
public sealed class SyncExecutor : ISyncExecutor
{
    private const int ErrorDiskFull = 112;          // ERROR_DISK_FULL
    private const int ErrorHandleDiskFull = 39;     // ERROR_HANDLE_DISK_FULL

    private readonly IFileSystem _fs;
    private readonly ILogService _log;
    private readonly TimeProvider _time;
    private readonly SafeFileCopier _copier;

    public SyncExecutor(IFileSystem fs, ILogService log, TimeProvider time)
    {
        _fs = fs;
        _log = log;
        _time = time;
        _copier = new SafeFileCopier(fs);
    }

    public async Task<ExecutionResult> ExecuteAsync(SyncPlan plan, SyncProfile profile,
        IProgress<ExecutionProgress>? progress, CancellationToken ct)
    {
        var run = new Run(plan, profile, progress, _time.GetUtcNow());
        _log.Info("PROFILE START", $"{profile.Name} | {plan.Method} | {plan.SourceRoot} → {plan.TargetRoot}");

        if (plan.IsBlocked) return Finish(run, ExecutionStatus.Failed, plan.BlockedReason, ct);

        var executable = plan.Operations.Where(o => o.IsExecutable).ToList();
        try
        {
            // O Source só precisa aceitar escrita se o plano realmente altera o Source (sincronização bidirecional, setas, conflitos).
            var needSourceWrite = executable.Any(o => o.WritesSource);
            var src = await _fs.CheckRootAsync(plan.SourceRoot, needSourceWrite, ct);
            if (!src.IsUsable(needSourceWrite))
                return Finish(run, ExecutionStatus.Failed,
                    $"Source indisponível{(needSourceWrite ? " ou sem permissão de escrita" : "")}: {src.Error ?? plan.SourceRoot}. Nada foi alterado.", ct);
            var tgt = await _fs.CheckRootAsync(plan.TargetRoot, true, ct);
            if (!tgt.IsUsable(true))
                return Finish(run, ExecutionStatus.Failed, $"Target indisponível ou sem permissão de escrita: {tgt.Error ?? plan.TargetRoot}. Nada foi alterado.", ct);
        }
        catch (OperationCanceledException)
        {
            return Finish(run, ExecutionStatus.Cancelled, "Cancelado antes de iniciar.", ct);
        }

        run.TotalOps = executable.Count;
        run.TotalBytes = executable.Where(o => o.Type is SyncOperationType.Copy or SyncOperationType.Update).Sum(o => o.Bytes);

        var byPath = StringComparer.OrdinalIgnoreCase;
        var createDirs = executable.Where(o => o.Type == SyncOperationType.CreateDirectory).OrderBy(o => o.RelativePath, byPath);
        var transfers = executable.Where(o => o.Type is SyncOperationType.Copy or SyncOperationType.Update).ToList();
        var deletes = executable.Where(o => o.Type == SyncOperationType.Delete).OrderBy(o => o.RelativePath, byPath);
        var deleteDirs = executable.Where(o => o.Type == SyncOperationType.DeleteDirectory)
            .OrderByDescending(o => PathUtil.Depth(o.RelativePath)).ThenBy(o => o.RelativePath, byPath);

        try
        {
            await RunSequentialAsync(run, createDirs, ct);
            // Estágios em ordem: operações do mesmo estágio rodam em paralelo; o estágio seguinte só começa quando o anterior termina
            // (ex.: "manter ambos" lê a versão do Target no estágio 0 antes de ela ser sobrescrita no estágio 1).
            foreach (var stage in transfers.GroupBy(o => o.Stage).OrderBy(g => g.Key))
            {
                if (run.Token(ct).IsCancellationRequested) break;
                await RunParallelAsync(run, stage.ToList(), ct);
            }
            await RunSequentialAsync(run, deletes, ct);
            await RunSequentialAsync(run, deleteDirs, ct);
        }
        catch (OperationCanceledException)
        {
            // cancelamento do usuário ou abortagem interna: o estado final é decidido em Finish
        }

        run.Report(force: true);
        return Finish(run, null, null, ct);
    }

    // ---------------------------------------------------------------- orquestração

    private async Task RunSequentialAsync(Run run, IEnumerable<SyncOperation> ops, CancellationToken ct)
    {
        foreach (var op in ops)
        {
            if (run.Token(ct).IsCancellationRequested) break;
            await RunOneAsync(run, op, ct);
        }
    }

    private async Task RunParallelAsync(Run run, List<SyncOperation> ops, CancellationToken ct)
    {
        if (ops.Count == 0) return;
        var options = new ParallelOptions { MaxDegreeOfParallelism = run.MaxParallel, CancellationToken = run.Token(ct) };
        await Parallel.ForEachAsync(ops, options, async (op, _) => await RunOneAsync(run, op, ct));
    }

    private async Task RunOneAsync(Run run, SyncOperation op, CancellationToken ct)
    {
        var token = run.Token(ct);
        if (token.IsCancellationRequested) return;
        run.CurrentFile = op.RelativePath;
        run.Report(force: false);

        OperationResult result;
        try
        {
            result = await ExecuteWithRetryAsync(run, op, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new OperationResult(op, OperationOutcome.Cancelled, "Cancelado", 0);
        }
        catch (SourceChangedException ex)
        {
            result = new OperationResult(op, OperationOutcome.Warning, ex.Message + " Execute Comparar novamente.", 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            result = new OperationResult(op, OperationOutcome.Warning, "Item desapareceu durante a execução.", 0);
        }
        catch (UnauthorizedAccessException ex)
        {
            result = new OperationResult(op, OperationOutcome.Failed, "Acesso negado: " + ex.Message, 0);
        }
        catch (PathTooLongException ex)
        {
            result = new OperationResult(op, OperationOutcome.Failed, "Caminho longo demais: " + ex.Message, 0);
        }
        catch (IOException ex)
        {
            var hr = ex.HResult & 0xFFFF;
            if (hr is ErrorDiskFull or ErrorHandleDiskFull)
            {
                result = new OperationResult(op, OperationOutcome.Failed, "Disco cheio no destino.", 0);
                run.Abort("Disco cheio no destino.");
            }
            else if (hr is 32 or 33)
                result = new OperationResult(op, OperationOutcome.Failed, "Arquivo em uso/bloqueado por outro processo.", 0);
            else
                result = new OperationResult(op, OperationOutcome.Failed, ex.Message, 0);
        }
        catch (Exception ex)
        {
            result = new OperationResult(op, OperationOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}", 0);
        }

        run.Record(result);
        LogResult(result);
        run.Report(force: false);

        if (result.Outcome == OperationOutcome.Failed && !run.IsAborted)
            await HandleFailureAsync(run, ct);
    }

    /// <summary>Repete a operação diante de erro TEMPORÁRIO (rede instável, bloqueio momentâneo); erro definitivo sobe na hora.</summary>
    private async Task<OperationResult> ExecuteWithRetryAsync(Run run, SyncOperation op, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteOperationAsync(run, op, token);
            }
            catch (Exception ex) when (attempt < run.Profile.TransientRetryCount && TransientErrors.IsTransient(ex) && !token.IsCancellationRequested)
            {
                _log.Warning("RETRY", $"{op.RelativePath} | tentativa {attempt + 2} de {run.Profile.TransientRetryCount + 1} | {ex.Message}");
                await Task.Delay(run.Profile.TransientRetryDelayMs * (attempt + 1), token);
            }
        }
    }

    /// <summary>Após uma falha decide se vale continuar: pára se o perfil manda ou se Source/Target sumiram.</summary>
    private async Task HandleFailureAsync(Run run, CancellationToken ct)
    {
        if (!run.Profile.ContinueOnError)
        {
            run.Abort("Interrompido após erro (\"continuar em caso de erro\" está desativado).");
            return;
        }

        await run.RootCheckGate.WaitAsync(CancellationToken.None);
        try
        {
            if (run.IsAborted) return;
            var src = await SafeCheckAsync(run.Plan.SourceRoot, false);
            var tgt = await SafeCheckAsync(run.Plan.TargetRoot, false);
            if (!src.IsUsable(false)) run.Abort("Source ficou indisponível durante a execução.");
            else if (!tgt.IsUsable(false)) run.Abort("Target ficou indisponível durante a execução.");
        }
        finally { run.RootCheckGate.Release(); }
    }

    private async Task<RootStatus> SafeCheckAsync(string root, bool write)
    {
        try { return await _fs.CheckRootAsync(root, write, CancellationToken.None); }
        catch (Exception ex) { return new RootStatus(false, false, false, ex.Message); }
    }

    // ---------------------------------------------------------------- operações

    private static (long? Size, DateTime? Modified) Snapshot(ComparisonItem i, FileSide side) =>
        side == FileSide.Source ? (i.SourceSize, i.SourceModified) : (i.TargetSize, i.TargetModified);

    private async Task<OperationResult> ExecuteOperationAsync(Run run, SyncOperation op, CancellationToken ct)
    {
        switch (op.Type)
        {
            case SyncOperationType.CreateDirectory:
                _fs.CreateDirectory(op.TargetPath!);
                return new OperationResult(op, OperationOutcome.Succeeded, null, 0);

            case SyncOperationType.Copy:
            case SyncOperationType.Update:
                return await TransferAsync(run, op, ct);

            case SyncOperationType.Delete:
                return Delete(run, op);

            case SyncOperationType.DeleteDirectory:
                return DeleteDirectory(op);

            default:
                return new OperationResult(op, OperationOutcome.Skipped, "Sem ação", 0);
        }
    }

    private async Task<OperationResult> TransferAsync(Run run, SyncOperation op, CancellationToken ct)
    {
        var (originSize, originModified) = Snapshot(op.Item, op.From);

        var originNow = _fs.GetItem(op.SourcePath!);
        if (originNow is null || originNow.IsDirectory)
            return new OperationResult(op, OperationOutcome.Warning, "O arquivo de origem não existe mais; ignorado.", 0);
        if (originNow.Size != originSize || originNow.ModifiedUtc != originModified)
            return new OperationResult(op, OperationOutcome.Warning, "A origem mudou depois da comparação; ignorado. Compare novamente.", 0);

        var destNow = _fs.GetItem(op.TargetPath!);
        if (op.Type == SyncOperationType.Copy)
        {
            if (destNow is not null)
                return new OperationResult(op, OperationOutcome.Warning, "O destino passou a existir depois da comparação; não foi sobrescrito. Compare novamente.", 0);
        }
        else
        {
            var (destSize, destModified) = Snapshot(op.Item, op.To);
            if (destNow is null || destNow.IsDirectory || destNow.Size != destSize || destNow.ModifiedUtc != destModified)
                return new OperationResult(op, OperationOutcome.Warning, "O destino mudou depois da comparação; não foi sobrescrito. Compare novamente.", 0);
        }

        var copyOptions = new SafeCopyOptions(
            run.Profile.PreserveTimestamps, run.Profile.PreserveAttributes, run.Profile.VerifyCopyByHash,
            BeforeReplace: op.Type == SyncOperationType.Update && run.Profile.BackupBeforeOverwrite
                ? c => BackupOverwrittenAsync(run, op, c)
                : null);

        var bytes = await _copier.CopyAsync(op.SourcePath!, op.TargetPath!, originNow, copyOptions,
            new InlineProgress<long>(run.AddBytes), ct);

        return new OperationResult(op, OperationOutcome.Succeeded, null, bytes);
    }

    /// <summary>Guarda a versão que está prestes a ser sobrescrita na pasta de segurança do lado do destino.</summary>
    private async Task BackupOverwrittenAsync(Run run, SyncOperation op, CancellationToken ct)
    {
        var dest = UniquePath(Path.Combine(run.SafetyRoot(op.To), op.RelativePath));
        _fs.CreateDirectory(Path.GetDirectoryName(dest)!);
        await _fs.CopyFileAsync(op.TargetPath!, dest, null, ct);
        run.MarkSafetyUsed(op.To);
    }

    private OperationResult Delete(Run run, SyncOperation op)
    {
        var (size, modified) = Snapshot(op.Item, op.To);
        var now = _fs.GetItem(op.TargetPath!);
        var where = op.To == FileSide.Source ? "Source" : "Target";
        if (now is null)
            return new OperationResult(op, OperationOutcome.Warning, $"Já não existe no {where}.", 0);
        if (now.IsDirectory || now.Size != size || now.ModifiedUtc != modified)
            return new OperationResult(op, OperationOutcome.Warning, $"O item mudou no {where} depois da comparação; não foi excluído. Compare novamente.", 0);

        switch (run.Profile.DeleteStrategy)
        {
            case DeleteStrategy.SafetyFolder:
                var dest = UniquePath(Path.Combine(run.SafetyRoot(op.To), op.RelativePath));
                _fs.CreateDirectory(Path.GetDirectoryName(dest)!);
                _fs.MoveFile(op.TargetPath!, dest, overwrite: false);
                run.MarkSafetyUsed(op.To);
                return new OperationResult(op, OperationOutcome.Succeeded, "Movido para a pasta de segurança.", 0);

            case DeleteStrategy.RecycleBin:
                _fs.SendToRecycleBin(op.TargetPath!);
                return new OperationResult(op, OperationOutcome.Succeeded, "Enviado para a Lixeira.", 0);

            default:
                _fs.DeleteFile(op.TargetPath!);
                return new OperationResult(op, OperationOutcome.Succeeded, "Excluído permanentemente.", 0);
        }
    }

    private OperationResult DeleteDirectory(SyncOperation op)
    {
        var now = _fs.GetItem(op.TargetPath!);
        if (now is null)
            return new OperationResult(op, OperationOutcome.Warning, "Já não existe.", 0);
        if (!now.IsDirectory)
            return new OperationResult(op, OperationOutcome.Warning, "Não é mais uma pasta; ignorado.", 0);
        try
        {
            _fs.DeleteEmptyDirectory(op.TargetPath!);   // nunca recursivo: só remove pasta vazia
            return new OperationResult(op, OperationOutcome.Succeeded, null, 0);
        }
        catch (IOException ex) when (ex is not DirectoryNotFoundException)
        {
            return new OperationResult(op, OperationOutcome.Warning, "Pasta mantida: ainda contém itens (ex.: filtrados ou ignorados).", 0);
        }
    }

    private string UniquePath(string path)
    {
        if (_fs.GetItem(path) is null) return path;
        return path + "." + Guid.NewGuid().ToString("N")[..6];
    }

    // ---------------------------------------------------------------- resultado e log

    private void LogResult(OperationResult r)
    {
        var op = r.Operation;
        var side = op.To == FileSide.Source ? " [Source]" : "";
        var label = op.Type switch
        {
            SyncOperationType.Copy => "COPY",
            SyncOperationType.Update => "UPDATE",
            SyncOperationType.Delete => "DELETE" + side,
            SyncOperationType.CreateDirectory => "CREATE DIR",
            SyncOperationType.DeleteDirectory => "DELETE DIR" + side,
            _ => op.Type.ToString().ToUpperInvariant()
        };
        var detail = op.Type is SyncOperationType.Copy or SyncOperationType.Update
            ? $"{op.SourcePath} → {op.TargetPath}"
            : op.TargetPath ?? op.RelativePath;

        switch (r.Outcome)
        {
            case OperationOutcome.Succeeded: _log.Info(label, detail); break;
            case OperationOutcome.Warning: _log.Warning(label + " SKIPPED", $"{detail} | {r.Message}"); break;
            case OperationOutcome.Failed: _log.Error("ERROR", $"{op.RelativePath} | {r.Message}"); break;
            case OperationOutcome.Cancelled: break;
        }
    }

    private ExecutionResult Finish(Run run, ExecutionStatus? forced, string? message, CancellationToken ct)
    {
        var results = run.Results.ToList();
        int Count(SyncOperationType t) => results.Count(r => r.Outcome == OperationOutcome.Succeeded && r.Operation.Type == t);

        var errors = results.Count(r => r.Outcome == OperationOutcome.Failed);
        var warnings = results.Count(r => r.Outcome == OperationOutcome.Warning) + run.Plan.ErrorCount;
        var executable = run.Plan.ExecutableCount;
        var notExecuted = forced is null ? executable - results.Count : executable;

        ExecutionStatus status;
        if (forced is not null) status = forced.Value;
        else if (run.AbortReason is not null) status = ExecutionStatus.Failed;
        else if (ct.IsCancellationRequested) status = ExecutionStatus.Cancelled;
        else if (errors > 0 || warnings > 0) status = ExecutionStatus.SuccessWithWarnings;
        else if (run.Plan.ConflictCount > 0) status = ExecutionStatus.ConflictsUnresolved;
        else status = ExecutionStatus.Success;

        message ??= run.AbortReason;
        var finished = _time.GetUtcNow();
        var safety = run.SafetyUsedTarget ? run.SafetyRoot(FileSide.Target) : run.SafetyUsedSource ? run.SafetyRoot(FileSide.Source) : null;

        var result = new ExecutionResult
        {
            Status = status,
            StartedAt = run.StartedAt,
            FinishedAt = finished,
            Message = message,
            SafetyFolder = safety,
            FilesCompared = run.Plan.Operations.Count,
            FilesCopied = Count(SyncOperationType.Copy),
            FilesUpdated = Count(SyncOperationType.Update),
            FilesDeleted = Count(SyncOperationType.Delete) + Count(SyncOperationType.DeleteDirectory),
            FilesSkipped = run.Plan.SkipCount,
            Conflicts = run.Plan.ConflictCount,
            Errors = errors,
            Warnings = warnings,
            NotExecuted = Math.Max(0, notExecuted),
            BytesTransferred = results.Sum(r => r.Bytes),
            Results = results
        };

        var summary = $"{status} | copiados {result.FilesCopied}, atualizados {result.FilesUpdated}, excluídos {result.FilesDeleted}, " +
                      $"erros {errors}, avisos {warnings}, conflitos {result.Conflicts}" + (message is null ? "" : $" | {message}");
        if (status is ExecutionStatus.Failed) _log.Error("PROFILE FINISHED", summary);
        else if (status is ExecutionStatus.SuccessWithWarnings or ExecutionStatus.Cancelled or ExecutionStatus.ConflictsUnresolved)
            _log.Warning("PROFILE FINISHED", summary);
        else _log.Info("PROFILE FINISHED", summary);
        if (run.SafetyUsedTarget) _log.Info("SAFETY FOLDER", run.SafetyRoot(FileSide.Target));
        if (run.SafetyUsedSource) _log.Info("SAFETY FOLDER", run.SafetyRoot(FileSide.Source));

        return result;
    }

    // ---------------------------------------------------------------- estado da execução

    private sealed class Run
    {
        private readonly IProgress<ExecutionProgress>? _progress;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly CancellationTokenSource _abort = new();
        private readonly string _stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        private CancellationTokenSource? _linked;
        private long _completed;
        private long _bytes;
        private long _lastReportMs = -1000;

        public Run(SyncPlan plan, SyncProfile profile, IProgress<ExecutionProgress>? progress, DateTimeOffset startedAt)
        {
            Plan = plan;
            Profile = profile;
            _progress = progress;
            StartedAt = startedAt;
            var unc = PathUtil.IsUnc(plan.SourceRoot) || PathUtil.IsUnc(plan.TargetRoot);
            MaxParallel = profile.MaxParallelFileOperations > 0 ? profile.MaxParallelFileOperations : (unc ? 2 : 4);
        }

        public SyncPlan Plan { get; }
        public SyncProfile Profile { get; }
        public DateTimeOffset StartedAt { get; }
        public int MaxParallel { get; }
        public int TotalOps { get; set; }
        public long TotalBytes { get; set; }
        public volatile bool SafetyUsedTarget;
        public volatile bool SafetyUsedSource;
        public volatile string? CurrentFile;
        public volatile string? AbortReason;
        public bool IsAborted => AbortReason is not null;
        public ConcurrentQueue<OperationResult> Results { get; } = new();
        public SemaphoreSlim RootCheckGate { get; } = new(1, 1);

        /// <summary>
        /// Pasta de segurança do lado indicado: a versão que perde (excluída/sobrescrita) fica junto do lado que a perdeu.
        /// Padrão: &lt;raiz do lado&gt;\.SyncBackup\aaaa-mm-dd_hhmmss. Com pasta personalizada, o lado Source usa a subpasta "_source".
        /// </summary>
        public string SafetyRoot(FileSide side)
        {
            if (!string.IsNullOrWhiteSpace(Profile.SafetyFolderPath))
            {
                var custom = Path.Combine(PathUtil.NormalizeRoot(Profile.SafetyFolderPath), _stamp);
                return side == FileSide.Source ? Path.Combine(custom, "_source") : custom;
            }
            return Path.Combine(side == FileSide.Source ? Plan.SourceRoot : Plan.TargetRoot, ".SyncBackup", _stamp);
        }

        public void MarkSafetyUsed(FileSide side)
        {
            if (side == FileSide.Source) SafetyUsedSource = true; else SafetyUsedTarget = true;
        }

        /// <summary>Token que representa cancelamento do usuário OU abortagem interna.</summary>
        public CancellationToken Token(CancellationToken user)
        {
            if (_linked is null)
                Interlocked.CompareExchange(ref _linked, CancellationTokenSource.CreateLinkedTokenSource(user, _abort.Token), null);
            return _linked.Token;
        }

        public void Abort(string reason)
        {
            AbortReason ??= reason;
            _abort.Cancel();
        }

        public void AddBytes(long n) => Interlocked.Add(ref _bytes, n);

        public void Record(OperationResult r)
        {
            Results.Enqueue(r);
            Interlocked.Increment(ref _completed);
        }

        public void Report(bool force)
        {
            if (_progress is null) return;
            var now = _sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReportMs);
            if (!force && now - last < 100) return;
            if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last && !force) return;

            var bytes = Interlocked.Read(ref _bytes);
            var secs = Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
            _progress.Report(new ExecutionProgress(CurrentFile, (int)Interlocked.Read(ref _completed), TotalOps,
                bytes, TotalBytes, bytes / secs, _sw.Elapsed));
        }
    }
}
