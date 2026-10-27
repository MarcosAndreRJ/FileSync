using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Wpf.ViewModels;

/// <summary>
/// Representa uma linha compartilhada na visualização dual alinhada (Source / Target),
/// com suporte a hierarquia, expansão/recolhimento e virtualização.
/// </summary>
public sealed class ComparisonTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isVisible = true;
    private bool? _isChecked = true;
    private bool _isSelected;

    public ComparisonTreeNode(
        string relativePath,
        string name,
        int depth,
        bool isDirectory,
        ComparisonItem? item = null,
        IReadOnlyList<SyncOperation>? operations = null,
        ComparisonTreeNode? parent = null)
    {
        RelativePath = relativePath;
        Name = name;
        Depth = depth;
        IsDirectory = isDirectory;
        Item = item;
        Operations = operations ?? Array.Empty<SyncOperation>();
        Parent = parent;

        if (item is not null)
        {
            var (glyph, text, color) = Presentation.Status(item.Status);
            StatusGlyph = glyph;
            StatusKey = item.Status.ToString();
            StatusText = text;
            StatusBrush = color;

            if (Operations.Count > 0)
            {
                var (actText, actBrush) = Presentation.Action(Operations);
                ActionText = actText;
                ActionBrush = actBrush;
                CanInclude = Operations.Any(o => o.IsExecutable);
            }
            else
            {
                ActionText = "— ignorar";
                ActionBrush = Presentation.Gray;
                CanInclude = false;
            }

            SourceSizeText = !item.SourceExists ? "" : isDirectory ? "‹pasta›" : Presentation.Bytes(item.SourceSize ?? 0);
            TargetSizeText = !item.TargetExists ? "" : isDirectory ? "‹pasta›" : Presentation.Bytes(item.TargetSize ?? 0);
            SourceModifiedText = Presentation.Date(item.SourceModified);
            TargetModifiedText = Presentation.Date(item.TargetModified);

            SourceExists = item.SourceExists;
            TargetExists = item.TargetExists;
            IsConflict = Operations.Any(o => o.Type == SyncOperationType.Conflict);

            RowKind = IsConflict ? "conflict"
                    : item.Status == ComparisonStatus.Error ? "error"
                    : Operations.Any(o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory) ? "delete"
                    : Operations.Any(o => o.IsExecutable) ? "change" : "none";

            _isChecked = CanInclude;
        }
        else
        {
            // Nó de pasta intermediária sintética (apenas para organizar hierarquia)
            StatusGlyph = "";
            StatusKey = "None";
            StatusText = "";
            StatusBrush = Presentation.Gray;
            ActionText = "";
            ActionBrush = Presentation.Gray;
            CanInclude = true;
            SourceExists = true;
            TargetExists = true;
            RowKind = "none";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RelativePath { get; }
    public string Name { get; }
    public int Depth { get; }
    public double Indent => Depth * 16.0;
    public bool IsDirectory { get; }
    public ComparisonItem? Item { get; }
    public IReadOnlyList<SyncOperation> Operations { get; }
    public ComparisonTreeNode? Parent { get; }
    public List<ComparisonTreeNode> Children { get; } = new();

    public bool SourceExists { get; }
    public bool TargetExists { get; }
    public bool IsConflict { get; }
    public string RowKind { get; }

    public string StatusGlyph { get; }
    public string StatusKey { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }

    public string ActionText { get; }
    public Brush ActionBrush { get; }

    public string SourceSizeText { get; } = "";
    public string TargetSizeText { get; } = "";
    public string SourceModifiedText { get; } = "";
    public string TargetModifiedText { get; } = "";

    public bool CanInclude { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(ExpandIconGlyph));
            ExpandedChanged?.Invoke(this);
        }
    }

    public string ExpandIconGlyph => !IsDirectory ? "" : IsExpanded ? "▼" : "▶";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    /// <summary>
    /// Tri-state para pastas; booleano para arquivos.
    /// </summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (IsDirectory && value.HasValue)
            {
                SetChildrenChecked(this, value.Value);
            }

            Parent?.UpdateCheckFromChildren();
            CheckChanged?.Invoke(this);
        }
    }

    internal void SetCheckedQuietly(bool? value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    private void UpdateCheckFromChildren()
    {
        if (!IsDirectory || Children.Count == 0) return;

        var executables = Children.Where(c => c.CanInclude || c.IsDirectory).ToList();
        if (executables.Count == 0) return;

        bool allTrue = executables.All(c => c.IsChecked == true);
        bool allFalse = executables.All(c => c.IsChecked == false);

        bool? newState = allTrue ? true : allFalse ? false : (bool?)null;
        if (_isChecked != newState)
        {
            _isChecked = newState;
            OnPropertyChanged(nameof(IsChecked));
            Parent?.UpdateCheckFromChildren();
        }
    }

    private static void SetChildrenChecked(ComparisonTreeNode node, bool isChecked)
    {
        foreach (var child in node.Children)
        {
            if (child.CanInclude || child.IsDirectory)
            {
                child.SetCheckedQuietly(isChecked);
                if (child.IsDirectory)
                    SetChildrenChecked(child, isChecked);
            }
        }
    }

    internal Action<ComparisonTreeNode>? ExpandedChanged { get; set; }
    internal Action<ComparisonTreeNode>? CheckChanged { get; set; }

    private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
