using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public class FtpDiagnosticStepResult : ObservableObject
{
    private string _stepName = string.Empty;
    private FtpDiagnosticStepStatus _status;
    private string _message = string.Empty;
    private TimeSpan _duration;
    private string? _details;

    public string StepName
    {
        get => _stepName;
        set => SetProperty(ref _stepName, value);
    }

    public FtpDiagnosticStepStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public TimeSpan Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    public string? Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }
}

public sealed class FtpDiagnosticsViewModel : ObservableObject
{
    private readonly IFtpDiagnosticService _diagnosticService;
    private readonly FtpConnectionProfile _profile;
    private readonly string _password;

    private bool _isRunning;
    private bool _isCompleted;
    private bool _overallSuccess;
    private string _statusSummary = "Pronto para iniciar diagnóstico.";
    private CancellationTokenSource? _cts;

    public ObservableCollection<FtpDiagnosticStepResult> Steps { get; } = new();
    public ObservableCollection<FtpProtocolLogEntry> ProtocolLogs { get; } = new();

    public string ServerInfo => $"{_profile.Name} ({_profile.Host}:{_profile.Port} - {_profile.Protocol})";

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                CloseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public bool OverallSuccess
    {
        get => _overallSuccess;
        set => SetProperty(ref _overallSuccess, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        set => SetProperty(ref _statusSummary, value);
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyLogsCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? RequestClose;
    public event Action? RequestScrollToBottom;

    public FtpDiagnosticsViewModel(
        FtpConnectionProfile profile,
        string password,
        IFtpDiagnosticService diagnosticService)
    {
        _profile = profile;
        _password = password;
        _diagnosticService = diagnosticService;

        StartCommand = new RelayCommand(async () => await RunDiagnosticsAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(CancelDiagnostics, () => IsRunning);
        CopyLogsCommand = new RelayCommand(CopyLogsToClipboard);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !IsRunning);

        InitializeSteps();
    }

    private void InitializeSteps()
    {
        Steps.Clear();
        Steps.Add(new FtpDiagnosticStepResult { StepName = "1. Resolução DNS", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "2. Conexão TCP", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "3. Negociação TLS", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "4. Validação Certificado", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "5. Autenticação", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "6. Modo Passivo (PASV/EPSV)", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "7. Listagem de Diretório", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
        Steps.Add(new FtpDiagnosticStepResult { StepName = "8. Suporte a Timestamps (MFMT/MDTM)", Status = FtpDiagnosticStepStatus.Pending, Message = "Aguardando..." });
    }

    public async Task RunDiagnosticsAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        IsCompleted = false;
        OverallSuccess = false;
        StatusSummary = "Executando testes de conectividade e protocolo...";
        ProtocolLogs.Clear();
        InitializeSteps();

        _cts = new CancellationTokenSource();

        try
        {
            bool hasFailure = false;
            string? failureReason = null;

            await foreach (var stepResult in _diagnosticService.RunDiagnosticsAsync(_profile, _password, _cts.Token))
            {
                var stepIndex = (int)stepResult.Step - 1;
                if (stepIndex >= 0 && stepIndex < Steps.Count)
                {
                    var existing = Steps[stepIndex];
                    existing.Status = stepResult.Status;
                    existing.Message = string.IsNullOrWhiteSpace(stepResult.Details) ? stepResult.Description : stepResult.Details;
                    existing.Duration = TimeSpan.FromMilliseconds(stepResult.ElapsedMilliseconds);
                    existing.Details = stepResult.Details;

                    if (stepResult.Status == FtpDiagnosticStepStatus.Failed)
                    {
                        hasFailure = true;
                        failureReason ??= $"{stepResult.Title}: {stepResult.Details ?? stepResult.Description}";
                    }
                }

                ProtocolLogs.Add(new FtpProtocolLogEntry($"[{stepResult.Step}] {stepResult.Title} - {stepResult.Status}: {stepResult.Details ?? stepResult.Description}"));
                RequestScrollToBottom?.Invoke();
            }

            OverallSuccess = !hasFailure;
            StatusSummary = !hasFailure
                ? "Todos os testes de diagnóstico foram concluídos com sucesso!"
                : $"Diagnóstico concluído com falhas: {failureReason}";
        }
        catch (OperationCanceledException)
        {
            StatusSummary = "Diagnóstico cancelado pelo usuário.";
        }
        catch (Exception ex)
        {
            OverallSuccess = false;
            StatusSummary = $"Erro inesperado: {ex.Message}";
            ProtocolLogs.Add(new FtpProtocolLogEntry($"Erro: {ex.Message}"));
        }
        finally
        {
            IsRunning = false;
            IsCompleted = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelDiagnostics()
    {
        _cts?.Cancel();
    }

    private void CopyLogsToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- DIAGNÓSTICO FTP: {_profile.Name} ({_profile.Host}:{_profile.Port}) ---");
        sb.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("ETAPAS:");
        foreach (var s in Steps)
        {
            sb.AppendLine($"[{s.Status}] {s.StepName}: {s.Message} ({s.Duration.TotalMilliseconds:F0}ms)");
            if (!string.IsNullOrEmpty(s.Details))
            {
                sb.AppendLine($"   Detalhes: {s.Details}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("LOGS DE PROTOCOLO FTP:");
        foreach (var l in ProtocolLogs)
        {
            sb.AppendLine($"[{l.Timestamp:HH:mm:ss.fff}] [{l.Direction}] {l.Message}");
        }

        Clipboard.SetText(sb.ToString());
        MessageBox.Show("Logs e etapas de diagnóstico copiados para a área de transferência!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
