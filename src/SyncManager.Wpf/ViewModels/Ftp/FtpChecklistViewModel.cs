using System.Collections.ObjectModel;
using System.Windows;
using SyncManager.Application.Ftp;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class ChecklistItemRow : ObservableObject
{
    private bool _isSelected;

    public FtpChecklistItem Item { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Item.IsSelected = value;
                OnSelectionChanged?.Invoke();
            }
        }
    }

    public string RelativePath => Item.RelativePath;
    public FtpPlannedAction Action => Item.ProposedAction;
    public long SizeBytes => Item.FileSizeBytes;
    public string FormattedSize => FormatBytes(Item.FileSizeBytes);
    public string? Reason => Item.StatusReason;

    public Action? OnSelectionChanged { get; set; }

    public ChecklistItemRow(FtpChecklistItem item)
    {
        Item = item;
        _isSelected = item.IsSelected;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed class FtpChecklistViewModel : ObservableObject
{
    private readonly FtpSyncTask _task;
    private readonly Func<FtpSyncTask, IReadOnlyList<FtpChecklistItem>, FtpExecutionViewModel> _executionViewModelFactory;

    private bool _confirmedRisk;
    private string _riskConfirmationText = string.Empty;

    public FtpSyncTask Task => _task;
    public ObservableCollection<ChecklistItemRow> Items { get; } = new();

    public int TotalItemsCount => Items.Count;
    public int SelectedItemsCount => Items.Count(i => i.IsSelected);
    public int UploadCount => Items.Count(i => i.IsSelected && (i.Action == FtpPlannedAction.UploadNew || i.Action == FtpPlannedAction.UpdateExisting));
    public int DownloadCount => 0;
    public int DeleteCount => Items.Count(i => i.IsSelected && i.Action == FtpPlannedAction.DeleteRemote);
    public long TotalSelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

    public string FormattedTotalBytes
    {
        get
        {
            long bytes = TotalSelectedBytes;
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public bool HasDeletions => DeleteCount > 0;
    public bool RequiresRiskConfirmation => HasDeletions && _task.RequireExplicitChecklistConfirmation;

    public bool ConfirmedRisk
    {
        get => _confirmedRisk;
        set
        {
            if (SetProperty(ref _confirmedRisk, value))
            {
                OnPropertyChanged(nameof(CanExecute));
            }
        }
    }

    public bool CanExecute => SelectedItemsCount > 0 && (!RequiresRiskConfirmation || ConfirmedRisk);

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectOnlyUploadsCommand { get; }
    public RelayCommand ExecuteTransferCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? RequestClose;
    public event Action<FtpExecutionViewModel>? RequestExecutionDialog;

    public FtpChecklistViewModel(
        FtpSyncTask task,
        IReadOnlyList<FtpChecklistItem> items,
        Func<FtpSyncTask, IReadOnlyList<FtpChecklistItem>, FtpExecutionViewModel> executionViewModelFactory)
    {
        _task = task;
        _executionViewModelFactory = executionViewModelFactory;

        foreach (var item in items)
        {
            var row = new ChecklistItemRow(item)
            {
                OnSelectionChanged = UpdateCounts
            };
            Items.Add(row);
        }

        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        SelectNoneCommand = new RelayCommand(() => SetSelection(false));
        SelectOnlyUploadsCommand = new RelayCommand(SelectUploadsOnly);
        ExecuteTransferCommand = new RelayCommand(ExecuteTransfer, () => CanExecute);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    private void SetSelection(bool selected)
    {
        foreach (var item in Items)
        {
            item.IsSelected = selected;
        }
        UpdateCounts();
    }

    private void SelectUploadsOnly()
    {
        foreach (var item in Items)
        {
            item.IsSelected = item.Action == FtpPlannedAction.UploadNew || item.Action == FtpPlannedAction.UpdateExisting;
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(SelectedItemsCount));
        OnPropertyChanged(nameof(UploadCount));
        OnPropertyChanged(nameof(DownloadCount));
        OnPropertyChanged(nameof(DeleteCount));
        OnPropertyChanged(nameof(TotalSelectedBytes));
        OnPropertyChanged(nameof(FormattedTotalBytes));
        OnPropertyChanged(nameof(HasDeletions));
        OnPropertyChanged(nameof(RequiresRiskConfirmation));
        OnPropertyChanged(nameof(CanExecute));
    }

    private void ExecuteTransfer()
    {
        var selectedItems = Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();

        // Checar travas de segurança
        if (_task.ProhibitRemoteDeletions && selectedItems.Any(i => i.ProposedAction == FtpPlannedAction.DeleteRemote))
        {
            MessageBox.Show(
                "A trava de segurança 'Proibir Exclusões Remotas' está ativa nesta tarefa. Nenhuma exclusão no servidor FTP é permitida.",
                "Trava de Segurança Violada",
                MessageBoxButton.OK,
                MessageBoxImage.Stop);
            return;
        }

        int totalRemoteFiles = Items.Count;
        int remoteDeletions = selectedItems.Count(i => i.ProposedAction == FtpPlannedAction.DeleteRemote);
        if (totalRemoteFiles > 0 && _task.MaxRemoteDeletePercentage > 0)
        {
            double deletePercent = (double)remoteDeletions / totalRemoteFiles;
            if (deletePercent > _task.MaxRemoteDeletePercentage)
            {
                MessageBox.Show(
                    $"A quantidade de exclusões remotas ({remoteDeletions} itens = {deletePercent:P1}) excede o limite configurado de segurança ({_task.MaxRemoteDeletePercentage:P1}).\nTransferência abortada por segurança.",
                    "Limite de Exclusão Excedido",
                    MessageBoxButton.OK,
                    MessageBoxImage.Stop);
                return;
            }
        }

        var executionVm = _executionViewModelFactory(_task, selectedItems);
        RequestExecutionDialog?.Invoke(executionVm);
        RequestClose?.Invoke();
    }
}
