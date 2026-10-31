using System.Collections.ObjectModel;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;

namespace SyncManager.Wpf.ViewModels;

public enum ComparisonTreeFilter
{
    All,
    Different,
    Identical,
    SourceOnly,
    TargetOnly,
    Conflict,
    Error,
    Checked
}

/// <summary>
/// Constrói a árvore hierárquica compartilhada e projeta a coleção linearizada de linhas visíveis
/// com suporte total a expansão, recolhimento, filtros combináveis e virtualização de interface WPF.
/// </summary>
public sealed class HierarchicalComparisonManager
{
    private readonly List<ComparisonTreeNode> _rootNodes = new();
    private readonly Dictionary<string, ComparisonTreeNode> _nodeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenPaths = new(StringComparer.OrdinalIgnoreCase);

    private ComparisonTreeFilter _activeFilter = ComparisonTreeFilter.All;
    private string _searchText = "";

    public ObservableCollection<ComparisonTreeNode> FlatVisibleRows { get; } = new();

    public event Action? StructureOrFilterChanged;
    public event Action? CheckChanged;

    public ComparisonTreeFilter ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (_activeFilter != value)
            {
                _activeFilter = value;
                RebuildFlatList();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var clean = (value ?? "").Trim();
            if (!string.Equals(_searchText, clean, StringComparison.OrdinalIgnoreCase))
            {
                _searchText = clean;
                if (!string.IsNullOrEmpty(_searchText))
                {
                    AutoExpandMatchingParents();
                }
                RebuildFlatList();
            }
        }
    }

    public void Build(SyncPlan plan, IEnumerable<ItemRow>? rows = null)
    {
        _rootNodes.Clear();
        _nodeByPath.Clear();
        FlatVisibleRows.Clear();
        _hiddenPaths.Clear();

        var opsByPath = plan.Operations
            .GroupBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SyncOperation>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Todos os itens da comparação
        var allItems = plan.Operations
            .Select(o => o.Item)
            .DistinctBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in allItems)
        {
            EnsurePathNode(item.RelativePath, item.IsDirectory, item, opsByPath.GetValueOrDefault(item.RelativePath));
        }

        // Propagar existência de Source e Target para as pastas pai
        PropagateFolderExistence();

        // Atualizar estado tri-state de todas as pastas
        UpdateAllFolderChecks();

        RebuildFlatList();
    }

    private void PropagateFolderExistence()
    {
        foreach (var node in _nodeByPath.Values.Where(n => n.IsDirectory))
        {
            bool hasSource = HasSourceRecursive(node);
            bool hasTarget = HasTargetRecursive(node);
            node.SourceExists = hasSource;
            node.TargetExists = hasTarget;

            if (node.Item is null)
            {
                if (hasSource && !hasTarget)
                {
                    node.StatusSymbol = "→";
                }
                else if (!hasSource && hasTarget)
                {
                    node.StatusSymbol = "←";
                }
                else
                {
                    node.StatusSymbol = "=";
                }
            }
        }
    }

    private void UpdateAllFolderChecks()
    {
        // Pastas mais profundas primeiro para propagar para cima
        var dirs = _nodeByPath.Values
            .Where(n => n.IsDirectory)
            .OrderByDescending(n => n.Depth)
            .ToList();

        foreach (var dir in dirs)
        {
            dir.UpdateCheckFromChildrenDirect();
        }
    }

    private static bool HasSourceRecursive(ComparisonTreeNode node)
    {
        if (node.Item is not null && node.Item.SourceExists) return true;
        return node.Children.Any(HasSourceRecursive);
    }

    private static bool HasTargetRecursive(ComparisonTreeNode node)
    {
        if (node.Item is not null && node.Item.TargetExists) return true;
        return node.Children.Any(HasTargetRecursive);
    }

    private ComparisonTreeNode EnsurePathNode(string relativePath, bool isDirectory, ComparisonItem? item, IReadOnlyList<SyncOperation>? ops)
    {
        var norm = relativePath.Replace('/', '\\').Trim('\\');
        if (_nodeByPath.TryGetValue(norm, out var existing))
        {
            return existing;
        }

        var parts = norm.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        ComparisonTreeNode? parent = null;
        string currentPath = "";

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            currentPath = i == 0 ? part : currentPath + "\\" + part;
            bool isCurrentDir = (i < parts.Length - 1) || isDirectory;

            if (!_nodeByPath.TryGetValue(currentPath, out var node))
            {
                var isLeaf = (i == parts.Length - 1);
                var nodeItem = isLeaf ? item : null;
                var nodeOps = isLeaf ? ops : null;

                node = new ComparisonTreeNode(currentPath, part, i, isCurrentDir, nodeItem, nodeOps, parent);
                node.ExpandedChanged = _ => RebuildFlatList();
                node.CheckChanged = _ => CheckChanged?.Invoke();

                _nodeByPath[currentPath] = node;

                if (parent is not null)
                {
                    parent.Children.Add(node);
                }
                else
                {
                    _rootNodes.Add(node);
                }
            }
            parent = node;
        }

        return parent!;
    }

    public void ExpandAll()
    {
        foreach (var node in _nodeByPath.Values.Where(n => n.IsDirectory))
            node.IsExpanded = true;
        RebuildFlatList();
    }

    public void CollapseAll()
    {
        foreach (var node in _nodeByPath.Values.Where(n => n.IsDirectory))
            node.IsExpanded = false;
        RebuildFlatList();
    }

    private void AutoExpandMatchingParents()
    {
        foreach (var node in _nodeByPath.Values.Where(n => !n.IsDirectory))
        {
            if (node.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                node.RelativePath.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            {
                var p = node.Parent;
                while (p is not null)
                {
                    p.IsExpanded = true;
                    p = p.Parent;
                }
            }
        }
    }

    public void HideNode(ComparisonTreeNode node)
    {
        _hiddenPaths.Add(node.RelativePath);
        RebuildFlatList();
    }

    public void HideNodes(IEnumerable<ComparisonTreeNode> nodes)
    {
        foreach (var n in nodes)
            _hiddenPaths.Add(n.RelativePath);
        RebuildFlatList();
    }

    public void RestoreHidden()
    {
        _hiddenPaths.Clear();
        RebuildFlatList();
    }

    public int HiddenCount => _hiddenPaths.Count;

    public void SetAllChecked(bool isChecked)
    {
        foreach (var root in _rootNodes)
        {
            root.IsChecked = isChecked;
        }
        CheckChanged?.Invoke();
    }

    public HashSet<string> GetIncludedRelativePaths()
    {
        return _nodeByPath.Values
            .Where(n => !n.IsDirectory && n.CanInclude && n.IsChecked == true)
            .Select(n => n.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Contadores para o resumo compacto
    public int TotalAnalyzed => _nodeByPath.Values.Count(n => !n.IsDirectory);
    public int IdenticalCount => _nodeByPath.Values.Count(n => !n.IsDirectory && (n.Item?.Status == ComparisonStatus.Identical));
    public int DifferentCount => _nodeByPath.Values.Count(n => !n.IsDirectory && n.Item is not null && n.Item.Status != ComparisonStatus.Identical);
    public int SourceOnlyCount => _nodeByPath.Values.Count(n => !n.IsDirectory && n.Item?.Status == ComparisonStatus.SourceOnly);
    public int TargetOnlyCount => _nodeByPath.Values.Count(n => !n.IsDirectory && n.Item?.Status == ComparisonStatus.TargetOnly);
    public int ConflictCount => _nodeByPath.Values.Count(n => !n.IsDirectory && (n.IsConflict || n.Item?.Status == ComparisonStatus.Conflict));
    public int ErrorCount => _nodeByPath.Values.Count(n => !n.IsDirectory && n.Item?.Status == ComparisonStatus.Error);
    public int ExecutableCheckedCount => _nodeByPath.Values.Count(n => !n.IsDirectory && n.CanInclude && n.IsChecked == true);

    private bool NodeMatchesFilter(ComparisonTreeNode node)
    {
        // Se houver busca por texto, o nome ou caminho relativo deve bater
        if (_searchText.Length > 0 &&
            !node.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
            !node.RelativePath.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        {
            // Se for pasta, pode ser que algum filho combine
            if (node.IsDirectory && node.Children.Any(NodeMatchesFilter))
                return true;
            return false;
        }

        if (_activeFilter == ComparisonTreeFilter.All)
            return true;

        if (node.IsDirectory)
        {
            // Pastas são exibidas se elas mesmas combinarem ou se tiverem filhos que combinam
            return node.Children.Any(NodeMatchesFilter);
        }

        var status = node.Item?.Status ?? ComparisonStatus.Identical;

        return _activeFilter switch
        {
            ComparisonTreeFilter.Different => status != ComparisonStatus.Identical,
            ComparisonTreeFilter.Identical => status == ComparisonStatus.Identical,
            ComparisonTreeFilter.SourceOnly => status == ComparisonStatus.SourceOnly,
            ComparisonTreeFilter.TargetOnly => status == ComparisonStatus.TargetOnly,
            ComparisonTreeFilter.Conflict => node.IsConflict || status == ComparisonStatus.Conflict,
            ComparisonTreeFilter.Error => status == ComparisonStatus.Error,
            ComparisonTreeFilter.Checked => node.IsChecked == true,
            _ => true
        };
    }

    public void RebuildFlatList()
    {
        FlatVisibleRows.Clear();

        void Traverse(ComparisonTreeNode node)
        {
            if (_hiddenPaths.Contains(node.RelativePath))
                return;

            bool matches = NodeMatchesFilter(node);

            if (matches)
            {
                FlatVisibleRows.Add(node);
            }

            if (node.IsDirectory && node.IsExpanded)
            {
                foreach (var child in node.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Traverse(child);
                }
            }
        }

        foreach (var root in _rootNodes.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Traverse(root);
        }

        StructureOrFilterChanged?.Invoke();
    }

    public ComparisonTreeNode? FindByPath(string relativePath)
    {
        var norm = relativePath.Replace('/', '\\').Trim('\\');
        return _nodeByPath.GetValueOrDefault(norm);
    }

    public IEnumerable<ComparisonTreeNode> AllNodes => _nodeByPath.Values;
}
