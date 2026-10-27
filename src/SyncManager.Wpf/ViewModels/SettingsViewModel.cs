using SyncManager.Application;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

public sealed record NotifyOption(NotifyMode Mode, string Label);

public enum SettingsCategory { General, Execution, Filters, Scheduling, Interface, Logs }

/// <summary>Página Configurações: só o que o aplicativo realmente usa (nada de opções que não fazem nada).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private readonly IDialogService _dialogs;
    private SettingsCategory _category = SettingsCategory.General;
    private int _local = 4;
    private int _network = 2;
    private int _retention = 30;
    private string _excludes = "";
    private NotifyOption _notify;
    private bool _startMinimized;
    private bool _confirmBeforeExecute = true;
    private string _message = "";

    public SettingsViewModel(SettingsService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        NotifyOptions = new[]
        {
            new NotifyOption(NotifyMode.OnProblems, "Somente quando houver avisos, erros ou conflitos (recomendado)"),
            new NotifyOption(NotifyMode.Always, "Sempre, ao terminar cada execução agendada"),
            new NotifyOption(NotifyMode.Never, "Nunca")
        };
        _notify = NotifyOptions[0];
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        SelectCategoryCommand = new RelayCommand<string>(c => Category = Enum.Parse<SettingsCategory>(c));
        OpenLogsCommand = new RelayCommand(_dialogs.OpenLogFolder);
    }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand<string> SelectCategoryCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    /// <summary>Configurações gravadas.</summary>
    public event Action? Saved;

    public IReadOnlyList<NotifyOption> NotifyOptions { get; }
    public NotifyOption SelectedNotify { get => _notify; set { if (value is not null) SetProperty(ref _notify, value); } }
    public int MaxParallelLocal { get => _local; set => SetProperty(ref _local, value); }
    public int MaxParallelNetwork { get => _network; set => SetProperty(ref _network, value); }
    public int LogRetentionDays { get => _retention; set => SetProperty(ref _retention, value); }
    public string DefaultExcludePatterns { get => _excludes; set => SetProperty(ref _excludes, value); }
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }
    public bool ConfirmBeforeExecute { get => _confirmBeforeExecute; set => SetProperty(ref _confirmBeforeExecute, value); }
    public string DatabasePath { get; set; } = "";
    public string LogDirectory { get; set; } = "";
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public SettingsCategory Category
    {
        get => _category;
        set
        {
            if (!SetProperty(ref _category, value)) return;
            foreach (var n in new[] { nameof(IsGeneral), nameof(IsExecution), nameof(IsFilters), nameof(IsScheduling), nameof(IsInterface), nameof(IsLogs),
                         nameof(GeneralTag), nameof(ExecutionTag), nameof(FiltersTag), nameof(SchedulingTag), nameof(InterfaceTag), nameof(LogsTag) })
                OnPropertyChanged(n);
        }
    }
    public bool IsGeneral => _category == SettingsCategory.General;
    public bool IsExecution => _category == SettingsCategory.Execution;
    public bool IsFilters => _category == SettingsCategory.Filters;
    public bool IsScheduling => _category == SettingsCategory.Scheduling;
    public bool IsInterface => _category == SettingsCategory.Interface;
    public bool IsLogs => _category == SettingsCategory.Logs;
    public string GeneralTag => IsGeneral ? "active" : "";
    public string ExecutionTag => IsExecution ? "active" : "";
    public string FiltersTag => IsFilters ? "active" : "";
    public string SchedulingTag => IsScheduling ? "active" : "";
    public string InterfaceTag => IsInterface ? "active" : "";
    public string LogsTag => IsLogs ? "active" : "";

    public async Task LoadAsync()
    {
        Apply(await _service.LoadAsync());
        Message = "";
    }

    private void Apply(AppSettings s)
    {
        MaxParallelLocal = s.MaxParallelLocal;
        MaxParallelNetwork = s.MaxParallelNetwork;
        LogRetentionDays = s.LogRetentionDays;
        DefaultExcludePatterns = s.DefaultExcludePatterns;
        SelectedNotify = NotifyOptions.First(o => o.Mode == s.NotifyMode);
        StartMinimized = s.StartMinimized;
        ConfirmBeforeExecute = s.ConfirmBeforeExecute;
    }

    /// <summary>Volta os campos da tela aos valores originais (só grava ao clicar em Salvar).</summary>
    private void RestoreDefaults()
    {
        Apply(new AppSettings());
        Message = "Valores padrão carregados. Clique em Salvar para aplicá-los.";
    }

    public async Task SaveAsync()
    {
        try
        {
            await _service.SaveAsync(new AppSettings
            {
                MaxParallelLocal = MaxParallelLocal,
                MaxParallelNetwork = MaxParallelNetwork,
                LogRetentionDays = LogRetentionDays,
                NotifyMode = SelectedNotify.Mode,
                DefaultExcludePatterns = DefaultExcludePatterns,
                StartMinimized = StartMinimized,
                ConfirmBeforeExecute = ConfirmBeforeExecute
            });
            await LoadAsync();   // mostra os valores já normalizados (limites)
            Message = "Configurações salvas.";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Não foi possível salvar as configurações", ex.Message);
        }
    }
}
