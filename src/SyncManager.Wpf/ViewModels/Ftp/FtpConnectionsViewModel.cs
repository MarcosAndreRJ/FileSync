using System.Collections.ObjectModel;
using System.Windows;
using SyncManager.Application.Ftp;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public sealed class FtpConnectionsViewModel : ObservableObject
{
    private readonly IFtpConnectionService _connectionService;
    private readonly Func<FtpConnectionProfile, FtpConnectionEditViewModel> _editViewModelFactory;
    private readonly Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> _diagnosticsViewModelFactory;

    private FtpConnectionProfile? _selectedConnection;
    private bool _isLoading;
    private string _filterText = string.Empty;

    public ObservableCollection<FtpConnectionProfile> Connections { get; } = new();

    public FtpConnectionProfile? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedConnection != null;

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
                _ = RefreshConnectionsAsync();
            }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewConnectionCommand { get; }
    public RelayCommand EditConnectionCommand { get; }
    public RelayCommand DeleteConnectionCommand { get; }
    public RelayCommand TestConnectionCommand { get; }

    public event Action<FtpConnectionEditViewModel>? RequestEditConnectionDialog;
    public event Action<FtpDiagnosticsViewModel>? RequestDiagnosticsDialog;

    public FtpConnectionsViewModel(
        IFtpConnectionService connectionService,
        Func<FtpConnectionProfile, FtpConnectionEditViewModel> editViewModelFactory,
        Func<FtpConnectionProfile, string, FtpDiagnosticsViewModel> diagnosticsViewModelFactory)
    {
        _connectionService = connectionService;
        _editViewModelFactory = editViewModelFactory;
        _diagnosticsViewModelFactory = diagnosticsViewModelFactory;

        RefreshCommand = new RelayCommand(async () => await RefreshConnectionsAsync());
        NewConnectionCommand = new RelayCommand(ExecuteNewConnection);
        EditConnectionCommand = new RelayCommand(ExecuteEditConnection, () => HasSelection);
        DeleteConnectionCommand = new RelayCommand(async () => await ExecuteDeleteConnectionAsync(), () => HasSelection);
        TestConnectionCommand = new RelayCommand(async () => await ExecuteTestConnectionAsync(), () => HasSelection);
    }

    public async Task InitializeAsync()
    {
        await RefreshConnectionsAsync();
    }

    public Task LoadConnectionsAsync() => RefreshConnectionsAsync();

    public async Task RefreshConnectionsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _connectionService.GetAllAsync();
            Connections.Clear();

            var filtered = string.IsNullOrWhiteSpace(FilterText)
                ? list
                : list.Where(c => c.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                  c.Host.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

            foreach (var item in filtered.OrderBy(c => c.Name))
            {
                Connections.Add(item);
            }
        }
        catch (Exception ex)
        {
            if (System.Windows.Application.Current != null)
            {
                MessageBox.Show($"Falha ao carregar conexões FTP: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteNewConnection()
    {
        var profile = new FtpConnectionProfile();
        var vm = _editViewModelFactory(profile);
        vm.OnSaved += async () => await RefreshConnectionsAsync();
        RequestEditConnectionDialog?.Invoke(vm);
    }

    private void ExecuteEditConnection()
    {
        if (SelectedConnection == null) return;
        var vm = _editViewModelFactory(SelectedConnection.Clone());
        vm.OnSaved += async () => await RefreshConnectionsAsync();
        RequestEditConnectionDialog?.Invoke(vm);
    }

    private async Task ExecuteDeleteConnectionAsync()
    {
        if (SelectedConnection == null) return;

        var result = MessageBox.Show(
            $"Deseja realmente excluir a conexão FTP \"{SelectedConnection.Name}\"?\nAs senhas salvas no cofre DPAPI do Windows também serão removidas.",
            "Confirmar Exclusão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await _connectionService.DeleteAsync(SelectedConnection.Id);
                await RefreshConnectionsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao excluir perfil: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task ExecuteTestConnectionAsync()
    {
        if (SelectedConnection == null) return;

        var password = await _connectionService.GetPasswordAsync(SelectedConnection.Id);
        var diagVm = _diagnosticsViewModelFactory(SelectedConnection, password);
        RequestDiagnosticsDialog?.Invoke(diagVm);
    }
}
