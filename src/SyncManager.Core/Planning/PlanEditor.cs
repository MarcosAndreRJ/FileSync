using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Core.Planning;

/// <summary>Resultado de montar um plano manual: o plano e o que ficou de fora (com o motivo).</summary>
public sealed record ManualPlanResult(SyncPlan Plan, IReadOnlyList<string> NotApplicable, int Overwrites, int OverwritesNewer);

/// <summary>
/// Edições do plano feitas pelo usuário. Tudo continua passando por <see cref="SyncPlan"/> e pelo SyncExecutor:
/// aqui só se montam/alteram operações, sem tocar em disco.
/// </summary>
public static class PlanEditor
{
    // ------------------------------------------------------------------ cópia manual (setas ← →)

    /// <summary>Copia os itens no sentido pedido, sobrescrevendo o destino se ele já existir e for diferente.</summary>
    public static ManualPlanResult CopyItems(SyncPlan basePlan, IEnumerable<ComparisonItem> items, FileSide from)
    {
        var to = from == FileSide.Source ? FileSide.Target : FileSide.Source;
        var ops = new List<SyncOperation>();
        var notApplicable = new List<string>();
        var overwrites = 0;
        var overwritesNewer = 0;

        foreach (var i in items)
        {
            var originExists = from == FileSide.Source ? i.SourceExists : i.TargetExists;
            var destExists = to == FileSide.Source ? i.SourceExists : i.TargetExists;

            if (i.Status is ComparisonStatus.Error or ComparisonStatus.Ignored)
            { notApplicable.Add($"{i.RelativePath}: {i.Message ?? "item não pôde ser lido/foi ignorado"}"); continue; }
            if (i.Status == ComparisonStatus.Conflict)
            { notApplicable.Add($"{i.RelativePath}: {i.Message ?? "conflito de tipo (arquivo × pasta)"}"); continue; }
            if (!originExists)
            { notApplicable.Add($"{i.RelativePath}: não existe no {(from == FileSide.Source ? "Source" : "Target")}"); continue; }

            if (i.IsDirectory)
            {
                if (destExists) { notApplicable.Add($"{i.RelativePath}: a pasta já existe no destino"); continue; }
                ops.Add(SyncPlanner.MakeOp(SyncOperationType.CreateDirectory, i, "Copiar pasta (manual)", from, to));
                continue;
            }

            if (destExists)
            {
                if (i.Status == ComparisonStatus.Identical) { notApplicable.Add($"{i.RelativePath}: já está igual"); continue; }
                overwrites++;
                var destNewer = from == FileSide.Source ? i.Status == ComparisonStatus.TargetNewer : i.Status == ComparisonStatus.SourceNewer;
                if (destNewer) overwritesNewer++;
                ops.Add(SyncPlanner.MakeOp(SyncOperationType.Update, i,
                    destNewer ? "Cópia manual: sobrescreve um arquivo MAIS NOVO no destino" : "Cópia manual: sobrescreve o destino", from, to));
            }
            else
            {
                ops.Add(SyncPlanner.MakeOp(SyncOperationType.Copy, i, "Cópia manual", from, to));
            }
        }

        return new ManualPlanResult(Fresh(basePlan, ops), notApplicable, overwrites, overwritesNewer);
    }

    // ------------------------------------------------------------------ exclusão física manual (Delete)

    /// <summary>Exclui os itens do(s) lado(s) indicado(s). Usa a estratégia de exclusão do perfil (pasta de segurança por padrão).</summary>
    public static ManualPlanResult DeleteItems(SyncPlan basePlan, IEnumerable<ComparisonItem> items, bool atSource, bool atTarget)
    {
        var ops = new List<SyncOperation>();
        var notApplicable = new List<string>();

        foreach (var i in items)
        {
            if (i.Status is ComparisonStatus.Error or ComparisonStatus.Ignored or ComparisonStatus.Conflict)
            { notApplicable.Add($"{i.RelativePath}: {i.Message ?? "item não pôde ser lido/está em conflito de tipo"}"); continue; }

            var type = i.IsDirectory ? SyncOperationType.DeleteDirectory : SyncOperationType.Delete;
            var any = false;
            if (atSource && i.SourceExists)
            {
                ops.Add(SyncPlanner.MakeOp(type, i, "Exclusão manual no Source", FileSide.Target, FileSide.Source));
                any = true;
            }
            if (atTarget && i.TargetExists)
            {
                ops.Add(SyncPlanner.MakeOp(type, i, "Exclusão manual no Target", FileSide.Source, FileSide.Target));
                any = true;
            }
            if (!any) notApplicable.Add($"{i.RelativePath}: não existe no(s) lado(s) escolhido(s)");
        }

        return new ManualPlanResult(Fresh(basePlan, ops), notApplicable, 0, 0);
    }

    private static SyncPlan Fresh(SyncPlan basePlan, List<SyncOperation> ops) =>
        // plano manual: ações explícitas do usuário, então sem bloqueio nem avisos herdados do plano automático
        new(basePlan.Method, basePlan.SourceRoot, basePlan.TargetRoot, ops, basePlan.TargetEntryCount,
            basePlan.MassDeleteThreshold, null, Array.Empty<string>(), basePlan.SourceEntryCount);

    // ------------------------------------------------------------------ resolução de conflitos

    /// <summary>Nome da cópia de conflito: <c>arquivo.conflict-20260930-143500.txt</c>.</summary>
    public static string ConflictName(string relativePath, DateTime now)
    {
        var dir = Path.GetDirectoryName(relativePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath);
        var file = $"{name}.conflict-{now:yyyyMMdd-HHmmss}{ext}";
        return dir.Length == 0 ? file : Path.Combine(dir, file);
    }

    /// <summary>
    /// Troca as operações Conflict dos caminhos indicados pela resolução escolhida. Nada é executado: o resultado é
    /// um novo plano que o usuário ainda verá no Preview e confirmará. Conflito de tipo (arquivo × pasta) só pode ser ignorado.
    /// </summary>
    public static SyncPlan ResolveConflicts(SyncPlan plan, IReadOnlyCollection<string> relativePaths, ConflictResolution resolution, DateTime now)
    {
        if (resolution == ConflictResolution.None) return plan;
        var paths = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        var result = new List<SyncOperation>(plan.Operations.Count + 4);

        foreach (var op in plan.Operations)
        {
            if (op.Type != SyncOperationType.Conflict || !paths.Contains(op.RelativePath)) { result.Add(op); continue; }
            result.AddRange(Resolve(op, resolution, now, plan));
        }
        return plan.WithOperations(result);
    }

    private static IEnumerable<SyncOperation> Resolve(SyncOperation conflict, ConflictResolution r, DateTime now, SyncPlan plan)
    {
        var i = conflict.Item;
        var typeMismatch = i.Status == ComparisonStatus.Conflict;

        if (r == ConflictResolution.Skip)
            return new[] { SyncPlanner.MakeOp(SyncOperationType.Skip, i, "Conflito ignorado por você (nada será feito)") };

        if (typeMismatch)
            return new[] { conflict };   // arquivo × pasta: resolver exige decisão manual fora do app

        return r switch
        {
            ConflictResolution.KeepSource => KeepSide(i, FileSide.Source),
            ConflictResolution.KeepTarget => KeepSide(i, FileSide.Target),
            ConflictResolution.KeepBoth => KeepBoth(i, now),
            _ => new[] { conflict }
        };
    }

    /// <summary>Faz o outro lado ficar igual ao lado mantido (copiando, ou excluindo se o lado mantido não tem o arquivo).</summary>
    private static IEnumerable<SyncOperation> KeepSide(ComparisonItem i, FileSide keep)
    {
        var other = keep == FileSide.Source ? FileSide.Target : FileSide.Source;
        var keepExists = keep == FileSide.Source ? i.SourceExists : i.TargetExists;
        var otherExists = keep == FileSide.Source ? i.TargetExists : i.SourceExists;
        var name = keep == FileSide.Source ? "Source" : "Target";

        if (!keepExists)
            return new[] { SyncPlanner.MakeOp(SyncOperationType.Delete, i, $"Conflito resolvido: manter {name} (ele não tem o arquivo; exclui do outro lado)", from: keep, to: other) };

        return new[]
        {
            SyncPlanner.MakeOp(otherExists ? SyncOperationType.Update : SyncOperationType.Copy, i,
                $"Conflito resolvido: manter {name}", from: keep, to: other)
        };
    }

    /// <summary>
    /// Mantém as duas versões: a do Source fica com o nome original nos dois lados e a do Target vira
    /// <c>nome.conflict-AAAAMMDD-HHMMSS.ext</c>, também nos dois lados. As cópias leem o Target antes (Stage 0) de ele ser sobrescrito (Stage 1).
    /// </summary>
    private static IEnumerable<SyncOperation> KeepBoth(ComparisonItem i, DateTime now)
    {
        // Só um lado tem o arquivo (excluído no outro + modificado aqui): "manter ambos" = restaurar no lado que perdeu.
        if (i.SourceExists && !i.TargetExists)
            return new[] { SyncPlanner.MakeOp(SyncOperationType.Copy, i, "Conflito resolvido: manter ambos (restaura o arquivo no Target)", FileSide.Source, FileSide.Target) };
        if (!i.SourceExists && i.TargetExists)
            return new[] { SyncPlanner.MakeOp(SyncOperationType.Copy, i, "Conflito resolvido: manter ambos (restaura o arquivo no Source)", FileSide.Target, FileSide.Source) };

        var conflictRel = ConflictName(i.RelativePath, now);
        var conflictName = Path.GetFileName(conflictRel);
        var conflictAtSource = PathCombine(i.SourcePath, i.RelativePath, conflictRel);
        var conflictAtTarget = PathCombine(i.TargetPath, i.RelativePath, conflictRel);
        var bytes = i.TargetSize ?? 0;

        return new[]
        {
            // versão do Target guardada ao lado dela mesma...
            new SyncOperation(SyncOperationType.Copy, i, i.TargetPath, conflictAtTarget, bytes,
                $"Conflito resolvido: manter ambos — guarda a versão do Target como {conflictName}", FileSide.Target, FileSide.Target, Stage: 0),
            // ...e também no Source (para os dois lados terminarem iguais)
            new SyncOperation(SyncOperationType.Copy, i, i.TargetPath, conflictAtSource, bytes,
                $"Conflito resolvido: manter ambos — cópia da versão do Target no Source como {conflictName}", FileSide.Target, FileSide.Source, Stage: 0),
            // só depois a versão do Source assume o nome original no Target
            new SyncOperation(SyncOperationType.Update, i, i.SourcePath, i.TargetPath, i.SourceSize ?? 0,
                "Conflito resolvido: manter ambos — a versão do Source fica com o nome original nos dois lados", FileSide.Source, FileSide.Target, Stage: 1)
        };
    }

    private static string PathCombine(string originalFullPath, string relativePath, string newRelativePath) =>
        string.Concat(originalFullPath.AsSpan(0, originalFullPath.Length - relativePath.Length), newRelativePath);

    // ------------------------------------------------------------------ remover da lista

    /// <summary>Tira os itens do plano (viram Skip) sem tocar em disco: "remover da lista".</summary>
    public static SyncPlan RemoveFromPlan(SyncPlan plan, IReadOnlyCollection<string> relativePaths,
        string reason = "Removido da lista por você (nada será feito)")
    {
        var paths = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        var result = new List<SyncOperation>(plan.Operations.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in plan.Operations)
        {
            if (!paths.Contains(op.RelativePath)) { result.Add(op); continue; }
            if (seen.Add(op.RelativePath))
                result.Add(SyncPlanner.MakeOp(SyncOperationType.Skip, op.Item, reason));
        }
        return plan.WithOperations(result);
    }
}
