using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SyncManager.Application.Ftp;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Infrastructure.Ftp;
using SyncManager.Infrastructure.Security;
using SyncManager.Persistence;
using SyncManager.Wpf.ViewModels.Ftp;
using Xunit;

namespace SyncManager.Tests;

public class FtpAdvancedFeaturesTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _testDbPath;

    public FtpAdvancedFeaturesTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "SyncManagerFtpAdvTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _testDbPath = Path.Combine(_tempDirectory, "ftp_adv_test.db");
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
            // Ignore cleanup errors
        }
    }

    #region Framework Presets & Ignore Tests

    [Theory]
    [InlineData(WebFrameworkPreset.WordPress, "wp-config.php")]
    [InlineData(WebFrameworkPreset.WordPress, "wp-content/uploads/")]
    [InlineData(WebFrameworkPreset.Laravel, ".env")]
    [InlineData(WebFrameworkPreset.Laravel, "vendor/")]
    [InlineData(WebFrameworkPreset.Laravel, "storage/")]
    [InlineData(WebFrameworkPreset.NodeJs, "node_modules/")]
    [InlineData(WebFrameworkPreset.NodeJs, ".env.*")]
    [InlineData(WebFrameworkPreset.DotNet, "bin/")]
    [InlineData(WebFrameworkPreset.DotNet, "obj/")]
    [InlineData(WebFrameworkPreset.DotNet, "appsettings.Development.json")]
    [InlineData(WebFrameworkPreset.AngularReactVue, "node_modules/")]
    [InlineData(WebFrameworkPreset.AngularReactVue, ".env.local")]
    public void FtpFrameworkPresetHelper_ReturnsExpectedPatterns(WebFrameworkPreset preset, string expectedPattern)
    {
        var patterns = FtpFrameworkPresetHelper.GetPresetExclusionPatterns(preset);
        Assert.Contains(expectedPattern, patterns);
    }

    [Fact]
    public void FtpFrameworkPresetHelper_None_ReturnsEmpty()
    {
        var patterns = FtpFrameworkPresetHelper.GetPresetExclusionPatterns(WebFrameworkPreset.None);
        Assert.Empty(patterns);
    }

    [Fact]
    public void FtpFrameworkPresetHelper_ResolvesFtpIgnoreFile_Correctly()
    {
        var projectDir = Path.Combine(_tempDirectory, "local_site");
        Directory.CreateDirectory(projectDir);

        var ftpIgnorePath = Path.Combine(projectDir, ".ftpignore");
        File.WriteAllLines(ftpIgnorePath, new[]
        {
            "# Comentario",
            "secrets.json",
            "*.log",
            "",
            "cache/"
        });

        var task = new FtpSyncTask
        {
            SourceLocalPath = projectDir,
            FrameworkPreset = WebFrameworkPreset.WordPress,
            ExcludePatterns = new List<string> { "*.bak" }
        };

        var resolved = FtpFrameworkPresetHelper.ResolveAllExclusions(task.ExcludePatterns, task.FrameworkPreset, task.SourceLocalPath);

        // Deve conter exclusões padrão da tarefa
        Assert.Contains("*.bak", resolved);
        // Deve conter regras lidas do .ftpignore
        Assert.Contains("secrets.json", resolved);
        Assert.Contains("*.log", resolved);
        Assert.Contains("cache/", resolved);
        // Deve conter proteções do preset WordPress
        Assert.Contains("wp-config.php", resolved);
    }

    #endregion

    #region Notification Service & Webhook Formatting Tests

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpStatusCode StatusCodeToReturn { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(StatusCodeToReturn)
            {
                Content = new StringContent("{\"status\":\"ok\"}")
            };
        }
    }

    [Fact]
    public async Task NotificationService_SendsDiscordWebhook_WithValidJsonEmbeds()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new FtpNotificationService(httpClient);

        var task = new FtpSyncTask
        {
            Name = "Deploy WordPress Produção",
            WebhookUrl = "https://discord.com/api/webhooks/123/abc",
            WebhookType = FtpWebhookType.Discord
        };
        var profile = new FtpConnectionProfile { Name = "Servidor HostGator" };
        var history = new FtpExecutionHistoryEntry
        {
            TaskName = task.Name,
            ConnectionName = profile.Name,
            Status = FtpExecutionStatus.Success,
            TotalFiles = 10,
            ProcessedFiles = 10,
            FailedFiles = 0,
            TotalBytesTransferred = 1048576,
            Duration = TimeSpan.FromSeconds(15)
        };

        await service.SendNotificationAsync(task, profile, history);

        Assert.NotNull(mockHandler.LastRequestBody);

        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("embeds", out var embeds));
        Assert.Equal(1, embeds.GetArrayLength());

        var firstEmbed = embeds[0];
        Assert.Contains("Deploy WordPress Produção", firstEmbed.GetProperty("description").GetString());
        Assert.Equal(0x2ECC71, firstEmbed.GetProperty("color").GetInt32()); // Verde de sucesso
    }

    [Fact]
    public async Task NotificationService_SendsSlackWebhook_WithBlocks()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new FtpNotificationService(httpClient);

        var task = new FtpSyncTask
        {
            Name = "Deploy API Backend",
            WebhookUrl = "https://hooks.slack.com/services/T00/B00/X00",
            WebhookType = FtpWebhookType.Slack
        };
        var profile = new FtpConnectionProfile { Name = "VPS DigitalOcean" };
        var history = new FtpExecutionHistoryEntry
        {
            TaskName = task.Name,
            ConnectionName = profile.Name,
            Status = FtpExecutionStatus.Failed,
            TotalFiles = 5,
            ProcessedFiles = 2,
            FailedFiles = 3,
            TotalBytesTransferred = 512,
            Duration = TimeSpan.FromSeconds(8),
            ErrorMessage = "Timeout durante conexão de dados"
        };

        await service.SendNotificationAsync(task, profile, history);

        Assert.NotNull(mockHandler.LastRequestBody);

        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("attachments", out var attachments));
        Assert.True(attachments.GetArrayLength() > 0);
        Assert.Contains("Deploy API Backend", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task NotificationService_SendsTeamsWebhook_WithSummary()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new FtpNotificationService(httpClient);

        var task = new FtpSyncTask
        {
            Name = "Deploy Frontend Nuxt",
            WebhookUrl = "https://outlook.office.com/webhook/test",
            WebhookType = FtpWebhookType.MicrosoftTeams
        };
        var profile = new FtpConnectionProfile { Name = "FTP Hostinger" };
        var history = new FtpExecutionHistoryEntry
        {
            TaskName = task.Name,
            ConnectionName = profile.Name,
            Status = FtpExecutionStatus.Warning,
            TotalFiles = 20,
            ProcessedFiles = 19,
            FailedFiles = 1,
            TotalBytesTransferred = 204800,
            Duration = TimeSpan.FromSeconds(5)
        };

        await service.SendNotificationAsync(task, profile, history);

        Assert.NotNull(mockHandler.LastRequestBody);

        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("MessageCard", root.GetProperty("type").GetString());
        Assert.Equal("e67e22", root.GetProperty("themeColor").GetString()); // Laranja de Warning
        Assert.Contains("Deploy Frontend Nuxt", root.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task NotificationService_SendsGenericJson_WithCompletePayload()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new FtpNotificationService(httpClient);

        var task = new FtpSyncTask
        {
            Name = "Sync Storage",
            WebhookUrl = "https://api.empresa.com/webhooks/ftp",
            WebhookType = FtpWebhookType.GenericJson
        };
        var profile = new FtpConnectionProfile { Name = "Backup Server" };
        var history = new FtpExecutionHistoryEntry
        {
            TaskName = task.Name,
            ConnectionName = profile.Name,
            Status = FtpExecutionStatus.Success,
            TotalFiles = 50,
            ProcessedFiles = 50,
            TotalBytesTransferred = 10000000,
            Duration = TimeSpan.FromSeconds(30)
        };

        await service.SendNotificationAsync(task, profile, history);

        Assert.NotNull(mockHandler.LastRequestBody);

        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("ftp_sync_completed", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("execution", out var execution));
        Assert.Equal("Sucesso", execution.GetProperty("status").GetString());
        Assert.Equal(50, execution.GetProperty("totalFiles").GetInt32());
    }

    [Fact]
    public async Task NotificationService_TestWebhook_ExecutesSuccessfully()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new FtpNotificationService(httpClient);

        await service.SendTestWebhookAsync(
            "https://discord.com/api/webhooks/test",
            FtpWebhookType.Discord);

        Assert.NotNull(mockHandler.LastRequestBody);
        Assert.Contains("Teste de Webhook", mockHandler.LastRequestBody!);
    }

    #endregion

    #region Database Persistence Tests for New Fields

    [Fact]
    public async Task FtpConnectionRepository_PersistsDefaultWebhookFields()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var repo = new FtpConnectionRepository(db);

        var profile = new FtpConnectionProfile
        {
            Name = "Conexão com Webhook",
            Host = "ftp.empresa.com",
            Port = 21,
            Username = "deployer",
            DefaultWebhookUrl = "https://discord.com/api/webhooks/default/123",
            DefaultWebhookType = FtpWebhookType.Discord
        };

        await repo.SaveAsync(profile);

        var loaded = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Equal("https://discord.com/api/webhooks/default/123", loaded!.DefaultWebhookUrl);
        Assert.Equal(FtpWebhookType.Discord, loaded.DefaultWebhookType);
    }

    [Fact]
    public async Task FtpTaskRepository_PersistsAdvancedSafeDeployAndWebhookFields()
    {
        var db = new Database(_testDbPath);
        await db.MigrateAsync();

        var connRepo = new FtpConnectionRepository(db);
        var profile = new FtpConnectionProfile
        {
            Name = "Perfil Conexão Teste Seguro",
            Host = "ftp.seguro.com",
            Port = 21,
            Username = "user"
        };
        await connRepo.SaveAsync(profile);

        var repo = new FtpTaskRepository(db);

        var task = new FtpSyncTask
        {
            Name = "Tarefa de Produção Segura",
            ConnectionProfileId = profile.Id,
            SourceLocalPath = @"C:\Projetos\App",
            TargetRemotePath = "/public_html",
            FrameworkPreset = WebFrameworkPreset.Laravel,
            UseAtomicUpload = true,
            EnableMaintenanceMode = true,
            MaintenanceFileName = "down.flag",
            NotifyOnCompletion = true,
            WebhookUrl = "https://hooks.slack.com/services/test",
            WebhookType = FtpWebhookType.Slack
        };

        await repo.SaveAsync(task);

        var loaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(WebFrameworkPreset.Laravel, loaded!.FrameworkPreset);
        Assert.True(loaded.UseAtomicUpload);
        Assert.True(loaded.EnableMaintenanceMode);
        Assert.Equal("down.flag", loaded.MaintenanceFileName);
        Assert.True(loaded.NotifyOnCompletion);
        Assert.Equal("https://hooks.slack.com/services/test", loaded.WebhookUrl);
        Assert.Equal(FtpWebhookType.Slack, loaded.WebhookType);
    }

    #endregion

    #region ViewModel Tests

    [Fact]
    public void FtpTaskEditViewModel_ApplyPresetExclusionsCommand_InsertsPatterns()
    {
        var task = new FtpSyncTask
        {
            FrameworkPreset = WebFrameworkPreset.WordPress,
            ExcludePatterns = new List<string> { "*.tmp" }
        };

        var dummyTaskService = new FtpTaskService(null!, null!, null!, null!);
        var dummyConnService = new FtpConnectionService(null!, null!);

        var vm = new FtpTaskEditViewModel(task, dummyTaskService, dummyConnService);

        Assert.Equal(WebFrameworkPreset.WordPress, vm.FrameworkPreset);
        Assert.False(string.IsNullOrWhiteSpace(vm.FrameworkPresetDescription));

        vm.ApplyPresetExclusionsCommand.Execute(null);

        var patterns = vm.Exclusions.Select(e => e.Text).ToList();
        Assert.Contains("wp-config.php", patterns);
        Assert.Contains("wp-content/uploads/", patterns);
        Assert.Contains("*.tmp", patterns);
        Assert.Equal(patterns, task.ExcludePatterns);   // a tarefa recebe as regras
    }

    [Fact]
    public void FtpConnectionEditViewModel_WebhookProperties_BindCorrectly()
    {
        var profile = new FtpConnectionProfile
        {
            DefaultWebhookUrl = "https://example.com/hook",
            DefaultWebhookType = FtpWebhookType.GenericJson
        };

        var dummyConnService = new FtpConnectionService(null!, null!);
        var vm = new FtpConnectionEditViewModel(profile, dummyConnService, (_, _) => null!);

        Assert.Equal("https://example.com/hook", vm.DefaultWebhookUrl);
        Assert.Equal(FtpWebhookType.GenericJson, vm.DefaultWebhookType);
        Assert.Contains(FtpWebhookType.Discord, vm.AvailableWebhookTypes);
        Assert.Contains(FtpWebhookType.MicrosoftTeams, vm.AvailableWebhookTypes);
    }

    #endregion
}
