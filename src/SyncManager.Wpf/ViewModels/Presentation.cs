using System.Windows.Media;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Wpf.ViewModels;

/// <summary>Textos, ícones e cores. A cor é só reforço: todo estado também tem glifo e texto próprios.</summary>
public static class Presentation
{
    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // Mesmos valores de Resources/Colors.xaml (há um teste que garante a paridade). As cores de texto de status
    // são variantes um pouco mais escuras da paleta, para manter contraste sobre fundo branco.
    public static readonly Brush Gray = Brush("#667085");     // TextSecondary
    public static readonly Brush Green = Brush("#1B8758");    // SuccessText
    public static readonly Brush Blue = Brush("#1677FF");     // Primary
    public static readonly Brush Orange = Brush("#B77B00");   // WarningText
    public static readonly Brush Purple = Brush("#7E57C2");   // Purple
    public static readonly Brush Red = Brush("#D64545");      // Danger
    public static readonly Brush Dark = Brush("#1F2937");     // TextPrimary

    public static (string Glyph, string Text, Brush Color) Status(ComparisonStatus s) => s switch
    {
        ComparisonStatus.Identical => ("✔", "Igual", Green),
        ComparisonStatus.SourceOnly => ("◀", "Só no Source", Blue),
        ComparisonStatus.TargetOnly => ("▶", "Só no Target", Orange),
        ComparisonStatus.SourceNewer => ("⬅", "Source mais novo", Blue),
        ComparisonStatus.TargetNewer => ("➡", "Target mais novo", Purple),
        ComparisonStatus.DifferentSize => ("≠", "Tamanho diferente", Orange),
        ComparisonStatus.DifferentContent => ("≠", "Conteúdo diferente", Orange),
        ComparisonStatus.Conflict => ("⚠", "Conflito", Red),
        ComparisonStatus.Error => ("✖", "Erro", Red),
        ComparisonStatus.Ignored => ("⊘", "Ignorado", Gray),
        _ => ("?", s.ToString(), Gray)
    };

    public static (string Text, Brush Color) Action(SyncOperationType t) => t switch
    {
        SyncOperationType.Copy => ("COPIAR →", Green),
        SyncOperationType.Update => ("ATUALIZAR →", Blue),
        SyncOperationType.Delete => ("EXCLUIR ✖", Red),
        SyncOperationType.CreateDirectory => ("CRIAR PASTA →", Green),
        SyncOperationType.DeleteDirectory => ("EXCLUIR PASTA ✖", Red),
        SyncOperationType.Conflict => ("⚠ CONFLITO (nada será feito)", Red),
        _ => ("— ignorar", Gray)
    };

    /// <summary>Texto e cor da ação de UMA operação, com a direção (→ para o Target, ← para o Source).</summary>
    public static (string Text, Brush Color) Action(SyncOperation op)
    {
        var toSource = op.To == FileSide.Source;
        var sameSide = op.From == op.To;   // cópia dentro do próprio lado (cópia de conflito)
        return op.Type switch
        {
            SyncOperationType.Copy when sameSide => ("COPIAR (cópia de conflito)", Purple),
            SyncOperationType.Copy => (toSource ? "← COPIAR" : "COPIAR →", Green),
            SyncOperationType.Update => (toSource ? "← ATUALIZAR" : "ATUALIZAR →", Blue),
            SyncOperationType.Delete => (toSource ? "✖ EXCLUIR do Source" : "EXCLUIR do Target ✖", Red),
            SyncOperationType.CreateDirectory => (toSource ? "← CRIAR PASTA" : "CRIAR PASTA →", Green),
            SyncOperationType.DeleteDirectory => (toSource ? "✖ EXCLUIR PASTA do Source" : "EXCLUIR PASTA do Target ✖", Red),
            SyncOperationType.Conflict => ("⚠ CONFLITO (resolver)", Red),
            _ => ("— ignorar", Gray)
        };
    }

    /// <summary>Ação de um item que pode ter várias operações (ex.: "manter ambos" = 3).</summary>
    public static (string Text, Brush Color) Action(IReadOnlyList<SyncOperation> ops) =>
        ops.Count == 1 ? Action(ops[0]) : ($"⇄ MANTER AMBOS ({ops.Count} operações)", Purple);

    /// <summary>Rótulo da operação no Preview, com a direção quando não é o padrão (Source → Target).</summary>
    public static string PlanLabel(SyncOperation op)
    {
        var label = PlanLabel(op.Type);
        if (op.Type is SyncOperationType.Skip or SyncOperationType.Conflict) return label;
        if (op.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory)
            return label + (op.To == FileSide.Source ? " [Source]" : " [Target]");
        if (op.From == op.To) return label + " (conflito)";
        return op.To == FileSide.Source ? "← " + label : label + " →";
    }

    public static string PlanLabel(SyncOperationType t) => t switch
    {
        SyncOperationType.Copy => "COPY",
        SyncOperationType.Update => "UPDATE",
        SyncOperationType.Delete => "DELETE",
        SyncOperationType.CreateDirectory => "CREATE DIR",
        SyncOperationType.DeleteDirectory => "DELETE DIR",
        SyncOperationType.Conflict => "CONFLICT",
        _ => "SKIP"
    };

    public static string MethodName(SyncMethod m) => m switch
    {
        SyncMethod.MirrorSourceToTarget => "Backup — Mirror Source → Target",
        SyncMethod.ReplicationAugment => "Replicação — Augment Target",
        SyncMethod.ReplicationRefresh => "Replicação — Refresh Target",
        SyncMethod.ReplicationUpdate => "Replicação — Update Target",
        SyncMethod.Synchronization => "Sincronização (bidirecional)",
        _ => m.ToString()
    };

    public static string StatusName(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Success => "Concluído com sucesso",
        ExecutionStatus.SuccessWithWarnings => "Concluído com avisos",
        ExecutionStatus.ConflictsUnresolved => "Concluído; há conflitos não resolvidos",
        ExecutionStatus.Failed => "Falhou / interrompido",
        ExecutionStatus.Cancelled => "Cancelado pelo usuário",
        _ => s.ToString()
    };

    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b:N0} B" : $"{v:N1} {u[i]}";
    }

    public static string Date(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
}

/// <summary>Uma linha da grade principal (um item comparado + a ação prevista).</summary>
public sealed class ItemRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _included;

    public ItemRow(IReadOnlyList<SyncOperation> ops)
    {
        Operations = ops;
        var op = ops[0];
        var i = op.Item;
        var (glyph, text, color) = Presentation.Status(i.Status);
        StatusGlyph = glyph;
        StatusKey = i.Status.ToString();
        RowKind = ops.Any(o => o.Type == SyncOperationType.Conflict) ? "conflict"
                : i.Status == ComparisonStatus.Error ? "error"
                : ops.Any(o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory) ? "delete"
                : ops.Any(o => o.IsExecutable) ? "change" : "none";
        StatusText = text;
        StatusBrush = color;
        (ActionText, ActionBrush) = Presentation.Action(ops);
        Tooltip = string.Join(Environment.NewLine, ops.Select(o => o.Reason).Distinct()) + (i.Message is null || ops.Any(o => o.Reason == i.Message) ? "" : Environment.NewLine + i.Message);
        DisplayPath = i.IsDirectory ? i.RelativePath + "\\" : i.RelativePath;
        SourceSizeText = !i.SourceExists ? "" : i.IsDirectory ? "‹pasta›" : Presentation.Bytes(i.SourceSize ?? 0);
        TargetSizeText = !i.TargetExists ? "" : i.IsDirectory ? "‹pasta›" : Presentation.Bytes(i.TargetSize ?? 0);
        SourceModifiedText = Presentation.Date(i.SourceModified);
        TargetModifiedText = Presentation.Date(i.TargetModified);
        CanInclude = ops.Any(o => o.IsExecutable);
        _included = CanInclude;   // o que tem ação pendente nasce marcado para executar
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tem alguma operação que altera arquivos (copiar, atualizar, excluir)? Só esses itens podem ser marcados.</summary>
    public bool CanInclude { get; }

    /// <summary>Marcado para ser executado. Desmarcar não remove da lista: só deixa o item de fora desta execução.</summary>
    public bool IsIncluded
    {
        get => _included;
        set
        {
            if (!CanInclude || _included == value) return;
            _included = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsIncluded)));
            IncludedChanged?.Invoke(this);
        }
    }

    /// <summary>Avisa o dono da lista (um só delegado compartilhado: nada de assinatura por linha).</summary>
    internal Action<ItemRow>? IncludedChanged { get; set; }

    /// <summary>Altera sem avisar o dono: usado em lote (marcar centenas de milhares de itens de uma vez).</summary>
    internal bool SetIncludedQuietly(bool value)
    {
        if (!CanInclude || _included == value) return false;
        _included = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsIncluded)));
        return true;
    }

    public IReadOnlyList<SyncOperation> Operations { get; }
    public SyncOperation Operation => Operations[0];
    public bool IsConflict => Operations.Any(o => o.Type == SyncOperationType.Conflict);
    public ComparisonItem Item => Operation.Item;
    public string DisplayPath { get; }
    public string StatusGlyph { get; }
    /// <summary>Nome do ComparisonStatus: o XAML escolhe o ícone vetorial por DataTrigger (sem conversor por célula).</summary>
    public string StatusKey { get; }
    /// <summary>conflict / error / delete / change / none: define o realce suave da linha.</summary>
    public string RowKind { get; }
    public string StatusText { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
    public string ActionText { get; }
    public System.Windows.Media.Brush ActionBrush { get; }
    public string Tooltip { get; }
    public string SourceSizeText { get; }
    public string TargetSizeText { get; }
    public string SourceModifiedText { get; }
    public string TargetModifiedText { get; }
    public long SourceSizeSort => Item.SourceSize ?? -1;
    public long TargetSizeSort => Item.TargetSize ?? -1;
    public DateTime SourceModifiedSort => Item.SourceModified ?? DateTime.MinValue;
    public DateTime TargetModifiedSort => Item.TargetModified ?? DateTime.MinValue;
}
