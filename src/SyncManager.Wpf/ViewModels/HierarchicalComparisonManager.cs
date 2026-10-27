using System.Collections.ObjectModel;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;

namespace SyncManager.Wpf.ViewModels;

/// <summary>
/// Constrói a árvore hierárquica compartilhada e projeta a coleção linearizada de linhas visíveis
/// com suporte total a expansão, recolhimento, filtros e virtualização de interface WPF.
/// </summary>
public sealed class HierarchicalComparisonManager
{
    private readonly List<ComparisonTreeNode> _rootNodes = new();
    private readonly Dictionary<string, ComparisonTreeNode> _nodeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenPaths = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ComparisonTreeNode> FlatVisibleRows { get; } = new();

    public event Action? StructureOrFilterChanged;
    public event Action? CheckChanged;

    public void Build(SyncPlan plan, IEnumerable<ItemRow>? rows = null)
    {
        _rootNodes.Clear();
        _nodeByPath.Clear();
        FlatVisibleRows.Clear();

        var opsByPath = plan.Operations
            .GroupBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SyncOperation>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Agrupa todos os itens de comparação
        var allItems = plan.Operations
            .Select(o => o.Item)
            .DistinctBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in allItems)
        {
            EnsurePathNode(item.RelativePath, item.IsDirectory, item, opsByPath.GetValueOrDefault(item.RelativePath));
        }

        RebuildFlatList();
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

    public void HideNode(ComparisonTreeNode node)
    {
        _hiddenPaths.Add(node.RelativePath);
        RebuildFlatList();
    }

    public void RestoreHidden()
    {
        _hiddenPaths.Clear();
        RebuildFlatList();
    }

    public int HiddenCount => _hiddenPaths.Count;

    public void RebuildFlatList(Func<ComparisonTreeNode, bool>? filter = null)
    {
        FlatVisibleRows.Clear();

        void Traverse(ComparisonTreeNode node)
        {
            if (_hiddenPaths.Contains(node.RelativePath))
                return;

            bool matches = filter is null || filter(node);

            // Se for arquivo ou pasta que combina com o filtro, adicionamos
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
