using Microsoft.Extensions.DependencyInjection;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels;
using Xunit;

namespace SyncManager.Tests;

public class HomeViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerHomeTests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;
    private readonly Database _db;

    public HomeViewModelTests()
    {
        _sp = App.BuildServices(Path.Combine(_root, "db", "home.db"), Path.Combine(_root, "logs"));
        _db = _sp.GetRequiredService<Database>();
        _db.MigrateAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task HomeViewModel_loads_empty_state_correctly()
    {
        var vm = _sp.GetRequiredService<HomeViewModel>();
        await vm.LoadAsync();

        Assert.Equal(0, vm.TotalProfiles);
        Assert.Equal(0, vm.TotalSchedules);
        Assert.Equal(0, vm.TotalFtpConnections);
        Assert.Equal(0, vm.TotalFtpTasks);
        Assert.Equal(0, vm.TotalExecutions);
        Assert.False(vm.HasProfiles);
        Assert.False(vm.HasActivities);
        Assert.False(vm.HasLastExecution);
        Assert.Empty(vm.RecentProfiles);
        Assert.Empty(vm.RecentActivities);
    }

    [Fact]
    public async Task HomeViewModel_loads_profiles_and_execution_history()
    {
        var profileRepo = _sp.GetRequiredService<IProfileRepository>();
        var historyRepo = _sp.GetRequiredService<IExecutionHistoryRepository>();

        var profile = new SyncProfile
        {
            Name = "Projeto X",
            SourcePath = @"C:\Source",
            TargetPath = @"D:\Target",
            Method = SyncMethod.MirrorSourceToTarget
        };
        var savedProfile = await profileRepo.SaveAsync(profile);

        var run = new ExecutionHistory
        {
            ProfileId = savedProfile.Id,
            ProfileName = "Projeto X",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            Status = ExecutionStatus.Success,
            FilesCopied = 15,
            BytesTransferred = 1024 * 1024,
            FilesDeleted = 2,
            Errors = 0
        };
        await historyRepo.AddAsync(run, Array.Empty<OperationResult>());

        var vm = _sp.GetRequiredService<HomeViewModel>();
        await vm.LoadAsync();

        Assert.Equal(1, vm.TotalProfiles);
        Assert.Equal(1, vm.TotalExecutions);
        Assert.True(vm.HasProfiles);
        Assert.True(vm.HasActivities);
        Assert.True(vm.HasLastExecution);
        Assert.Single(vm.RecentProfiles);
        Assert.Single(vm.RecentActivities);

        var pItem = vm.RecentProfiles[0];
        Assert.Equal("Projeto X", pItem.Name);
        Assert.Equal(@"C:\Source", pItem.SourcePath);
        Assert.Equal(@"D:\Target", pItem.TargetPath);
        Assert.Equal("Concluído com sucesso", pItem.StatusText);

        var aItem = vm.RecentActivities[0];
        Assert.Equal("Projeto X", aItem.Title);
        Assert.Equal("Local", aItem.BadgeText);
        Assert.False(aItem.IsFtp);
    }

    [Fact]
    public async Task HomeViewModel_loads_ftp_history_and_aggregates_activities()
    {
        var ftpConnRepo = _sp.GetRequiredService<IFtpConnectionRepository>();
        var ftpTaskRepo = _sp.GetRequiredService<IFtpTaskRepository>();
        var ftpHistoryRepo = _sp.GetRequiredService<IFtpHistoryRepository>();

        var conn = new FtpConnectionProfile
        {
            Name = "Servidor Web",
            Host = "ftp.exemplo.com",
            Port = 21,
            Username = "usuario"
        };
        var savedConn = await ftpConnRepo.SaveAsync(conn);

        var task = new FtpSyncTask
        {
            Name = "Backup Web",
            ConnectionProfileId = savedConn.Id,
            TargetRemotePath = "/public_html",
            SourceLocalPath = @"C:\BackupWeb",
            Method = FtpSyncMethod.UpdateTarget
        };
        var savedTask = await ftpTaskRepo.SaveAsync(task);

        var ftpRecord = new FtpExecutionHistoryEntry
        {
            TaskId = savedTask.Id,
            TaskName = "Backup Web",
            ConnectionName = "Servidor Web",
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            Status = FtpExecutionStatus.Success,
            TotalFiles = 8,
            ProcessedFiles = 8,
            FailedFiles = 0,
            TotalBytesTransferred = 500000,
            Duration = TimeSpan.FromMinutes(2)
        };
        await ftpHistoryRepo.AddAsync(ftpRecord);

        var vm = _sp.GetRequiredService<HomeViewModel>();
        await vm.LoadAsync();

        Assert.Equal(1, vm.TotalFtpConnections);
        Assert.Equal(1, vm.TotalFtpTasks);
        Assert.True(vm.HasActivities);
        Assert.Single(vm.RecentActivities);

        var item = vm.RecentActivities[0];
        Assert.Equal("Backup Web", item.Title);
        Assert.Equal("FTP", item.BadgeText);
        Assert.True(item.IsFtp);
    }

    [Fact]
    public void HomeViewModel_navigation_commands_trigger_expected_events()
    {
        var vm = _sp.GetRequiredService<HomeViewModel>();

        bool newComparisonFired = false;
        bool newProfileFired = false;
        bool openProfilesFired = false;
        bool openSchedulesFired = false;
        bool openHistoryFired = false;
        bool openSettingsFired = false;
        bool openFtpConnectionsFired = false;
        bool openFtpTasksFired = false;
        SyncProfile? comparedProfile = null;

        vm.NewComparisonRequested += () => newComparisonFired = true;
        vm.NewProfileRequested += () => newProfileFired = true;
        vm.OpenProfilesRequested += () => openProfilesFired = true;
        vm.OpenSchedulesRequested += () => openSchedulesFired = true;
        vm.OpenHistoryRequested += () => openHistoryFired = true;
        vm.OpenSettingsRequested += () => openSettingsFired = true;
        vm.OpenFtpConnectionsRequested += () => openFtpConnectionsFired = true;
        vm.OpenFtpTasksRequested += () => openFtpTasksFired = true;
        vm.CompareProfileRequested += p => comparedProfile = p;

        vm.NewComparisonCommand.Execute(null);
        Assert.True(newComparisonFired);

        vm.NewProfileCommand.Execute(null);
        Assert.True(newProfileFired);

        vm.OpenProfilesCommand.Execute(null);
        Assert.True(openProfilesFired);

        vm.OpenSchedulesCommand.Execute(null);
        Assert.True(openSchedulesFired);

        vm.OpenHistoryCommand.Execute(null);
        Assert.True(openHistoryFired);

        vm.OpenSettingsCommand.Execute(null);
        Assert.True(openSettingsFired);

        vm.OpenFtpConnectionsCommand.Execute(null);
        Assert.True(openFtpConnectionsFired);

        vm.OpenFtpTasksCommand.Execute(null);
        Assert.True(openFtpTasksFired);

        var testProfile = new SyncProfile { Name = "Teste" };
        var homeItem = new HomeProfileItem(testProfile, null);
        vm.CompareProfileCommand.Execute(homeItem);
        Assert.Same(testProfile, comparedProfile);
    }
}
