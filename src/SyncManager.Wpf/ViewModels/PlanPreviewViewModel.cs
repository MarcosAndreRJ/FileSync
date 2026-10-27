using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels;

public sealed class PlanRow
{
    public PlanRow(SyncOperation op, string sourceRoot = "", string targetRoot = "")
    {
        Operation = op;
        Label = Presentation.PlanLabel(op);
        Brush = Presentation.Action(op).Color;
        Reason = op.Reason;
        SizeText = op.Bytes > 0 ? Presentation.Bytes(op.Bytes) : "";
        SizeSort = op.Bytes;

        var writes = op.Type is SyncOperationType.Copy or SyncOperationType.Update or SyncOperationType.CreateDirectory;
        From = op.Type is SyncOperationType.Copy or SyncOperationType.Update ? op.SourcePath ?? "" : "";
        To = writes || op.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory
            ? op.TargetPath ?? op.RelativePath
            : op.RelativePath;
        Detail = string.IsNullOrEmpty(From) ? To : From + " → " + To;
        FromDisplay = Short(From, sourceRoot, targetRoot);
        ToDisplay = Short(To, sourceRoot, targetRoot);

        (StatusText, StatusKey, StatusBrush) = op.Type switch
        {
            SyncOperationType.Conflict => ("Conflito", "conflict", Presentation.Red),
            SyncOperationType.Skip when op.Item.Status == ComparisonStatus.Error => ("Erro", "error", Presentation.Red),
            SyncOperationType.Skip => ("Ignorado", "skip", Presentation.Gray),
            _ => ("Pendente", "pending", Presentation.Blue)
        };
    }

    public SyncOperation Operation { get; }
    public string Label { get; }
    public System.Windows.Media.Brush Brush { get; }
    /// <summary>Caminho de origem da operação (vazio em exclusões, criação de pasta e itens ignorados).</summary>
    public string From { get; }
    /// <summary>Caminho de destino (ou o item excluído/criado).</summary>
    public string To { get; }
    /// <summary>Caminho curto para a grade: "Source\pasta\arquivo" / "Target\..." (o completo vai no tooltip e no CSV).</summary>
    public string FromDisplay { get; }
    public string ToDisplay { get; }
    public string Detail { get; }

    private static string Short(string path, string sourceRoot, string targetRoot)
    {
        static string Trim(string root) => root.TrimEnd('\\');
        static bool Under(string p, string root) => root.Length > 0 && p.StartsWith(Trim(root) + "\\", StringComparison.OrdinalIgnoreCase);
        if (Under(path, sourceRoot)) return "Source\\" + path[(Trim(sourceRoot).Length + 1)..];
        if (Under(path, targetRoot)) return "Target\\" + path[(Trim(targetRoot).Length + 1)..];
        return path;
    }
    public string Reason { get; }
    public string SizeText { get; }
    public long SizeSort { get; }
    public string StatusText { get; }
    /// <summary>pending / skip / conflict / error: o XAML escolhe o ícone por DataTrigger.</summary>
    public string StatusKey { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
}

public sealed record OperationFilter(string Label, Func<SyncOperation, bool> Predicate);

/// <summary>Plano de execução (somente leitura): o que será copiado, atualizado, excluído ou ignorado. Nada é executado aqui.</summary>
public sealed class PlanPreviewViewModel : ObservableObject
{
    private OperationFilter _selectedFilter;
    private string _search = "";
    private int _visibleCount;

    public PlanPreviewViewModel(SyncPlan plan)
    {
        Plan = plan;
        Filters = new[]
        {
            new OperationFilter("Somente ações (sem SKIP)", o => o.Type != SyncOperationType.Skip),
            new OperationFilter("Todas", _ => true),
            new OperationFilter("COPY", o => o.Type == SyncOperationType.Copy),
            new OperationFilter("UPDATE", o => o.Type == SyncOperationType.Update),
            new OperationFilter("DELETE", o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory),
            new OperationFilter("SKIP", o => o.Type == SyncOperationType.Skip),
            new OperationFilter("CONFLICT", o => o.Type == SyncOperationType.Conflict),
        };
        _selectedFilter = Filters[0];   // precisa existir antes da view: o filtro roda já na criação dela

        var rows = plan.Operations.Select(o => new PlanRow(o, plan.SourceRoot, plan.TargetRoot)).ToList();
        View = new ListCollectionView(rows) { Filter = o => Matches((PlanRow)o) };
        _visibleCount = View.Cast<object>().Count();

        Title = $"Plano — {Presentation.MethodName(plan.Method)}";
        Header = $"{plan.SourceRoot}   →   {plan.TargetRoot}";
        Cards = new[]
        {
            new SummaryEntry("Copiar", plan.CopyCount.ToString("N0"), Presentation.Green, "IconCopy"),
            new SummaryEntry("Atualizar", plan.UpdateCount.ToString("N0"), Presentation.Blue, "IconSync"),
            new SummaryEntry("Excluir", plan.DeleteCount.ToString("N0"), Presentation.Red, "IconDelete"),
            new SummaryEntry("Ignorar", plan.SkipCount.ToString("N0"), Presentation.Gray, "IconBlock"),
            new SummaryEntry("Conflitos", plan.ConflictCount.ToString("N0"), Presentation.Orange, "IconWarning"),
            new SummaryEntry("Erros", plan.ErrorCount.ToString("N0"), Presentation.Red, "IconError"),
        };
        DataText = $"Dados a transferir: {Presentation.Bytes(plan.TotalBytes)}" +
                   (plan.CreateDirectoryCount > 0 ? $"   •   Pastas a criar: {plan.CreateDirectoryCount:N0}" : "");
        Notices = plan.Warnings.Concat(plan.IsBlocked ? new[] { "BLOQUEADO: " + plan.BlockedReason } : Array.Empty<string>())
            .Concat(plan.OverwritesNewerTargetCount > 0
                ? new[] { $"{plan.OverwritesNewerTargetCount:N0} arquivo(s) do Target mais novos serão sobrescritos pelo Source." }
                : Array.Empty<string>()).ToList();
        Footer = plan.ConflictCount > 0
            ? $"{plan.ConflictCount:N0} conflito(s) não serão alterados nesta execução. Nenhuma operação foi executada ainda."
            : "Nenhuma operação foi executada ainda. Confira a lista e clique em EXECUTAR.";
    }

    public SyncPlan Plan { get; }
    public string Title { get; }
    public string Header { get; }
    public string Footer { get; }
    public string DataText { get; }
    public IReadOnlyList<SummaryEntry> Cards { get; }
    public IReadOnlyList<string> Notices { get; }
    public bool HasNotices => Notices.Count > 0;
    public IReadOnlyList<OperationFilter> Filters { get; }
    public ICollectionView View { get; }
    public int VisibleCount { get => _visibleCount; private set => SetProperty(ref _visibleCount, value); }
    public string VisibleText => $"{VisibleCount:N0} de {Plan.Operations.Count:N0} operações";

    public OperationFilter SelectedFilter
    {
        get => _selectedFilter;
        set { if (SetProperty(ref _selectedFilter, value)) Refresh(); }
    }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) Refresh(); }
    }

    private void Refresh()
    {
        View.Refresh();
        VisibleCount = View.Cast<object>().Count();
        OnPropertyChanged(nameof(VisibleText));
    }

    private bool Matches(PlanRow r) =>
        _selectedFilter.Predicate(r.Operation) &&
        (_search.Length == 0 || r.Operation.RelativePath.Contains(_search, StringComparison.OrdinalIgnoreCase));

    /// <summary>A lista exatamente como está na tela (filtro, pesquisa e ordenação), em CSV com ';' e UTF-8 (abre direto no Excel pt-BR).</summary>
    public string ToCsv()
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Operação;Origem;Destino;Tamanho (bytes);Status;Motivo");
        foreach (var r in View.Cast<PlanRow>())
            sb.AppendLine(string.Join(';', Q(r.Label), Q(r.From), Q(r.To),
                r.SizeSort.ToString(CultureInfo.InvariantCulture), Q(r.StatusText), Q(r.Reason)));
        return sb.ToString();
    }
}
