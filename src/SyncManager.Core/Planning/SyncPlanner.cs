using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Planning;

/// <summary>
/// Função pura (sem I/O) que transforma comparação + perfil em um <see cref="SyncPlan"/>.
/// Cada item comparado gera exatamente uma operação (inclusive Skip), para que o Preview mostre tudo.
/// Nos métodos de replicação/espelho a decisão vem do status da comparação; na sincronização bidirecional vem do
/// confronto entre os dois lados e o histórico da última sincronização (ancestral comum).
/// </summary>
public sealed class SyncPlanner : ISyncPlanner
{
    public SyncPlan Build(ComparisonResult result, SyncProfile profile, int knownSyncStates = 0)
    {
        var sync = profile.Method == SyncMethod.Synchronization;
        var warnings = new List<string>();
        var ops = new List<SyncOperation>(result.Items.Count);
        foreach (var item in result.Items)
            ops.Add(PlanItem(item, profile));

        string? blocked = null;
        if (profile.Method == SyncMethod.MirrorSourceToTarget && result.SourceEntryCount == 0 && result.TargetEntryCount > 0)
            blocked = "O Source está vazio e o Target não. O Mirror apagaria todo o Target; " +
                      "isso normalmente indica Source errado ou indisponível. Plano bloqueado.";

        // Na bidirecional o risco é simétrico: um lado "vazio" (pasta desmontada, share offline que respondeu vazio)
        // contra um histórico cheio propagaria a exclusão de tudo para o outro lado.
        if (sync && knownSyncStates > 0 &&
            ((result.SourceEntryCount == 0 && result.TargetEntryCount > 0) || (result.TargetEntryCount == 0 && result.SourceEntryCount > 0)))
            blocked = "Um dos lados está vazio, mas o histórico da última sincronização indica que ele tinha arquivos. " +
                      "Propagar isso apagaria tudo do outro lado; normalmente indica pasta errada ou indisponível. Plano bloqueado.";

        var unreadable = result.Count(ComparisonStatus.Error);
        if (unreadable > 0)
            warnings.Add($"{unreadable} item(ns) não puderam ser lidos e serão ignorados (nenhuma cópia/exclusão será feita sobre eles).");
        if (result.Count(ComparisonStatus.Ignored) > 0)
            warnings.Add($"{result.Count(ComparisonStatus.Ignored)} item(ns) ignorados (ex.: links simbólicos).");
        if (sync && knownSyncStates == 0 && (result.SourceEntryCount > 0 || result.TargetEntryCount > 0))
            warnings.Add("Primeira sincronização deste perfil (sem histórico): arquivos diferentes nos dois lados viram CONFLITOS para você decidir, e nenhuma exclusão é propagada.");

        return new SyncPlan(profile.Method, result.SourceRoot, result.TargetRoot, ops,
            result.TargetEntryCount, profile.MassDeleteThreshold, blocked, warnings,
            result.SourceEntryCount, result.StaleStatePaths);
    }

    public SyncOperation PlanItem(ComparisonItem item, SyncProfile profile) =>
        profile.Method switch
        {
            SyncMethod.Synchronization => DecideBidirectional(item),
            _ => Decide(profile.Method, item)
        };

    // ------------------------------------------------------------------ fábrica de operações

    internal static SyncOperation MakeOp(SyncOperationType type, ComparisonItem i, string reason,
        FileSide from = FileSide.Source, FileSide to = FileSide.Target, bool recordsState = false)
    {
        string PathOf(FileSide s) => s == FileSide.Source ? i.SourcePath : i.TargetPath;

        var transfer = type is SyncOperationType.Copy or SyncOperationType.Update;
        var bytes = transfer ? (from == FileSide.Source ? i.SourceSize : i.TargetSize) ?? 0 : 0;
        var origin = transfer || type == SyncOperationType.CreateDirectory ? PathOf(from) : null;
        return new SyncOperation(type, i, origin, PathOf(to), bytes, reason, from, to, 0, recordsState);
    }

    private static FileSide Other(FileSide s) => s == FileSide.Source ? FileSide.Target : FileSide.Source;

    // ------------------------------------------------------------------ bidirecional

    private static SyncOperation DecideBidirectional(ComparisonItem i)
    {
        SyncOperation Skip(string reason, bool record = false) => MakeOp(SyncOperationType.Skip, i, reason, recordsState: record);
        SyncOperation Conflict(string reason) => MakeOp(SyncOperationType.Conflict, i, reason);

        if (i.Status == ComparisonStatus.Error) return Skip(i.Message ?? "Erro de leitura");
        if (i.Status == ComparisonStatus.Ignored) return Skip(i.Message ?? "Ignorado");
        if (i.Status == ComparisonStatus.Conflict) return Conflict(i.Message ?? "Conflito");

        var s = i.SourceExists;
        var t = i.TargetExists;

        if (i.IsDirectory)
        {
            if (s && t) return Skip("Pasta já existe nos dois lados", record: !i.HasSyncState);
            if (s)
                return i.HasSyncState
                    ? MakeOp(SyncOperationType.DeleteDirectory, i, "Pasta excluída no Target desde a última sincronização", from: FileSide.Target, to: FileSide.Source)
                    : MakeOp(SyncOperationType.CreateDirectory, i, "Pasta nova no Source");
            return i.HasSyncState
                ? MakeOp(SyncOperationType.DeleteDirectory, i, "Pasta excluída no Source desde a última sincronização")
                : MakeOp(SyncOperationType.CreateDirectory, i, "Pasta nova no Target", from: FileSide.Target, to: FileSide.Source);
        }

        if (s && t)
        {
            if (i.Status == ComparisonStatus.Identical)
                return Skip("Já está igual", record: !i.HasSyncState || i.SourceChanged == true || i.TargetChanged == true);

            if (!i.HasSyncState)
                return Conflict("Existe nos dois lados com conteúdo diferente e não há histórico de sincronização");

            var sc = i.SourceChanged ?? true;
            var tc = i.TargetChanged ?? true;
            if (sc && !tc) return MakeOp(SyncOperationType.Update, i, "Alterado só no Source desde a última sincronização");
            if (!sc && tc) return MakeOp(SyncOperationType.Update, i, "Alterado só no Target desde a última sincronização", FileSide.Target, FileSide.Source);
            if (sc) return Conflict("Alterado nos dois lados desde a última sincronização");
            return Conflict("Os lados diferem, mas nenhum mudou em relação ao histórico (histórico inconsistente)");
        }

        if (s)
        {
            if (!i.HasSyncState) return MakeOp(SyncOperationType.Copy, i, "Novo no Source");
            if (i.SourceChanged == false)
                return MakeOp(SyncOperationType.Delete, i, "Excluído no Target desde a última sincronização; a exclusão é propagada ao Source",
                    from: FileSide.Target, to: FileSide.Source);
            return Conflict("Excluído no Target, mas modificado no Source");
        }

        if (!i.HasSyncState) return MakeOp(SyncOperationType.Copy, i, "Novo no Target", FileSide.Target, FileSide.Source);
        if (i.TargetChanged == false)
            return MakeOp(SyncOperationType.Delete, i, "Excluído no Source desde a última sincronização; a exclusão é propagada ao Target");
        return Conflict("Excluído no Source, mas modificado no Target");
    }

    // ------------------------------------------------------------------ espelho / replicação

    private static SyncOperation Decide(SyncMethod method, ComparisonItem i)
    {
        SyncOperation Op(SyncOperationType type, string reason) => MakeOp(type, i, reason);
        SyncOperation Skip(string reason) => MakeOp(SyncOperationType.Skip, i, reason);

        if (i.Status == ComparisonStatus.Error) return Skip(i.Message ?? "Erro de leitura");
        if (i.Status == ComparisonStatus.Ignored) return Skip(i.Message ?? "Ignorado");
        if (i.Status == ComparisonStatus.Conflict) return Op(SyncOperationType.Conflict, i.Message ?? "Conflito");
        if (i.Status == ComparisonStatus.Identical) return Skip("Já está igual");

        var fileOrDirCopy = i.IsDirectory ? SyncOperationType.CreateDirectory : SyncOperationType.Copy;
        var deleteType = i.IsDirectory ? SyncOperationType.DeleteDirectory : SyncOperationType.Delete;

        switch (method)
        {
            case SyncMethod.MirrorSourceToTarget:
                return i.Status switch
                {
                    ComparisonStatus.SourceOnly => Op(fileOrDirCopy, "Existe só no Source"),
                    ComparisonStatus.TargetOnly => Op(deleteType, "Não existe no Source (Mirror remove do Target)"),
                    ComparisonStatus.TargetNewer => Op(SyncOperationType.Update, "Target é mais novo, mas o Mirror força o Target a ficar igual ao Source"),
                    ComparisonStatus.SourceNewer => Op(SyncOperationType.Update, "Source é mais novo"),
                    ComparisonStatus.DifferentSize => Op(SyncOperationType.Update, "Tamanho diferente"),
                    ComparisonStatus.DifferentContent => Op(SyncOperationType.Update, "Conteúdo diferente"),
                    _ => Skip("Sem ação")
                };

            case SyncMethod.ReplicationAugment:
                return i.Status == ComparisonStatus.SourceOnly
                    ? Op(fileOrDirCopy, "Existe só no Source")
                    : Skip(i.Status == ComparisonStatus.TargetOnly ? "Extra no Target é mantido" : "Já existe no Target (Augment não atualiza)");

            case SyncMethod.ReplicationRefresh:
                return i.Status == ComparisonStatus.SourceNewer
                    ? Op(SyncOperationType.Update, "Source é mais novo")
                    : Skip("Refresh só atualiza arquivos existentes nos dois lados quando o Source é mais novo");

            case SyncMethod.ReplicationUpdate:
                return i.Status switch
                {
                    ComparisonStatus.SourceOnly => Op(fileOrDirCopy, "Novo no Source"),
                    ComparisonStatus.SourceNewer => Op(SyncOperationType.Update, "Source é mais novo"),
                    ComparisonStatus.DifferentSize => Op(SyncOperationType.Update, "Tamanho diferente"),
                    ComparisonStatus.DifferentContent => Op(SyncOperationType.Update, "Conteúdo diferente"),
                    ComparisonStatus.TargetNewer => Skip("Target é mais novo (não é sobrescrito)"),
                    ComparisonStatus.TargetOnly => Skip("Extra no Target é mantido"),
                    _ => Skip("Sem ação")
                };

            default:
                throw new NotSupportedException($"Método não suportado: {method}");
        }
    }
}
