using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Comparison;
using SyncManager.Core.Execution;
using SyncManager.Core.Planning;
using SyncManager.Core.Scanning;
using SyncManager.Infrastructure;
using SyncManager.Persistence;
using SyncManager.Wpf.Services;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;

namespace SyncManager.Wpf;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = BuildServices(Database.DefaultPath, null);
        Start(_services);
    }

    /// <summary>Composição das dependências. Parametrizada para que os testes usem banco/logs temporários.</summary>
    public static ServiceProvider BuildServices(string databasePath, string? logDirectory, string? cliPath = null)
    {
        var services = new ServiceCollection();
        // Mesmo motor da CLI (agendador, travas, repositórios): a interface só acrescenta ViewModels e diálogos.
        services.AddSyncManagerEngine(databasePath, logDirectory, cliPath);
        services.AddLogging(b => b.AddDebug());
        services.AddTransient<ProfilesViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScheduleViewModel>();
        services.AddTransient<ProfileEditorViewModel>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void Start(ServiceProvider services)
    {
        _services = services;
        DispatcherUnhandledException += (_, args) =>
        {
            _services.GetRequiredService<ILogService>().Write(new SyncManager.Core.Models.LogEntry(
                DateTimeOffset.Now, SyncManager.Core.Enums.LogSeverity.Error, "UNHANDLED", args.Exception.ToString()));
            MessageBox.Show(args.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var startMinimized = false;
        var log = _services.GetRequiredService<ILogService>();
        try
        {
            _services.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
            var settings = _services.GetRequiredService<SettingsService>().LoadAsync().GetAwaiter().GetResult();
            startMinimized = settings.StartMinimized;
            if (log is FileLogService fileLog && fileLog.PurgeOlderThan(settings.LogRetentionDays) is > 0 and var removed)
                log.Info("LOG RETENTION", $"{removed} arquivo(s) de log antigos removidos");
        }
        catch (Exception ex)
        {
            log.Error("DATABASE ERROR", ex.ToString());
            MessageBox.Show($"Não foi possível abrir o banco de dados local:\n{Database.DefaultPath}\n\n{ex.Message}",
                "SyncManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        log.Info("APP START", "SyncManager");
        var window = _services.GetRequiredService<MainWindow>();
        if (startMinimized) window.WindowState = WindowState.Minimized;
        window.Show();
        _ = _services.GetRequiredService<MainViewModel>().ApplyDefaultsAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
