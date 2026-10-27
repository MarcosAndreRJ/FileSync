namespace SyncManager.Core.Paths;

public static class PathUtil
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    /// <summary>Caminho absoluto, sem aspas e sem separador final (exceto em raízes como C:\).</summary>
    public static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
                                             || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);

    /// <summary>True se <paramref name="child"/> é igual a <paramref name="parent"/> ou está dentro dele.</summary>
    public static bool IsSameOrInside(string parent, string child)
    {
        var p = Path.TrimEndingDirectorySeparator(parent);
        var c = Path.TrimEndingDirectorySeparator(child);
        if (c.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = p.EndsWith(Sep) ? p : p + Sep;
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + Sep + name;

    /// <summary>Profundidade (número de separadores) de um caminho relativo.</summary>
    public static int Depth(string relative)
    {
        var d = 0;
        foreach (var ch in relative) if (ch == Sep) d++;
        return d;
    }
}
