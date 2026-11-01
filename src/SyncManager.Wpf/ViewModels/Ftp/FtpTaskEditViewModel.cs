using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Infrastructure.Ftp;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class FtpTaskEditViewModel : ObservableObject
{
    private readonly IFtpTaskService _taskService;
    private readonly IFtpConnectionService _connectionService;
    private readonly IFtpNotificationService? _notificationService;

    private FtpSyncTask _task;
    private FtpConnectionProfile? _selectedProfile;
    private string _excludePatternsText = string.Empty;
    private bool _isSaving;

    public FtpSyncTask Task
    {
        get => _task;
        set => SetProperty(ref _task, value);
    }

    public string Name
    {
        get => _task.Name;
        set
        {
            if (_task.Name != value)
            {
                _task.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<FtpConnectionProfile> AvailableProfiles { get; } = new();

    public FtpConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                if (value != null)
                {
                    _task.ConnectionProfileId = value.Id;
                    if (string.IsNullOrWhiteSpace(_task.TargetRemotePath) && !string.IsNullOrWhiteSpace(value.DefaultRemotePath))
                    {
                        TargetRemotePath = value.DefaultRemotePath;
                    }
                    if (string.IsNullOrWhiteSpace(_task.SourceLocalPath) && !string.IsNullOrWhiteSpace(value.DefaultLocalPath))
                    {
                        SourceLocalPath = value.DefaultLocalPath;
                    }
                }
            }
        }
    }

    public string SourceLocalPath
    {
        get => _task.SourceLocalPath;
        set
        {
            if (_task.SourceLocalPath != value)
            {
                _task.SourceLocalPath = value;
                OnPropertyChanged();
            }
        }
    }

    public string TargetRemotePath
    {
        get => _task.TargetRemotePath;
        set
        {
            if (_task.TargetRemotePath != value)
            {
                _task.TargetRemotePath = value;
                OnPropertyChanged();
            }
        }
    }

    public FtpSyncMethod SyncMethod
    {
        get => _task.SyncMethod;
        set
        {
            if (_task.SyncMethod != value)
            {
                _task.SyncMethod = value;
                OnPropertyChanged();
            }
        }
    }

    public FtpComparisonStrategy ComparisonStrategy
    {
        get => _task.ComparisonStrategy;
        set
        {
            if (_task.ComparisonStrategy != value)
            {
                _task.ComparisonStrategy = value;
                OnPropertyChanged();
            }
        }
    }

    public int TimestampToleranceSeconds
    {
        get => _task.TimestampToleranceSeconds;
        set
        {
            if (_task.TimestampToleranceSeconds != value)
            {
                _task.TimestampToleranceSeconds = value;
                OnPropertyChanged();
            }
        }
    }

    public string ExcludePatternsText
    {
        get => _excludePatternsText;
        set
        {
            if (SetProperty(ref _excludePatternsText, value))
            {
                _task.ExcludePatterns = value
                    .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
            }
        }
    }

    // --- 3 Travas de Segurança Mandatórias ---
    public bool ProhibitRemoteDeletions
    {
        get => _task.ProhibitRemoteDeletions;
        set
        {
            if (_task.ProhibitRemoteDeletions != value)
            {
                _task.ProhibitRemoteDeletions = value;
                OnPropertyChanged();
            }
        }
    }

    public double MaxRemoteDeletePercentage
    {
        get => _task.MaxRemoteDeletePercentage;
        set
        {
            if (Math.Abs(_task.MaxRemoteDeletePercentage - value) > 0.001)
            {
                _task.MaxRemoteDeletePercentage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool RequireExplicitChecklistConfirmation
    {
        get => _task.RequireExplicitChecklistConfirmation;
        set
        {
            if (_task.RequireExplicitChecklistConfirmation != value)
            {
                _task.RequireExplicitChecklistConfirmation = value;
                OnPropertyChanged();
            }
        }
    }

    // --- Predefinições de Framework / CMS ---
    public WebFrameworkPreset FrameworkPreset
    {
        get => _task.FrameworkPreset;
        set
        {
            if (_task.FrameworkPreset != value)
            {
                _task.FrameworkPreset = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FrameworkPresetDescription));
            }
        }
    }

    public string FrameworkPresetDescription => FtpFrameworkPresetHelper.GetPresetDescription(FrameworkPreset);

    // --- Safe Deploy & Modo de Manutenção ---
    public bool UseAtomicUpload
    {
        get => _task.UseAtomicUpload;
        set
        {
            if (_task.UseAtomicUpload != value)
            {
                _task.UseAtomicUpload = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableMaintenanceMode
    {
        get => _task.EnableMaintenanceMode;
        set
        {
            if (_task.EnableMaintenanceMode != value)
            {
                _task.EnableMaintenanceMode = value;
                OnPropertyChanged();
            }
        }
    }

    public string MaintenanceFileName
    {
        get => _task.MaintenanceFileName ?? string.Empty;
        set
        {
            if (_task.MaintenanceFileName != value)
            {
                _task.MaintenanceFileName = value;
                OnPropertyChanged();
            }
        }
    }

    // --- Webhooks & Notificações ---
    public bool NotifyOnCompletion
    {
        get => _task.NotifyOnCompletion;
        set
        {
            if (_task.NotifyOnCompletion != value)
            {
                _task.NotifyOnCompletion = value;
                OnPropertyChanged();
            }
        }
    }

    public string WebhookUrl
    {
        get => _task.WebhookUrl ?? string.Empty;
        set
        {
            if (_task.WebhookUrl != value)
            {
                _task.WebhookUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public FtpWebhookType WebhookType
    {
        get => _task.WebhookType;
        set
        {
            if (_task.WebhookType != value)
            {
                _task.WebhookType = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set => SetProperty(ref _isSaving, value);
    }

    public ObservableCollection<FtpSyncMethod> AvailableSyncMethods { get; } = new()
    {
        FtpSyncMethod.UpdateTarget,
        FtpSyncMethod.MirrorSource,
        FtpSyncMethod.CopyNewOnly,
        FtpSyncMethod.DownloadServer
    };

    public ObservableCollection<FtpComparisonStrategy> AvailableStrategies { get; } = new()
    {
        FtpComparisonStrategy.SizeAndTimestamp,
        FtpComparisonStrategy.SizeOnly,
        FtpComparisonStrategy.ChecksumIfSupported
    };

    public ObservableCollection<WebFrameworkPreset> AvailableFrameworkPresets { get; } = new()
    {
        WebFrameworkPreset.None,
        WebFrameworkPreset.WordPress,
        WebFrameworkPreset.Laravel,
        WebFrameworkPreset.NodeJs,
        WebFrameworkPreset.DotNet,
        WebFrameworkPreset.AngularReactVue
    };

    public ObservableCollection<FtpWebhookType> AvailableWebhookTypes { get; } = new()
    {
        FtpWebhookType.Discord,
        FtpWebhookType.Slack,
        FtpWebhookType.MicrosoftTeams,
        FtpWebhookType.GenericJson
    };

    public RelayCommand BrowseLocalFolderCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ApplyPresetExclusionsCommand { get; }
    public RelayCommand TestWebhookCommand { get; }

    public event Action? OnSaved;
    public event Action? RequestClose;

    public FtpTaskEditViewModel(
        FtpSyncTask task,
        IFtpTaskService taskService,
        IFtpConnectionService connectionService,
        IFtpNotificationService? notificationService = null)
    {
        _task = task;
        _taskService = taskService;
        _connectionService = connectionService;
        _notificationService = notificationService;

        _excludePatternsText = string.Join(Environment.NewLine, _task.ExcludePatterns);

        BrowseLocalFolderCommand = new RelayCommand(ExecuteBrowseLocalFolder);
        SaveCommand = new RelayCommand(async () => await ExecuteSaveAsync());
        ApplyPresetExclusionsCommand = new RelayCommand(ExecuteApplyPresetExclusions);
        TestWebhookCommand = new RelayCommand(async () => await ExecuteTestWebhookAsync());

        _ = LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        if (_connectionService == null) return;

        try
        {
            var profiles = await _connectionService.GetAllAsync();
            AvailableProfiles.Clear();
            foreach (var p in profiles)
            {
                AvailableProfiles.Add(p);
            }

            if (_task.ConnectionProfileId != Guid.Empty)
            {
                SelectedProfile = AvailableProfiles.FirstOrDefault(p => p.Id == _task.ConnectionProfileId);
            }
            else if (AvailableProfiles.Any())
            {
                SelectedProfile = AvailableProfiles.First();
            }
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Erro ao carregar perfis de conexão: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteBrowseLocalFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecione a Pasta Local de Origem",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(SourceLocalPath) && Directory.Exists(SourceLocalPath))
        {
            dialog.InitialDirectory = SourceLocalPath;
        }

        if (dialog.ShowDialog() == true)
        {
            SourceLocalPath = dialog.FolderName;
        }
    }

    private void ExecuteApplyPresetExclusions()
    {
        if (FrameworkPreset == WebFrameworkPreset.None)
        {
            ShowMessageBox("Selecione uma predefinição de framework válida (WordPress, Laravel, Node.js, .NET ou SPA) para aplicar suas regras recomendadas.",
                "Predefinições de Framework", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var presetPatterns = FtpFrameworkPresetHelper.GetPresetExclusionPatterns(FrameworkPreset);
        var currentPatterns = ExcludePatternsText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        int addedCount = 0;
        foreach (var p in presetPatterns)
        {
            if (!currentPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                currentPatterns.Add(p);
                addedCount++;
            }
        }

        ExcludePatternsText = string.Join(Environment.NewLine, currentPatterns);

        ShowMessageBox($"Foram adicionadas {addedCount} regras de exclusão recomendadas para {FrameworkPreset}.",
            "Predefinição Aplicada", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ExecuteTestWebhookAsync()
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl))
        {
            ShowMessageBox("Informe a URL do Webhook antes de efetuar o teste de disparo.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_notificationService == null)
        {
            ShowMessageBox("Serviço de notificação não inicializado.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            await _notificationService.SendTestWebhookAsync(WebhookUrl, WebhookType, default);
            ShowMessageBox($"Notificação de teste enviada com sucesso para o canal {WebhookType}!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Falha ao disparar Webhook de teste: {ex.Message}", "Erro de Disparo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExecuteSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ShowMessageBox("Informe o nome da tarefa de sincronização.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedProfile == null)
        {
            ShowMessageBox("Selecione um perfil de conexão FTP.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceLocalPath))
        {
            ShowMessageBox("Informe o diretório local de origem.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetRemotePath))
        {
            ShowMessageBox("Informe o diretório remoto de destino.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsSaving = true;
        try
        {
            await _taskService.SaveAsync(_task);
            OnSaved?.Invoke();
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Erro ao salvar tarefa FTP: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static void ShowMessageBox(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (System.Windows.Application.Current != null)
        {
            MessageBox.Show(message, caption, button, icon);
        }
    }
}
