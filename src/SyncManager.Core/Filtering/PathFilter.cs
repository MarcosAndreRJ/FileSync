using System.Text;
using System.Text.RegularExpressions;

namespace SyncManager.Core.Filtering;

/// <summary>
/// Filtro por padrões com curingas. Sintaxe:
/// <c>*.tmp</c> (nome, em qualquer nível), <c>Thumbs.db</c>, <c>node_modules\</c> (somente pastas),
/// <c>src\obj</c> (caminho relativo), <c>\build</c> (ancorado na raiz), <c>**</c> atravessa pastas.
/// Linhas vazias e iniciadas por # são ignoradas.
/// </summary>
public sealed class PathFilter
{
    public static readonly PathFilter Empty = new(Array.Empty<string>());

    private sealed record Rule(Regex Regex, bool DirectoryOnly, bool MatchFullPath);

    private readonly List<Rule> _excludes;
    private readonly List<Rule> _includes;

    /// <param name="caseSensitive">Diferencia maiúsculas de minúsculas (padrão: não, como o NTFS).</param>
    /// <param name="wildcards">Liga * e ? (padrão). Desligado, os padrões são texto literal.</param>
    public PathFilter(IEnumerable<string> excludePatterns, IEnumerable<string>? includePatterns = null,
        bool caseSensitive = false, bool wildcards = true)
    {
        _excludes = Parse(excludePatterns, caseSensitive, wildcards);
        _includes = Parse(includePatterns ?? Array.Empty<string>(), caseSensitive, wildcards);
    }

    public bool IsEmpty => _excludes.Count == 0 && _includes.Count == 0;

    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        var name = relativePath;
        var idx = relativePath.LastIndexOf('\\');
        if (idx >= 0) name = relativePath[(idx + 1)..];

        foreach (var r in _excludes)
        {
            if (r.DirectoryOnly && !isDirectory) continue;
            if (r.Regex.IsMatch(r.MatchFullPath ? relativePath : name)) return true;
        }

        if (_includes.Count > 0 && !isDirectory)
        {
            foreach (var r in _includes)
                if (r.Regex.IsMatch(r.MatchFullPath ? relativePath : name)) return false;
            return true;
        }

        return false;
    }

    private static List<Rule> Parse(IEnumerable<string> patterns, bool caseSensitive, bool wildcards)
    {
        var rules = new List<Rule>();
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('/', '\\');
            if (p.Length == 0 || p[0] == '#') continue;

            var dirOnly = p.EndsWith('\\');
            p = p.TrimEnd('\\');
            var anchored = p.StartsWith('\\');
            p = p.TrimStart('\\');
            if (p.Length == 0) continue;

            var hasSeparator = p.Contains('\\');
            var fullPath = anchored || hasSeparator;
            var body = wildcards ? GlobToRegex(p) : Regex.Escape(p);
            var pattern = anchored ? $"^{body}$" : fullPath ? $@"(^|\\){body}$" : $"^{body}$";
            rules.Add(new Rule(new Regex(pattern, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant | RegexOptions.Compiled), dirOnly, fullPath));
        }
        return rules;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append(@"[^\\]*");
            }
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
