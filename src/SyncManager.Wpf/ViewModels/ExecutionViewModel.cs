using System.Collections.ObjectModel;
using System.Windows.Media;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels;

/// <summary>Uma linha da lista de operações da tela de execução (Hora / Operação / Arquivo / Status).</summary>
public sealed record OperationEntry(string Time, string Operation, string File, string Status, string StatusKey, Brush StatusBrush);

/// <summary>
/// Estado da tela de Execução: só apresenta o <see cref="ExecutionProgress"/> que o executor já informa e, no fim, o
/// <see cref="ExecutionResult"/>. Não executa nada.
/// </summary>
public sealed class ExecutionViewModel : ObservableObject
{
    private const int LiveLimit = 2000;   // a lista ao vivo não cresce sem limite numa execução de milhares de arquivos

    private readonly Dictionary<string, string> _seenAt = new(StringComparer.OrdinalIgnoreCase);
    private string _profileName = "";
    private string _title = "";
    private string _currentOperation = "";
    private double _percent;
    private string _percentText = "0%";
    private string _filesText = "";
    private string _bytesText = "";
    private string _speedText = "";
    private string _elapsedText = "00:00:00";
    private string _remainingText = "—";
    private bool _isRunning;
    private bool _isFinished;
    private string _resultTitle = "";
    private string _resultText = "";
    private Brush _resultBrush = Presentation.Gray;
    private string? _resultIcon;
    private string? _currentSeen;
    private object _operations = new ObservableCollection<OperationEntry>();

    public string ProfileName { get => _profileName; private set => SetProperty(ref _profileName, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string CurrentOperation { get => _currentOperation; private set => SetProperty(ref _currentOperation, value); }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public string PercentText { get => _percentText; private set => SetProperty(ref _percentText, value); }
    public string FilesText { get => _filesText; private set => SetProperty(ref _filesText, value); }
    public string BytesText { get => _bytesText; private set => SetProperty(ref _bytesText, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(IsFinishedOrIdle)); } }
    public bool IsFinished { get => _isFinished; private set { if (SetProperty(ref _isFinished, value)) OnPropertyChanged(nameof(IsFinishedOrIdle)); } }
    public bool IsFinishedOrIdle => !_isRunning;
    public string ResultTitle { get => _resultTitle; private set => SetProperty(ref _resultTitle, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public Brush ResultBrush { get => _resultBrush; private set => SetProperty(ref _resultBrush, value); }
    public string? ResultIcon { get => _resultIcon; private set => SetProperty(ref _resultIcon, value); }

    /// <summary>Durante a execução: os arquivos que o executor foi anunciando. Depois: o resultado de cada operação.</summary>
    public object Operations { get => _operations; private set => SetProperty(ref _operations, value); }

    public void Begin(string profileName, SyncPlan plan)
    {
        _seenAt.Clear();
        _currentSeen = null;
        ProfileName = string.IsNullOrWhiteSpace(profileName) ? "Perfil sem nome" : profileName;
        Title = $"{Presentation.MethodName(plan.Method)}   •   {plan.SourceRoot}  →  {plan.TargetRoot}";
        CurrentOperation = "Preparando...";
        Percent = 0; PercentText = "0%";
        FilesText = $"0 / {plan.ExecutableCount:N0}";
        BytesText = $"0 B / {Presentation.Bytes(plan.TotalBytes)}";
        SpeedText = "—";
        ElapsedText = "00:00:00";
        RemainingText = "—";
        ResultTitle = ""; ResultText = ""; ResultIcon = null;
        Operations = new ObservableCollection<OperationEntry>();
        IsFinished = false;
        IsRunning = true;
    }

    public void Update(ExecutionProgress p)
    {
        Percent = Math.Round(p.Fraction * 100, 1);
        PercentText = $"{Math.Floor(p.Fraction * 100):0}%";
        FilesText = $"{p.CompletedOperations:N0} / {p.TotalOperations:N0}";
        BytesText = $"{Presentation.Bytes(p.BytesProcessed)} / {Presentation.Bytes(p.TotalBytes)}";
        SpeedText = p.BytesPerSecond > 0 ? Presentation.Bytes((long)p.BytesPerSecond) + "/s" : "—";
        ElapsedText = p.Elapsed.ToString(@"hh\:mm\:ss");
        RemainingText = Remaining(p);

        if (!string.IsNullOrEmpty(p.CurrentFile))
        {
            CurrentOperation = p.CurrentFile;
            if (p.CurrentFile != _currentSeen && Operations is ObservableCollection<OperationEntry> live)
            {
                _currentSeen = p.CurrentFile;
                var time = DateTime.Now.ToString("HH:mm:ss");
                _seenAt[p.CurrentFile] = time;
                live.Add(new OperationEntry(time, "Processando", p.CurrentFile, "Em andamento", "running", Presentation.Blue));
                if (live.Count > LiveLimit) live.RemoveAt(0);
            }
        }
    }

    /// <summary>Estimativa: pelos bytes restantes quando há velocidade; senão pela fração de operações já feitas.</summary>
    public static string Remaining(ExecutionProgress p)
    {
        TimeSpan? left = null;
        if (p.TotalBytes > 0 && p.BytesPerSecond > 1 && p.BytesProcessed < p.TotalBytes)
            left = TimeSpan.FromSeconds((p.TotalBytes - p.BytesProcessed) / p.BytesPerSecond);
        else if (p.CompletedOperations > 0 && p.CompletedOperations < p.TotalOperations)
            left = TimeSpan.FromSeconds(p.Elapsed.TotalSeconds * (p.TotalOperations - p.CompletedOperations) / p.CompletedOperations);
        return left is { } l ? l.ToString(@"hh\:mm\:ss") : "—";
    }

    public void Finish(ExecutionResult r)
    {
        var entries = new List<OperationEntry>(r.Results.Count);
        foreach (var x in r.Results)
        {
            var (status, key, brush) = x.Outcome switch
            {
                OperationOutcome.Succeeded => ("Concluído", "ok", Presentation.Green),
                OperationOutcome.Skipped => ("Ignorado", "skip", Presentation.Gray),
                OperationOutcome.Warning => ("Aviso", "warning", Presentation.Orange),
                OperationOutcome.Cancelled => ("Cancelado", "skip", Presentation.Gray),
                _ => ("Erro", "error", Presentation.Red)
            };
            var file = x.Message is { Length: > 0 } && x.Outcome is OperationOutcome.Failed or OperationOutcome.Warning
                ? $"{x.Operation.RelativePath} — {x.Message}" : x.Operation.RelativePath;
            entries.Add(new OperationEntry(_seenAt.GetValueOrDefault(x.Operation.RelativePath, ""),
                Presentation.PlanLabel(x.Operation), file, status, key, brush));
        }
        Operations = entries;

        var (icon, brushResult) = r.Status switch
        {
            ExecutionStatus.Success => ("IconCheck", Presentation.Green),
            ExecutionStatus.SuccessWithWarnings or ExecutionStatus.ConflictsUnresolved => ("IconWarning", Presentation.Orange),
            ExecutionStatus.Cancelled => ("IconBlock", Presentation.Gray),
            _ => ("IconError", Presentation.Red)
        };
        ResultIcon = icon;
        ResultBrush = brushResult;
        ResultTitle = Presentation.StatusName(r.Status);
        ResultText =
            $"Copiados {r.FilesCopied:N0}  •  Atualizados {r.FilesUpdated:N0}  •  Excluídos {r.FilesDeleted:N0}  •  Ignorados {r.FilesSkipped:N0}  •  " +
            $"Conflitos {r.Conflicts:N0}  •  Erros {r.Errors:N0}  •  Avisos {r.Warnings:N0}" +
            (r.NotExecuted > 0 ? $"  •  Não executados {r.NotExecuted:N0}" : "") +
            $"\nTransferido {Presentation.Bytes(r.BytesTransferred)} em {r.Duration:hh\\:mm\\:ss}" +
            (r.SafetyFolder is not null ? $"\nPasta de segurança: {r.SafetyFolder}" : "") +
            (r.Message is { Length: > 0 } ? $"\n{r.Message}" : "");

        if (r.Status == ExecutionStatus.Success) { Percent = 100; PercentText = "100%"; }
        ElapsedText = r.Duration.ToString(@"hh\:mm\:ss");
        RemainingText = "—";
        CurrentOperation = Presentation.StatusName(r.Status);
        IsRunning = false;
        IsFinished = true;
    }

    /// <summary>Erro inesperado: encerra o estado "em execução" sem resultado.</summary>
    public void Fail(string message)
    {
        ResultIcon = "IconError";
        ResultBrush = Presentation.Red;
        ResultTitle = "Falhou / interrompido";
        ResultText = message;
        CurrentOperation = "Falhou / interrompido";
        IsRunning = false;
        IsFinished = true;
    }
}
