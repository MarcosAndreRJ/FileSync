using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Infrastructure.Security;
using SyncManager.Persistence;
using SyncManager.Wpf.ViewModels.Ftp;
using Xunit;

namespace SyncManager.Tests;

public class FtpTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _testDbPath;
    private readonly string _vaultPath;

    public FtpTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "SyncManagerFtpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _testDbPath = Path.Combine(_tempDirectory, "ftp_test.db");
        _vaultPath = Path.Combine(_tempDirectory, "vault");
        Directory.CreateDirectory(_vaultPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task DpapiCredentialStore_EncryptsAndDecrypts_Successfully()
    {
        var store = new DpapiFtpCredentialStore(_vaultPath);
        const string credId = "conn_prod_web";
        const string password = "MyP@ssw0rd!#2026_Secure$";

        await store.SavePasswordAsync(credId, password);

        var retrieved = await store.GetPasswordAsync(credId);
        Assert.Equal(password, retrieved);

        await store.DeletePasswordAsync(credId);
        var afterDelete = await store.GetPasswordAsync(credId);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task DpapiCredentialStore_ThrowsOnInvalidIdentifier()
    {
        var store = new DpapiFtpCredentialStore(_vaultPath);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SavePasswordAsync("", "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SavePasswordAsync("   ", "secret"));
    }

    [Fact]
    public async Task FtpConnectionRepository_CrudOperations_WorkCorrectly()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var repo = new FtpConnectionRepository(db);

        var profile = new FtpConnectionProfile
        {
            Name = "Servidor Web Produção",
            Host = "ftp.empresa.com.br",
            Port = 21,
            Protocol = FtpProtocol.FtpsExplicit,
            Username = "deployer",
            CredentialIdentifier = "cred_web_prod",
            TransferMode = FtpTransferMode.Passive,
            RequireTls12OrHigher = true,
            CertificatePinning = false,
            DefaultLocalPath = @"C:\Projetos\Web",
            DefaultRemotePath = "/public_html",
            MaxParallelConnections = 4,
            TimeoutSeconds = 45,
            PreserveModTime = true,
            SendKeepAlive = true
        };

        // 1. Create
        await repo.SaveAsync(profile);

        // 2. Read
        var retrieved = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Servidor Web Produção", retrieved.Name);
        Assert.Equal("ftp.empresa.com.br", retrieved.Host);
        Assert.Equal(21, retrieved.Port);
        Assert.Equal(FtpProtocol.FtpsExplicit, retrieved.Protocol);
        Assert.Equal("deployer", retrieved.Username);
        Assert.Equal("cred_web_prod", retrieved.CredentialIdentifier);
        Assert.Equal(FtpTransferMode.Passive, retrieved.TransferMode);
        Assert.True(retrieved.RequireTls12OrHigher);
        Assert.Equal(@"C:\Projetos\Web", retrieved.DefaultLocalPath);
        Assert.Equal("/public_html", retrieved.DefaultRemotePath);

        var all = await repo.GetAllAsync();
        Assert.Single(all);

        // 3. Name check
        Assert.True(await repo.NameExistsAsync("Servidor Web Produção", Guid.NewGuid()));
        Assert.False(await repo.NameExistsAsync("Servidor Web Produção", profile.Id));
        Assert.False(await repo.NameExistsAsync("Outro Nome Inexistente", Guid.NewGuid()));

        // 4. Update
        retrieved.Name = "Servidor Web Homologação";
        retrieved.Port = 2121;
        await repo.SaveAsync(retrieved);

        var updated = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(updated);
        Assert.Equal("Servidor Web Homologação", updated.Name);
        Assert.Equal(2121, updated.Port);

        // 5. Delete
        await repo.DeleteAsync(profile.Id);
        var deleted = await repo.GetByIdAsync(profile.Id);
        Assert.Null(deleted);

        var allAfterDelete = await repo.GetAllAsync();
        Assert.Empty(allAfterDelete);
    }

    [Fact]
    public async Task FtpTaskRepository_CrudOperations_WorkCorrectly()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var connRepo = new FtpConnectionRepository(db);
        var taskRepo = new FtpTaskRepository(db);

        var conn = new FtpConnectionProfile
        {
            Name = "FTP Local",
            Host = "127.0.0.1",
            Port = 21,
            Username = "user"
        };
        await connRepo.SaveAsync(conn);

        var task = new FtpSyncTask
        {
            Name = "Deploy Frontend",
            Description = "Sincronização de arquivos estáticos",
            ConnectionProfileId = conn.Id,
            SourceLocalPath = @"C:\App\Dist",
            TargetRemotePath = "/var/www/html",
            SyncMethod = FtpSyncMethod.UpdateTarget,
            ComparisonStrategy = FtpComparisonStrategy.SizeAndTimestamp,
            TimestampToleranceSeconds = 3,
            RequireChecklistApproval = true,
            BackupBeforeOverwrite = true,
            ProhibitRemoteDeletions = true,
            RequireExplicitChecklistConfirmation = true,
            IncludedRelativePaths = new() { "index.html", "assets/css", "assets/js" },
            ExclusionPatterns = new() { "*.map", "**/.git/**", "*.log" }
        };

        // 1. Create
        await taskRepo.SaveAsync(task);

        // 2. Read
        var retrieved = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Deploy Frontend", retrieved.Name);
        Assert.Equal(conn.Id, retrieved.ConnectionProfileId);
        Assert.Equal(@"C:\App\Dist", retrieved.SourceLocalPath);
        Assert.Equal("/var/www/html", retrieved.TargetRemotePath);
        Assert.Equal(FtpSyncMethod.UpdateTarget, retrieved.SyncMethod);
        Assert.True(retrieved.BackupBeforeOverwrite);
        Assert.Equal(3, retrieved.IncludedRelativePaths.Count);
        Assert.Equal(3, retrieved.ExclusionPatterns.Count);

        var tasksByConn = await taskRepo.GetByConnectionIdAsync(conn.Id);
        Assert.Single(tasksByConn);

        // 3. Name check
        Assert.True(await taskRepo.NameExistsAsync("Deploy Frontend", Guid.NewGuid()));
        Assert.False(await taskRepo.NameExistsAsync("Deploy Frontend", task.Id));

        // 4. Update
        retrieved.Name = "Deploy Frontend Prod";
        retrieved.Description = "Nova Descrição Atualizada";
        await taskRepo.SaveAsync(retrieved);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal("Deploy Frontend Prod", updated.Name);
        Assert.Equal("Nova Descrição Atualizada", updated.Description);

        // 5. Delete
        await taskRepo.DeleteAsync(task.Id);
        var deleted = await taskRepo.GetByIdAsync(task.Id);
        Assert.Null(deleted);

        var allTasks = await taskRepo.GetAllAsync();
        Assert.Empty(allTasks);
    }

    [Fact]
    public async Task FtpHistoryRepository_CrudOperations_WorkCorrectly()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var repo = new FtpHistoryRepository(db);

        var entry = new FtpExecutionHistoryEntry
        {
            TaskId = Guid.NewGuid(),
            TaskName = "Sincronização Teste",
            ConnectionName = "Conexão Teste",
            ExecutedAt = DateTime.UtcNow,
            Status = FtpExecutionStatus.Success,
            TotalFiles = 100,
            ProcessedFiles = 10,
            FailedFiles = 0,
            TotalBytesTransferred = 1048576,
            Duration = TimeSpan.FromSeconds(2),
            ErrorMessage = null
        };

        // 1. Add
        await repo.AddAsync(entry);

        // 2. Read
        var retrieved = await repo.GetByIdAsync(entry.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Sincronização Teste", retrieved.TaskName);
        Assert.Equal(FtpExecutionStatus.Success, retrieved.Status);
        Assert.Equal(10, retrieved.ProcessedFiles);
        Assert.Equal(1048576, retrieved.TotalBytesTransferred);

        // 3. List
        var list = await repo.ListAsync(50, entry.TaskId);
        Assert.Single(list);

        // 4. Delete older than
        await repo.DeleteOlderThanAsync(DateTime.UtcNow.AddMinutes(1));
        var listAfter = await repo.ListAsync(50);
        Assert.Empty(listAfter);
    }

    [Fact]
    public async Task FtpConnectionService_ValidatesAndManagesCredentials()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var connRepo = new FtpConnectionRepository(db);
        var credStore = new DpapiFtpCredentialStore(_vaultPath);
        var service = new FtpConnectionService(connRepo, credStore);

        // Test Validation
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpConnectionProfile { Name = "", Host = "ftp.host.com" }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpConnectionProfile { Name = "Profile", Host = "" }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(new FtpConnectionProfile { Name = "Profile", Host = "ftp.host.com", Port = 0 }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(new FtpConnectionProfile { Name = "Profile", Host = "ftp.host.com", Port = 70000 }));

        // Test Successful Save with Password
        var profile = new FtpConnectionProfile
        {
            Name = "Servidor Seguro",
            Host = "secure.ftp.com",
            Port = 21,
            Username = "secuser"
        };

        const string password = "SecretPassWord123!";
        await service.SaveAsync(profile, password);

        // Verify profile was saved in DB
        var savedProfile = await service.GetByIdAsync(profile.Id);
        Assert.NotNull(savedProfile);
        Assert.NotEmpty(savedProfile.CredentialIdentifier);

        // Verify password is encrypted and can be retrieved via service
        var retrievedPassword = await service.GetPasswordAsync(profile.Id);
        Assert.Equal(password, retrievedPassword);

        // Test Delete removes both profile and credential
        await service.DeleteAsync(profile.Id);
        var afterDelete = await service.GetByIdAsync(profile.Id);
        Assert.Null(afterDelete);

        var credAfterDelete = await credStore.GetPasswordAsync(savedProfile.CredentialIdentifier);
        Assert.Null(credAfterDelete);
    }

    [Fact]
    public async Task FtpTaskService_ValidatesInputFields()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var connRepo = new FtpConnectionRepository(db);
        var taskRepo = new FtpTaskRepository(db);

        // Insert valid connection profile first to satisfy foreign key constraint
        var connProfile = new FtpConnectionProfile
        {
            Name = "FTP Para Tarefas",
            Host = "ftp.tarefas.com",
            Port = 21,
            Username = "user"
        };
        await connRepo.SaveAsync(connProfile);

        // We can pass null or dummy for engines as we only test Save/Validation
        var service = new FtpTaskService(taskRepo, connRepo, null!, null!);

        var validConnId = connProfile.Id;

        // 1. Missing name
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpSyncTask { Name = "", ConnectionProfileId = validConnId, SourceLocalPath = @"C:\test", TargetRemotePath = "/test" }));

        // 2. Missing connection ID
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpSyncTask { Name = "Task", ConnectionProfileId = Guid.Empty, SourceLocalPath = @"C:\test", TargetRemotePath = "/test" }));

        // 3. Missing local path
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpSyncTask { Name = "Task", ConnectionProfileId = validConnId, SourceLocalPath = "", TargetRemotePath = "/test" }));

        // 4. Missing remote path
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new FtpSyncTask { Name = "Task", ConnectionProfileId = validConnId, SourceLocalPath = @"C:\test", TargetRemotePath = "" }));

        // 5. Valid task saves successfully
        var task = new FtpSyncTask
        {
            Name = "Tarefa Válida",
            ConnectionProfileId = validConnId,
            SourceLocalPath = @"C:\test",
            TargetRemotePath = "/test"
        };

        await service.SaveAsync(task);
        var retrieved = await service.GetByIdAsync(task.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Tarefa Válida", retrieved.Name);
    }

    [Fact]
    public void FtpConnectionEditViewModel_ProtocolChange_UpdatesDefaultPort()
    {
        var profile = new FtpConnectionProfile { Port = 21 };
        var vm = new FtpConnectionEditViewModel(profile, null!, null!);

        Assert.Equal(21, vm.Port);

        // Switching to FtpsImplicit should update default port to 990
        vm.Protocol = FtpProtocol.FtpsImplicit;
        Assert.Equal(990, vm.Port);

        // Switching back to FtpsExplicit should restore port 21
        vm.Protocol = FtpProtocol.FtpsExplicit;
        Assert.Equal(21, vm.Port);
    }

    [Fact]
    public void FtpTaskEditViewModel_SelectingProfile_PopulatesDefaultPaths()
    {
        var profile = new FtpConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Servidor Padrão",
            DefaultLocalPath = @"D:\Projetos\App",
            DefaultRemotePath = "/www/app"
        };

        var task = new FtpSyncTask();
        var vm = new FtpTaskEditViewModel(task, null!, null!);

        Assert.Empty(vm.SourceLocalPath);
        Assert.Equal("/", vm.TargetRemotePath);

        // Selecting profile populates empty default paths
        vm.TargetRemotePath = "";
        vm.SelectedProfile = profile;

        Assert.Equal(@"D:\Projetos\App", vm.SourceLocalPath);
        Assert.Equal("/www/app", vm.TargetRemotePath);
        Assert.Equal(profile.Id, vm.Task.ConnectionProfileId);
    }

    [Fact]
    public async Task FtpConnectionsViewModel_LoadsAndFiltersConnections()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var repo = new FtpConnectionRepository(db);
        var credStore = new DpapiFtpCredentialStore(_vaultPath);
        var service = new FtpConnectionService(repo, credStore);

        await service.SaveAsync(new FtpConnectionProfile { Name = "Alpha Web", Host = "alpha.server.com" });
        await service.SaveAsync(new FtpConnectionProfile { Name = "Beta Storage", Host = "beta.server.com" });
        await service.SaveAsync(new FtpConnectionProfile { Name = "Gamma Database", Host = "gamma.server.com" });

        var vm = new FtpConnectionsViewModel(service, null!, null!);
        await vm.LoadConnectionsAsync();

        Assert.Equal(3, vm.Connections.Count);

        // Filter by name
        vm.FilterText = "Alpha";
        await vm.RefreshConnectionsAsync();
        Assert.Single(vm.Connections);
        Assert.Equal("Alpha Web", vm.Connections[0].Name);

        // Filter by host
        vm.FilterText = "beta.server";
        await vm.RefreshConnectionsAsync();
        Assert.Single(vm.Connections);
        Assert.Equal("Beta Storage", vm.Connections[0].Name);

        // Clear filter
        vm.FilterText = "";
        await vm.RefreshConnectionsAsync();
        Assert.Equal(3, vm.Connections.Count);
    }
}
