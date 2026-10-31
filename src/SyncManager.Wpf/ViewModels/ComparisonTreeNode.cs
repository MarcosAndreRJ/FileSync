using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels;

/// <summary>
/// Representa uma linha compartilhada na visualização dual alinhada (Source / Target),
/// com suporte a hierarquia, expansão/recolhimento e virtualização no WPF.
/// </summary>
public sealed class ComparisonTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isVisible = true;
    private bool? _isChecked = true;
    private bool _isSelected;
    private bool _sourceExists;
    private bool _targetExists;

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

        ToggleExpandCommand = new RelayCommand(ToggleExpand);

        if (item is not null)
        {
            _sourceExists = item.SourceExists;
            _targetExists = item.TargetExists;

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

            SourceSizeText = !item.SourceExists ? "—" : isDirectory ? "—" : Presentation.Bytes(item.SourceSize ?? 0);
            TargetSizeText = !item.TargetExists ? "—" : isDirectory ? "—" : Presentation.Bytes(item.TargetSize ?? 0);
            SourceModifiedText = !item.SourceExists ? "—" : Presentation.Date(item.SourceModified);
            TargetModifiedText = !item.TargetExists ? "—" : Presentation.Date(item.TargetModified);

            IsConflict = Operations.Any(o => o.Type == SyncOperationType.Conflict);

            RowKind = IsConflict ? "conflict"
                    : item.Status == ComparisonStatus.Error ? "error"
                    : Operations.Any(o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory) ? "delete"
                    : Operations.Any(o => o.IsExecutable) ? "change" : "none";

            // Símbolo compacto para a coluna central Status dos mockups
            StatusSymbol = ComputeStatusSymbol(item.Status, Operations);

            _isChecked = CanInclude;
        }
        else
        {
            // Nó de pasta intermediária sintética
            _sourceExists = true;
            _targetExists = true;
            StatusGlyph = "=";
            StatusKey = "Identical";
            StatusText = "Pasta";
            StatusBrush = Presentation.Gray;
            ActionText = "";
            ActionBrush = Presentation.Gray;
            StatusSymbol = "=";
            CanInclude = true;
            RowKind = "none";
            SourceSizeText = "—";
            TargetSizeText = "—";
            SourceModifiedText = "—";
            TargetModifiedText = "—";
        }
    }

    private static string ComputeStatusSymbol(ComparisonStatus status, IReadOnlyList<SyncOperation> ops)
    {
        if (ops.Any(o => o.Type == SyncOperationType.Conflict)) return "⚠";
        if (status == ComparisonStatus.Error) return "✖";
        if (ops.Any(o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory)) return "✖";

        var copyOrUpdate = ops.FirstOrDefault(o => o.Type is SyncOperationType.Copy or SyncOperationType.Update or SyncOperationType.CreateDirectory);
        if (copyOrUpdate is not null)
        {
            return copyOrUpdate.To == FileSide.Source ? "←" : "→";
        }

        return status switch
        {
            ComparisonStatus.Identical => "=",
            ComparisonStatus.SourceOnly => "→",
            ComparisonStatus.TargetOnly => "←",
            ComparisonStatus.SourceNewer => "→",
            ComparisonStatus.TargetNewer => "←",
            ComparisonStatus.DifferentSize or ComparisonStatus.DifferentContent => "≠",
            ComparisonStatus.Conflict => "⚠",
            ComparisonStatus.Error => "✖",
            _ => "="
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RelativePath { get; }
    public string Name { get; }
    public int Depth { get; }
    public double Indent => Depth * 18.0;
    public System.Windows.Thickness IndentMargin => new(Depth * 18.0, 0, 0, 0);
    public bool IsDirectory { get; }
    public System.Windows.FontWeight FontWeight => IsDirectory ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
    public ComparisonItem? Item { get; }
    public IReadOnlyList<SyncOperation> Operations { get; }
    public ComparisonTreeNode? Parent { get; }
    public List<ComparisonTreeNode> Children { get; } = new();

    public bool SourceExists
    {
        get => _sourceExists;
        internal set { _sourceExists = value; OnPropertyChanged(nameof(SourceExists)); OnPropertyChanged(nameof(SourceNameDisplay)); }
    }

    public bool TargetExists
    {
        get => _targetExists;
        internal set { _targetExists = value; OnPropertyChanged(nameof(TargetExists)); OnPropertyChanged(nameof(TargetNameDisplay)); }
    }

    public string SourceNameDisplay => SourceExists ? Name : "—";
    public string TargetNameDisplay => TargetExists ? Name : "—";

    public bool IsConflict { get; }
    public string RowKind { get; }

    public string StatusGlyph { get; }
    public string StatusKey { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public string StatusSymbol { get; internal set; }

    public string ActionText { get; }
    public Brush ActionBrush { get; }

    public string SourceSizeText { get; }
    public string TargetSizeText { get; }
    public string SourceModifiedText { get; }
    public string TargetModifiedText { get; }

    public bool CanInclude { get; }

    public bool HasChildren => Children.Count > 0;

    public RelayCommand ToggleExpandCommand { get; }

    private void ToggleExpand()
    {
        if (IsDirectory) IsExpanded = !IsExpanded;
    }

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
    /// Tri-state para pastas; booleano para arquivos executáveis.
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

    internal void UpdateCheckFromChildrenDirect() => UpdateCheckFromChildren();

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
