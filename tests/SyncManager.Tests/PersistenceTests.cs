using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;

namespace SyncManager.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Migrations_are_idempotent_and_versioned()
    {
        using var env = new TestEnv();
        Assert.Equal(Database.LatestVersion, await env.Db.GetSchemaVersionAsync());
        await env.Db.MigrateAsync();
        await env.Db.MigrateAsync();
        Assert.Equal(Database.LatestVersion, await env.Db.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task Profile_round_trips_every_setting()
    {
        using var env = new TestEnv();
        var p = new SyncProfile
        {
            Name = "Backup Financeiro",
            SourcePath = @"C:\Financeiro",
            TargetPath = @"\\192.168.0.10\Backup\Financeiro",
            Method = SyncMethod.ReplicationUpdate,
            IncludeSubfolders = false,
            PreserveTimestamps = false,
            PreserveAttributes = false,
            VerifyContentByHash = true,
            VerifyCopyByHash = true,
            TimestampToleranceSeconds = 7,
            IncludeHidden = false,
            IncludeSystem = true,
            ContinueOnError = false,
            ConfirmDeletes = false,
            BackupBeforeOverwrite = false,
            DeleteStrategy = DeleteStrategy.Permanent,
            SafetyFolderPath = @"D:\Safety",
            ExcludePatterns = { "*.tmp", "*.log" },
            IncludePatterns = { "*.xlsx" },
            MaxParallelFileOperations = 3,
            MassDeleteThreshold = 0.5
        };

        var saved = await env.Profiles.SaveAsync(p);
        Assert.True(saved.Id > 0);

        var loaded = (await env.Profiles.GetAsync(saved.Id))!;
        Assert.Equal(p.Name, loaded.Name);
        Assert.Equal(p.SourcePath, loaded.SourcePath);
        Assert.Equal(p.TargetPath, loaded.TargetPath);
        Assert.Equal(p.Method, loaded.Method);
        Assert.False(loaded.IncludeSubfolders);
        Assert.False(loaded.PreserveTimestamps);
        Assert.False(loaded.PreserveAttributes);
        Assert.True(loaded.VerifyContentByHash);
        Assert.True(loaded.VerifyCopyByHash);
        Assert.Equal(7, loaded.TimestampToleranceSeconds);
        Assert.False(loaded.IncludeHidden);
        Assert.True(loaded.IncludeSystem);
        Assert.False(loaded.ContinueOnError);
        Assert.False(loaded.ConfirmDeletes);
        Assert.False(loaded.BackupBeforeOverwrite);
        Assert.Equal(DeleteStrategy.Permanent, loaded.DeleteStrategy);
        Assert.Equal(@"D:\Safety", loaded.SafetyFolderPath);
        Assert.Equal(new[] { "*.tmp", "*.log" }, loaded.ExcludePatterns);
        Assert.Equal(new[] { "*.xlsx" }, loaded.IncludePatterns);
        Assert.Equal(3, loaded.MaxParallelFileOperations);
        Assert.Equal(0.5, loaded.MassDeleteThreshold);
    }

    [Fact]
    public async Task Schedule_round_trips_and_there_is_only_one_per_profile()
    {
        using var env = new TestEnv();
        var profile = await env.Profiles.SaveAsync(new SyncProfile { Name = "P", SourcePath = "s", TargetPath = "t" });
        var repo = new ScheduleRepository(env.Db);

        Assert.Null(await repo.GetByProfileAsync(profile.Id));

        var saved = await repo.SaveAsync(new ScheduleDefinition
        {
            ProfileId = profile.Id, Kind = ScheduleKind.Weekly, Days = WeekDays.Monday | WeekDays.Friday,
            TimeOfDay = new TimeSpan(18, 30, 0), IntervalMinutes = 90, DayOfMonth = 0,
            RunOnStartup = true, DelayMinutes = 7, TaskName = @"\SyncManager\Profile_1", Enabled = false
        });
        Assert.True(saved.Id > 0);

        var loaded = (await repo.GetByProfileAsync(profile.Id))!;
        Assert.Equal(ScheduleKind.Weekly, loaded.Kind);
        Assert.Equal(WeekDays.Monday | WeekDays.Friday, loaded.Days);
        Assert.Equal(new TimeSpan(18, 30, 0), loaded.TimeOfDay);
        Assert.Equal(90, loaded.IntervalMinutes);
        Assert.Equal(0, loaded.DayOfMonth);
        Assert.True(loaded.RunOnStartup);
        Assert.Equal(7, loaded.DelayMinutes);
        Assert.Equal(@"\SyncManager\Profile_1", loaded.TaskName);
        Assert.False(loaded.Enabled);

        // salvar de novo atualiza a mesma linha (índice único por perfil)
        loaded.Kind = ScheduleKind.Daily;
        loaded.Id = 0;
        var again = await repo.SaveAsync(loaded);
        Assert.Equal(saved.Id, again.Id);
        Assert.Single(await repo.GetAllAsync());
        Assert.Equal(ScheduleKind.Daily, (await repo.GetByProfileAsync(profile.Id))!.Kind);

        // schedule inválido é recusado
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(new ScheduleDefinition { ProfileId = profile.Id, Kind = ScheduleKind.Weekly, Days = WeekDays.None }));

        // excluir o perfil leva o agendamento junto (cascata)
        await env.Profiles.DeleteAsync(profile.Id);
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Profile_update_delete_and_unique_names()
    {
        using var env = new TestEnv();
        var a = await env.Profiles.SaveAsync(new SyncProfile { Name = "A", SourcePath = "s", TargetPath = "t" });
        var b = await env.Profiles.SaveAsync(new SyncProfile { Name = "B", SourcePath = "s", TargetPath = "t" });

        await Assert.ThrowsAsync<DuplicateProfileNameException>(() =>
            env.Profiles.SaveAsync(new SyncProfile { Name = "a", SourcePath = "x", TargetPath = "y" })); // case-insensitive
        b.Name = "A";
        await Assert.ThrowsAsync<DuplicateProfileNameException>(() => env.Profiles.SaveAsync(b));

        a.TargetPath = "novo";
        await env.Profiles.SaveAsync(a);
        Assert.Equal("novo", (await env.Profiles.GetAsync(a.Id))!.TargetPath);

        await env.Profiles.DeleteAsync(a.Id);
        Assert.Null(await env.Profiles.GetAsync(a.Id));
        Assert.Single(await env.Profiles.GetAllAsync());
    }

    [Fact]
    public async Task Executing_records_history_with_items_and_survives_profile_deletion()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a"); env.S("b.txt", "b"); env.T("extra.txt", "x");
        var profile = await env.Profiles.SaveAsync(env.Profile());

        var outcome = await env.CompareAsync(profile);
        var result = await env.Workflow.ExecuteAsync(outcome, null, default);

        var list = await env.History.ListAsync(10, profile.Id);
        var h = Assert.Single(list);
        Assert.Equal(profile.Id, h.ProfileId);
        Assert.Equal(profile.Name, h.ProfileName);
        Assert.Equal(ExecutionStatus.Success, h.Status);
        Assert.Equal(2, h.FilesCopied);
        Assert.Equal(1, h.FilesDeleted);
        Assert.Equal(result.SafetyFolder, h.SafetyFolder);
        Assert.Equal(result.BytesTransferred, h.BytesTransferred);

        var items = await env.History.GetItemsAsync(h.Id, 100);
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.RelativePath == "extra.txt" && i.OperationType == "Delete" && i.Outcome == "Succeeded");

        var last = await env.History.GetLastRunsAsync();
        Assert.Equal(ExecutionStatus.Success, last[profile.Id].Status);

        await env.Profiles.DeleteAsync(profile.Id);
        Assert.Single(await env.History.ListAsync(10)); // histórico permanece
    }

    [Fact]
    public async Task History_lists_newest_first_and_failures_come_first_in_items()
    {
        using var env = new TestEnv();
        var locked = env.S("locked.bin", "locked");
        env.S("ok.txt", "ok");
        var o1 = await env.CompareAsync(env.Profile());
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            await env.Workflow.ExecuteAsync(o1, null, default);

        env.S("another.txt", "new");
        var o2 = await env.CompareAsync(env.Profile());
        await env.Workflow.ExecuteAsync(o2, null, default);

        var list = await env.History.ListAsync(10);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].StartedAt >= list[1].StartedAt);
        Assert.Equal(ExecutionStatus.SuccessWithWarnings, list[1].Status);
        Assert.Equal(1, list[1].Errors);

        var items = await env.History.GetItemsAsync(list[1].Id, 100);
        Assert.Equal("Failed", items[0].Outcome);
        Assert.Null(list[0].ProfileId); // execução manual
    }

    [Fact]
    public async Task Cancelled_execution_is_also_recorded()
    {
        using var cts = new CancellationTokenSource();
        using var env = new TestEnv(fs => new FaultyFileSystem(fs) { AfterCopy = _ => cts.Cancel() });
        for (var i = 0; i < 10; i++) env.S($"f{i}.txt", "x" + i);
        var o = await env.CompareAsync(env.Profile());
        await env.Workflow.ExecuteAsync(o, null, cts.Token);
        Assert.Equal(ExecutionStatus.Cancelled, Assert.Single(await env.History.ListAsync(5)).Status);
    }

    [Fact]
    public async Task Settings_round_trip_and_drive_default_parallelism()
    {
        using var env = new TestEnv();
        Assert.Equal(4, (await env.Settings.LoadAsync()).MaxParallelLocal);

        await env.Settings.SaveAsync(new AppSettings { MaxParallelLocal = 7, MaxParallelNetwork = 3, LogRetentionDays = 10, DefaultExcludePatterns = "*.bak" });
        var s = await env.Settings.LoadAsync();
        Assert.Equal(7, s.MaxParallelLocal);
        Assert.Equal(3, s.MaxParallelNetwork);
        Assert.Equal(10, s.LogRetentionDays);
        Assert.Equal("*.bak", s.DefaultExcludePatterns);

        env.S("a.txt", "a");
        var p = env.Profile(); p.MaxParallelFileOperations = 0; // automático
        var o = await env.CompareAsync(p);
        Assert.Equal(7, o.Profile.MaxParallelFileOperations);

        p.MaxParallelFileOperations = 2; // valor do perfil prevalece
        Assert.Equal(2, (await env.CompareAsync(p)).Profile.MaxParallelFileOperations);

        await env.Settings.SaveAsync(new AppSettings { MaxParallelLocal = 999, LogRetentionDays = -5 });
        var clamped = await env.Settings.LoadAsync();
        Assert.Equal(32, clamped.MaxParallelLocal);
        Assert.Equal(0, clamped.LogRetentionDays);
    }

    [Fact]
    public void Old_logs_are_purged_by_retention()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(env.Log.LogDirectory);
        var old = Path.Combine(env.Log.LogDirectory, "syncmanager-20200101.log");
        var recent = Path.Combine(env.Log.LogDirectory, "syncmanager-20991231.log");
        File.WriteAllText(old, "x"); File.WriteAllText(recent, "y");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-90));

        Assert.Equal(1, env.Log.PurgeOlderThan(30));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(0, env.Log.PurgeOlderThan(0));
    }

    [Fact]
    public async Task Include_patterns_restrict_files_but_keep_folders()
    {
        using var env = new TestEnv();
        env.S("a.xlsx", "x"); env.S("b.txt", "y"); env.S(@"sub\c.xlsx", "z");
        var p = env.Profile(); p.IncludePatterns.Add("*.xlsx");
        var o = await env.CompareAsync(p);
        Assert.DoesNotContain(o.Comparison.Items, i => i.RelativePath.EndsWith("b.txt"));
        Assert.Contains(o.Comparison.Items, i => i.RelativePath == @"sub\c.xlsx");
    }
}
