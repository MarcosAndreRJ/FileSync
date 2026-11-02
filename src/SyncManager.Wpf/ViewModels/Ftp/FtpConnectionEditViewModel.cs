using System.Collections.ObjectModel;
using System.Windows;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class FtpConnectionEditViewModel : ObservableObject
{
    private readonly IFtpConnectionService _connectionService;
    private readonly Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> _diagnosticsViewModelFactory;
    private readonly IFtpNotificationService? _notificationService;

    private FtpConnectionProfile _profile;
    private string _password = string.Empty;
    private bool _isPasswordModified;
    private bool _isSaving;

    public FtpConnectionProfile Profile
    {
        get => _profile;
        set => SetProperty(ref _profile, value);
    }

    public string Name
    {
        get => _profile.Name;
        set
        {
            if (_profile.Name != value)
            {
                _profile.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public string Host
    {
        get => _profile.Host;
        set
        {
            if (_profile.Host != value)
            {
                _profile.Host = value;
                OnPropertyChanged();
            }
        }
    }

    public int Port
    {
        get => _profile.Port;
        set
        {
            if (_profile.Port != value)
            {
                _profile.Port = value;
                OnPropertyChanged();
            }
        }
    }

    public FtpProtocol Protocol
    {
        get => _profile.Protocol;
        set
        {
            if (_profile.Protocol != value)
            {
                _profile.Protocol = value;
                OnPropertyChanged();
                // Ajustar porta padrão caso seja implícito
                if (value == FtpProtocol.FtpsImplicit && Port == 21)
                {
                    Port = 990;
                }
                else if (value != FtpProtocol.FtpsImplicit && Port == 990)
                {
                    Port = 21;
                }
            }
        }
    }

    public string Username
    {
        get => _profile.Username;
        set
        {
            if (_profile.Username != value)
            {
                _profile.Username = value;
                OnPropertyChanged();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                _isPasswordModified = true;
            }
        }
    }

    public FtpTransferMode TransferMode
    {
        get => _profile.TransferMode;
        set
        {
            if (_profile.TransferMode != value)
            {
                _profile.TransferMode = value;
                OnPropertyChanged();
            }
        }
    }

    public bool RequireTls12OrHigher
    {
        get => _profile.RequireTls12OrHigher;
        set
        {
            if (_profile.RequireTls12OrHigher != value)
            {
                _profile.RequireTls12OrHigher = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CertificatePinning
    {
        get => _profile.CertificatePinning;
        set
        {
            if (_profile.CertificatePinning != value)
            {
                _profile.CertificatePinning = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ExpectedCertificateThumbprint
    {
        get => _profile.ExpectedCertificateThumbprint;
        set
        {
            if (_profile.ExpectedCertificateThumbprint != value)
            {
                _profile.ExpectedCertificateThumbprint = value;
                OnPropertyChanged();
            }
        }
    }

    public int TimeoutSeconds
    {
        get => _profile.TimeoutSeconds;
        set
        {
            if (_profile.TimeoutSeconds != value)
            {
                _profile.TimeoutSeconds = value;
                OnPropertyChanged();
            }
        }
    }

    public int MaxParallelConnections
    {
        get => _profile.MaxParallelConnections;
        set
        {
            if (_profile.MaxParallelConnections != value)
            {
                _profile.MaxParallelConnections = value;
                OnPropertyChanged();
            }
        }
    }

    public bool PreserveModTime
    {
        get => _profile.PreserveModTime;
        set
        {
            if (_profile.PreserveModTime != value)
            {
                _profile.PreserveModTime = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SendKeepAlive
    {
        get => _profile.SendKeepAlive;
        set
        {
            if (_profile.SendKeepAlive != value)
            {
                _profile.SendKeepAlive = value;
                OnPropertyChanged();
            }
        }
    }

    public string DefaultWebhookUrl
    {
        get => _profile.DefaultWebhookUrl ?? string.Empty;
        set
        {
            if (_profile.DefaultWebhookUrl != value)
            {
                _profile.DefaultWebhookUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public FtpWebhookType DefaultWebhookType
    {
        get => _profile.DefaultWebhookType;
        set
        {
            if (_profile.DefaultWebhookType != value)
            {
                _profile.DefaultWebhookType = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set => SetProperty(ref _isSaving, value);
    }

    public ObservableCollection<FtpProtocol> AvailableProtocols { get; } = new()
    {
        FtpProtocol.FtpsExplicit,
        FtpProtocol.FtpsImplicit,
        FtpProtocol.FtpPlain
    };

    public ObservableCollection<FtpTransferMode> AvailableTransferModes { get; } = new()
    {
        FtpTransferMode.Passive,
        FtpTransferMode.Active
    };

    public ObservableCollection<FtpWebhookType> AvailableWebhookTypes { get; } = new()
    {
        FtpWebhookType.Discord,
        FtpWebhookType.Slack,
        FtpWebhookType.MicrosoftTeams,
        FtpWebhookType.GenericJson
    };

    public RelayCommand SaveCommand { get; }
    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand TestDefaultWebhookCommand { get; }

    public event Action? OnSaved;
    public event Action? RequestClose;
    public event Action<FtpDiagnosticsViewModel>? RequestDiagnosticsDialog;

    public FtpConnectionEditViewModel(
        FtpConnectionProfile profile,
        IFtpConnectionService connectionService,
        Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> diagnosticsViewModelFactory,
        IFtpNotificationService? notificationService = null)
    {
        _profile = profile;
        _connectionService = connectionService;
        _diagnosticsViewModelFactory = diagnosticsViewModelFactory;
        _notificationService = notificationService;

        SaveCommand = new RelayCommand(async () => await ExecuteSaveAsync());
        TestConnectionCommand = new RelayCommand(ExecuteTestConnection);
        TestDefaultWebhookCommand = new RelayCommand(async () => await ExecuteTestDefaultWebhookAsync());

        _ = LoadExistingPasswordAsync();
    }

    private async Task LoadExistingPasswordAsync()
    {
        if (_profile.Id != Guid.Empty && !string.IsNullOrEmpty(_profile.CredentialIdentifier))
        {
            var pwd = await _connectionService.GetPasswordAsync(_profile.Id);
            Password = pwd;
            _isPasswordModified = false;
        }
    }

    private async Task ExecuteSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ShowMessageBox("Por favor, informe o nome descritivo da conexão.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            ShowMessageBox("Por favor, informe o endereço de Host / IP do servidor FTP.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ShowMessageBox($"Erro ao salvar conexão FTP: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSaving = false;
        }
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

    private void ExecuteTestConnection()
    {
        var tempProfile = _profile.Clone();
        var diagVm = _diagnosticsViewModelFactory(tempProfile, Password);
        RequestDiagnosticsDialog?.Invoke(diagVm);
    }

    private static void ShowMessageBox(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (System.Windows.Application.Current != null)
        {
            MessageBox.Show(message, caption, button, icon);
        }
    }
}
