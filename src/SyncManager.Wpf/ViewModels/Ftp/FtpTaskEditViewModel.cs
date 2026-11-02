using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;
using SyncManager.Infrastructure.Ftp;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public enum PathCheckState { Unknown, Ok, Warning, Error }

/// <summary>Cartão de método de sincronização (escolha única).</summary>
public sealed class MethodChoice : ObservableObject
{
    private bool _isSelected;
    private readonly Action<FtpSyncMethod> _onSelected;

    public MethodChoice(FtpSyncMethod method, string label, string[] description, Action<FtpSyncMethod> onSelected)
    {
        Method = method; Label = label; Description = description; _onSelected = onSelected;
    }

    public FtpSyncMethod Method { get; }
    public string Label { get; }
    public string[] Description { get; }
    public string DescriptionText => string.Join(Environment.NewLine, Description);

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(Method); }
    }

    internal void Mark(bool value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
}

/// <summary>Exceção exibida na etapa Filtros: padrão glob editável ou caminho excluído na árvore (somente remoção).</summary>
public sealed class ExclusionEntry : ObservableObject
{
    private string _text;
    private readonly Action<ExclusionEntry>? _changed;

    public ExclusionEntry(string text, bool isPath, Action<ExclusionEntry>? changed = null) { _text = text; IsPath = isPath; _changed = changed; }

    public bool IsPath { get; }
    public bool IsEditable => !IsPath;
    public string Kind => IsPath ? "Caminho" : "Padrão";
    public string Text
    {
        get => _text;
        set { if (SetProperty(ref _text, value)) _changed?.Invoke(this); }
    }
}

public sealed record StrategyOption(FtpComparisonStrategy Value, string Label);
public sealed record ReviewLine(string Label, string Value, bool IsWarning = false);
public sealed record ReviewSection(string Title, int EditStep, IReadOnlyList<ReviewLine> Lines);

/// <summary>Assistente guiado de tarefa FTP em seis etapas, com seleção persistente por regras.</summary>
public sealed class FtpTaskEditViewModel : ObservableObject
{
    public const int LastStep = 6;

    private readonly IFtpTaskService _taskService;
    private readonly IFtpConnectionService _connectionService;
    private readonly IFtpNotificationService? _notificationService;
    private readonly IFtpDirectoryBrowser? _directoryBrowser;
    private readonly bool _isNew;

    private FtpSyncTask _task;
    private FtpConnectionProfile? _selectedProfile;
    private bool _isSaving;
    private int _step = 1;
    private string _validationMessage = string.Empty;
    private string _errorField = string.Empty;
    private int _validationStep;
    private bool _remoteUnverifiedAcknowledged;
    private readonly FtpSelectionModel _selection;
    private bool _isLoadingTree;
    private string _treeMessage = string.Empty;
    private bool _isLoadingRemote;
    private string _remoteMessage = string.Empty;
    private string _treeLoadedFor = string.Empty;
    private CancellationTokenSource? _remoteCts;
    private int _selectionTab;
    private PathCheckState _localCheck;
    private string _localCheckText = string.Empty;
    private PathCheckState _remoteCheck;
    private string _remoteCheckText = string.Empty;
    private string _newExclusionText = string.Empty;
    private string _presetMessage = string.Empty;
    private int _previewIncluded;
    private int _previewIgnored;
    private long _previewBytes;
    private string _previewText = string.Empty;
    private readonly List<ExclusionEntry> _patternEntries = new();

    public FtpTaskEditViewModel(
        FtpSyncTask task,
        IFtpTaskService taskService,
        IFtpConnectionService connectionService,
        IFtpNotificationService? notificationService = null,
        IFtpDirectoryBrowser? directoryBrowser = null)
    {
        _task = task;
        _isNew = task.ConnectionProfileId == Guid.Empty && string.IsNullOrWhiteSpace(task.Name);
        _taskService = taskService;
        _connectionService = connectionService;
        _notificationService = notificationService;
        _directoryBrowser = directoryBrowser;
        _selection = new FtpSelectionModel(task.IncludedRelativePaths, task.ExcludedRelativePaths);
        _selection.Changed += OnSelectionChanged;

        Steps = new ObservableCollection<WizardStepItem>
        {
            new(1, "Geral"), new(2, "Conexão e caminhos"), new(3, "Método"), new(4, "Pastas selecionadas"), new(5, "Filtros"), new(6, "Revisão")
        };
        MethodChoices =
        [
            new(FtpSyncMethod.UpdateTarget, "Atualizar destino", ["Envia novos e modificados.", "Não exclui arquivos no servidor."], m => SyncMethod = m),
            new(FtpSyncMethod.MirrorSource, "Espelhar origem", ["Faz o servidor ficar igual à pasta local.", "Pode excluir arquivos remotos."], m => SyncMethod = m),
            new(FtpSyncMethod.CopyNewOnly, "Copiar somente novos", ["Não substitui arquivos existentes."], m => SyncMethod = m),
            new(FtpSyncMethod.DownloadServer, "Baixar do servidor", ["Sincroniza remoto → local."], m => SyncMethod = m),
        ];
        MarkMethod();

        foreach (var p in task.ExclusionPatterns) _patternEntries.Add(NewPatternEntry(p));
        RebuildExclusions();

        BackCommand = new RelayCommand(GoBack, () => Step > 1);
        NextCommand = new RelayCommand(async () => await GoNextAsync(), () => Step < LastStep);
        NavigateCommand = new RelayCommand<int>(async s => await NavigateToAsync(s));
        CancelCommand = new RelayCommand(() => RequestCancel?.Invoke());
        SaveCommand = new RelayCommand(async () => await ExecuteSaveAsync(), () => Step == LastStep);
        RetryTreeCommand = new RelayCommand(async () => await LoadLocalTreeAsync(true));
        RetryRemoteCommand = new RelayCommand(async () => await LoadRemotePreviewAsync());
        BrowseRemoteCommand = new RelayCommand(OpenRemotePicker);
        BrowseLocalFolderCommand = new RelayCommand(ExecuteBrowseLocalFolder);
        CheckLocalCommand = new RelayCommand(CheckLocal);
        CheckRemoteCommand = new RelayCommand(async () => await CheckRemoteAsync());
        ClearSelectionCommand = new RelayCommand(() => _selection.Clear());
        ApplyPresetExclusionsCommand = new RelayCommand(ExecuteApplyPresetExclusions);
        TestWebhookCommand = new RelayCommand(async () => await ExecuteTestWebhookAsync());
        AddExclusionCommand = new RelayCommand(AddExclusion, () => !string.IsNullOrWhiteSpace(NewExclusionText));
        RemoveExclusionCommand = new RelayCommand<ExclusionEntry>(RemoveExclusion);
        RemoveInclusionCommand = new RelayCommand<string>(r => _selection.SetChecked(r, false));
        EditStepCommand = new RelayCommand<int>(async s => await NavigateToAsync(s));
        RefreshPreviewCommand = new RelayCommand(async () => await UpdatePreviewAsync());

        _ = LoadProfilesAsync();
    }

    // ------------------------------------------------------------------ dados da tarefa
    public FtpSyncTask Task { get => _task; set => SetProperty(ref _task, value); }

    public string Name { get => _task.Name; set { if (_task.Name != value) { _task.Name = value; Edited(); OnPropertyChanged(); OnPropertyChanged(nameof(WindowTitle)); } } }
    public string Description { get => _task.Description; set { if (_task.Description != value) { _task.Description = value; Edited(); OnPropertyChanged(); } } }

    public ObservableCollection<FtpConnectionProfile> AvailableProfiles { get; } = new();

    public FtpConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            if (value != null)
            {
                _task.ConnectionProfileId = value.Id;
                if (string.IsNullOrWhiteSpace(_task.TargetRemotePath) || _task.TargetRemotePath == "/")
                    if (!string.IsNullOrWhiteSpace(value.DefaultRemotePath)) TargetRemotePath = value.DefaultRemotePath;
                if (string.IsNullOrWhiteSpace(_task.SourceLocalPath) && !string.IsNullOrWhiteSpace(value.DefaultLocalPath))
                    SourceLocalPath = value.DefaultLocalPath;
            }
            ResetRemoteCheck();
            Edited();
            OnPropertyChanged(nameof(ConnectionSummary));
        }
    }

    public string ConnectionSummary => SelectedProfile == null ? "Nenhuma conexão selecionada"
        : $"{SelectedProfile.Name} · {ProtocolLabel(SelectedProfile.Protocol)} · {SelectedProfile.Host}";

    private static string ProtocolLabel(FtpProtocol p) => p switch { FtpProtocol.FtpsExplicit => "FTPS", FtpProtocol.FtpsImplicit => "FTPS implícito", _ => "FTP" };

    public string SourceLocalPath
    {
        get => _task.SourceLocalPath;
        set
        {
            if (_task.SourceLocalPath == value) return;
            _task.SourceLocalPath = value; _treeLoadedFor = string.Empty;
            _localCheck = PathCheckState.Unknown; _localCheckText = string.Empty;
            Edited(); OnPropertyChanged(); OnPropertyChanged(nameof(LocalCheck)); OnPropertyChanged(nameof(LocalCheckText)); OnPropertyChanged(nameof(MappingText));
        }
    }

    public string TargetRemotePath
    {
        get => _task.TargetRemotePath;
        set
        {
            if (_task.TargetRemotePath == value) return;
            _task.TargetRemotePath = value;
            ResetRemoteCheck(); Edited(); OnPropertyChanged();
        }
    }

    public FtpSyncMethod SyncMethod
    {
        get => _task.SyncMethod;
        set
        {
            if (_task.SyncMethod == value) return;
            _task.SyncMethod = value; MarkMethod(); Edited();
            foreach (var n in new[] { nameof(SyncMethod), nameof(SelectedMethodChoice), nameof(MethodWarning), nameof(IsMirror), nameof(IsDownload) }) OnPropertyChanged(n);
        }
    }

    public IReadOnlyList<MethodChoice> MethodChoices { get; }
    public MethodChoice? SelectedMethodChoice => MethodChoices.FirstOrDefault(m => m.Method == SyncMethod);
    public bool IsMirror => SyncMethod == FtpSyncMethod.MirrorSource;
    public bool IsDownload => SyncMethod == FtpSyncMethod.DownloadServer;
    private void MarkMethod() { foreach (var m in MethodChoices) m.Mark(m.Method == _task.SyncMethod); }

    public string MethodWarning => SyncMethod switch
    {
        FtpSyncMethod.MirrorSource => "Atenção: o espelhamento pode excluir no servidor arquivos que não existem na origem. Exclusões remotas permanecem bloqueadas enquanto a trava abaixo estiver ativa e, quando habilitadas, aparecem destacadas no checklist.",
        FtpSyncMethod.DownloadServer => "Atenção: este método grava na pasta local arquivos vindos do servidor e pode substituir arquivos locais diferentes. Revise o checklist antes de aprovar.",
        _ => "Exclusões remotas devem ser bloqueadas por padrão e, quando habilitadas, são mostradas com destaque no checklist."
    };

    public FtpComparisonStrategy ComparisonStrategy { get => _task.ComparisonStrategy; set { if (_task.ComparisonStrategy != value) { _task.ComparisonStrategy = value; Edited(); OnPropertyChanged(); } } }
    public int TimestampToleranceSeconds { get => _task.TimestampToleranceSeconds; set { if (_task.TimestampToleranceSeconds != value) { _task.TimestampToleranceSeconds = value; Edited(); OnPropertyChanged(); } } }

    public bool ProhibitRemoteDeletions { get => _task.ProhibitRemoteDeletions; set { if (_task.ProhibitRemoteDeletions != value) { _task.ProhibitRemoteDeletions = value; Edited(); OnPropertyChanged(); } } }
    public bool BackupBeforeOverwrite { get => _task.BackupBeforeOverwrite; set { if (_task.BackupBeforeOverwrite != value) { _task.BackupBeforeOverwrite = value; Edited(); OnPropertyChanged(); } } }
    public bool RequireExplicitChecklistConfirmation { get => _task.RequireExplicitChecklistConfirmation; set { if (_task.RequireExplicitChecklistConfirmation != value) { _task.RequireExplicitChecklistConfirmation = value; Edited(); OnPropertyChanged(); } } }
    public double MaxRemoteDeletePercentage { get => _task.MaxRemoteDeletePercentage; set { if (Math.Abs(_task.MaxRemoteDeletePercentage - value) > 0.001) { _task.MaxRemoteDeletePercentage = value; Edited(); OnPropertyChanged(); } } }
    public bool UseAtomicUpload { get => _task.UseAtomicUpload; set { if (_task.UseAtomicUpload != value) { _task.UseAtomicUpload = value; Edited(); OnPropertyChanged(); } } }
    public bool EnableMaintenanceMode { get => _task.EnableMaintenanceMode; set { if (_task.EnableMaintenanceMode != value) { _task.EnableMaintenanceMode = value; Edited(); OnPropertyChanged(); } } }
    public string MaintenanceFileName { get => _task.MaintenanceFileName ?? string.Empty; set { if (_task.MaintenanceFileName != value) { _task.MaintenanceFileName = value; Edited(); OnPropertyChanged(); } } }
    public bool NotifyOnCompletion { get => _task.NotifyOnCompletion; set { if (_task.NotifyOnCompletion != value) { _task.NotifyOnCompletion = value; Edited(); OnPropertyChanged(); } } }
    public string WebhookUrl { get => _task.WebhookUrl ?? string.Empty; set { if (_task.WebhookUrl != value) { _task.WebhookUrl = value; Edited(); OnPropertyChanged(); } } }
    public FtpWebhookType WebhookType { get => _task.WebhookType; set { if (_task.WebhookType != value) { _task.WebhookType = value; Edited(); OnPropertyChanged(); } } }

    public WebFrameworkPreset FrameworkPreset
    {
        get => _task.FrameworkPreset;
        set { if (_task.FrameworkPreset != value) { _task.FrameworkPreset = value; Edited(); OnPropertyChanged(); OnPropertyChanged(nameof(FrameworkPresetDescription)); } }
    }
    public string FrameworkPresetDescription => FtpFrameworkPresetHelper.GetPresetDescription(FrameworkPreset);

    public bool IsSaving { get => _isSaving; set => SetProperty(ref _isSaving, value); }

    public IReadOnlyList<StrategyOption> StrategyOptions { get; } =
    [
        new(FtpComparisonStrategy.SizeAndTimestamp, "Tamanho e data de modificação"),
        new(FtpComparisonStrategy.SizeOnly, "Somente tamanho"),
        new(FtpComparisonStrategy.ChecksumIfSupported, "Checksum, se o servidor suportar"),
    ];
    public StrategyOption SelectedStrategy
    {
        get => StrategyOptions.First(o => o.Value == ComparisonStrategy);
        set { if (value != null) { ComparisonStrategy = value.Value; OnPropertyChanged(); } }
    }

    public ObservableCollection<FtpComparisonStrategy> AvailableStrategies { get; } = new()
    {
        FtpComparisonStrategy.SizeAndTimestamp, FtpComparisonStrategy.SizeOnly, FtpComparisonStrategy.ChecksumIfSupported
    };
    public ObservableCollection<WebFrameworkPreset> AvailableFrameworkPresets { get; } = new()
    {
        WebFrameworkPreset.None, WebFrameworkPreset.WordPress, WebFrameworkPreset.Laravel, WebFrameworkPreset.NodeJs, WebFrameworkPreset.DotNet, WebFrameworkPreset.AngularReactVue
    };
    public ObservableCollection<FtpWebhookType> AvailableWebhookTypes { get; } = new()
    {
        FtpWebhookType.Discord, FtpWebhookType.Slack, FtpWebhookType.MicrosoftTeams, FtpWebhookType.GenericJson
    };

    /// <summary>Qualquer edição invalida confirmações anteriores e limpa o erro da etapa atual quando ele deixa de existir.</summary>
    private void Edited()
    {
        _remoteUnverifiedAcknowledged = false;
        if (_validationStep != 0)
        {
            var (m, f) = ValidateStepCore(_validationStep);
            if (m == null || f != _errorField) ClearValidation();   // o campo apontado foi corrigido
        }
        OnPropertyChanged(nameof(ReviewSections));
    }

    // ------------------------------------------------------------------ navegação do assistente
    public ObservableCollection<WizardStepItem> Steps { get; }

    public int Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            foreach (var n in new[] { nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(IsStep6), nameof(StepTitle), nameof(StepLabel),
                         nameof(StepHeading), nameof(StepDescription), nameof(IsLastStep), nameof(IsNotLastStep), nameof(NextButtonText), nameof(FooterNote), nameof(ReviewSections), nameof(ReviewWarnings) })
                OnPropertyChanged(n);
            RefreshSteps();
            BackCommand.RaiseCanExecuteChanged(); NextCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsStep1 => _step == 1;
    public bool IsStep2 => _step == 2;
    public bool IsStep3 => _step == 3;
    public bool IsStep4 => _step == 4;
    public bool IsStep5 => _step == 5;
    public bool IsStep6 => _step == 6;
    public bool IsLastStep => _step == LastStep;
    public bool IsNotLastStep => _step != LastStep;
    public string StepTitle => Steps[_step - 1].Title;
    public string StepLabel => _step switch
    {
        1 => "INFORMAÇÕES GERAIS", 2 => "CONEXÃO E CAMINHOS", 3 => "MÉTODO DE SINCRONIZAÇÃO", 4 => "ESCOPO PERSISTENTE", 5 => "FILTROS E EXCEÇÕES", _ => "REVISÃO FINAL"
    };
    public string StepHeading => _step switch
    {
        1 => "Definir tarefa e caminhos", 2 => "Confirmar origem e destino", 3 => "O que o FileSync deve fazer?",
        4 => "Escolha o que esta tarefa pode sincronizar", 5 => "Controle preciso da seleção", _ => "Revise antes de salvar"
    };
    public string StepDescription => _step switch
    {
        1 => "Dê um nome à tarefa e informe a conexão, a pasta local de origem e o diretório remoto de destino. Você poderá reutilizar a mesma conexão em várias tarefas.",
        2 => "Examine a pasta local, navegue pelo servidor e confirme o mapeamento antes de escolher o que será sincronizado.",
        3 => "O método define a direção e o comportamento da sincronização. Escolha com cuidado: ele determina o que o checklist vai propor.",
        4 => "Marque as pastas e arquivos que fazem parte desta tarefa. A seleção é salva como regras: novos arquivos dentro de pastas marcadas entram automaticamente.",
        5 => "Veja o que está incluído, adicione exceções e entenda como elas afetam a seleção. Exceções prevalecem dentro das pastas selecionadas.",
        _ => "Confira os dados reais da tarefa. Volte a qualquer etapa para corrigir; a tarefa só é gravada ao clicar em Salvar tarefa."
    };
    public string WindowTitle => _isNew ? "Nova tarefa de sincronização FTP" : $"Editar tarefa — {_task.Name}";
    public string WindowSubtitle => "Configure uma tarefa reutilizável com seleção permanente de arquivos";
    public string NextButtonText => _step == 5 ? "Revisar tarefa →" : "Avançar →";
    public string FooterNote => _step == LastStep ? "Nada foi gravado ainda: a tarefa é salva somente ao clicar em Salvar tarefa." : "A configuração será salva na tarefa.";

    public string ValidationMessage
    {
        get => _validationMessage;
        private set { if (SetProperty(ref _validationMessage, value)) { OnPropertyChanged(nameof(HasValidationMessage)); RaiseFieldErrors(); } }
    }
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);
    public bool HasNameError => HasValidationMessage && _errorField == nameof(Name);
    public bool HasProfileError => HasValidationMessage && _errorField == nameof(SelectedProfile);
    public bool HasSourceError => HasValidationMessage && _errorField == nameof(SourceLocalPath);
    public bool HasTargetError => HasValidationMessage && _errorField == nameof(TargetRemotePath);
    public bool HasSelectionError => HasValidationMessage && _errorField == nameof(Selection);
    /// <summary>Mensagem sem campo específico (ex.: aviso ao salvar): exibida junto ao rodapé.</summary>
    public bool HasGeneralError => HasValidationMessage && _errorField.Length == 0;

    private void RaiseFieldErrors()
    {
        foreach (var n in new[] { nameof(HasNameError), nameof(HasProfileError), nameof(HasSourceError), nameof(HasTargetError), nameof(HasSelectionError), nameof(HasGeneralError) })
            OnPropertyChanged(n);
    }

    private void ClearValidation()
    {
        _validationStep = 0; _errorField = string.Empty;
        ValidationMessage = string.Empty;
        RefreshSteps();
    }

    private void RefreshSteps()
    {
        foreach (var s in Steps)
        {
            if (s.Number == _step) s.State = WizardStepState.Current;
            else if (HasValidationMessage && _validationStep == s.Number) s.State = WizardStepState.Error;
            else if (s.Number < _step && ValidateStepCore(s.Number).Message == null) s.State = WizardStepState.Done;
            else s.State = WizardStepState.Pending;
        }
    }

    public string? ValidateStep(int step) => ValidateStepCore(step).Message;

    private (string? Message, string Field) ValidateStepCore(int step)
    {
        switch (step)
        {
            case 1:
                if (string.IsNullOrWhiteSpace(Name)) return ("Informe o nome da tarefa.", nameof(Name));
                if (SelectedProfile == null) return ("Selecione a conexão FTP (cadastre uma em Conexões FTP se a lista estiver vazia).", nameof(SelectedProfile));
                if (string.IsNullOrWhiteSpace(SourceLocalPath)) return ("Informe a pasta local de origem.", nameof(SourceLocalPath));
                if (string.IsNullOrWhiteSpace(TargetRemotePath)) return ("Informe o diretório remoto de destino.", nameof(TargetRemotePath));
                return (null, string.Empty);
            case 2:
                if (!Directory.Exists(SourceLocalPath)) return ($"A pasta local não existe ou não está acessível: {SourceLocalPath}", nameof(SourceLocalPath));
                if (TargetRemotePath.Contains("..")) return ("O caminho remoto não pode conter \"..\".", nameof(TargetRemotePath));
                return (null, string.Empty);
            case 4:
                if (!_selection.HasSelection)
                    return ("Selecione ao menos uma pasta ou arquivo. Uma seleção vazia não sincroniza a origem inteira.", nameof(Selection));
                return (null, string.Empty);
            default:
                return (null, string.Empty);
        }
    }

    private bool TryValidate(int step)
    {
        var (message, field) = ValidateStepCore(step);
        _errorField = field; _validationStep = message == null ? 0 : step;
        ValidationMessage = message ?? string.Empty;
        RaiseFieldErrors();
        RefreshSteps();
        return message == null;
    }

    private async Task GoNextAsync()
    {
        if (Step >= LastStep) return;
        if (!TryValidate(Step)) return;
        Step++;
        await OnEnterStepAsync();
    }

    private void GoBack()
    {
        ClearValidation();
        if (Step > 1) Step--;
    }

    /// <summary>Navegação lateral/atalhos: voltar é livre; avançar exige todas as etapas intermediárias válidas.</summary>
    private async Task NavigateToAsync(int target)
    {
        if (target < 1 || target > LastStep || target == Step) return;
        if (target < Step) { ClearValidation(); Step = target; await OnEnterStepAsync(); return; }
        for (var s = Step; s < target; s++)
        {
            if (!TryValidate(s)) { Step = s; TryValidate(s); return; }
        }
        ClearValidation();
        Step = target;
        await OnEnterStepAsync();
    }

    private async Task OnEnterStepAsync()
    {
        if (Step == 2) CheckLocal();
        if (Step == 4)
        {
            await LoadLocalTreeAsync();
            _ = LoadRemotePreviewAsync();
        }
        if (Step is 4 or 5 or 6) await UpdatePreviewAsync();
        OnPropertyChanged(nameof(ReviewSections)); OnPropertyChanged(nameof(ReviewWarnings));
    }

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand<int> NavigateCommand { get; }
    public RelayCommand<int> EditStepCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RetryTreeCommand { get; }
    public RelayCommand RetryRemoteCommand { get; }
    public RelayCommand BrowseRemoteCommand { get; }
    public RelayCommand BrowseLocalFolderCommand { get; }
    public RelayCommand CheckLocalCommand { get; }
    public RelayCommand CheckRemoteCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ApplyPresetExclusionsCommand { get; }
    public RelayCommand TestWebhookCommand { get; }
    public RelayCommand AddExclusionCommand { get; }
    public RelayCommand<ExclusionEntry> RemoveExclusionCommand { get; }
    public RelayCommand<string> RemoveInclusionCommand { get; }
    public RelayCommand RefreshPreviewCommand { get; }

    public event Action? OnSaved;
    public event Action? RequestClose;
    public event Action? RequestCancel;
    public event Action<FtpRemotePickerViewModel>? RequestRemotePicker;

    // ------------------------------------------------------------------ etapa 2: validação de caminhos
    public PathCheckState LocalCheck => _localCheck;
    public string LocalCheckText => _localCheckText;
    public PathCheckState RemoteCheck => _remoteCheck;
    public string RemoteCheckText => _remoteCheckText;
    public string MappingText => $"{SourceLocalPath}  →  {TargetRemotePath}";

    private void ResetRemoteCheck()
    {
        _remoteCheck = PathCheckState.Unknown; _remoteCheckText = "Ainda não verificado.";
        OnPropertyChanged(nameof(RemoteCheck)); OnPropertyChanged(nameof(RemoteCheckText)); OnPropertyChanged(nameof(MappingText));
    }

    /// <summary>Examina a pasta local: existência, permissão de leitura e conteúdo de primeiro nível.</summary>
    public void CheckLocal()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourceLocalPath)) { _localCheck = PathCheckState.Error; _localCheckText = "Informe a pasta local de origem."; }
            else if (!Directory.Exists(SourceLocalPath)) { _localCheck = PathCheckState.Error; _localCheckText = "A pasta não existe ou não está acessível."; }
            else
            {
                var di = new DirectoryInfo(SourceLocalPath);
                var dirs = di.EnumerateDirectories().Count();
                var files = di.EnumerateFiles().Count();
                _localCheck = PathCheckState.Ok;
                _localCheckText = $"Pasta acessível: {dirs} pasta(s) e {files} arquivo(s) no primeiro nível.";
            }
        }
        catch (Exception ex) { _localCheck = PathCheckState.Error; _localCheckText = $"Sem permissão de leitura: {ex.Message}"; }
        OnPropertyChanged(nameof(LocalCheck)); OnPropertyChanged(nameof(LocalCheckText)); OnPropertyChanged(nameof(MappingText)); OnPropertyChanged(nameof(ReviewSections));
    }

    /// <summary>Consulta o servidor de verdade: lista o diretório de destino com a conexão escolhida.</summary>
    public async Task CheckRemoteAsync()
    {
        if (_directoryBrowser == null || SelectedProfile == null)
        {
            _remoteCheck = PathCheckState.Error; _remoteCheckText = "Selecione uma conexão para verificar o destino.";
        }
        else
        {
            _remoteCheck = PathCheckState.Unknown; _remoteCheckText = "Verificando o servidor…";
            OnPropertyChanged(nameof(RemoteCheck)); OnPropertyChanged(nameof(RemoteCheckText));
            try
            {
                var items = await _directoryBrowser.ListDirectoryAsync(SelectedProfile, TargetRemotePath);
                _remoteCheck = PathCheckState.Ok;
                _remoteCheckText = $"Diretório acessível no servidor: {items.Count(i => i.IsDirectory)} pasta(s) e {items.Count(i => !i.IsDirectory)} arquivo(s).";
            }
            catch (Exception ex)
            {
                _remoteCheck = PathCheckState.Warning;
                _remoteCheckText = $"Não foi possível confirmar \"{TargetRemotePath}\": {ex.Message}";
            }
        }
        OnPropertyChanged(nameof(RemoteCheck)); OnPropertyChanged(nameof(RemoteCheckText));
        OnPropertyChanged(nameof(ReviewSections)); OnPropertyChanged(nameof(ReviewWarnings));
    }

    private void OpenRemotePicker()
    {
        if (_directoryBrowser == null || SelectedProfile == null)
        {
            _errorField = nameof(SelectedProfile); _validationStep = Step;
            ValidationMessage = "Selecione uma conexão FTP antes de procurar no servidor.";
            return;
        }
        var picker = new FtpRemotePickerViewModel(_directoryBrowser, SelectedProfile, string.IsNullOrWhiteSpace(TargetRemotePath) ? "/" : TargetRemotePath, SourceLocalPath) { IsPickerMode = true };
        picker.Picked += path => TargetRemotePath = path;
        RequestRemotePicker?.Invoke(picker);
    }

    private void ExecuteBrowseLocalFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta local de origem", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(SourceLocalPath) && Directory.Exists(SourceLocalPath)) dialog.InitialDirectory = SourceLocalPath;
        if (dialog.ShowDialog() == true) SourceLocalPath = dialog.FolderName;
    }

    // ------------------------------------------------------------------ etapa 4: seleção persistente
    public FtpSelectionModel Selection => _selection;
    public ObservableCollection<FsNode> LocalRoots { get; } = new();
    public ObservableCollection<FsNode> RemoteRoots { get; } = new();
    public bool IsLoadingTree { get => _isLoadingTree; private set => SetProperty(ref _isLoadingTree, value); }
    public string TreeMessage { get => _treeMessage; private set { if (SetProperty(ref _treeMessage, value)) OnPropertyChanged(nameof(HasTreeMessage)); } }
    public bool HasTreeMessage => !string.IsNullOrEmpty(_treeMessage);
    public bool IsLoadingRemote { get => _isLoadingRemote; private set => SetProperty(ref _isLoadingRemote, value); }
    public string RemoteMessage { get => _remoteMessage; private set { if (SetProperty(ref _remoteMessage, value)) OnPropertyChanged(nameof(HasRemoteMessage)); } }
    public bool HasRemoteMessage => !string.IsNullOrEmpty(_remoteMessage);

    public int SelectionTab
    {
        get => _selectionTab;
        set { if (SetProperty(ref _selectionTab, value)) { OnPropertyChanged(nameof(IsTabFiles)); OnPropertyChanged(nameof(IsTabExceptions)); OnPropertyChanged(nameof(IsTabPreview)); } }
    }
    public bool IsTabFiles { get => _selectionTab == 0; set { if (value) SelectionTab = 0; } }
    public bool IsTabExceptions { get => _selectionTab == 1; set { if (value) SelectionTab = 1; } }
    public bool IsTabPreview { get => _selectionTab == 2; set { if (value) { SelectionTab = 2; _ = UpdatePreviewAsync(); } } }

    public bool HasSelection => _selection.HasSelection;
    public string SelectionSummary => _selection.HasSelection
        ? $"{_selection.Includes.Count} pasta(s)/arquivo(s) incluído(s) e {_selection.Excludes.Count} exceção(ões) por caminho. Novos arquivos dentro das pastas incluídas são considerados automaticamente; itens desmarcados ficam fora desta tarefa."
        : "Nenhuma pasta ou arquivo selecionado. Marque ao menos um item: uma seleção vazia NÃO sincroniza a origem inteira.";

    public ObservableCollection<string> InclusionRules { get; } = new();
    public ObservableCollection<ExclusionEntry> Exclusions { get; } = new();

    private void OnSelectionChanged()
    {
        foreach (var r in LocalRoots) r.RefreshState();
        RebuildExclusions();
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ReviewSections)); OnPropertyChanged(nameof(ReviewWarnings));
        if (_validationStep == 4 && _selection.HasSelection) ClearValidation();
        _remoteUnverifiedAcknowledged = false;
    }

    private void RebuildExclusions()
    {
        InclusionRules.Clear();
        foreach (var i in _selection.Includes) InclusionRules.Add(i);
        Exclusions.Clear();
        foreach (var e in _selection.Excludes) Exclusions.Add(new ExclusionEntry(e, true));
        foreach (var p in _patternEntries) Exclusions.Add(p);
    }

    public async Task LoadLocalTreeAsync(bool force = false)
    {
        var path = SourceLocalPath;
        if (!force && _treeLoadedFor == path && LocalRoots.Count > 0) return;
        IsLoadingTree = true; TreeMessage = string.Empty;
        try
        {
            var roots = await FsNode.LoadLocalRootAsync(path, _selection);
            LocalRoots.Clear();
            foreach (var r in roots) LocalRoots.Add(r);
            _treeLoadedFor = path;
            if (roots.Count == 0) TreeMessage = "A pasta de origem está vazia.";
        }
        catch (Exception ex)
        {
            LocalRoots.Clear(); _treeLoadedFor = string.Empty;
            TreeMessage = $"Não foi possível ler a pasta local: {ex.Message}";
        }
        finally { IsLoadingTree = false; }
    }

    /// <summary>Lista o destino remoto real (somente leitura) ao lado da seleção local.</summary>
    public async Task LoadRemotePreviewAsync()
    {
        RemoteRoots.Clear(); RemoteMessage = string.Empty;
        if (_directoryBrowser == null || SelectedProfile == null) { RemoteMessage = "Selecione uma conexão para consultar o servidor."; return; }

        _remoteCts?.Cancel();
        _remoteCts = new CancellationTokenSource();
        var token = _remoteCts.Token;
        IsLoadingRemote = true;
        try
        {
            var roots = await FsNode.LoadRemoteRootAsync(_directoryBrowser, SelectedProfile, TargetRemotePath, token);
            if (token.IsCancellationRequested) return;
            foreach (var r in roots) RemoteRoots.Add(r);
            if (roots.Count == 0) RemoteMessage = "O diretório remoto está vazio (ou ainda não existe): tudo será enviado como novo.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemoteMessage = $"Não foi possível listar o servidor: {ex.Message}"; }
        finally { IsLoadingRemote = false; }
    }

    // ------------------------------------------------------------------ etapa 5: filtros
    public string NewExclusionText
    {
        get => _newExclusionText;
        set { if (SetProperty(ref _newExclusionText, value)) AddExclusionCommand.RaiseCanExecuteChanged(); }
    }

    public string PresetMessage { get => _presetMessage; private set => SetProperty(ref _presetMessage, value); }

    private ExclusionEntry NewPatternEntry(string pattern) => new(pattern, false, _ => SyncPatterns());

    private void SyncPatterns()
    {
        _task.ExclusionPatterns = _patternEntries.Select(e => e.Text.Trim()).Where(t => t.Length > 0).ToList();
        Edited();
        OnPropertyChanged(nameof(SensitiveWarning)); OnPropertyChanged(nameof(SensitiveIsRisk));
    }

    private void AddExclusion()
    {
        var text = NewExclusionText.Trim().Replace('\\', '/');
        if (text.Length == 0) return;
        if (!_patternEntries.Any(e => e.Text.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            _patternEntries.Add(NewPatternEntry(text));
            SyncPatterns();
            RebuildExclusions();
        }
        NewExclusionText = string.Empty;
    }

    private void RemoveExclusion(ExclusionEntry? entry)
    {
        if (entry == null) return;
        if (entry.IsPath) { _selection.SetChecked(entry.Text, true); return; }   // volta a valer a regra da pasta incluída
        _patternEntries.Remove(entry);
        SyncPatterns();
        RebuildExclusions();
    }

    private void ExecuteApplyPresetExclusions()
    {
        if (FrameworkPreset == WebFrameworkPreset.None)
        {
            PresetMessage = "Selecione uma predefinição (WordPress, Laravel, Node.js, .NET ou SPA) para aplicar suas regras recomendadas.";
            return;
        }
        var added = 0;
        foreach (var p in FtpFrameworkPresetHelper.GetPresetExclusionPatterns(FrameworkPreset))
        {
            if (_patternEntries.Any(e => e.Text.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            _patternEntries.Add(NewPatternEntry(p)); added++;
        }
        SyncPatterns(); RebuildExclusions();
        PresetMessage = $"{added} regra(s) recomendada(s) adicionada(s) para {FrameworkPreset}.";
    }

    /// <summary>Aviso real: padrões típicos de arquivos sensíveis ausentes das exceções.</summary>
    public string SensitiveWarning
    {
        get
        {
            var texts = _patternEntries.Select(e => e.Text).ToList();
            var missing = new List<string>();
            if (!texts.Any(t => t.Contains(".env", StringComparison.OrdinalIgnoreCase))) missing.Add(".env");
            if (!texts.Any(t => t.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) &&
                Directory.Exists(SourceLocalPath) && Directory.Exists(Path.Combine(SourceLocalPath, "node_modules"))) missing.Add("node_modules");
            return missing.Count == 0
                ? "Arquivos sensíveis como .env estão protegidos pelas exceções. A seleção salva define o escopo permitido; o checklist continua obrigatório em cada execução manual."
                : $"Atenção: {string.Join(" e ", missing)} não está nas exceções. Arquivos sensíveis (.env) e diretórios de dependências (node_modules, vendor) normalmente não devem ser enviados ao servidor.";
        }
    }
    public bool SensitiveIsRisk => SensitiveWarning.StartsWith("Atenção");

    // ------------------------------------------------------------------ prévia real das regras
    public int PreviewIncluded => _previewIncluded;
    public int PreviewIgnored => _previewIgnored;
    public string PreviewText => _previewText;
    public string PreviewSummary => !_selection.HasSelection ? "Sem inclusões: nenhum arquivo seria sincronizado."
        : $"{_previewIncluded} arquivo(s) ({FsNode.FormatSize(_previewBytes)}) seriam considerados; {_previewIgnored} arquivo(s) da seleção são ignorados pelas exceções.";

    public async Task UpdatePreviewAsync()
    {
        if (!Directory.Exists(SourceLocalPath) || !_selection.HasSelection)
        {
            _previewIncluded = 0; _previewIgnored = 0; _previewBytes = 0; _previewText = string.Empty;
            RaisePreview(); return;
        }
        var root = SourceLocalPath;
        var inc = _selection.Includes.ToList(); var exc = _selection.Excludes.ToList();
        var patterns = _task.ExclusionPatterns.ToList(); var preset = _task.FrameworkPreset;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                var all = FtpComparisonEngine.ScanLocalFiles(root, inc, exc, PathFilter.Empty);
                var resolved = FtpFrameworkPresetHelper.ResolveAllExclusions(patterns, preset, root);
                var filter = resolved.Count > 0 ? new PathFilter(resolved) : PathFilter.Empty;
                var kept = FtpComparisonEngine.ScanLocalFiles(root, inc, exc, filter);
                var sample = string.Join(Environment.NewLine, kept.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(300).Select(k => "+ " + k));
                if (kept.Count > 300) sample += Environment.NewLine + $"… e mais {kept.Count - 300} arquivo(s)";
                return (Kept: kept.Count, Ignored: all.Count - kept.Count, Bytes: kept.Values.Sum(f => f.Length), Sample: sample);
            });
            _previewIncluded = result.Kept; _previewIgnored = result.Ignored; _previewBytes = result.Bytes; _previewText = result.Sample;
        }
        catch (Exception ex) { _previewText = $"Não foi possível calcular a prévia: {ex.Message}"; }
        RaisePreview();
    }

    private void RaisePreview()
    {
        foreach (var n in new[] { nameof(PreviewIncluded), nameof(PreviewIgnored), nameof(PreviewText), nameof(PreviewSummary), nameof(ReviewSections), nameof(ReviewWarnings) })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------------ revisão (dados reais)
    public IReadOnlyList<ReviewSection> ReviewSections
    {
        get
        {
            var proto = SelectedProfile == null ? "—" : ProtocolLabel(SelectedProfile.Protocol);
            return
            [
                new("Tarefa", 1,
                [
                    new("Nome", Name), new("Descrição", string.IsNullOrWhiteSpace(Description) ? "(sem descrição)" : Description),
                    new("Conexão", SelectedProfile == null ? "(nenhuma)" : $"{SelectedProfile.Name} — {proto} — {SelectedProfile.Host}:{SelectedProfile.Port}"),
                ]),
                new("Caminhos", 2,
                [
                    new("Origem local", SourceLocalPath), new("Destino remoto", TargetRemotePath),
                    new("Pasta local", string.IsNullOrEmpty(_localCheckText) ? "Não verificada." : _localCheckText, _localCheck == PathCheckState.Error),
                    new("Destino no servidor", _remoteCheckText.Length == 0 ? "Ainda não verificado." : _remoteCheckText, _remoteCheck != PathCheckState.Ok),
                ]),
                new("Método", 3,
                [
                    new("Método", SelectedMethodChoice?.Label ?? "—"), new("Comparação", $"{SelectedStrategy.Label} (tolerância {TimestampToleranceSeconds}s)"),
                ]),
                new("Escopo persistente", 4,
                [
                    new("Incluídos", _selection.Includes.Count == 0 ? "(nenhum)" : string.Join(", ", _selection.Includes), _selection.Includes.Count == 0),
                    new("Exceções por caminho", _selection.Excludes.Count == 0 ? "(nenhuma)" : string.Join(", ", _selection.Excludes)),
                    new("Resultado da prévia", PreviewSummary),
                ]),
                new("Exclusões e proteções", 5,
                [
                    new("Padrões de exclusão", _task.ExclusionPatterns.Count == 0 ? "(nenhum)" : string.Join(", ", _task.ExclusionPatterns)),
                    new("Checklist com aprovação", "obrigatório"),
                    new("Backup antes de sobrescrever", BackupBeforeOverwrite ? "sim" : "não"),
                    new("Proibir exclusões remotas", ProhibitRemoteDeletions ? "sim" : "não", !ProhibitRemoteDeletions),
                    new("Upload atômico", UseAtomicUpload ? "sim" : "não"),
                ]),
            ];
        }
    }

    public IReadOnlyList<string> ReviewWarnings
    {
        get
        {
            var w = new List<string>();
            if (!_selection.HasSelection) w.Add("Nenhuma pasta ou arquivo selecionado.");
            if (_remoteCheck != PathCheckState.Ok) w.Add("O destino remoto ainda não foi confirmado no servidor.");
            if (_localCheck == PathCheckState.Error) w.Add("A pasta local de origem não está acessível.");
            if (IsMirror && !ProhibitRemoteDeletions) w.Add("Espelhamento com exclusões remotas permitidas: arquivos do servidor poderão ser excluídos após confirmação no checklist.");
            if (IsMirror && ProhibitRemoteDeletions) w.Add("Espelhamento com exclusões remotas bloqueadas: arquivos órfãos no servidor serão apenas mantidos.");
            if (IsDownload) w.Add("Método de download: arquivos locais poderão ser substituídos pelos do servidor.");
            if (SelectedProfile?.Protocol == FtpProtocol.FtpPlain) w.Add("A conexão usa FTP sem criptografia: credenciais e arquivos trafegam em texto puro.");
            if (SensitiveIsRisk) w.Add(SensitiveWarning);
            return w;
        }
    }

    // ------------------------------------------------------------------ persistência
    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _connectionService.GetAllAsync();
            AvailableProfiles.Clear();
            foreach (var p in profiles) AvailableProfiles.Add(p);

            if (_task.ConnectionProfileId != Guid.Empty) SelectedProfile = AvailableProfiles.FirstOrDefault(p => p.Id == _task.ConnectionProfileId);
            else if (AvailableProfiles.Count == 1) SelectedProfile = AvailableProfiles.First();
        }
        catch (Exception ex)
        {
            _errorField = string.Empty; _validationStep = Step;
            ValidationMessage = $"Erro ao carregar as conexões: {ex.Message}";
        }
    }

    private async Task ExecuteTestWebhookAsync()
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl)) { PresetMessage = "Informe a URL do webhook antes do teste de disparo."; return; }
        if (_notificationService == null) { PresetMessage = "Serviço de notificação indisponível."; return; }
        try
        {
            await _notificationService.SendTestWebhookAsync(WebhookUrl, WebhookType, default);
            PresetMessage = $"Notificação de teste enviada ao canal {WebhookType}.";
        }
        catch (Exception ex) { PresetMessage = $"Falha ao disparar o webhook: {ex.Message}"; }
    }

    private void SyncSelectionToTask()
    {
        _task.IncludedRelativePaths = [.. _selection.Includes];
        _task.ExcludedRelativePaths = [.. _selection.Excludes];
        _task.ExclusionPatterns = _patternEntries.Select(e => e.Text.Trim()).Where(t => t.Length > 0).ToList();
    }

    private async Task ExecuteSaveAsync()
    {
        for (var s = 1; s <= 4; s++)
        {
            if (!TryValidate(s)) { Step = s; TryValidate(s); return; }
        }
        ClearValidation();

        IsSaving = true;
        try
        {
            // caminho remoto: informa (e exige confirmação) se o servidor não confirmar a pasta de destino
            if (!_remoteUnverifiedAcknowledged && _directoryBrowser != null && SelectedProfile != null)
            {
                if (_remoteCheck != PathCheckState.Ok) await CheckRemoteAsync();
                if (_remoteCheck != PathCheckState.Ok)
                {
                    _remoteUnverifiedAcknowledged = true;
                    _errorField = string.Empty; _validationStep = 2;
                    ValidationMessage = $"{_remoteCheckText} Corrija o caminho ou clique em Salvar tarefa novamente para gravar mesmo assim (destino NÃO confirmado).";
                    RefreshSteps();
                    return;
                }
            }

            SyncSelectionToTask();
            _task.ConnectionProfileId = SelectedProfile!.Id;
            await _taskService.SaveAsync(_task);
            OnSaved?.Invoke();
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _errorField = string.Empty; _validationStep = Step;
            ValidationMessage = $"Erro ao salvar tarefa FTP: {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}
