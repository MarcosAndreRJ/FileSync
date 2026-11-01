using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class FtpExecutionLogItem
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public string Severity { get; init; } = "INFO";
}

public sealed class FtpExecutionViewModel : ObservableObject
{
    private readonly FtpSyncTask _task;
    private readonly IReadOnlyList<FtpChecklistItem> _items;
    private readonly IFtpTransferEngine _transferEngine;
    private readonly IFtpConnectionService _connectionService;
    private readonly IFtpHistoryRepository _historyRepo;

    private bool _isRunning;
    private bool _isCompleted;
    private bool _isCancelled;
    private bool _hasErrors;

    private int _completedFilesCount;
    private int _totalFilesCount;
    private long _transferredBytes;
    private long _totalBytes;
    private double _overallProgressPercentage;

    private string _currentFileName = string.Empty;
    private double _currentFilePercentage;

    private double _transferRateBytesPerSecond;
    private TimeSpan _estimatedTimeRemaining = TimeSpan.Zero;
    private string _statusMessage = "Pronto para iniciar transferência.";

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _rateTimer;
    private long _lastBytesSample;

    public FtpSyncTask Task => _task;
    public ObservableCollection<FtpExecutionLogItem> Logs { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public bool HasErrors
    {
        get => _hasErrors;
        set => SetProperty(ref _hasErrors, value);
    }

    public int CompletedFilesCount
    {
        get => _completedFilesCount;
        set => SetProperty(ref _completedFilesCount, value);
    }

    public int TotalFilesCount
    {
        get => _totalFilesCount;
        set => SetProperty(ref _totalFilesCount, value);
    }

    public long TransferredBytes
    {
        get => _transferredBytes;
        set
        {
            if (SetProperty(ref _transferredBytes, value))
            {
                OnPropertyChanged(nameof(FormattedTransferredBytes));
            }
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(FormattedTotalBytes));
            }
        }
    }

    public string FormattedTransferredBytes => FormatBytes(TransferredBytes);
    public string FormattedTotalBytes => FormatBytes(TotalBytes);

    public double OverallProgressPercentage
    {
        get => _overallProgressPercentage;
        set => SetProperty(ref _overallProgressPercentage, value);
    }

    public string CurrentFileName
    {
        get => _currentFileName;
        set => SetProperty(ref _currentFileName, value);
    }

    public double CurrentFilePercentage
    {
        get => _currentFilePercentage;
        set => SetProperty(ref _currentFilePercentage, value);
    }

    public string TransferSpeedFormatted => $"{FormatBytes((long)_transferRateBytesPerSecond)}/s";

    public string EtaFormatted
    {
        get
        {
            if (_estimatedTimeRemaining <= TimeSpan.Zero || _transferRateBytesPerSecond <= 0)
                return "--:--";

            if (_estimatedTimeRemaining.TotalHours >= 1)
                return _estimatedTimeRemaining.ToString(@"hh\:mm\:ss");

            return _estimatedTimeRemaining.ToString(@"mm\:ss");
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand StartTransferCommand { get; }
    public RelayCommand CancelTransferCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? RequestClose;
    public event Action? RequestScrollToBottom;

    public FtpExecutionViewModel(
        FtpSyncTask task,
        IReadOnlyList<FtpChecklistItem> items,
        IFtpTransferEngine transferEngine,
        IFtpConnectionService connectionService,
        IFtpHistoryRepository historyRepo)
    {
        _task = task;
        _items = items;
        _transferEngine = transferEngine;
        _connectionService = connectionService;
        _historyRepo = historyRepo;

        TotalFilesCount = items.Count;
        TotalBytes = items.Where(i => i.ProposedAction == FtpPlannedAction.UploadNew || i.ProposedAction == FtpPlannedAction.UpdateExisting).Sum(i => i.FileSizeBytes);

        StartTransferCommand = new RelayCommand(async () => await StartExecutionAsync(), () => !IsRunning && !IsCompleted);
        CancelTransferCommand = new RelayCommand(CancelExecution, () => IsRunning);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !IsRunning);

        _rateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rateTimer.Tick += (_, _) => UpdateSpeedAndEta();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void UpdateSpeedAndEta()
    {
        if (!IsRunning) return;

        long currentBytes = TransferredBytes;
        long delta = currentBytes - _lastBytesSample;
        _lastBytesSample = currentBytes;

        _transferRateBytesPerSecond = delta;
        OnPropertyChanged(nameof(TransferSpeedFormatted));

        long remainingBytes = TotalBytes - currentBytes;
        if (_transferRateBytesPerSecond > 0 && remainingBytes > 0)
        {
            double seconds = (double)remainingBytes / _transferRateBytesPerSecond;
            _estimatedTimeRemaining = TimeSpan.FromSeconds(Math.Min(seconds, 86400));
        }
        else
        {
            _estimatedTimeRemaining = TimeSpan.Zero;
        }
        OnPropertyChanged(nameof(EtaFormatted));
    }

    public Task StartTransferAsync() => StartExecutionAsync();

    public async Task StartExecutionAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        IsCompleted = false;
        _isCancelled = false;
        HasErrors = false;
        CompletedFilesCount = 0;
        TransferredBytes = 0;
        _lastBytesSample = 0;
        Logs.Clear();

        _cts = new CancellationTokenSource();
        _stopwatch.Restart();
        _rateTimer.Start();

        AddLog("Iniciando execução da tarefa de sincronização...", "INFO");

        var profile = await _connectionService.GetByIdAsync(_task.ConnectionProfileId);
        if (profile == null)
        {
            AddLog("Perfil de conexão FTP não encontrado.", "ERROR");
            HasErrors = true;
            IsRunning = false;
            IsCompleted = true;
            return;
        }

        var password = await _connectionService.GetPasswordAsync(profile.Id);

        var progress = new Progress<FtpTransferProgressInfo>(p =>
        {
            CurrentFileName = p.CurrentFileRelativePath;
            CurrentFilePercentage = p.CurrentFileProgressPercentage;
            CompletedFilesCount = p.TotalBytes > 0 ? (int)(p.TransferredBytes * TotalFilesCount / p.TotalBytes) : 0;
            TransferredBytes = p.TransferredBytes;
            OverallProgressPercentage = p.ProgressPercentage;

            if (!string.IsNullOrWhiteSpace(p.CurrentOperationDescription))
            {
                StatusMessage = p.CurrentOperationDescription;
            }
        });

        var logProgress = new Progress<FtpProtocolLogEntry>(entry =>
        {
            AddLog($"[{entry.Direction}] {entry.Message}", entry.IsCommand ? "CMD" : "INFO");
        });

        int successfulCount = 0;
        int failedCount = 0;
        FtpExecutionHistoryEntry? executionResult = null;

        try
        {
            executionResult = await _transferEngine.ExecuteTransferAsync(
                _task,
                profile,
                _items,
                progress,
                logProgress,
                _cts.Token);

            successfulCount = executionResult.ProcessedFiles;
            failedCount = executionResult.FailedFiles;
            HasErrors = failedCount > 0 || executionResult.Status == FtpExecutionStatus.Failed;

            if (executionResult.Status == FtpExecutionStatus.Success)
            {
                StatusMessage = "Transferência concluída com sucesso!";
                AddLog("Todos os itens foram processados com sucesso.", "SUCCESS");
            }
            else
            {
                StatusMessage = $"Concluído com avisos/erros. Falhas: {failedCount}";
                AddLog($"Aviso: {failedCount} arquivo(s) apresentaram erro.", "WARN");
            }
        }
        catch (OperationCanceledException)
        {
            _isCancelled = true;
            StatusMessage = "Transferência cancelada pelo usuário.";
            AddLog("Execução interrompida a pedido do usuário.", "WARN");
        }
        catch (Exception ex)
        {
            HasErrors = true;
            StatusMessage = $"Erro fatal durante transferência: {ex.Message}";
            AddLog($"ERRO FATAL: {ex.Message}", "ERROR");
        }
        finally
        {
            _stopwatch.Stop();
            _rateTimer.Stop();
            IsRunning = false;
            IsCompleted = true;

            if (executionResult == null)
            {
                var historyRecord = new FtpExecutionHistoryEntry
                {
                    TaskId = _task.Id,
                    TaskName = _task.Name,
                    ConnectionName = profile.Name,
                    ExecutedAt = DateTime.UtcNow - _stopwatch.Elapsed,
                    Duration = _stopwatch.Elapsed,
                    TotalFiles = TotalFilesCount,
                    ProcessedFiles = successfulCount,
                    FailedFiles = failedCount,
                    TotalBytesTransferred = TransferredBytes,
                    Status = _isCancelled ? FtpExecutionStatus.Cancelled : FtpExecutionStatus.Failed
                };

                try
                {
                    await _historyRepo.AddAsync(historyRecord);
                    AddLog("Registro de histórico salvo com sucesso.", "INFO");
                }
                catch (Exception ex)
                {
                    AddLog($"Falha ao salvar histórico: {ex.Message}", "WARN");
                }
            }

            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelExecution()
    {
        if (IsRunning && _cts != null && !_cts.IsCancellationRequested)
        {
            AddLog("Solicitando cancelamento da transferência...", "WARN");
            _cts.Cancel();
        }
    }

    private void AddLog(string message, string severity)
    {
        Logs.Add(new FtpExecutionLogItem
        {
            Message = message,
            Severity = severity
        });
        RequestScrollToBottom?.Invoke();
    }
}
