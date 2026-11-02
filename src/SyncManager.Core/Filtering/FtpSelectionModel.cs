namespace SyncManager.Core.Filtering;

/// <summary>
/// Edição das regras persistentes de seleção (inclusões + exceções) usada pela árvore com checkboxes.
/// O estado de qualquer pasta/arquivo é derivado apenas das regras — por isso pastas ainda não carregadas
/// na árvore e arquivos criados depois continuam corretamente cobertos.
/// </summary>
public sealed class FtpSelectionModel
{
    private readonly List<string> _includes;
    private readonly List<string> _excludes;

    public FtpSelectionModel(IEnumerable<string>? includes = null, IEnumerable<string>? excludes = null)
    {
        _includes = Distinct(includes);
        _excludes = Distinct(excludes);
    }

    /// <summary>Disparado a cada alteração das regras (a árvore usa para atualizar checkboxes).</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Includes => _includes;
    public IReadOnlyList<string> Excludes => _excludes;
    public bool HasSelection => _includes.Count > 0;

    /// <summary>true = incluído, false = fora do escopo, null = parcial (há exceções ou inclusões abaixo).</summary>
    public bool? GetState(string relativePath, bool isDirectory)
    {
        var rel = FtpSelectionRules.Normalize(relativePath);
        var included = FtpSelectionRules.IsIncluded(rel, _includes, _excludes);
        if (!isDirectory) return included;

        if (included)
            return HasRuleBelow(rel, _excludes) ? null : true;
        return HasRuleBelow(rel, _includes) ? null : false;
    }

    /// <summary>Marca ou desmarca o item (e, recursivamente, tudo abaixo dele), descartando regras mais específicas.</summary>
    public void SetChecked(string relativePath, bool value)
    {
        var rel = FtpSelectionRules.Normalize(relativePath);
        if (rel.Length == 0) return;

        _includes.RemoveAll(r => IsSameOrBelow(r, rel));
        _excludes.RemoveAll(r => IsSameOrBelow(r, rel));

        var includedByAncestors = FtpSelectionRules.IsIncluded(rel, _includes, _excludes);
        if (value && !includedByAncestors) _includes.Add(rel);
        else if (!value && includedByAncestors) _excludes.Add(rel);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _includes.Clear();
        _excludes.Clear();
        Changed?.Invoke();
    }

    private static bool HasRuleBelow(string dir, List<string> rules) =>
        rules.Any(r => FtpSelectionRules.Normalize(r).StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrBelow(string rule, string rel)
    {
        var r = FtpSelectionRules.Normalize(rule);
        return r.Equals(rel, StringComparison.OrdinalIgnoreCase) || r.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Distinct(IEnumerable<string>? source) =>
        (source ?? [])
            .Select(FtpSelectionRules.Normalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
