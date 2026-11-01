using System.Collections.ObjectModel;
using System.Windows;
using SyncManager.Application.Ftp;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class FtpTasksViewModel : ObservableObject
{
    private readonly IFtpTaskService _taskService;
    private readonly IFtpConnectionService _connectionService;
    private readonly Func<FtpSyncTask, FtpTaskEditViewModel> _editTaskViewModelFactory;
    private readonly Func<FtpSyncTask, IReadOnlyList<FtpChecklistItem>, FtpChecklistViewModel> _checklistViewModelFactory;

    private FtpSyncTask? _selectedTask;
    private bool _isLoading;
    private string _filterText = string.Empty;

    public ObservableCollection<FtpSyncTask> Tasks { get; } = new();

    public FtpSyncTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedTask != null;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                _ = RefreshTasksAsync();
            }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand EditTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand RunTaskCommand { get; }

    public event Action<FtpTaskEditViewModel>? RequestEditTaskDialog;
    public event Action<FtpChecklistViewModel>? RequestChecklistDialog;

    public FtpTasksViewModel(
        IFtpTaskService taskService,
        IFtpConnectionService connectionService,
        Func<FtpSyncTask, FtpTaskEditViewModel> editTaskViewModelFactory,
        Func<FtpSyncTask, IReadOnlyList<FtpChecklistItem>, FtpChecklistViewModel> checklistViewModelFactory)
    {
        _taskService = taskService;
        _connectionService = connectionService;
        _editTaskViewModelFactory = editTaskViewModelFactory;
        _checklistViewModelFactory = checklistViewModelFactory;

        RefreshCommand = new RelayCommand(async () => await RefreshTasksAsync());
        NewTaskCommand = new RelayCommand(ExecuteNewTask);
        EditTaskCommand = new RelayCommand(ExecuteEditTask, () => HasSelection);
        DeleteTaskCommand = new RelayCommand(async () => await ExecuteDeleteTaskAsync(), () => HasSelection);
        RunTaskCommand = new RelayCommand(async () => await ExecuteRunTaskAsync(), () => HasSelection);
    }

    public async Task InitializeAsync()
    {
        await RefreshTasksAsync();
    }

    public Task LoadTasksAsync() => RefreshTasksAsync();

    public async Task RefreshTasksAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _taskService.GetAllAsync();
            Tasks.Clear();

            var filtered = string.IsNullOrWhiteSpace(FilterText)
                ? list
                : list.Where(t => t.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                  t.SourceLocalPath.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                  t.TargetRemotePath.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

            foreach (var item in filtered.OrderBy(t => t.Name))
            {
                Tasks.Add(item);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao carregar tarefas FTP: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteNewTask()
    {
        var task = new FtpSyncTask();
        var vm = _editTaskViewModelFactory(task);
        vm.OnSaved += async () => await RefreshTasksAsync();
        RequestEditTaskDialog?.Invoke(vm);
    }

    private void ExecuteEditTask()
    {
        if (SelectedTask == null) return;
        var vm = _editTaskViewModelFactory(SelectedTask.Clone());
        vm.OnSaved += async () => await RefreshTasksAsync();
        RequestEditTaskDialog?.Invoke(vm);
    }

    private async Task ExecuteDeleteTaskAsync()
    {
        if (SelectedTask == null) return;

        var result = MessageBox.Show(
            $"Deseja realmente excluir a tarefa de sincronização FTP \"{SelectedTask.Name}\"?",
            "Confirmar Exclusão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await _taskService.DeleteAsync(SelectedTask.Id);
                await RefreshTasksAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao excluir tarefa: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task ExecuteRunTaskAsync()
    {
        if (SelectedTask == null) return;

        IsLoading = true;
        try
        {
            var statusProgress = new Progress<string>(msg =>
            {
                // Progresso da comparação
            });

            var checklist = await _taskService.GenerateChecklistAsync(SelectedTask.Id, statusProgress);
            var checklistVm = _checklistViewModelFactory(SelectedTask, checklist);
            RequestChecklistDialog?.Invoke(checklistVm);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao gerar checklist de sincronização FTP:\n{ex.Message}", "Erro de Comparação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
