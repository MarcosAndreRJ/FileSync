using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Comparison;
using SyncManager.Core.Execution;
using SyncManager.Core.Planning;
using SyncManager.Core.Scanning;
using SyncManager.Persistence;

namespace SyncManager.Infrastructure;

public static class ServiceRegistration
{
    public const string CliFileName = "SyncManager.Cli.exe";

    /// <summary>Motor completo (sem UI). Compartilhado pela interface WPF e pela CLI para que se comportem igual.</summary>
    public static IServiceCollection AddSyncManagerEngine(this IServiceCollection services,
        string databasePath, string? logDirectory = null, string? cliPath = null)
    {
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<ILogService>(sp => new FileLogService(sp.GetService<ILogger<FileLogService>>(), logDirectory));
        services.AddSingleton(new Database(databasePath));
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IExecutionHistoryRepository, ExecutionHistoryRepository>();
        services.AddSingleton<IScheduleRepository, ScheduleRepository>();
        services.AddSingleton<IFileSyncStateRepository, FileSyncStateRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IProfileLockProvider>(_ => new ProfileLockProvider(databasePath));
        services.AddSingleton<IFileSystemScanner, FileSystemScanner>();
        services.AddSingleton<IFileComparer, FileComparer>();
        services.AddSingleton<DirectoryComparer>();
        services.AddSingleton<ISyncPlanner, SyncPlanner>();
        services.AddSingleton<ISyncExecutor, SyncExecutor>();
        services.AddSingleton<PreflightValidator>();
        services.AddSingleton<SyncWorkflow>();
        services.AddSingleton<ISchedulerService>(_ => new TaskSchedulerService(
            new TaskSchedulerOptions(cliPath ?? Path.Combine(AppContext.BaseDirectory, CliFileName),
                DatabasePath: string.Equals(Path.GetFullPath(databasePath), Path.GetFullPath(Database.DefaultPath), StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(databasePath))));
        return services;
    }
}
