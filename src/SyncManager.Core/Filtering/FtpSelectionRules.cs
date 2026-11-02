namespace SyncManager.Core.Filtering;

/// <summary>
/// Regras persistentes de seleção de uma tarefa FTP: pastas/arquivos incluídos e exceções (subpastas/arquivos excluídos).
/// A regra mais específica (caminho mais longo que contém o item) vence; em empate, a exclusão vence.
/// Sem nenhuma inclusão, NADA é selecionado — uma seleção vazia nunca autoriza a origem inteira.
/// </summary>
public static class FtpSelectionRules
{
    public static string Normalize(string path) => path.Replace('\\', '/').Trim().Trim('/');

    /// <summary>Indica se o caminho relativo (arquivo ou pasta) está autorizado pelas regras.</summary>
    public static bool IsIncluded(string relativePath, IEnumerable<string> includes, IEnumerable<string> excludes)
    {
        var rel = Normalize(relativePath);
        if (rel.Length == 0) return false;

        var bestInclude = BestMatchLength(rel, includes);
        if (bestInclude < 0) return false;
        var bestExclude = BestMatchLength(rel, excludes);
        return bestExclude < bestInclude;   // exclusão igual ou mais específica vence
    }

    /// <summary>Uma pasta pode conter itens incluídos se ela ou algum descendente tem regra de inclusão (para podar a varredura).</summary>
    public static bool MayContainIncluded(string relativeDir, IEnumerable<string> includes, IEnumerable<string> excludes)
    {
        var rel = Normalize(relativeDir);
        foreach (var inc in includes)
        {
            var i = Normalize(inc);
            if (i.Length == 0) continue;
            // o include é a própria pasta, um ancestral, ou um descendente (arquivo/subpasta explicitamente incluídos)
            if (Matches(rel, i) || Matches(i, rel)) return true;
        }
        return false;
    }

    private static int BestMatchLength(string rel, IEnumerable<string> rules)
    {
        var best = -1;
        foreach (var rule in rules)
        {
            var r = Normalize(rule);
            if (r.Length == 0) continue;
            if (Matches(rel, r) && r.Length > best) best = r.Length;
        }
        return best;
    }

    /// <summary>true quando <paramref name="path"/> é igual a <paramref name="rule"/> ou está dentro dela.</summary>
    private static bool Matches(string path, string rule) =>
        path.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase);
}
