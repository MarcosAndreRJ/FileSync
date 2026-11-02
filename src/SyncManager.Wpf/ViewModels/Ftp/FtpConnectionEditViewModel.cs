using System.Collections.ObjectModel;
using System.Windows;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed record ProtocolOption(FtpProtocol Protocol, string Label);
public sealed record TransferModeOption(FtpTransferMode Mode, string Label);

/// <summary>Assistente guiado de conexão FTP/FTPS em três etapas: Geral, Segurança e Testar conexão.</summary>
public sealed class FtpConnectionEditViewModel : ObservableObject
{
    public const int LastStep = 3;

    private readonly IFtpConnectionService _connectionService;
    private readonly Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> _diagnosticsViewModelFactory;
    private readonly IFtpNotificationService? _notificationService;
    private readonly Func<FtpConnectionProfile, string, FtpRemotePickerViewModel>? _browserFactory;
    private readonly bool _isNew;

    private FtpConnectionProfile _profile;
    private string _password = string.Empty;
    private bool _isPasswordModified;
    private bool _isSaving;
    private int _step = 1;
    private string _validationMessage = string.Empty;
    private int _validationStep;
    private FtpDiagnosticsViewModel? _diagnostics;
    private string? _testedSignature;
    private bool _unverifiedConfirmed;

    public FtpConnectionEditViewModel(
        FtpConnectionProfile profile,
        IFtpConnectionService connectionService,
        Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> diagnosticsViewModelFactory,
        IFtpNotificationService? notificationService = null,
        Func<FtpConnectionProfile, string, FtpRemotePickerViewModel>? browserFactory = null)
    {
        _profile = profile;
        _isNew = string.IsNullOrEmpty(profile.CredentialIdentifier);
        _connectionService = connectionService;
        _diagnosticsViewModelFactory = diagnosticsViewModelFactory;
        _notificationService = notificationService;
        _browserFactory = browserFactory;

        Steps = new ObservableCollection<WizardStepItem>
        {
            new(1, "Geral"), new(2, "Segurança"), new(3, "Testar conexão")
        };
        RefreshSteps();

        BackCommand = new RelayCommand(GoBack, () => Step > 1);
        NextCommand = new RelayCommand(GoNext, () => Step < LastStep);
        NavigateCommand = new RelayCommand<int>(NavigateTo);
        CancelCommand = new RelayCommand(() => RequestCancel?.Invoke());
        RunTestCommand = new RelayCommand(async () => await RunTestAsync());
        OpenBrowserCommand = new RelayCommand(OpenBrowser, () => TestPassed && _browserFactory != null);
        SaveCommand = new RelayCommand(async () => await ExecuteSaveAsync());
        TestDefaultWebhookCommand = new RelayCommand(async () => await ExecuteTestDefaultWebhookAsync());

        _ = LoadExistingPasswordAsync();
    }

    // ------------------------------------------------------------------ dados do perfil
    public FtpConnectionProfile Profile { get => _profile; set => SetProperty(ref _profile, value); }

    public string Name { get => _profile.Name; set { if (_profile.Name != value) { _profile.Name = value; Touched(); } } }
    public string Host { get => _profile.Host; set { if (_profile.Host != value) { _profile.Host = value; Touched(); } } }
    public int Port { get => _profile.Port; set { if (_profile.Port != value) { _profile.Port = value; Touched(); } } }
    public string Username { get => _profile.Username; set { if (_profile.Username != value) { _profile.Username = value; Touched(); } } }

    public string DefaultRemotePath
    {
        get => _profile.DefaultRemotePath;
        set { if (_profile.DefaultRemotePath != value) { _profile.DefaultRemotePath = value; Touched(); } }
    }

    public FtpProtocol Protocol
    {
        get => _profile.Protocol;
        set
        {
            if (_profile.Protocol == value) return;
            _profile.Protocol = value;
            if (value == FtpProtocol.FtpsImplicit && Port == 21) Port = 990;
            else if (value != FtpProtocol.FtpsImplicit && Port == 990) Port = 21;
            OnPropertyChanged(nameof(SelectedProtocol));
            OnPropertyChanged(nameof(IsPlainFtp));
            OnPropertyChanged(nameof(ProtocolRecommendation));
            Touched();
        }
    }

    public IReadOnlyList<ProtocolOption> ProtocolOptions { get; } =
    [
        new(FtpProtocol.FtpsExplicit, "FTPS explícito (TLS)"),
        new(FtpProtocol.FtpsImplicit, "FTPS implícito (porta 990)"),
        new(FtpProtocol.FtpPlain, "FTP sem criptografia (inseguro)")
    ];

    public ProtocolOption SelectedProtocol
    {
        get => ProtocolOptions.First(o => o.Protocol == Protocol);
        set { if (value != null) Protocol = value.Protocol; }
    }

    public bool IsPlainFtp => Protocol == FtpProtocol.FtpPlain;

    public string ProtocolRecommendation => IsPlainFtp
        ? "Atenção: FTP sem TLS envia usuário, senha e arquivos em texto puro pela rede. Use somente em redes confiáveis; prefira FTPS."
        : "Utilize FTPS para criptografar as transferências. FTP sem TLS não protege as credenciais em trânsito.";

    public string Password
    {
        get => _password;
        set { if (SetProperty(ref _password, value)) { _isPasswordModified = true; Touched(); } }
    }

    public FtpTransferMode TransferMode
    {
        get => _profile.TransferMode;
        set { if (_profile.TransferMode != value) { _profile.TransferMode = value; OnPropertyChanged(nameof(SelectedTransferMode)); Touched(); } }
    }

    public IReadOnlyList<TransferModeOption> TransferModeOptions { get; } =
    [
        new(FtpTransferMode.Passive, "Passivo (PASV) — recomendado com firewall/NAT"),
        new(FtpTransferMode.Active, "Ativo (PORT)")
    ];

    public TransferModeOption SelectedTransferMode
    {
        get => TransferModeOptions.First(o => o.Mode == TransferMode);
        set { if (value != null) TransferMode = value.Mode; }
    }

    public bool RequireTls12OrHigher { get => _profile.RequireTls12OrHigher; set { if (_profile.RequireTls12OrHigher != value) { _profile.RequireTls12OrHigher = value; Touched(); } } }

    public bool CertificatePinning
    {
        get => _profile.CertificatePinning;
        set { if (_profile.CertificatePinning != value) { _profile.CertificatePinning = value; Touched(); } }
    }

    public string? ExpectedCertificateThumbprint
    {
        get => _profile.ExpectedCertificateThumbprint;
        set { if (_profile.ExpectedCertificateThumbprint != value) { _profile.ExpectedCertificateThumbprint = value; Touched(); } }
    }

    public int TimeoutSeconds { get => _profile.TimeoutSeconds; set { if (_profile.TimeoutSeconds != value) { _profile.TimeoutSeconds = value; Touched(); } } }
    public int MaxParallelConnections { get => _profile.MaxParallelConnections; set { if (_profile.MaxParallelConnections != value) { _profile.MaxParallelConnections = value; Touched(); } } }
    public bool PreserveModTime { get => _profile.PreserveModTime; set { if (_profile.PreserveModTime != value) { _profile.PreserveModTime = value; Touched(); } } }
    public bool SendKeepAlive { get => _profile.SendKeepAlive; set { if (_profile.SendKeepAlive != value) { _profile.SendKeepAlive = value; Touched(); } } }

    public string DefaultWebhookUrl
    {
        get => _profile.DefaultWebhookUrl ?? string.Empty;
        set { if (_profile.DefaultWebhookUrl != value) { _profile.DefaultWebhookUrl = value; Touched(); } }
    }

    public FtpWebhookType DefaultWebhookType
    {
        get => _profile.DefaultWebhookType;
        set { if (_profile.DefaultWebhookType != value) { _profile.DefaultWebhookType = value; Touched(); } }
    }

    public ObservableCollection<FtpWebhookType> AvailableWebhookTypes { get; } = new()
    {
        FtpWebhookType.Discord, FtpWebhookType.Slack, FtpWebhookType.MicrosoftTeams, FtpWebhookType.GenericJson
    };

    public bool IsSaving { get => _isSaving; set => SetProperty(ref _isSaving, value); }

    /// <summary>Qualquer alteração de parâmetro invalida o teste anterior e a confirmação de salvar sem validação.</summary>
    private void Touched([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        _unverifiedConfirmed = false;
        OnPropertyChanged(nameof(TestPassed));
        OnPropertyChanged(nameof(TestStatusText));
        OnPropertyChanged(nameof(SaveButtonText));
        OpenBrowserCommand.RaiseCanExecuteChanged();
        if (_validationStep is 1 or 2)
        {
            var (m, f) = ValidateStepCore(_validationStep);
            if (m == null || f != _errorField) ClearValidation();   // o campo apontado foi corrigido
        }
    }

    // ------------------------------------------------------------------ assistente
    public ObservableCollection<WizardStepItem> Steps { get; }

    public int Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            foreach (var n in new[] { nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(StepTitle), nameof(IsLastStep), nameof(IsNotLastStep),
                         nameof(StepLabel), nameof(StepHeading), nameof(StepDescription), nameof(FooterNote), nameof(NextButtonText) })
                OnPropertyChanged(n);
            RefreshSteps();
            BackCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsStep1 => _step == 1;
    public bool IsStep2 => _step == 2;
    public bool IsStep3 => _step == 3;
    public bool IsLastStep => _step == LastStep;
    public bool IsNotLastStep => _step != LastStep;
    public string StepTitle => Steps[_step - 1].Title;
    public string StepLabel => $"ETAPA {_step} DE {LastStep}";

    public string StepHeading => _step switch { 1 => "Dados da conexão", 2 => "Segurança e autenticação", _ => TestHeading };
    public string StepDescription => _step switch
    {
        1 => "Defina um nome que permita reutilizar esta conexão em diferentes tarefas.",
        2 => "Informe as credenciais e as proteções aplicadas à sessão. A senha é guardada com a proteção do Windows, fora do banco de dados.",
        _ => "Os testes abaixo verificam resolução de nome, criptografia, autenticação e leitura do diretório remoto, usando exatamente os dados informados."
    };

    public string WindowTitle => _isNew ? "Nova conexão FTP" : "Editar conexão FTP";
    public string WindowSubtitle => _step switch
    {
        1 => "Informações básicas do servidor remoto",
        2 => "Autenticação e segurança",
        _ => "Verificação de acesso antes de salvar"
    };
    public string FooterNote => _step switch
    {
        1 => "Os dados são guardados somente ao final do assistente.",
        2 => "A validação do certificado TLS é sempre aplicada: certificados inválidos são recusados.",
        _ => TestPassed ? "Conexão validada com os parâmetros atuais." : "Salvar sem testar registra a conexão como NÃO validada."
    };
    public string NextButtonText => _step == 2 ? "Testar conexão →" : "Avançar →";

    public string ValidationMessage
    {
        get => _validationMessage;
        private set { if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage)); }
    }
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    /// <summary>Etapa (1-3) a que a mensagem de validação visível pertence; 0 = nenhuma. Usada para mensagens próximas aos campos.</summary>
    public int ValidationStep => _validationStep;

    private void ClearValidation()
    {
        _validationStep = 0;
        ValidationMessage = string.Empty;
        OnPropertyChanged(nameof(ValidationStep));
        OnPropertyChanged(nameof(HasNameError)); OnPropertyChanged(nameof(HasHostError)); OnPropertyChanged(nameof(HasUserError)); OnPropertyChanged(nameof(HasAdvancedError));
    }

    private string _errorField = string.Empty;
    public bool HasNameError => HasValidationMessage && _errorField == nameof(Name);
    public bool HasHostError => HasValidationMessage && _errorField is nameof(Host) or nameof(Port);
    public bool HasUserError => HasValidationMessage && _errorField == nameof(Username);
    public bool HasAdvancedError => HasValidationMessage && _errorField is nameof(TimeoutSeconds) or nameof(MaxParallelConnections) or nameof(ExpectedCertificateThumbprint);

    private void RefreshSteps()
    {
        foreach (var s in Steps)
        {
            if (s.Number == _step) s.State = WizardStepState.Current;
            else if (_validationStep == s.Number && HasValidationMessage) s.State = WizardStepState.Error;
            else if (s.Number < _step && ValidateStep(s.Number) == null) s.State = WizardStepState.Done;
            else if (s.Number == 3 && TestPassed) s.State = WizardStepState.Done;
            else s.State = WizardStepState.Pending;
        }
    }

    /// <summary>Valida somente os campos da etapa; devolve a mensagem de erro ou null.</summary>
    public string? ValidateStep(int step) => ValidateStepCore(step).Message;

    private (string? Message, string Field) ValidateStepCore(int step)
    {
        if (step == 1)
        {
            if (string.IsNullOrWhiteSpace(Name)) return ("Informe o nome da conexão.", nameof(Name));
            if (string.IsNullOrWhiteSpace(Host)) return ("Informe o servidor (host ou IP).", nameof(Host));
            if (Host.Contains(' ') || Host.Contains("://")) return ("O servidor deve ser apenas o nome ou IP, sem protocolo (ftp://) nem espaços.", nameof(Host));
            if (Port is < 1 or > 65535) return ("A porta deve estar entre 1 e 65535.", nameof(Port));
        }
        if (step == 2)
        {
            if (string.IsNullOrWhiteSpace(Username)) return ("Informe o usuário FTP.", nameof(Username));
            if (TimeoutSeconds is < 1 or > 600) return ("O timeout deve estar entre 1 e 600 segundos.", nameof(TimeoutSeconds));
            if (MaxParallelConnections is < 1 or > 10) return ("As conexões simultâneas devem estar entre 1 e 10.", nameof(MaxParallelConnections));
            if (CertificatePinning && string.IsNullOrWhiteSpace(ExpectedCertificateThumbprint))
                return ("Informe o thumbprint esperado do certificado ou desative o Certificate Pinning.", nameof(ExpectedCertificateThumbprint));
        }
        return (null, string.Empty);
    }

    private bool TryValidate(int step)
    {
        var (message, field) = ValidateStepCore(step);
        _errorField = field;
        _validationStep = message == null ? 0 : step;
        ValidationMessage = message ?? string.Empty;
        OnPropertyChanged(nameof(ValidationStep));
        OnPropertyChanged(nameof(HasNameError)); OnPropertyChanged(nameof(HasHostError)); OnPropertyChanged(nameof(HasUserError)); OnPropertyChanged(nameof(HasAdvancedError));
        RefreshSteps();
        return message == null;
    }

    private void GoNext()
    {
        if (Step >= LastStep) return;
        if (!TryValidate(Step)) return;
        var fromSecurity = Step == 2;
        Step++;
        if (fromSecurity) _ = RunTestAsync();   // "Testar conexão →": o diagnóstico real começa ao entrar na etapa 3
    }

    private void GoBack()
    {
        ClearValidation();
        if (Step > 1) Step--;
    }

    /// <summary>Navegação lateral: voltar é livre; avançar exige que todas as etapas anteriores estejam válidas.</summary>
    private void NavigateTo(int target)
    {
        if (target < 1 || target > LastStep || target == Step) return;
        if (target < Step) { ClearValidation(); Step = target; return; }
        for (var s = Step; s < target; s++)
        {
            if (!TryValidate(s)) { Step = s; TryValidate(s); return; }
        }
        Step = target;
    }

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand<int> NavigateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RunTestCommand { get; }
    public RelayCommand OpenBrowserCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand TestDefaultWebhookCommand { get; }

    public event Action? OnSaved;
    public event Action? RequestClose;
    public event Action? RequestCancel;
    public event Action<FtpRemotePickerViewModel>? RequestBrowser;

    // ------------------------------------------------------------------ teste de conexão (etapa 3)
    public FtpDiagnosticsViewModel? Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }

    private string Signature() =>
        string.Join("|", Host, Port, (int)Protocol, Username, Password, (int)TransferMode, RequireTls12OrHigher, CertificatePinning, ExpectedCertificateThumbprint, TimeoutSeconds);

    /// <summary>true somente se o diagnóstico real terminou sem falhas com os parâmetros atuais (mudou algo depois? deixa de valer).</summary>
    public bool TestPassed => Diagnostics is { IsCompleted: true, OverallSuccess: true } && _testedSignature == Signature();
    public bool HasTestResult => Diagnostics is { IsCompleted: true };

    public string TestHeading => Diagnostics == null || !Diagnostics.IsCompleted
        ? (Diagnostics?.IsRunning == true ? "Testando a conexão…" : "Conexão ainda não testada")
        : Diagnostics.OverallSuccess ? "Conectividade confirmada" : "Falha na verificação da conexão";

    public string TestStatusText => Diagnostics == null || (!Diagnostics.IsCompleted && !Diagnostics.IsRunning)
        ? "Ainda não testada — execute o teste para validar o acesso."
        : Diagnostics.IsRunning ? "Executando verificações…"
        : TestPassed ? "Conexão testada com sucesso"
        : Diagnostics.OverallSuccess ? "Parâmetros alterados após o teste — teste novamente."
        : Diagnostics.StatusSummary;

    public string TestGuidance
    {
        get
        {
            if (Diagnostics is not { IsCompleted: true } || Diagnostics.OverallSuccess) return string.Empty;
            var failed = Diagnostics.Steps.FirstOrDefault(s => s.Status == FtpDiagnosticStepStatus.Failed);
            if (failed == null) return string.Empty;
            return failed.StepName switch
            {
                var n when n.Contains("DNS") => "Verifique o nome do servidor e a conexão de rede.",
                var n when n.Contains("TCP") => "Confira a porta, o firewall e se o servidor está acessível.",
                var n when n.Contains("TLS") => "O servidor pode não suportar TLS na versão exigida ou o protocolo escolhido está incorreto (explícito/implícito).",
                var n when n.Contains("ertificado") => "O certificado do servidor não é confiável. Corrija o certificado no servidor ou informe o thumbprint esperado (Certificate Pinning). Certificados inválidos não são aceitos silenciosamente.",
                var n when n.Contains("utenticação") => "Usuário ou senha recusados pelo servidor.",
                var n when n.Contains("Canal") => "Falha no canal de dados: alterne entre modo passivo e ativo, ou revise o firewall.",
                _ => "Revise os parâmetros da etapa anterior e execute o teste novamente."
            };
        }
    }

    public string SaveButtonText => TestPassed ? "Salvar conexão" : (_unverifiedConfirmed ? "Confirmar: salvar sem validação" : "Salvar conexão");

    private async Task RunTestAsync()
    {
        if (!TryValidate(1)) { Step = 1; return; }
        if (!TryValidate(2)) { Step = 2; return; }
        ClearValidation();
        _unverifiedConfirmed = false;
        _testedSignature = Signature();
        Diagnostics = _diagnosticsViewModelFactory(_profile.Clone(), Password);
        Diagnostics.PropertyChanged += (_, _) => RaiseTestState();
        RaiseTestState();
        await Diagnostics.RunDiagnosticsAsync();
        RaiseTestState();
    }

    private void RaiseTestState()
    {
        foreach (var n in new[] { nameof(TestPassed), nameof(HasTestResult), nameof(TestHeading), nameof(TestStatusText), nameof(TestGuidance), nameof(SaveButtonText),
                     nameof(StepHeading), nameof(FooterNote) })
            OnPropertyChanged(n);
        OpenBrowserCommand.RaiseCanExecuteChanged();
        RefreshSteps();
    }

    private void OpenBrowser()
    {
        if (!TestPassed || _browserFactory == null) return;
        RequestBrowser?.Invoke(_browserFactory(_profile.Clone(), Password));
    }

    // ------------------------------------------------------------------ persistência
    private async Task LoadExistingPasswordAsync()
    {
        if (_profile.Id != Guid.Empty && !string.IsNullOrEmpty(_profile.CredentialIdentifier))
        {
            var pwd = await _connectionService.GetPasswordAsync(_profile.Id);
            _password = pwd;
            _isPasswordModified = false;
            OnPropertyChanged(nameof(Password));
        }
    }

    private async Task ExecuteSaveAsync()
    {
        for (var s = 1; s <= 2; s++)
        {
            if (!TryValidate(s)) { Step = s; TryValidate(s); return; }
        }

        // Conexão não validada: exige confirmação consciente (um segundo clique) e nunca a apresenta como testada.
        if (!TestPassed && !_unverifiedConfirmed)
        {
            _unverifiedConfirmed = true;
            Step = 3;
            _errorField = string.Empty;
            _validationStep = 3;
            ValidationMessage = Diagnostics is { IsCompleted: true, OverallSuccess: false }
                ? "O teste falhou. Corrija os parâmetros ou clique novamente para salvar mesmo assim (conexão NÃO validada)."
                : "Esta conexão ainda não foi testada. Execute o teste ou clique novamente para salvar mesmo assim (conexão NÃO validada).";
            OnPropertyChanged(nameof(SaveButtonText));
            RefreshSteps();
            return;
        }

        IsSaving = true;
        try
        {
            string? pwdToSave = _isPasswordModified ? Password : null;
            await _connectionService.SaveAsync(_profile, pwdToSave);
            OnSaved?.Invoke();
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _validationStep = Step;
            ValidationMessage = $"Erro ao salvar conexão FTP: {ex.Message}";
        }
        finally { IsSaving = false; }
    }

    private async Task ExecuteTestDefaultWebhookAsync()
    {
        if (string.IsNullOrWhiteSpace(DefaultWebhookUrl))
        {
            ShowMessageBox("Informe a URL do Webhook padrão antes de realizar o teste.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_notificationService == null)
        {
            ShowMessageBox("Serviço de notificação não inicializado.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        try
        {
            await _notificationService.SendTestWebhookAsync(DefaultWebhookUrl, DefaultWebhookType, default);
            ShowMessageBox($"Notificação de teste enviada com sucesso para o canal {DefaultWebhookType}!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Falha ao disparar Webhook padrão: {ex.Message}", "Erro de Disparo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ShowMessageBox(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (System.Windows.Application.Current != null) MessageBox.Show(message, caption, button, icon);
    }
}
