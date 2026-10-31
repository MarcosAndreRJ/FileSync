using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;
using SyncManager.Infrastructure;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

/// <summary>Página exibida na área de conteúdo: a comparação permanece viva por trás (seleção, filtros e rolagem não se perdem).</summary>
public enum AppScreen { Comparison, Plan, Execution, Profiles, ProfileEditor, Schedules, History, Logs, Settings }

public sealed record MethodOption(SyncMethod Method, string Label, string Description);
public sealed record DeleteOption(DeleteStrategy Strategy, string Label);
/// <summary>Filtro da lista. Recebe a linha inteira porque "Conflitos" vem do plano (inclui conflitos da bidirecional), não só do status.</summary>
public sealed record StatusFilter(string Label, Func<ItemRow, bool> Predicate);
/// <param name="Select">Quais linhas da lista o cartão representa (clicar no cartão seleciona exatamente essas).</param>
public sealed record SummaryEntry(string Label, string Value, System.Windows.Media.Brush Brush, string? IconKey = null, Func<ItemRow, bool>? Select = null);

public sealed class MainViewModel : ObservableObject
{
    private readonly SyncWorkflow _workflow;
    private readonly IDialogService _dialogs;
    private readonly ILogService _log;
    private readonly IProfileRepository _profiles;
    private readonly SettingsService _settings;
    private readonly IServiceProvider _services;
    private SyncProfile _baseProfile = new();
    private bool _confirmBeforeExecute = true;
    private readonly ConcurrentQueue<string> _pendingLog = new();

    private int _profileId;
    private string _profileName = "";
    private string _includeText = "";
    private bool _preserveTimestamps = true;
    private bool _preserveAttributes = true;
    private bool _confirmDeletes = true;
    private int _maxParallel;
    private double _massDeleteThreshold = 0.30;
    private bool _isDirty;
    private bool _loading;
    private readonly DispatcherTimer _logTimer;

    private CancellationTokenSource? _cts;
    private CompareOutcome? _outcome;

    private string _sourcePath = "";
    private string _targetPath = "";
    private MethodOption _method;
    private DeleteOption _deleteOption;
    private bool _includeSubfolders = true;
    private bool _verifyByHash;
    private bool _verifyCopyByHash;
    private int _toleranceSeconds = 2;
    private bool _includeHidden = true;
    private bool _includeSystem;
    private bool _continueOnError = true;
    private bool _backupBeforeOverwrite = true;
    private string _safetyFolder = "";
    private string _excludeText = "*.tmp\r\n*.log\r\nThumbs.db\r\ndesktop.ini\r\n.git\\\r\nnode_modules\\\r\nbin\\\r\nobj\\";

    private bool _isBusy;
    private bool _isExecuting;
    private string _statusMessage = "Selecione ou arraste as pastas Source e Target e clique em Comparar.";
    private string _currentFile = "";
    private double _progressValue;
    private string _progressText = "";
    private string _executionSummary = "";
    private string _search = "";
    private StatusFilter _statusFilter;
    private ICollectionView? _rowsView;
    private int _visibleCount;

    public MainViewModel(SyncWorkflow workflow, IDialogService dialogs, ILogService log,
        IProfileRepository profiles, SettingsService settings, IServiceProvider services)
    {
        _services = services;
        _workflow = workflow;
        _dialogs = dialogs;
        _log = log;
        _profiles = profiles;
        _settings = settings;

        Methods = MethodCatalog.Methods;
        _method = Methods[0];

        DeleteOptions = MethodCatalog.DeleteOptions;
        _deleteOption = DeleteOptions[0];

        StatusFilters = new[]
        {
            new StatusFilter("Todos", _ => true),
            new StatusFilter("Iguais", r => r.Item.Status == ComparisonStatus.Identical),
            new StatusFilter("Diferentes", r => r.Item.Status != ComparisonStatus.Identical),
            new StatusFilter("Somente Source", r => r.Item.Status == ComparisonStatus.SourceOnly),
            new StatusFilter("Somente Target", r => r.Item.Status == ComparisonStatus.TargetOnly),
            new StatusFilter("Source mais novo", r => r.Item.Status == ComparisonStatus.SourceNewer),
            new StatusFilter("Target mais novo", r => r.Item.Status == ComparisonStatus.TargetNewer),
            new StatusFilter("Conflitos", r => r.IsConflict || r.Item.Status == ComparisonStatus.Conflict),
            new StatusFilter("Erros", r => r.Item.Status == ComparisonStatus.Error)
        };
        _statusFilter = StatusFilters[0];

        BrowseSourceCommand = new RelayCommand(() => Browse(true), () => !IsBusy);
        BrowseTargetCommand = new RelayCommand(() => Browse(false), () => !IsBusy);
        BrowseSafetyCommand = new RelayCommand(() =>
        {
            var p = _dialogs.BrowseFolder(SafetyFolder, "Pasta de segurança");
            if (p is not null) SafetyFolder = p;
        }, () => !IsBusy);
        SwapSidesCommand = new RelayCommand(() => { var s = SourcePath; SourcePath = TargetPath; TargetPath = s; }, () => !IsBusy);
        DropSourceCommand = new RelayCommand<string>(p => { if (!IsBusy) SourcePath = p; });
        DropTargetCommand = new RelayCommand<string>(p => { if (!IsBusy) TargetPath = p; });
        ShowAllCommand = new RelayCommand(() => SelectFilter("Todos"));
        ShowConflictsCommand = new RelayCommand(() => SelectFilter("Conflitos"));
        ShowErrorsCommand = new RelayCommand(() => SelectFilter("Erros"));
        CompareCommand = new AsyncRelayCommand(CompareAsync, () => !IsBusy);
        PreviewCommand = new RelayCommand(ShowPlan, () => !IsBusy && _outcome is not null);
        BackCommand = new RelayCommand(GoBack, () => !IsBusy);
        ExportPlanCommand = new RelayCommand(ExportPlan, () => Plan is not null);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy && _outcome is { Plan.IsBlocked: false } && EffectivePlan().ExecutableCount > 0);
        _includedHook = OnRowIncludedChanged;
        SelectRowsCommand = new RelayCommand<SummaryEntry>(SelectRowsOf);
        MarkAllCommand = new RelayCommand(() => SetAllIncluded(true), () => !IsBusy && _outcome is not null);
        UnmarkAllCommand = new RelayCommand(() => SetAllIncluded(false), () => !IsBusy && _outcome is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        NewProfileCommand = new RelayCommand(NewProfile, () => !IsBusy);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        ManageProfilesCommand = new AsyncRelayCommand(ShowProfilesAsync, () => !IsBusy);
        ShowComparisonCommand = new RelayCommand(() => GoTo(AppScreen.Comparison));
        ShowSchedulesCommand = new AsyncRelayCommand(ShowSchedulesAsync, () => !IsBusy);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync);
        ShowLogsCommand = new RelayCommand(() => GoTo(AppScreen.Logs));
        ShowSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync);
        ExitCommand = new RelayCommand(() => System.Windows.Application.Current.Shutdown());
        OpenLogFolderCommand = new RelayCommand(_dialogs.OpenLogFolder);
        AboutCommand = new RelayCommand(() => _dialogs.ShowInfo("Sobre o SyncManager",
            "SyncManager — Fase 1 (MVP)\nComparar → Planejar → Visualizar → Executar.\n\n" +
            "Toda alteração em arquivos passa pelo plano de sincronização; a interface nunca copia ou exclui diretamente.\n\n" +
            "Logs: " + _log.LogDirectory));

        // Inicialização da árvore hierárquica e sincronização
        TreeManager.CheckChanged += () =>
        {
            SyncTreeChecksToRows();
            RaiseTreeChanged();
            IncludeChanged();
        };
        TreeManager.StructureOrFilterChanged += RaiseTreeChanged;

        SetTreeFilterCommand = new RelayCommand<string>(filterName =>
        {
            if (Enum.TryParse<ComparisonTreeFilter>(filterName, true, out var f))
            {
                TreeManager.ActiveFilter = f;
                RaiseTreeChanged();
            }
        });
        ExpandAllTreeCommand = new RelayCommand(TreeManager.ExpandAll);
        CollapseAllTreeCommand = new RelayCommand(TreeManager.CollapseAll);
        HideNodeCommand = new RelayCommand<ComparisonTreeNode>(node =>
        {
            if (node is not null) TreeManager.HideNode(node);
        });
        RestoreHiddenCommand = new RelayCommand(TreeManager.RestoreHidden);
        MarkAllTreeCommand = new RelayCommand(() =>
        {
            TreeManager.SetAllChecked(true);
            SetAllIncluded(true);
        });
        UnmarkAllTreeCommand = new RelayCommand(() =>
        {
            TreeManager.SetAllChecked(false);
            SetAllIncluded(false);
        });
        OpenSettingsCommand = new RelayCommand(async () =>
        {
            var profile = BuildProfile();
            if (_profileId > 0) profile.Id = _profileId;
            await OpenEditorAsync(profile, EditorSection.General);
        });
        ClearProfileCommand = new RelayCommand(ClearProfileData);

        _log.EntryWritten += e => _pendingLog.Enqueue(FileLogService.Format(e));
        _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
    }

    // ------------------------------------------------------------ dados de apoio
    public HierarchicalComparisonManager TreeManager { get; } = new();
    public ObservableCollection<SyncProfile> AvailableProfiles { get; } = new();
    public RelayCommand<string> SetTreeFilterCommand { get; }
    public RelayCommand ExpandAllTreeCommand { get; }
    public RelayCommand CollapseAllTreeCommand { get; }
    public RelayCommand<ComparisonTreeNode> HideNodeCommand { get; }
    public RelayCommand RestoreHiddenCommand { get; }
    public RelayCommand MarkAllTreeCommand { get; }
    public RelayCommand UnmarkAllTreeCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ClearProfileCommand { get; }

    public int TreeTotalAnalyzed => TreeManager.TotalAnalyzed;
    public int TreeIdenticalCount => TreeManager.IdenticalCount;
    public int TreeDifferentCount => TreeManager.DifferentCount;
    public int TreeSourceOnlyCount => TreeManager.SourceOnlyCount;
    public int TreeTargetOnlyCount => TreeManager.TargetOnlyCount;
    public int TreeConflictCount => TreeManager.ConflictCount;
    public int TreeErrorCount => TreeManager.ErrorCount;
    public int TreeCheckedCount => TreeManager.ExecutableCheckedCount;

    public string FilterAllText => $"Todos ({TreeManager.TotalAnalyzed})";
    public string FilterDiffText => $"Diferenças ({TreeManager.DifferentCount})";
    public string FilterIdenticalText => $"Iguais ({TreeManager.IdenticalCount})";
    public string FilterSourceOnlyText => $"Só origem ({TreeManager.SourceOnlyCount})";
    public string FilterTargetOnlyText => $"Só destino ({TreeManager.TargetOnlyCount})";
    public string FilterConflictText => $"Conflitos ({TreeManager.ConflictCount})";
    public string FilterErrorText => $"Erros ({TreeManager.ErrorCount})";
    public string FilterCheckedText => $"Marcados ({TreeManager.ExecutableCheckedCount})";

    public string FilterAllTag => TreeManager.ActiveFilter == ComparisonTreeFilter.All ? "active" : "";
    public string FilterDiffTag => TreeManager.ActiveFilter == ComparisonTreeFilter.Different ? "active" : "";
    public string FilterIdenticalTag => TreeManager.ActiveFilter == ComparisonTreeFilter.Identical ? "active" : "";
    public string FilterSourceOnlyTag => TreeManager.ActiveFilter == ComparisonTreeFilter.SourceOnly ? "active" : "";
    public string FilterTargetOnlyTag => TreeManager.ActiveFilter == ComparisonTreeFilter.TargetOnly ? "active" : "";
    public string FilterConflictTag => TreeManager.ActiveFilter == ComparisonTreeFilter.Conflict ? "active" : "";
    public string FilterErrorTag => TreeManager.ActiveFilter == ComparisonTreeFilter.Error ? "active" : "";
    public string FilterCheckedTag => TreeManager.ActiveFilter == ComparisonTreeFilter.Checked ? "active" : "";

    public string TreeSearchText
    {
        get => TreeManager.SearchText;
        set
        {
            TreeManager.SearchText = value;
            OnPropertyChanged(nameof(TreeSearchText));
        }
    }

    public void RaiseTreeChanged()
    {
        OnPropertyChanged(nameof(TreeTotalAnalyzed));
        OnPropertyChanged(nameof(TreeIdenticalCount));
        OnPropertyChanged(nameof(TreeDifferentCount));
        OnPropertyChanged(nameof(TreeSourceOnlyCount));
        OnPropertyChanged(nameof(TreeTargetOnlyCount));
        OnPropertyChanged(nameof(TreeConflictCount));
        OnPropertyChanged(nameof(TreeErrorCount));
        OnPropertyChanged(nameof(TreeCheckedCount));

        OnPropertyChanged(nameof(FilterAllText));
        OnPropertyChanged(nameof(FilterDiffText));
        OnPropertyChanged(nameof(FilterIdenticalText));
        OnPropertyChanged(nameof(FilterSourceOnlyText));
        OnPropertyChanged(nameof(FilterTargetOnlyText));
        OnPropertyChanged(nameof(FilterConflictText));
        OnPropertyChanged(nameof(FilterErrorText));
        OnPropertyChanged(nameof(FilterCheckedText));

        OnPropertyChanged(nameof(FilterAllTag));
        OnPropertyChanged(nameof(FilterDiffTag));
        OnPropertyChanged(nameof(FilterIdenticalTag));
        OnPropertyChanged(nameof(FilterSourceOnlyTag));
        OnPropertyChanged(nameof(FilterTargetOnlyTag));
        OnPropertyChanged(nameof(FilterConflictTag));
        OnPropertyChanged(nameof(FilterErrorTag));
        OnPropertyChanged(nameof(FilterCheckedTag));
    }

    private void SyncTreeChecksToRows()
    {
        var included = TreeManager.GetIncludedRelativePaths();
        foreach (var r in _rows)
        {
            if (r.CanInclude)
            {
                r.SetIncludedQuietly(included.Contains(r.Item.RelativePath));
            }
        }
    }

    public IReadOnlyList<MethodOption> Methods { get; }
    public IReadOnlyList<DeleteOption> DeleteOptions { get; }
    public IReadOnlyList<StatusFilter> StatusFilters { get; }
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<SummaryEntry> ComparisonSummary { get; } = new();
    public ObservableCollection<SummaryEntry> PlanSummary { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();

    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand BrowseSafetyCommand { get; }
    public AsyncRelayCommand CompareCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand<SummaryEntry> SelectRowsCommand { get; }
    public RelayCommand MarkAllCommand { get; }
    public RelayCommand UnmarkAllCommand { get; }
    public RelayCommand ExportPlanCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SwapSidesCommand { get; }
    public RelayCommand<string> DropSourceCommand { get; }
    public RelayCommand<string> DropTargetCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowConflictsCommand { get; }
    public RelayCommand ShowErrorsCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand ManageProfilesCommand { get; }
    public RelayCommand ShowComparisonCommand { get; }
    public AsyncRelayCommand ShowSchedulesCommand { get; }
    public AsyncRelayCommand ShowHistoryCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public AsyncRelayCommand ShowSettingsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand AboutCommand { get; }

    // ------------------------------------------------------------ configuração (qualquer mudança invalida o plano)
    public string SourcePath { get => _sourcePath; set => Config(ref _sourcePath, value); }
    public string TargetPath { get => _targetPath; set => Config(ref _targetPath, value); }
    public MethodOption SelectedMethod
    {
        get => _method;
        set { if (Config(ref _method, value)) OnPropertyChanged(nameof(MethodDescription)); }
    }
    public string MethodDescription => _method.Description;
    public DeleteOption SelectedDeleteOption { get => _deleteOption; set => Config(ref _deleteOption, value); }
    public bool IncludeSubfolders { get => _includeSubfolders; set => Config(ref _includeSubfolders, value); }
    public bool VerifyByHash { get => _verifyByHash; set => Config(ref _verifyByHash, value); }
    public bool VerifyCopyByHash { get => _verifyCopyByHash; set => Config(ref _verifyCopyByHash, value); }
    public int ToleranceSeconds { get => _toleranceSeconds; set => Config(ref _toleranceSeconds, Math.Max(0, value)); }
    public bool IncludeHidden { get => _includeHidden; set => Config(ref _includeHidden, value); }
    public bool IncludeSystem { get => _includeSystem; set => Config(ref _includeSystem, value); }
    public bool ContinueOnError { get => _continueOnError; set => Config(ref _continueOnError, value); }
    public bool BackupBeforeOverwrite { get => _backupBeforeOverwrite; set => Config(ref _backupBeforeOverwrite, value); }
    public string SafetyFolder { get => _safetyFolder; set => Config(ref _safetyFolder, value); }
    public string ExcludeText { get => _excludeText; set => Config(ref _excludeText, value); }
    public string IncludeText { get => _includeText; set => Config(ref _includeText, value); }
    public bool PreserveTimestamps { get => _preserveTimestamps; set => Config(ref _preserveTimestamps, value); }
    public bool PreserveAttributes { get => _preserveAttributes; set => Config(ref _preserveAttributes, value); }
    public bool ConfirmDeletes { get => _confirmDeletes; set => Config(ref _confirmDeletes, value); }
    public int MaxParallel { get => _maxParallel; set => Config(ref _maxParallel, Math.Clamp(value, 0, 32)); }
    public string ProfileName { get => _profileName; set { if (SetProperty(ref _profileName, value)) { IsDirty = true; OnPropertyChanged(nameof(WindowTitle)); } } }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (SetProperty(ref _isDirty, value)) OnPropertyChanged(nameof(WindowTitle)); }
    }

    public string WindowTitle =>
        $"SyncManager — {(string.IsNullOrWhiteSpace(_profileName) ? "perfil sem nome" : _profileName)}{(_isDirty ? " *" : "")}";

    private bool Config<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        if (_loading) return true;
        IsDirty = true;
        if (!IsBusy && _outcome is not null)
        {
            ClearResults();
            StatusMessage = "A configuração mudou: o plano anterior foi descartado. Clique em Comparar novamente.";
        }
        return true;
    }

    // ------------------------------------------------------------ estado da tela
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsIndeterminate));
            CommandManager_Invalidate();
        }
    }
    public bool IsIdle => !_isBusy;
    public bool IsIndeterminate => _isBusy && !_isExecuting;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string ExecutionSummary { get => _executionSummary; private set => SetProperty(ref _executionSummary, value); }
    public int VisibleCount { get => _visibleCount; private set => SetProperty(ref _visibleCount, value); }
    public SyncPlan? CurrentPlan => _outcome?.Plan;
    public bool HasResults => _outcome is not null;
    public bool NoResults => _outcome is null;
    private bool _bottomExpanded;
    /// <summary>Painel de log/resultado: recolhido por padrão; abre sozinho quando uma execução termina.</summary>
    public bool BottomExpanded { get => _bottomExpanded; set => SetProperty(ref _bottomExpanded, value); }

    // ---- navegação entre Comparação / Plano / Execução
    private AppScreen _screen = AppScreen.Comparison;
    private PlanPreviewViewModel? _plan;

    public AppScreen CurrentScreen
    {
        get => _screen;
        private set
        {
            if (!SetProperty(ref _screen, value)) return;
            foreach (var n in new[] { nameof(IsComparisonScreen), nameof(IsPlanScreen), nameof(IsExecutionScreen), nameof(IsProfilesScreen),
                         nameof(IsEditorScreen), nameof(IsSchedulesScreen), nameof(IsHistoryScreen), nameof(IsLogsScreen), nameof(IsSettingsScreen),
                         nameof(ComparisonNavTag), nameof(ProfilesNavTag), nameof(SchedulesNavTag), nameof(HistoryNavTag), nameof(LogsNavTag), nameof(SettingsNavTag) })
                OnPropertyChanged(n);
            CommandManager_Invalidate();
        }
    }
    public bool IsComparisonScreen => _screen == AppScreen.Comparison;
    public bool IsPlanScreen => _screen == AppScreen.Plan;
    public bool IsExecutionScreen => _screen == AppScreen.Execution;
    public bool IsProfilesScreen => _screen == AppScreen.Profiles;
    public bool IsEditorScreen => _screen == AppScreen.ProfileEditor;
    public bool IsSchedulesScreen => _screen == AppScreen.Schedules;
    public bool IsHistoryScreen => _screen == AppScreen.History;
    public bool IsLogsScreen => _screen == AppScreen.Logs;
    public bool IsSettingsScreen => _screen == AppScreen.Settings;

    // item ativo da barra lateral (Plano e Execução pertencem a "Comparação"; o editor pertence a "Perfis")
    public string ComparisonNavTag => _screen is AppScreen.Comparison or AppScreen.Plan or AppScreen.Execution ? "active" : "";
    public string ProfilesNavTag => _screen is AppScreen.Profiles or AppScreen.ProfileEditor ? "active" : "";
    public string SchedulesNavTag => _screen == AppScreen.Schedules ? "active" : "";
    public string HistoryNavTag => _screen == AppScreen.History ? "active" : "";
    public string LogsNavTag => _screen == AppScreen.Logs ? "active" : "";
    public string SettingsNavTag => _screen == AppScreen.Settings ? "active" : "";

    // páginas com ViewModel próprio (criados sob demanda pelo contêiner)
    private ProfilesViewModel? _profilesPage;
    private HistoryViewModel? _historyPage;
    private SettingsViewModel? _settingsPage;
    private ProfileEditorViewModel? _editor;

    public ProfilesViewModel Profiles
    {
        get
        {
            if (_profilesPage is not null) return _profilesPage;
            _profilesPage = _services.GetRequiredService<ProfilesViewModel>();
            _profilesPage.ActionRequested += r => _ = HandleProfileRequestAsync(r);
            return _profilesPage;
        }
    }
    public HistoryViewModel History
    {
        get
        {
            if (_historyPage is not null) return _historyPage;
            _historyPage = _services.GetRequiredService<HistoryViewModel>();
            _historyPage.RerunRequested += id => _ = RerunAsync(id);
            return _historyPage;
        }
    }

    private async Task RerunAsync(int profileId)
    {
        var profile = await _profiles.GetAsync(profileId);
        if (profile is null) { _dialogs.ShowError("Executar novamente", "O perfil desta execução não existe mais."); return; }
        await HandleProfileRequestAsync(new ProfilesRequest(ProfilesAction.Execute, profile));
    }
    public SettingsViewModel Settings
    {
        get
        {
            if (_settingsPage is not null) return _settingsPage;
            _settingsPage = _services.GetRequiredService<SettingsViewModel>();
            _settingsPage.LogDirectory = _log.LogDirectory;
            _settingsPage.DatabasePath = _services.GetRequiredService<SyncManager.Persistence.Database>().DatabasePath;
            _settingsPage.Saved += () => { _ = RefreshSettingsAsync(); StatusMessage = "Configurações salvas."; };
            return _settingsPage;
        }
    }
    public ProfileEditorViewModel? Editor { get => _editor; private set => SetProperty(ref _editor, value); }

    /// <summary>
    /// Navega para uma página. Sair do editor de perfil com alterações pede confirmação (nada é salvo sem "Salvar").
    /// </summary>
    public bool GoTo(AppScreen screen)
    {
        if (screen == _screen) return true;
        if (_screen == AppScreen.ProfileEditor && Editor is { IsDirty: true } &&
            !_dialogs.Confirm("Alterações não salvas", "O perfil em edição tem alterações não salvas. Descartá-las?", true))
            return false;
        CurrentScreen = screen;
        return true;
    }

    public async Task ShowProfilesAsync()
    {
        if (!GoTo(AppScreen.Profiles)) return;
        Profiles.TrackSchedule = false;
        await Profiles.LoadAsync();
    }

    public async Task ShowSchedulesAsync()
    {
        if (!GoTo(AppScreen.Schedules)) return;
        Profiles.TrackSchedule = true;
        await Profiles.LoadAsync();
        if (Profiles.Selected is { } sel) await Profiles.Schedule.LoadAsync(sel.Profile);
    }

    public async Task ShowHistoryAsync()
    {
        if (!GoTo(AppScreen.History)) return;
        await History.LoadAsync();
    }

    public async Task ShowSettingsAsync()
    {
        if (!GoTo(AppScreen.Settings)) return;
        await Settings.LoadAsync();
    }

    private async Task RefreshSettingsAsync() => _confirmBeforeExecute = (await _settings.LoadAsync()).ConfirmBeforeExecute;

    /// <summary>Abre o editor de perfil em janela dedicada (ProfileWindow).</summary>
    public async Task OpenEditorAsync(SyncProfile profile, EditorSection section = EditorSection.General)
    {
        var editor = _services.GetRequiredService<ProfileEditorViewModel>();
        await editor.LoadAsync(profile, section);
        var win = new Views.ProfileWindow(editor);
        if (System.Windows.Application.Current?.MainWindow is { } main && main != win)
        {
            win.Owner = main;
        }

        var res = win.ShowDialog();
        if (res == true || win.Applied)
        {
            var saved = win.ResultProfile;
            if (saved is not null)
            {
                StatusMessage = $"Perfil \"{saved.Name}\" salvo.";
                _log.Info("PROFILE SAVED", $"{saved.Name} (#{saved.Id})");
                if (saved.Id == _profileId || _profileId == 0)
                {
                    LoadProfile(saved);
                }
            }
            await LoadAvailableProfilesAsync();
        }
    }

    private async Task OnEditorClosedAsync(SyncProfile? saved)
    {
        Editor?.ForgetChanges();   // salvar ou cancelar encerra a edição: sair não pergunta mais
        if (saved is not null)
        {
            StatusMessage = $"Perfil \"{saved.Name}\" salvo.";
            _log.Info("PROFILE SAVED", $"{saved.Name} (#{saved.Id})");
            // a comparação em tela usa este mesmo perfil: passa a refletir o que foi salvo
            if (saved.Id == _profileId && !IsBusy) LoadProfile(saved);
        }
        await ShowProfilesAsync();
    }

    /// <summary>Ação pedida pela página Perfis (novo, editar, comparar, executar agora, agendar).</summary>
    public async Task HandleProfileRequestAsync(ProfilesRequest request)
    {
        switch (request.Action)
        {
            case ProfilesAction.New:
                var fresh = new SyncProfile { Name = "" };
                fresh.ExcludePatterns = Lines((await _settings.LoadAsync()).DefaultExcludePatterns);
                await OpenEditorAsync(fresh);
                break;

            case ProfilesAction.Edit when request.Profile is not null:
                await OpenEditorAsync(request.Profile);
                break;

            case ProfilesAction.Schedule when request.Profile is not null:
                await ShowSchedulesAsync();
                break;

            case ProfilesAction.Compare when request.Profile is not null:
                if (!ConfirmDiscardChanges()) return;
                LoadProfile(request.Profile);
                GoTo(AppScreen.Comparison);
                await CompareAsync();
                break;

            case ProfilesAction.Execute when request.Profile is not null:
                // Mesma regra estrutural: COMPARE → PLAN → PREVIEW → (confirmação) → EXECUTE.
                if (!ConfirmDiscardChanges()) return;
                LoadProfile(request.Profile);
                GoTo(AppScreen.Comparison);
                await CompareAsync();
                // A página do plano substitui o diálogo de pré-visualização: o usuário confere e clica em EXECUTAR.
                if (_outcome is not null) ShowPlan();
                break;
        }
    }
    /// <summary>Plano mostrado na página "Plano de execução"; recriado a cada abertura, pois conflitos podem ter sido resolvidos.</summary>
    public PlanPreviewViewModel? Plan { get => _plan; private set { if (SetProperty(ref _plan, value)) CommandManager_Invalidate(); } }
    public ExecutionViewModel Execution { get; } = new();

    private void ShowPlan()
    {
        if (_outcome is null) return;
        Plan = new PlanPreviewViewModel(EffectivePlan());
        CurrentScreen = AppScreen.Plan;
    }

    private void GoBack() => CurrentScreen = AppScreen.Comparison;

    private void ExportPlan()
    {
        if (Plan is null) return;
        var path = _dialogs.AskSaveFile("Exportar lista do plano", $"plano-{DateTime.Now:yyyyMMdd-HHmmss}.csv", "Planilha CSV (*.csv)|*.csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, Plan.ToCsv(), new UTF8Encoding(true));
            _log.Info("PLAN EXPORTED", path);
            StatusMessage = $"Lista exportada: {path}";
            _dialogs.ShowInfo("Exportar lista", $"{Plan.VisibleCount:N0} operações exportadas para:\n{path}");
        }
        catch (Exception ex)
        {
            _log.Error("PLAN EXPORT ERROR", ex.Message);
            _dialogs.ShowError("Não foi possível exportar a lista", ex.Message);
        }
    }

    // ---- abas da lista (filtros rápidos sobre a mesma grade) e textos de contagem
    private int _conflictsTab;
    private int _errorsTab;
    private string _planSummaryText = "";
    public string PlanSummaryText { get => _planSummaryText; private set => SetProperty(ref _planSummaryText, value); }
    public string ConflictsTabText => $"Conflitos ({_conflictsTab:N0})";
    public string ErrorsTabText => $"Erros ({_errorsTab:N0})";
    public string ItemsCountText => _outcome is null ? "" : $"{_rows.Count:N0} itens ({_differentCount:N0} diferenças)";
    public string AllTabTag => _statusFilter.Label == "Todos" ? "active" : "";
    public string ConflictsTabTag => _statusFilter.Label == "Conflitos" ? "active" : "";
    public string ErrorsTabTag => _statusFilter.Label == "Erros" ? "active" : "";

    private void SelectFilter(string label) => SelectedStatusFilter = StatusFilters.First(f => f.Label == label);

    private void RaiseTabs()
    {
        OnPropertyChanged(nameof(AllTabTag));
        OnPropertyChanged(nameof(ConflictsTabTag));
        OnPropertyChanged(nameof(ErrorsTabTag));
    }
    public ICollectionView? RowsView { get => _rowsView; private set => SetProperty(ref _rowsView, value); }

    public StatusFilter SelectedStatusFilter
    {
        get => _statusFilter;
        set { if (SetProperty(ref _statusFilter, value)) { RefreshView(); RaiseTabs(); } }
    }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) RefreshView(); }
    }

    private static void CommandManager_Invalidate() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    // ------------------------------------------------------------ drag and drop / seleção
    public void SetSourceFromDrop(string folder) => SourcePath = folder;
    public void SetTargetFromDrop(string folder) => TargetPath = folder;

    private void Browse(bool source)
    {
        var current = source ? SourcePath : TargetPath;
        var path = _dialogs.BrowseFolder(current, source ? "Selecione a pasta Source" : "Selecione a pasta Target");
        if (path is null) return;
        if (source) SourcePath = path; else TargetPath = path;
    }

    private static List<string> Lines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // parte do perfil carregado: campos que esta tela não mostra (tentativas, filtros sensíveis a maiúsculas...) são preservados ao salvar
    private SyncProfile BuildProfile() { var p = _baseProfile.Clone(); Apply(p); return p; }

    private void Apply(SyncProfile p)
    {
        p.Id = _profileId;
        p.Name = string.IsNullOrWhiteSpace(_profileName) ? "Comparação manual" : _profileName.Trim();
        p.SourcePath = SourcePath;
        p.TargetPath = TargetPath;
        p.Method = _method.Method;
        p.IncludeSubfolders = _includeSubfolders;
        p.PreserveTimestamps = _preserveTimestamps;
        p.PreserveAttributes = _preserveAttributes;
        p.VerifyContentByHash = _verifyByHash;
        p.VerifyCopyByHash = _verifyCopyByHash;
        p.TimestampToleranceSeconds = _toleranceSeconds;
        p.IncludeHidden = _includeHidden;
        p.IncludeSystem = _includeSystem;
        p.ContinueOnError = _continueOnError;
        p.ConfirmDeletes = _confirmDeletes;
        p.BackupBeforeOverwrite = _backupBeforeOverwrite;
        p.DeleteStrategy = _deleteOption.Strategy;
        p.SafetyFolderPath = string.IsNullOrWhiteSpace(_safetyFolder) ? null : _safetyFolder.Trim();
        p.ExcludePatterns = Lines(_excludeText);
        p.IncludePatterns = Lines(_includeText);
        p.MaxParallelFileOperations = _maxParallel;
        p.MassDeleteThreshold = _massDeleteThreshold;
    }

    // ------------------------------------------------------------ perfis
    /// <summary>Aplica os padrões globais (configurações) a um perfil novo/em branco. Chamado na inicialização.</summary>
    public async Task ApplyDefaultsAsync()
    {
        var s = await _settings.LoadAsync();
        _confirmBeforeExecute = s.ConfirmBeforeExecute;
        _loading = true;
        try { ExcludeText = s.DefaultExcludePatterns; }
        finally { _loading = false; }
        IsDirty = false;
        await LoadAvailableProfilesAsync();
    }

    private SyncProfile? _selectedProfileItem;
    public SyncProfile? SelectedProfileItem
    {
        get => _selectedProfileItem;
        set
        {
            if (SetProperty(ref _selectedProfileItem, value) && value is not null)
            {
                if (value.Id != _profileId)
                {
                    LoadProfile(value);
                }
            }
        }
    }

    public async Task LoadAvailableProfilesAsync()
    {
        try
        {
            var list = await _profiles.GetAllAsync();
            AvailableProfiles.Clear();
            foreach (var p in list) AvailableProfiles.Add(p);
            _selectedProfileItem = AvailableProfiles.FirstOrDefault(p => p.Id == _profileId);
            OnPropertyChanged(nameof(SelectedProfileItem));
        }
        catch { }
    }

    private bool ConfirmDiscardChanges() =>
        !_isDirty || _profileId == 0 && string.IsNullOrWhiteSpace(_profileName) && string.IsNullOrWhiteSpace(_sourcePath) ||
        _dialogs.Confirm("Alterações não salvas", "O perfil atual tem alterações não salvas. Descartá-las?", true);

    private void NewProfile()
    {
        if (!ConfirmDiscardChanges()) return;
        LoadProfile(new SyncProfile { Name = "" });
        _ = ApplyDefaultsAsync();
        StatusMessage = "Novo perfil. Informe o nome, as pastas e as opções, e use Arquivo › Salvar perfil.";
    }

    public void LoadProfile(SyncProfile p)
    {
        _baseProfile = p.Clone();
        _loading = true;
        try
        {
            _profileId = p.Id;
            ProfileName = p.Name;
            SourcePath = p.SourcePath;
            TargetPath = p.TargetPath;
            SelectedMethod = Methods.FirstOrDefault(m => m.Method == p.Method) ?? Methods[0];
            SelectedDeleteOption = DeleteOptions.First(d => d.Strategy == p.DeleteStrategy);
            IncludeSubfolders = p.IncludeSubfolders;
            PreserveTimestamps = p.PreserveTimestamps;
            PreserveAttributes = p.PreserveAttributes;
            VerifyByHash = p.VerifyContentByHash;
            VerifyCopyByHash = p.VerifyCopyByHash;
            ToleranceSeconds = p.TimestampToleranceSeconds;
            IncludeHidden = p.IncludeHidden;
            IncludeSystem = p.IncludeSystem;
            ContinueOnError = p.ContinueOnError;
            ConfirmDeletes = p.ConfirmDeletes;
            BackupBeforeOverwrite = p.BackupBeforeOverwrite;
            SafetyFolder = p.SafetyFolderPath ?? "";
            ExcludeText = string.Join(Environment.NewLine, p.ExcludePatterns);
            IncludeText = string.Join(Environment.NewLine, p.IncludePatterns);
            MaxParallel = p.MaxParallelFileOperations;
            _massDeleteThreshold = p.MassDeleteThreshold;
        }
        finally { _loading = false; }
        _selectedProfileItem = AvailableProfiles.FirstOrDefault(x => x.Id == p.Id);
        OnPropertyChanged(nameof(SelectedProfileItem));
        ClearResults();
        IsDirty = false;
        OnPropertyChanged(nameof(WindowTitle));
    }

    public async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(_profileName))
        {
            _dialogs.ShowError("Salvar perfil", "Informe o nome do perfil antes de salvar.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(TargetPath))
        {
            _dialogs.ShowError("Salvar perfil", "Informe as pastas Source e Target antes de salvar.");
            return;
        }

        try
        {
            var saved = await _profiles.SaveAsync(BuildProfile());
            _profileId = saved.Id;
            ProfileName = saved.Name;
            IsDirty = false;
            StatusMessage = $"Perfil \"{saved.Name}\" salvo.";
            _log.Info("PROFILE SAVED", $"{saved.Name} (#{saved.Id})");
        }
        catch (DuplicateProfileNameException ex)
        {
            _dialogs.ShowError("Salvar perfil", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("PROFILE SAVE ERROR", ex.Message);
            _dialogs.ShowError("Não foi possível salvar o perfil", ex.Message);
        }
    }

    // ------------------------------------------------------------ comparar
    private ObservableCollection<ItemRow> _rows = new();
    private readonly Dictionary<string, ItemRow> _rowByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedFromList = new(StringComparer.OrdinalIgnoreCase);

    public bool HasConflicts => ConflictCount > 0;
    public int ConflictCount => _outcome?.Plan.ConflictCount ?? 0;

    public async Task CompareAsync()
    {
        ClearResults();
        _cts = new CancellationTokenSource();
        _isExecuting = false;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "";
        StatusMessage = "Comparando...";
        try
        {
            var outcome = await _workflow.CompareAsync(BuildProfile(), new Progress<string>(s => StatusMessage = s), _cts.Token);
            StatusMessage = "Montando a lista...";
            var rows = await Task.Run(() => GroupRows(outcome.Plan));
            _outcome = outcome;
            _removedFromList.Clear();
            FillRows(rows);
            ShowSummary();
            TreeManager.Build(outcome.Plan, rows);
            RaiseTreeChanged();
            RaiseOutcomeChanged();
            StatusMessage = outcome.Plan.IsBlocked
                ? "Plano BLOQUEADO: " + outcome.Plan.BlockedReason
                : outcome.Plan.ConflictCount > 0
                    ? $"Comparação concluída com {outcome.Plan.ConflictCount:N0} conflito(s). Resolva-os (Manter Source/Target/ambos ou Ignorar) antes de executar."
                    : outcome.Plan.ExecutableCount == 0
                        ? "Comparação concluída. Nada a fazer com o método escolhido."
                        : "Comparação concluída. Use \"Visualizar plano\" para conferir e depois \"Executar\".";
        }
        catch (SyncValidationException ex)
        {
            StatusMessage = "Não foi possível comparar.";
            _dialogs.ShowError("Não foi possível comparar", ex.Message);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Comparação cancelada.";
            _log.Warning("COMPARE CANCELLED");
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro durante a comparação.";
            _log.Error("COMPARE ERROR", ex.Message);
            _dialogs.ShowError("Erro durante a comparação", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Uma linha por item; um item pode ter várias operações (ex.: "manter ambos").</summary>
    private static List<ItemRow> GroupRows(SyncPlan plan) =>
        plan.Operations.GroupBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => new ItemRow(g.ToList())).ToList();

    private void FillRows(List<ItemRow> rows)
    {
        if (_removedFromList.Count > 0) rows = rows.Where(r => !_removedFromList.Contains(r.Item.RelativePath)).ToList();
        _rows = new ObservableCollection<ItemRow>(rows);
        _rowByPath.Clear();
        foreach (var r in rows) { _rowByPath[r.Item.RelativePath] = r; r.IncludedChanged = _includedHook; }
        _effective = null;
        var view = new ListCollectionView(_rows) { Filter = o => Matches((ItemRow)o) };
        RowsView = view;
        VisibleCount = view.Count;
    }

    /// <summary>
    /// Atualiza na grade só os caminhos indicados (mantendo rolagem e seleção do restante). Lotes muito grandes
    /// reconstroem a lista inteira, que é mais barato que milhares de trocas individuais.
    /// </summary>
    private void UpdateRows(IEnumerable<string> paths)
    {
        var plan = _outcome!.Plan;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 200) { FillRows(GroupRows(plan)); ShowSummary(); return; }

        var opsByPath = plan.Operations.Where(o => wanted.Contains(o.RelativePath))
            .GroupBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var path in wanted)
        {
            _rowByPath.TryGetValue(path, out var existing);
            if (_removedFromList.Contains(path) || !opsByPath.TryGetValue(path, out var ops))
            {
                if (existing is not null) { _rows.Remove(existing); _rowByPath.Remove(path); }
                continue;
            }

            var row = new ItemRow(ops) { IncludedChanged = _includedHook };
            _effective = null;
            if (existing is not null) _rows[_rows.IndexOf(existing)] = row;
            else InsertSorted(row);
            _rowByPath[path] = row;
        }
        RefreshView();
    }

    private void InsertSorted(ItemRow row)
    {
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(_rows[mid].Item.RelativePath, row.Item.RelativePath) < 0) lo = mid + 1; else hi = mid;
        }
        _rows.Insert(lo, row);
    }

    private bool Matches(ItemRow r) =>
        _statusFilter.Predicate(r) &&
        (_search.Length == 0 || r.Item.RelativePath.Contains(_search, StringComparison.OrdinalIgnoreCase));

    private void RefreshView()
    {
        if (RowsView is null) return;
        RowsView.Refresh();
        VisibleCount = RowsView.Cast<object>().Count();
    }

    private void RaiseOutcomeChanged()
    {
        OnPropertyChanged(nameof(CurrentPlan));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictCount));
        CommandManager_Invalidate();
    }

    /// <summary>Resumo a partir do que está na lista (itens removidos da lista não contam) e do plano atual.</summary>
    private void ShowSummary()
    {
        ComparisonSummary.Clear();
        PlanSummary.Clear();
        Notices.Clear();
        if (_outcome is null) { RaiseTabTexts(0, 0, 0); return; }
        var p = _outcome.Plan;

        var counts = new int[Enum.GetValues<ComparisonStatus>().Length];
        foreach (var r in _rows) counts[(int)r.Item.Status]++;
        int C(ComparisonStatus s) => counts[(int)s];
        void Add(string label, int n, System.Windows.Media.Brush b, string icon, Func<ItemRow, bool> select) =>
            ComparisonSummary.Add(new SummaryEntry(label, n.ToString("N0"), b, icon, select));
        Func<ItemRow, bool> Is(params ComparisonStatus[] s) => r => Array.IndexOf(s, r.Item.Status) >= 0;

        var different = _rows.Count - C(ComparisonStatus.Identical);
        Add("Analisados", _rows.Count, Presentation.Dark, "IconDocument", _ => true);
        Add("Iguais", C(ComparisonStatus.Identical), Presentation.Green, "IconCheck", Is(ComparisonStatus.Identical));
        Add("Só na origem", C(ComparisonStatus.SourceOnly), Presentation.Blue, "IconArrowRight", Is(ComparisonStatus.SourceOnly));
        Add("Só no destino", C(ComparisonStatus.TargetOnly), Presentation.Orange, "IconArrowLeft", Is(ComparisonStatus.TargetOnly));
        Add("Origem + nova", C(ComparisonStatus.SourceNewer), Presentation.Blue, "IconArrowUp", Is(ComparisonStatus.SourceNewer));
        Add("Destino + novo", C(ComparisonStatus.TargetNewer), Presentation.Purple, "IconArrowDown", Is(ComparisonStatus.TargetNewer));
        Add("Diferentes", C(ComparisonStatus.DifferentSize) + C(ComparisonStatus.DifferentContent), Presentation.Orange, "IconCompare", Is(ComparisonStatus.DifferentSize, ComparisonStatus.DifferentContent));
        Add("Conflitos", p.ConflictCount, Presentation.Red, "IconWarning", r => r.IsConflict || r.Item.Status == ComparisonStatus.Conflict);
        Add("Erros", C(ComparisonStatus.Error), Presentation.Red, "IconError", Is(ComparisonStatus.Error));

        RefreshPlanPart();

        RaiseTabTexts(different, p.ConflictCount, C(ComparisonStatus.Error));
    }

    // ------------------------------------------------------------ o que será executado (marcar / desmarcar)
    // Cada item com ação pendente tem uma marca. Desmarcar NÃO tira da lista: só deixa o item de fora desta execução.
    // O plano que vai para o executor é o "plano efetivo": o plano da comparação com os itens desmarcados virando Skip.
    private SyncPlan? _effective;
    private readonly Action<ItemRow> _includedHook;
    private string _includedText = "";

    /// <summary>"Marcados: 12 de 40" (itens com ação que entram na execução).</summary>
    public string IncludedText { get => _includedText; private set => SetProperty(ref _includedText, value); }

    /// <summary>Itens que o executor vai receber (marcados), mantendo as regras de segurança do plano.</summary>
    private SyncPlan EffectivePlan()
    {
        // o cache vale só para o plano base em que foi calculado (o plano base é trocado ao remover da lista, resolver conflitos etc.)
        var basePlan = _outcome!.Plan;
        if (_effective is null || !ReferenceEquals(_effectiveBase, basePlan))
        {
            _effective = BuildEffectivePlan();
            _effectiveBase = basePlan;
        }
        return _effective;
    }

    private SyncPlan? _effectiveBase;

    private SyncPlan BuildEffectivePlan()
    {
        var plan = _outcome!.Plan;
        HashSet<string>? excluded = null;
        foreach (var r in _rows)
            if (r.CanInclude && !r.IsIncluded) (excluded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(r.Item.RelativePath);
        if (excluded is null) return plan;

        // Uma pasta só é excluída se tudo o que está dentro dela também for: senão a exclusão da pasta falharia (ela não ficaria vazia).
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in excluded)
            for (var i = path.LastIndexOf('\\'); i > 0; i = path.LastIndexOf('\\', i - 1))
                parents.Add(path[..i]);
        foreach (var op in plan.Operations)
            if (op.Type == SyncOperationType.DeleteDirectory && parents.Contains(op.RelativePath)) excluded.Add(op.RelativePath);

        return PlanEditor.RemoveFromPlan(plan, excluded, "Desmarcado por você (nada será feito)");
    }

    private void OnRowIncludedChanged(ItemRow row)
    {
        if (TreeManager.FindByPath(row.Item.RelativePath) is { } node)
        {
            node.SetCheckedQuietly(row.IsIncluded);
            node.Parent?.UpdateCheckFromChildrenDirect();
            RaiseTreeChanged();
        }
        IncludeChanged();
    }

    private void IncludeChanged()
    {
        _effective = null;
        RefreshPlanPart();
        CommandManager_Invalidate();
    }

    public void SetIncluded(IEnumerable<ItemRow> rows, bool value)
    {
        var changed = false;
        foreach (var r in rows) changed |= r.SetIncludedQuietly(value);
        if (changed) IncludeChanged();
    }

    public void SetAllIncluded(bool value) => SetIncluded(_rows, value);

    /// <summary>Marca só os itens indicados (e desmarca todo o resto): "executar apenas estes".</summary>
    public void IncludeOnly(IEnumerable<ItemRow> keep)
    {
        var set = new HashSet<ItemRow>(keep);
        var changed = false;
        foreach (var r in _rows) changed |= r.SetIncludedQuietly(set.Contains(r));
        if (changed) IncludeChanged();
    }

    /// <summary>Resumo das ações previstas, avisos e contagem de marcados — refeito a cada marca (não mexe nos cartões de comparação).</summary>
    private void RefreshPlanPart()
    {
        PlanSummary.Clear();
        Notices.Clear();
        if (_outcome is null) { IncludedText = ""; return; }

        var p = EffectivePlan();
        PlanSummary.Add(new SummaryEntry("Copiar", p.CopyCount.ToString("N0"), Presentation.Green, "IconCopy",
            r => r.Operations.Any(o => o.Type == SyncOperationType.Copy)));
        PlanSummary.Add(new SummaryEntry("Atualizar", p.UpdateCount.ToString("N0"), Presentation.Blue, "IconSync",
            r => r.Operations.Any(o => o.Type == SyncOperationType.Update)));
        PlanSummary.Add(new SummaryEntry("Excluir", p.DeleteCount.ToString("N0"), Presentation.Red, "IconDelete",
            r => r.Operations.Any(o => o.Type is SyncOperationType.Delete or SyncOperationType.DeleteDirectory)));
        PlanSummary.Add(new SummaryEntry("Ignorar", p.SkipCount.ToString("N0"), Presentation.Gray, "IconBlock",
            r => !r.CanInclude && !r.IsConflict && r.Item.Status != ComparisonStatus.Error));
        PlanSummaryText = $"Dados a transferir: {Presentation.Bytes(p.TotalBytes)}";

        var pending = 0; var included = 0;
        foreach (var r in _rows) if (r.CanInclude) { pending++; if (r.IsIncluded) included++; }
        IncludedText = pending == 0 ? "" : $"Marcados: {included:N0} de {pending:N0}";

        foreach (var w in p.Warnings) Notices.Add(w);
        if (p.OverwritesNewerTargetCount > 0)
            Notices.Add($"{p.OverwritesNewerTargetCount:N0} arquivo(s) mais NOVOS no destino serão SOBRESCRITOS.");
        if (_removedFromList.Count > 0)
            Notices.Add($"{_removedFromList.Count:N0} item(ns) removidos da lista (não serão alterados nesta execução).");
        if (included < pending)
            Notices.Add($"{pending - included:N0} item(ns) desmarcados: ficam de fora desta execução (continuam na lista).");
        if (p.IsBlocked) Notices.Add("BLOQUEADO: " + p.BlockedReason);
    }

    // ------------------------------------------------------------ clicar num cartão seleciona os itens dele
    /// <summary>A tela seleciona estas linhas na grade (a seleção é da grade; o ViewModel só diz quais).</summary>
    public event Action<IReadOnlyList<ItemRow>>? SelectRowsRequested;

    private void SelectRowsOf(SummaryEntry entry)
    {
        if (entry.Select is null || _rows.Count == 0) return;
        // o filtro/pesquisa atuais poderiam esconder os itens do cartão: volta a mostrar tudo antes de selecionar
        if (_statusFilter.Label != "Todos") SelectFilter("Todos");
        if (_search.Length > 0) Search = "";
        var matches = _rows.Where(entry.Select).ToList();
        SelectRowsRequested?.Invoke(matches);
        StatusMessage = matches.Count == 0
            ? $"Nenhum item em \"{entry.Label}\"."
            : $"{matches.Count:N0} item(ns) selecionado(s): {entry.Label}. Marque/desmarque, copie com as setas ou use o clique direito.";
    }

    private int _differentCount;

    private void RaiseTabTexts(int different, int conflicts, int errors)
    {
        _differentCount = different;
        _conflictsTab = conflicts;
        _errorsTab = errors;
        OnPropertyChanged(nameof(ConflictsTabText));
        OnPropertyChanged(nameof(ErrorsTabText));
        OnPropertyChanged(nameof(ItemsCountText));
    }

    private void ClearResults()
    {
        _outcome = null;
        _effective = null;
        Plan = null;
        if (CurrentScreen == AppScreen.Plan) CurrentScreen = AppScreen.Comparison;
        _removedFromList.Clear();
        _rowByPath.Clear();
        _rows = new ObservableCollection<ItemRow>();
        TreeManager.FlatVisibleRows.Clear();
        RaiseTreeChanged();
        RowsView = null;
        VisibleCount = 0;
        ComparisonSummary.Clear();
        Notices.Clear();
        PlanSummary.Clear();
        RaiseTabTexts(0, 0, 0);
        ExecutionSummary = "";
        CurrentFile = "";
        ProgressValue = 0;
        ProgressText = "";
        OnPropertyChanged(nameof(CurrentPlan));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictCount));
        CommandManager_Invalidate();
    }

    public void ClearProfileData()
    {
        if (IsDirty && !ConfirmDiscardChanges()) return;

        _profileId = 0;
        _profileName = "";
        _selectedProfileItem = null;
        SourcePath = "";
        TargetPath = "";
        SelectedMethod = Methods.FirstOrDefault(m => m.Method == SyncMethod.MirrorSourceToTarget) ?? Methods[0];
        ClearResults();
        IsDirty = false;
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(SelectedProfileItem));
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = "Dados de perfil limpos.";
    }

    // ------------------------------------------------------------ conflitos (edição do plano; nada é executado aqui)
    public void ResolveSelection(IReadOnlyList<ItemRow> selected, ConflictResolution resolution)
    {
        if (IsBusy || _outcome is null) return;
        var paths = selected.Where(r => r.IsConflict).Select(r => r.Item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
        {
            _dialogs.ShowInfo("Nenhum conflito selecionado",
                "Selecione linhas com status Conflito. Dica: filtre por \"Conflitos\" e use Ctrl+A para selecionar todos.");
            return;
        }

        _outcome = _outcome with { Plan = PlanEditor.ResolveConflicts(_outcome.Plan, paths, resolution, DateTime.Now) };
        UpdateRows(paths);
        ShowSummary();
        RaiseOutcomeChanged();

        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var still = _outcome.Plan.Operations.Count(o => o.Type == SyncOperationType.Conflict && set.Contains(o.RelativePath));
        var what = resolution switch
        {
            ConflictResolution.KeepSource => "manter o Source",
            ConflictResolution.KeepTarget => "manter o Target",
            ConflictResolution.KeepBoth => "manter ambos",
            _ => "ignorar"
        };
        StatusMessage = $"{paths.Count - still:N0} conflito(s) resolvidos no plano ({what}). Nada foi executado ainda: use Visualizar plano e Executar." +
                        (still > 0 ? $" {still:N0} conflito(s) de tipo (arquivo × pasta) só podem ser ignorados." : "");
    }

    // ------------------------------------------------------------ cópia manual (setas ← →)
    /// <summary>Copia os itens selecionados no sentido pedido: Source → Target (→) ou Target → Source (←).</summary>
    public async Task CopySelectionAsync(IReadOnlyList<ItemRow> selected, FileSide from)
    {
        if (IsBusy || _outcome is null || selected.Count == 0) return;
        var fromName = from == FileSide.Source ? "Source" : "Target";
        var toName = from == FileSide.Source ? "Target" : "Source";

        var res = PlanEditor.CopyItems(_outcome.Plan, selected.Select(r => r.Item), from);
        if (res.Plan.ExecutableCount == 0)
        {
            _dialogs.ShowInfo($"Nada para copiar do {fromName} para o {toName}",
                $"Nenhum dos {selected.Count:N0} item(ns) selecionado(s) pode ser copiado nesse sentido:\n\n" +
                string.Join("\n", res.NotApplicable.Take(8)) + (res.NotApplicable.Count > 8 ? $"\n... e mais {res.NotApplicable.Count - 8:N0}" : ""));
            return;
        }

        // Copiar para um lado que não tem o arquivo é seguro e acontece direto (é o que a seta promete).
        // Sobrescrever algo existente, ou um lote grande, pede confirmação.
        var total = res.Plan.ExecutableCount;
        if (res.Overwrites > 0 || total > 100)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Copiar {total:N0} item(ns) do {fromName} para o {toName}?");
            sb.AppendLine();
            if (res.Overwrites > 0)
                sb.AppendLine($"• {res.Overwrites:N0} arquivo(s) que já existem no {toName} serão SOBRESCRITOS" +
                              (_outcome.Profile.BackupBeforeOverwrite ? " (a versão anterior vai para a pasta de segurança)." : " (sem cópia de segurança)."));
            if (res.OverwritesNewer > 0)
                sb.AppendLine($"• {res.OverwritesNewer:N0} deles são MAIS NOVOS no {toName} do que no {fromName}.");
            if (res.NotApplicable.Count > 0)
                sb.AppendLine($"• {res.NotApplicable.Count:N0} item(ns) selecionado(s) não se aplicam e serão ignorados.");
            if (!_dialogs.Confirm($"Copiar {(from == FileSide.Source ? "→" : "←")}", sb.ToString().TrimEnd(), res.Overwrites > 0)) return;
        }

        await RunManualAsync(res.Plan, res.Plan.Operations.Select(o => o.RelativePath), $"Cópia {fromName} → {toName}");
    }

    // ------------------------------------------------------------ Delete: remover da lista / excluir fisicamente
    public async Task DeleteSelectionAsync(IReadOnlyList<ItemRow> selected)
    {
        if (IsBusy || _outcome is null || selected.Count == 0) return;
        var profile = _outcome.Profile;
        var request = new DeleteChoiceRequest(selected.Count,
            selected.Any(r => r.Item.SourceExists), selected.Any(r => r.Item.TargetExists),
            DeleteStrategyText(profile.DeleteStrategy), selected[0].Item.RelativePath);

        var choice = _dialogs.AskDeleteChoice(request);
        var paths = selected.Select(r => r.Item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        switch (choice)
        {
            case DeleteChoice.Cancel:
                return;

            case DeleteChoice.RemoveFromList:
                _outcome = _outcome with { Plan = PlanEditor.RemoveFromPlan(_outcome.Plan, paths) };
                foreach (var p in paths) _removedFromList.Add(p);
                UpdateRows(paths);
                ShowSummary();
                RaiseOutcomeChanged();
                StatusMessage = $"{paths.Count:N0} item(ns) removidos da lista. Nenhum arquivo foi alterado e eles não serão tocados na execução.";
                return;
        }

        var atSource = choice is DeleteChoice.DeleteSource or DeleteChoice.DeleteBoth;
        var atTarget = choice is DeleteChoice.DeleteTarget or DeleteChoice.DeleteBoth;
        var res = PlanEditor.DeleteItems(_outcome.Plan, selected.Select(r => r.Item), atSource, atTarget);
        if (res.Plan.ExecutableCount == 0)
        {
            _dialogs.ShowInfo("Nada para excluir", "Nenhum dos itens selecionados existe no(s) lado(s) escolhido(s):\n\n" + string.Join("\n", res.NotApplicable.Take(8)));
            return;
        }

        var where = atSource && atTarget ? "do Source E do Target" : atSource ? "do Source" : "do Target";
        if (!_dialogs.Confirm("Excluir fisicamente",
                $"Excluir {res.Plan.ExecutableCount:N0} item(ns) {where}?\n\n{res.Plan.DeleteCount:N0} exclusão(ões) {DeleteStrategyText(profile.DeleteStrategy)}.", true))
            return;

        await RunManualAsync(res.Plan, res.Plan.Operations.Select(o => o.RelativePath), "Exclusão");
    }

    private static string DeleteStrategyText(DeleteStrategy s) => s switch
    {
        DeleteStrategy.SafetyFolder => "vão para a pasta de segurança (recuperáveis)",
        DeleteStrategy.RecycleBin => "vão para a Lixeira",
        _ => "serão EXCLUÍDOS PERMANENTEMENTE (sem recuperação)"
    };

    /// <summary>
    /// Executa um plano manual pelo mesmo SyncWorkflow/SyncExecutor de sempre (revalidação, pasta de segurança, histórico)
    /// e atualiza na lista só os itens tocados — sem recomparar as pastas inteiras.
    /// </summary>
    private async Task RunManualAsync(SyncPlan manualPlan, IEnumerable<string> paths, string title)
    {
        var outcome = _outcome!;
        var affected = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _cts = new CancellationTokenSource();
        _isExecuting = true;
        IsBusy = true;
        ExecutionSummary = "";
        ProgressValue = 0;
        StatusMessage = title + "...";
        try
        {
            var result = await _workflow.ExecuteAsync(outcome with { Plan = manualPlan }, new Progress<ExecutionProgress>(OnProgress), _cts.Token);
            ExecutionSummary = BuildExecutionSummary(result);
            BottomExpanded = true;
            StatusMessage = $"{title}: {Presentation.StatusName(result.Status)}.";
            if (result.Status == ExecutionStatus.Failed)
                _dialogs.ShowError(title + " interrompida(o)", result.Message ?? "A operação falhou.");

            _outcome = await _workflow.RefreshItemsAsync(outcome, affected, CancellationToken.None);
            UpdateRows(affected);
            ShowSummary();
            TreeManager.Build(_outcome.Plan, _rows);
            RaiseTreeChanged();
            RaiseOutcomeChanged();
        }
        catch (ProfileBusyException ex)
        {
            StatusMessage = "O perfil já está em execução.";
            _dialogs.ShowError("Perfil em execução", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("MANUAL OPERATION ERROR", ex.ToString());
            StatusMessage = "Erro inesperado.";
            _dialogs.ShowError("Erro inesperado", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _isExecuting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Operações manuais na árvore hierárquica compartilhada
    public async Task CopySelectionTreeAsync(IReadOnlyList<ComparisonTreeNode> nodes, FileSide from)
    {
        var items = nodes.Where(n => !n.IsDirectory && n.Item is not null).Select(n => n.Item!).ToList();
        if (items.Count == 0) return;
        var rows = items.Select(i => _rowByPath.GetValueOrDefault(i.RelativePath)).Where(r => r is not null).Cast<ItemRow>().ToList();
        if (rows.Count > 0)
        {
            await CopySelectionAsync(rows, from);
        }
    }

    public async Task DeleteSelectionTreeAsync(IReadOnlyList<ComparisonTreeNode> nodes)
    {
        var items = nodes.Where(n => !n.IsDirectory && n.Item is not null).Select(n => n.Item!).ToList();
        if (items.Count == 0) return;
        var rows = items.Select(i => _rowByPath.GetValueOrDefault(i.RelativePath)).Where(r => r is not null).Cast<ItemRow>().ToList();
        if (rows.Count > 0)
        {
            await DeleteSelectionAsync(rows);
        }
    }

    public void ResolveSelectionTree(IReadOnlyList<ComparisonTreeNode> nodes, ConflictResolution resolution)
    {
        var items = nodes.Where(n => !n.IsDirectory && n.Item is not null).Select(n => n.Item!).ToList();
        if (items.Count == 0) return;
        var rows = items.Select(i => _rowByPath.GetValueOrDefault(i.RelativePath)).Where(r => r is not null).Cast<ItemRow>().ToList();
        if (rows.Count > 0)
        {
            ResolveSelection(rows, resolution);
            if (_outcome is not null)
            {
                TreeManager.Build(_outcome.Plan, _rows);
                RaiseTreeChanged();
            }
        }
    }

    public void SetIncludedTree(IReadOnlyList<ComparisonTreeNode> nodes, bool isChecked)
    {
        foreach (var node in nodes)
        {
            node.IsChecked = isChecked;
        }
    }

    public void IncludeOnlyTree(IReadOnlyList<ComparisonTreeNode> nodes)
    {
        var set = new HashSet<string>(nodes.Where(n => !n.IsDirectory).Select(n => n.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var node in TreeManager.AllNodes)
        {
            if (!node.IsDirectory && node.CanInclude)
            {
                node.SetCheckedQuietly(set.Contains(node.RelativePath));
            }
        }
        foreach (var dir in TreeManager.AllNodes.Where(n => n.IsDirectory).OrderByDescending(n => n.Depth))
        {
            dir.UpdateCheckFromChildrenDirect();
        }
        SyncTreeChecksToRows();
        RaiseTreeChanged();
        IncludeChanged();
    }

    // ------------------------------------------------------------ executar
    public async Task ExecuteAsync()
    {
        // só o que está marcado: itens desmarcados já viram Skip no plano efetivo (a validação de segurança vale sobre ele)
        var outcome = _outcome! with { Plan = EffectivePlan() };
        var plan = outcome.Plan;

        var sb = new StringBuilder();
        sb.AppendLine($"Método: {Presentation.MethodName(plan.Method)}");
        sb.AppendLine($"Copiar: {plan.CopyCount:N0}    Atualizar: {plan.UpdateCount:N0}    Excluir: {plan.DeleteCount:N0}");
        sb.AppendLine($"Dados a transferir: {Presentation.Bytes(plan.TotalBytes)}");
        sb.AppendLine();
        if (plan.DeleteAtTargetCount > 0) sb.AppendLine($"• {plan.DeleteAtTargetCount:N0} item(ns) do Target {DeleteStrategyText(outcome.Profile.DeleteStrategy)}.");
        if (plan.DeleteAtSourceCount > 0) sb.AppendLine($"• {plan.DeleteAtSourceCount:N0} item(ns) do SOURCE {DeleteStrategyText(outcome.Profile.DeleteStrategy)}.");
        if (plan.OverwritesNewerTargetCount > 0)
            sb.AppendLine($"• {plan.OverwritesNewerTargetCount:N0} arquivo(s) MAIS NOVOS no destino serão sobrescritos.");
        if (plan.UpdateCount > 0 && outcome.Profile.BackupBeforeOverwrite)
            sb.AppendLine("• Versões anteriores sobrescritas serão guardadas na pasta de segurança.");
        if (plan.ConflictCount > 0)
            sb.AppendLine($"• {plan.ConflictCount:N0} conflito(s) NÃO serão alterados.");
        sb.AppendLine();
        sb.Append("Deseja executar?");

        // "Confirmar exclusões" desligado dispensa só esta confirmação padrão; a de exclusão em massa abaixo é sempre exigida.
        var skipStandardConfirm = plan.HasDestructiveOperations ? !outcome.Profile.ConfirmDeletes : !_confirmBeforeExecute;
        if (!skipStandardConfirm && !_dialogs.Confirm("Confirmar execução", sb.ToString(), plan.HasDestructiveOperations)) return;

        if (plan.RequiresStrongConfirmation &&
            !_dialogs.Confirm("ATENÇÃO — exclusão em massa",
                $"Este plano exclui {plan.DeleteCount:N0} de {plan.TargetEntryCount:N0} itens do Target ({plan.DeleteRatio:P0}).\n\n" +
                "Isso é muito acima do normal e pode indicar Source errado, filtro incorreto ou pasta de rede desconectada.\n\n" +
                "Tem certeza de que deseja continuar?", true))
            return;

        _cts = new CancellationTokenSource();
        _isExecuting = true;
        IsBusy = true;
        ExecutionSummary = "";
        ProgressValue = 0;
        StatusMessage = "Executando...";
        Execution.Begin(ProfileName, plan);
        CurrentScreen = AppScreen.Execution;
        var progress = new Progress<ExecutionProgress>(OnProgress);

        try
        {
            var result = await _workflow.ExecuteAsync(outcome, progress, _cts.Token);
            Execution.Finish(result);
            ExecutionSummary = BuildExecutionSummary(result);
            BottomExpanded = true;
            ProgressValue = result.Status == ExecutionStatus.Success ? 100 : ProgressValue;
            StatusMessage = $"{Presentation.StatusName(result.Status)}. Clique em Comparar para atualizar a lista.";
            if (result.Status is ExecutionStatus.Failed)
                _dialogs.ShowError("Execução interrompida", result.Message ?? "A execução falhou.");
            // o disco mudou: o plano antigo não vale mais
            _outcome = null;
            Plan = null;
            CommandManager_Invalidate();
        }
        catch (ProfileBusyException ex)
        {
            Execution.Fail(ex.Message);
            StatusMessage = "O perfil já está em execução.";
            _dialogs.ShowError("Perfil em execução",
                ex.Message + "\n\nUma tarefa agendada (ou outra janela) está executando este perfil agora. Aguarde terminar e compare novamente.");
        }
        catch (Exception ex)
        {
            _log.Error("EXECUTION ERROR", ex.ToString());
            StatusMessage = "Erro inesperado durante a execução.";
            Execution.Fail(ex.Message);
            _dialogs.ShowError("Erro inesperado durante a execução", ex.Message);
            _outcome = null;
        }
        finally
        {
            IsBusy = false;
            _isExecuting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(ExecutionProgress p)
    {
        if (CurrentScreen == AppScreen.Execution) Execution.Update(p);
        ProgressValue = p.Fraction * 100;
        CurrentFile = p.CurrentFile ?? "";
        ProgressText = $"{p.CompletedOperations:N0} / {p.TotalOperations:N0} operações  •  " +
                       $"{Presentation.Bytes(p.BytesProcessed)} / {Presentation.Bytes(p.TotalBytes)}  •  " +
                       $"{Presentation.Bytes((long)p.BytesPerSecond)}/s  •  {p.Elapsed:hh\\:mm\\:ss}";
    }

    private static string BuildExecutionSummary(ExecutionResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Resultado: {Presentation.StatusName(r.Status)}");
        if (r.Message is not null) sb.AppendLine(r.Message);
        sb.AppendLine($"Copiados: {r.FilesCopied:N0}   Atualizados: {r.FilesUpdated:N0}   Excluídos: {r.FilesDeleted:N0}   " +
                      $"Ignorados: {r.FilesSkipped:N0}   Conflitos: {r.Conflicts:N0}   Erros: {r.Errors:N0}   Avisos: {r.Warnings:N0}");
        if (r.NotExecuted > 0) sb.AppendLine($"Não executados: {r.NotExecuted:N0}");
        sb.AppendLine($"Transferido: {Presentation.Bytes(r.BytesTransferred)}   Duração: {r.Duration:hh\\:mm\\:ss}");
        if (r.SafetyFolder is not null) sb.AppendLine($"Pasta de segurança: {r.SafetyFolder}");

        var problems = r.Results.Where(x => x.Outcome is OperationOutcome.Failed or OperationOutcome.Warning).Take(30).ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Problemas:");
            foreach (var x in problems)
                sb.AppendLine($"  [{(x.Outcome == OperationOutcome.Failed ? "ERRO" : "AVISO")}] {x.Operation.RelativePath} — {x.Message}");
            var more = r.Results.Count(x => x.Outcome is OperationOutcome.Failed or OperationOutcome.Warning) - problems.Count;
            if (more > 0) sb.AppendLine($"  ... e mais {more:N0} (veja o log).");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ log na tela
    private void FlushLog()
    {
        var n = 0;
        while (n < 500 && _pendingLog.TryDequeue(out var line))
        {
            LogLines.Add(line);
            n++;
        }
        while (LogLines.Count > 3000) LogLines.RemoveAt(0);
    }
}
