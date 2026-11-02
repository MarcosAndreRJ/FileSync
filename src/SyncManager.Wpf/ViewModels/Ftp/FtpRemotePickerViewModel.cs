using System.Collections.ObjectModel;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

/// <summary>
/// Navegador FTP: lista diretórios remotos reais (e, opcionalmente, a origem local ao lado) com carga sob demanda,
/// estados de carregamento e erro. Em modo de seleção devolve o caminho remoto escolhido.
/// </summary>
public sealed class FtpRemotePickerViewModel : ObservableObject
{
    private readonly IFtpDirectoryBrowser _browser;
    private readonly FtpConnectionProfile _profile;
    private readonly string? _password;
    private string _currentPath;
    private bool _isLoading;
    private string _message = string.Empty;
    private FsNode? _selectedNode;
    private CancellationTokenSource? _cts;

    public FtpRemotePickerViewModel(IFtpDirectoryBrowser browser, FtpConnectionProfile profile, string startPath, string? localPath = null, string? password = null)
    {
        _password = password;
        _browser = browser;
        _profile = profile;
        _currentPath = Normalize(startPath);
        LocalPath = localPath ?? string.Empty;

        RefreshCommand = new RelayCommand(async () => await LoadAsync());
        GoCommand = new RelayCommand(async () => await LoadAsync());
        UpCommand = new RelayCommand(async () => { CurrentPath = ParentOf(CurrentPath); await LoadAsync(); }, () => CurrentPath != "/");
        ConfirmCommand = new RelayCommand(() => { Picked?.Invoke(CurrentPath); RequestClose?.Invoke(true); }, () => !string.IsNullOrWhiteSpace(CurrentPath));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public string Title => $"Navegador FTP — {_profile.Name}";
    public string ServerInfo => $"Servidor: {_profile.Host}:{_profile.Port} · {_profile.Protocol}";
    public string LocalPath { get; }
    public bool HasLocalPane => !string.IsNullOrEmpty(LocalPath);
    /// <summary>Seleção de caminho (assistente de tarefas) mostra "Usar este caminho"; o navegador livre apenas explora.</summary>
    public bool IsPickerMode { get; init; }

    public ObservableCollection<FsNode> RemoteRoots { get; } = new();
    public ObservableCollection<FsNode> LocalRoots { get; } = new();

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string Message
    {
        get => _message;
        private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public FsNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            if (value is { IsDirectory: true }) CurrentPath = value.FullPath;
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand GoCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action<string>? Picked;
    public event Action<bool>? RequestClose;

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        Message = string.Empty;
        RemoteRoots.Clear();
        CurrentPath = Normalize(CurrentPath);
        try
        {
            var roots = await FsNode.LoadRemoteRootAsync(_browser, _profile, CurrentPath, token, _password);
            if (token.IsCancellationRequested) return;
            foreach (var r in roots) RemoteRoots.Add(r);
            if (roots.Count == 0) Message = "Diretório vazio.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Message = $"Falha ao listar \"{CurrentPath}\": {ex.Message}";
        }
        finally { IsLoading = false; }

        if (HasLocalPane && LocalRoots.Count == 0)
        {
            try
            {
                foreach (var r in await FsNode.LoadLocalRootAsync(LocalPath, null)) LocalRoots.Add(r);
            }
            catch (Exception ex) { Message = (HasMessage ? Message + " " : string.Empty) + $"Origem local: {ex.Message}"; }
        }
    }

    private static string Normalize(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (p.Length == 0) return "/";
        if (!p.StartsWith('/')) p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static string ParentOf(string path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }
}
