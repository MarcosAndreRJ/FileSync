using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels;

public sealed class HomeProfileItem
{
    public HomeProfileItem(SyncProfile profile, ProfileLastRun? lastRun)
    {
        Profile = profile;
        Id = profile.Id;
        Name = profile.Name;
        SourcePath = profile.SourcePath;
        TargetPath = profile.TargetPath;
        MethodText = Presentation.MethodName(profile.Method);
        LastRunText = lastRun is null ? "Nunca executado" : lastRun.StartedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
        StatusText = lastRun is null ? "Sem execuções" : Presentation.StatusName(lastRun.Status);
        StatusBrush = lastRun?.Status switch
        {
            null => Presentation.Gray,
            ExecutionStatus.Success => Presentation.Green,
            ExecutionStatus.Failed => Presentation.Red,
            _ => Presentation.Orange
        };
    }

    public SyncProfile Profile { get; }
    public int Id { get; }
    public string Name { get; }
    public string SourcePath { get; }
    public string TargetPath { get; }
    public string MethodText { get; }
    public string LastRunText { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
}

public sealed class HomeActivityItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public string StatusText { get; init; } = "";
    public Brush StatusBrush { get; init; } = Presentation.Gray;
    public bool IsFtp { get; init; }
    public string BadgeText { get; init; } = "Local";
    public Brush BadgeBackground { get; init; } = Presentation.Blue;
}

public sealed class HomeViewModel : ObservableObject
{
    private readonly IProfileRepository _profiles;
    private readonly IExecutionHistoryRepository _history;
    private readonly IScheduleRepository _schedules;
    private readonly IFtpConnectionRepository _ftpConnections;
    private readonly IFtpTaskRepository _ftpTasks;
    private readonly IFtpHistoryRepository _ftpHistory;

    private int _totalProfiles;
    private int _totalSchedules;
    private int _totalFtpConnections;
    private int _totalFtpTasks;
    private int _totalExecutions;
    private bool _isLoading;
    private bool _hasProfiles;
    private bool _hasActivities;
    private bool _hasLastExecution;
    private string _lastExecutionTitle = "";
    private string _lastExecutionStatus = "";
    private Brush _lastExecutionBrush = Presentation.Gray;
    private string _lastExecutionTime = "";
    private string _lastExecutionDetails = "";

    public HomeViewModel(
        IProfileRepository profiles,
        IExecutionHistoryRepository history,
        IScheduleRepository schedules,
        IFtpConnectionRepository ftpConnections,
        IFtpTaskRepository ftpTasks,
        IFtpHistoryRepository ftpHistory)
    {
        _profiles = profiles;
        _history = history;
        _schedules = schedules;
        _ftpConnections = ftpConnections;
        _ftpTasks = ftpTasks;
        _ftpHistory = ftpHistory;

        RecentProfiles = new ObservableCollection<HomeProfileItem>();
        RecentActivities = new ObservableCollection<HomeActivityItem>();

        NewComparisonCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.Comparison);
            NewComparisonRequested?.Invoke();
        });
        OpenProfilesCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.Profiles);
            OpenProfilesRequested?.Invoke();
        });
        OpenSchedulesCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.Schedules);
            OpenSchedulesRequested?.Invoke();
        });
        OpenFtpConnectionsCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.FtpConnections);
            OpenFtpConnectionsRequested?.Invoke();
        });
        OpenFtpTasksCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.FtpTasks);
            OpenFtpTasksRequested?.Invoke();
        });
        OpenHistoryCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.History);
            OpenHistoryRequested?.Invoke();
        });
        OpenLogsCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.Logs);
            OpenLogsRequested?.Invoke();
        });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            RequestNavigate?.Invoke(AppScreen.Settings);
            OpenSettingsRequested?.Invoke();
        });
        NewProfileCommand = new RelayCommand(() =>
        {
            RequestNewProfile?.Invoke();
            NewProfileRequested?.Invoke();
        });
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        CompareProfileCommand = new RelayCommand<HomeProfileItem>(item =>
        {
            if (item != null)
            {
                RequestCompareProfile?.Invoke(item.Profile);
                CompareProfileRequested?.Invoke(item.Profile);
            }
        });
        ExecuteProfileCommand = new RelayCommand<HomeProfileItem>(item =>
        {
            if (item != null)
            {
                RequestExecuteProfile?.Invoke(item.Profile);
                ExecuteProfileRequested?.Invoke(item.Profile);
            }
        });
    }

    public event Action<AppScreen>? RequestNavigate;
    public event Action<SyncProfile>? RequestCompareProfile;
    public event Action<SyncProfile>? RequestExecuteProfile;
    public event Action? RequestNewProfile;

    public event Action? NewComparisonRequested;
    public event Action? OpenProfilesRequested;
    public event Action? OpenSchedulesRequested;
    public event Action? OpenFtpConnectionsRequested;
    public event Action? OpenFtpTasksRequested;
    public event Action? OpenHistoryRequested;
    public event Action? OpenLogsRequested;
    public event Action? OpenSettingsRequested;
    public event Action? NewProfileRequested;
    public event Action<SyncProfile>? CompareProfileRequested;
    public event Action<SyncProfile>? ExecuteProfileRequested;

    public int TotalProfiles { get => _totalProfiles; private set => SetProperty(ref _totalProfiles, value); }
    public int TotalSchedules { get => _totalSchedules; private set => SetProperty(ref _totalSchedules, value); }
    public int TotalFtpConnections { get => _totalFtpConnections; private set => SetProperty(ref _totalFtpConnections, value); }
    public int TotalFtpTasks { get => _totalFtpTasks; private set => SetProperty(ref _totalFtpTasks, value); }
    public int TotalExecutions { get => _totalExecutions; private set => SetProperty(ref _totalExecutions, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool HasProfiles { get => _hasProfiles; private set => SetProperty(ref _hasProfiles, value); }
    public bool HasActivities { get => _hasActivities; private set => SetProperty(ref _hasActivities, value); }
    public bool HasLastExecution { get => _hasLastExecution; private set => SetProperty(ref _hasLastExecution, value); }

    public string LastExecutionTitle { get => _lastExecutionTitle; private set => SetProperty(ref _lastExecutionTitle, value); }
    public string LastExecutionStatus { get => _lastExecutionStatus; private set => SetProperty(ref _lastExecutionStatus, value); }
    public Brush LastExecutionBrush { get => _lastExecutionBrush; private set => SetProperty(ref _lastExecutionBrush, value); }
    public string LastExecutionTime { get => _lastExecutionTime; private set => SetProperty(ref _lastExecutionTime, value); }
    public string LastExecutionDetails { get => _lastExecutionDetails; private set => SetProperty(ref _lastExecutionDetails, value); }

    public ObservableCollection<HomeProfileItem> RecentProfiles { get; }
    public ObservableCollection<HomeActivityItem> RecentActivities { get; }

    public ICommand NewComparisonCommand { get; }
    public ICommand OpenProfilesCommand { get; }
    public ICommand OpenSchedulesCommand { get; }
    public ICommand OpenFtpConnectionsCommand { get; }
    public ICommand OpenFtpTasksCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CompareProfileCommand { get; }
    public ICommand ExecuteProfileCommand { get; }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var profilesTask = _profiles.GetAllAsync();
            var schedulesTask = _schedules.GetAllAsync();
            var ftpConnectionsTask = _ftpConnections.GetAllAsync();
            var ftpTasksTask = _ftpTasks.GetAllAsync();
            var historyTask = _history.ListAsync(20);
            var ftpHistoryTask = _ftpHistory.ListAsync(20);
            var lastRunsTask = _history.GetLastRunsAsync();

            await Task.WhenAll(profilesTask, schedulesTask, ftpConnectionsTask, ftpTasksTask, historyTask, ftpHistoryTask, lastRunsTask);

            var profilesList = profilesTask.Result;
            var schedulesList = schedulesTask.Result;
            var ftpConnList = ftpConnectionsTask.Result;
            var ftpTaskList = ftpTasksTask.Result;
            var historyList = historyTask.Result;
            var ftpHistList = ftpHistoryTask.Result;
            var lastRuns = lastRunsTask.Result;

            TotalProfiles = profilesList.Count;
            TotalSchedules = schedulesList.Values.Count(s => s.Enabled && s.HasTrigger);
            TotalFtpConnections = ftpConnList.Count;
            TotalFtpTasks = ftpTaskList.Count;
            TotalExecutions = historyList.Count + ftpHistList.Count;

            RecentProfiles.Clear();
            foreach (var p in profilesList.Take(5))
            {
                lastRuns.TryGetValue(p.Id, out var lr);
                RecentProfiles.Add(new HomeProfileItem(p, lr));
            }
            HasProfiles = RecentProfiles.Count > 0;

            // Atividades recentes combinando Local e FTP ordenadas por data desc
            var combinedActivities = new List<(DateTimeOffset Date, HomeActivityItem Item)>();

            foreach (var h in historyList)
            {
                var brush = h.Status switch
                {
                    ExecutionStatus.Success => Presentation.Green,
                    ExecutionStatus.Failed => Presentation.Red,
                    _ => Presentation.Orange
                };

                combinedActivities.Add((h.FinishedAt, new HomeActivityItem
                {
                    Title = string.IsNullOrWhiteSpace(h.ProfileName) ? "Sincronização Manual" : h.ProfileName,
                    Subtitle = $"{h.FilesCopied + h.FilesUpdated} modificados • {Presentation.Bytes(h.BytesTransferred)} • {h.Duration.TotalSeconds:F1}s",
                    Timestamp = h.FinishedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm"),
                    StatusText = Presentation.StatusName(h.Status),
                    StatusBrush = brush,
                    IsFtp = false,
                    BadgeText = "Local",
                    BadgeBackground = Presentation.Blue
                }));
            }

            foreach (var fh in ftpHistList)
            {
                var brush = fh.Status switch
                {
                    FtpExecutionStatus.Success => Presentation.Green,
                    FtpExecutionStatus.Failed => Presentation.Red,
                    _ => Presentation.Orange
                };

                var statusName = fh.Status switch
                {
                    FtpExecutionStatus.Success => "FTP: Sucesso",
                    FtpExecutionStatus.Failed => "FTP: Falhou",
                    FtpExecutionStatus.Cancelled => "FTP: Cancelado",
                    _ => "FTP: Concluído"
                };

                combinedActivities.Add((new DateTimeOffset(fh.ExecutedAt), new HomeActivityItem
                {
                    Title = string.IsNullOrWhiteSpace(fh.TaskName) ? "Tarefa FTP" : fh.TaskName,
                    Subtitle = $"{fh.ProcessedFiles} arquivos • {Presentation.Bytes(fh.TotalBytesTransferred)} • {fh.Duration.TotalSeconds:F1}s",
                    Timestamp = fh.ExecutedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    StatusText = statusName,
                    StatusBrush = brush,
                    IsFtp = true,
                    BadgeText = "FTP",
                    BadgeBackground = Presentation.Purple
                }));
            }

            RecentActivities.Clear();
            var sortedActivities = combinedActivities.OrderByDescending(a => a.Date).Take(8).ToList();
            foreach (var (_, item) in sortedActivities)
            {
                RecentActivities.Add(item);
            }
            HasActivities = RecentActivities.Count > 0;

            if (sortedActivities.Count > 0)
            {
                var latest = sortedActivities[0].Item;
                LastExecutionTitle = latest.Title;
                LastExecutionStatus = latest.StatusText;
                LastExecutionBrush = latest.StatusBrush;
                LastExecutionTime = latest.Timestamp;
                LastExecutionDetails = latest.Subtitle;
                HasLastExecution = true;
            }
            else
            {
                HasLastExecution = false;
            }
        }
        catch
        {
            // Fail-safe para dados parciais
        }
        finally
        {
            IsLoading = false;
        }
    }
}
