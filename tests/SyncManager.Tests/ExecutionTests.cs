using SyncManager.Application;
using SyncManager.Core.Enums;
using SyncManager.Core.Paths;

namespace SyncManager.Tests;

public class ExecutionTests
{
    [Fact]
    public async Task Mirror_copies_updates_and_moves_extras_to_the_safety_folder()
    {
        using var env = new TestEnv();
        env.S("new.txt", "new");
        env.S("upd.txt", "updated content", TestEnv.BaseTime.AddHours(1));
        env.T("upd.txt", "old", TestEnv.BaseTime);
        env.T("extra.txt", "extra data");
        env.T(@"olddir\deep.txt", "deep");

        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(1, o.Plan.CopyCount);
        Assert.Equal(1, o.Plan.UpdateCount);
        Assert.Equal(3, o.Plan.DeleteCount); // extra.txt, olddir\deep.txt, olddir

        var r = await env.Workflow.ExecuteAsync(o, null, default);

        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("new", File.ReadAllText(Path.Combine(env.Target, "new.txt")));
        Assert.Equal("updated content", File.ReadAllText(Path.Combine(env.Target, "upd.txt")));
        Assert.Equal(TestEnv.BaseTime.AddHours(1), File.GetLastWriteTimeUtc(Path.Combine(env.Target, "upd.txt")));
        Assert.False(File.Exists(Path.Combine(env.Target, "extra.txt")));
        Assert.False(Directory.Exists(Path.Combine(env.Target, "olddir")));

        // recuperável: extra.txt, deep.txt e a versão antiga de upd.txt estão na pasta de segurança
        Assert.NotNull(r.SafetyFolder);
        Assert.Equal("extra data", File.ReadAllText(Path.Combine(r.SafetyFolder!, "extra.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(r.SafetyFolder!, "olddir", "deep.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.SafetyFolder!, "upd.txt")));
        Assert.Empty(Directory.EnumerateFiles(env.Target, "*.synctmp", SearchOption.AllDirectories));

        // depois de executar, comparar de novo (ignorando a pasta de segurança) dá tudo igual
        var again = await env.CompareAsync(env.Profile());
        Assert.Equal(0, again.Plan.ExecutableCount);
    }

    [Fact]
    public async Task Second_mirror_run_is_a_no_op()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a"); env.S(@"d\b.txt", "b");
        var o = await env.CompareAsync(env.Profile());
        await env.Workflow.ExecuteAsync(o, null, default);
        var o2 = await env.CompareAsync(env.Profile());
        Assert.Equal(0, o2.Plan.ExecutableCount);
    }

    [Fact] // 7 (Mirror) e 8 (Augment)
    public async Task Augment_never_removes_or_updates_extras()
    {
        using var env = new TestEnv();
        env.S("new.txt", "new");
        env.S("both.txt", "source version", TestEnv.BaseTime.AddHours(1));
        env.T("both.txt", "target version", TestEnv.BaseTime);
        env.T("extra.txt", "extra");

        var o = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));
        Assert.Equal(0, o.Plan.DeleteCount);
        Assert.Equal(0, o.Plan.UpdateCount);
        Assert.Equal(1, o.Plan.CopyCount);

        await env.Workflow.ExecuteAsync(o, null, default);
        Assert.True(File.Exists(Path.Combine(env.Target, "extra.txt")));
        Assert.Equal("target version", File.ReadAllText(Path.Combine(env.Target, "both.txt")));
        Assert.True(File.Exists(Path.Combine(env.Target, "new.txt")));
    }

    [Fact]
    public async Task Refresh_updates_only_existing_files_when_source_is_newer()
    {
        using var env = new TestEnv();
        env.S("only_source.txt", "s");
        env.S("newer.txt", "fresh", TestEnv.BaseTime.AddHours(1)); env.T("newer.txt", "stale", TestEnv.BaseTime);
        env.S("older.txt", "stale", TestEnv.BaseTime); env.T("older.txt", "fresh", TestEnv.BaseTime.AddHours(1));

        var o = await env.CompareAsync(env.Profile(SyncMethod.ReplicationRefresh));
        Assert.Equal(1, o.Plan.UpdateCount);
        Assert.Equal(0, o.Plan.CopyCount);
        Assert.Equal(0, o.Plan.DeleteCount);
        Assert.Equal(SyncOperationType.Update, TestEnv.OpOf(o, "newer.txt"));
    }

    [Fact]
    public async Task Update_copies_new_and_modified_but_keeps_newer_target_and_extras()
    {
        using var env = new TestEnv();
        env.S("new.txt", "n");
        env.S("mod.txt", "modified!", TestEnv.BaseTime.AddHours(1)); env.T("mod.txt", "old", TestEnv.BaseTime);
        env.S("keep.txt", "src", TestEnv.BaseTime); env.T("keep.txt", "tgt-newer", TestEnv.BaseTime.AddHours(2));
        env.T("extra.txt", "e");

        var o = await env.CompareAsync(env.Profile(SyncMethod.ReplicationUpdate));
        Assert.Equal(1, o.Plan.CopyCount);
        Assert.Equal(1, o.Plan.UpdateCount);
        Assert.Equal(0, o.Plan.DeleteCount);
        Assert.Equal(SyncOperationType.Skip, TestEnv.OpOf(o, "keep.txt"));
    }

    [Fact] // 9
    public async Task Locked_file_fails_alone_and_does_not_stop_the_run()
    {
        using var env = new TestEnv();
        var locked = env.S("locked.bin", "locked content");
        env.S("ok1.txt", "1"); env.S("ok2.txt", "2");

        var o = await env.CompareAsync(env.Profile());
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = await env.Workflow.ExecuteAsync(o, null, default);

        Assert.Equal(ExecutionStatus.SuccessWithWarnings, r.Status);
        Assert.Equal(1, r.Errors);
        Assert.Equal(2, r.FilesCopied);
        Assert.False(File.Exists(Path.Combine(env.Target, "locked.bin")));
        Assert.Empty(Directory.EnumerateFiles(env.Target, "*.synctmp", SearchOption.AllDirectories));
    }

    [Fact] // 10
    public async Task Unavailable_target_aborts_compare_instead_of_being_seen_as_empty()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs));
        env.S("a.txt", "a");
        ((FaultyFileSystem)env.Fs).Offline = true;

        var ex = await Assert.ThrowsAsync<SyncValidationException>(() => env.CompareAsync(env.Profile()));
        Assert.Contains(ex.Errors, e => e.Contains("inacess"));
    }

    [Fact]
    public async Task Missing_target_folder_is_an_error_not_an_empty_folder()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a");
        var p = env.Profile();
        p.TargetPath = Path.Combine(env.Root, "nao-existe");
        await Assert.ThrowsAsync<SyncValidationException>(() => env.CompareAsync(p));
    }

    [Fact]
    public async Task Target_going_offline_before_execution_changes_nothing()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs));
        env.S("a.txt", "a");
        env.T("extra.txt", "x");
        var o = await env.CompareAsync(env.Profile());

        ((FaultyFileSystem)env.Fs).Offline = true;
        var r = await env.Workflow.ExecuteAsync(o, null, default);
        ((FaultyFileSystem)env.Fs).Offline = false;

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.True(File.Exists(Path.Combine(env.Target, "extra.txt")));
        Assert.False(File.Exists(Path.Combine(env.Target, "a.txt")));
    }

    [Fact]
    public async Task Empty_source_with_populated_target_blocks_the_mirror_plan()
    {
        using var env = new TestEnv();
        env.T("precious.txt", "do not delete");
        var o = await env.CompareAsync(env.Profile());
        Assert.True(o.Plan.IsBlocked);

        var r = await env.Workflow.ExecuteAsync(o, null, default);
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.True(File.Exists(Path.Combine(env.Target, "precious.txt")));
    }

    [Fact] // 12
    public async Task Cancellation_stops_the_run_and_leaves_no_temp_files()
    {
        using var cts = new CancellationTokenSource();
        using var env = new TestEnv(fs => new FaultyFileSystem(fs) { AfterCopy = _ => cts.Cancel() });
        for (var i = 0; i < 20; i++) env.S($"f{i}.txt", "content " + i);

        var o = await env.CompareAsync(env.Profile());
        var r = await env.Workflow.ExecuteAsync(o, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.True(r.NotExecuted > 0);
        Assert.True(Directory.EnumerateFiles(env.Target).Count() < 20);
        Assert.Empty(Directory.EnumerateFiles(env.Target, "*.synctmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Compare_can_be_cancelled()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 100; i++) env.S($"f{i}.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.CompareAsync(env.Profile(), cts.Token));
    }

    [Fact] // 13
    public async Task File_modified_during_copy_is_not_committed_to_the_target()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs));
        var src = env.S("big.txt", "original");
        var faulty = (FaultyFileSystem)env.Fs;
        faulty.AfterCopy = path =>
        {
            File.AppendAllText(path, " + changed while copying");
            File.SetLastWriteTimeUtc(path, TestEnv.BaseTime.AddHours(5));
        };

        var o = await env.CompareAsync(env.Profile());
        var r = await env.Workflow.ExecuteAsync(o, null, default);

        Assert.Equal(ExecutionStatus.SuccessWithWarnings, r.Status);
        Assert.Equal(1, r.Warnings);
        Assert.False(File.Exists(Path.Combine(env.Target, "big.txt")));
        Assert.Empty(Directory.EnumerateFiles(env.Target, "*.synctmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Target_changed_after_compare_is_not_overwritten_or_deleted()
    {
        using var env = new TestEnv();
        env.S("a.txt", "source new", TestEnv.BaseTime.AddHours(1));
        env.T("a.txt", "target old", TestEnv.BaseTime);
        env.T("extra.txt", "extra");
        var o = await env.CompareAsync(env.Profile());

        env.T("a.txt", "someone edited this meanwhile", TestEnv.BaseTime.AddHours(3));
        env.T("extra.txt", "extra edited meanwhile!", TestEnv.BaseTime.AddHours(3));
        var r = await env.Workflow.ExecuteAsync(o, null, default);

        Assert.Equal("someone edited this meanwhile", File.ReadAllText(Path.Combine(env.Target, "a.txt")));
        Assert.True(File.Exists(Path.Combine(env.Target, "extra.txt")));
        Assert.Equal(ExecutionStatus.SuccessWithWarnings, r.Status);
    }

    [Fact]
    public async Task Hash_verified_copy_produces_identical_content()
    {
        using var env = new TestEnv();
        env.S("data.bin", new string('z', 200_000));
        var p = env.Profile(); p.VerifyCopyByHash = true;
        var o = await env.CompareAsync(p);
        var r = await env.Workflow.ExecuteAsync(o, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal(200_000, new FileInfo(Path.Combine(env.Target, "data.bin")).Length);
    }

    [Fact]
    public async Task Readonly_target_file_is_replaced()
    {
        using var env = new TestEnv();
        env.S("a.txt", "new content!", TestEnv.BaseTime.AddHours(1));
        var t = env.T("a.txt", "old", TestEnv.BaseTime);
        File.SetAttributes(t, FileAttributes.ReadOnly);
        var o = await env.CompareAsync(env.Profile());
        var r = await env.Workflow.ExecuteAsync(o, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("new content!", File.ReadAllText(t));
    }

    [Fact]
    public async Task Permanent_delete_removes_the_file()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a");
        env.T("gone.txt", "bye");
        var p = env.Profile(); p.DeleteStrategy = DeleteStrategy.Permanent; p.BackupBeforeOverwrite = false;
        var o = await env.CompareAsync(p);
        var r = await env.Workflow.ExecuteAsync(o, null, default);
        Assert.False(File.Exists(Path.Combine(env.Target, "gone.txt")));
        Assert.Null(r.SafetyFolder);
        Assert.False(Directory.Exists(Path.Combine(env.Target, ".SyncBackup")));
    }

    [Fact]
    public async Task Progress_reports_bytes_and_completion()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 5; i++) env.S($"f{i}.txt", new string('x', 1000));
        var o = await env.CompareAsync(env.Profile());
        Core.Models.ExecutionProgress? last = null;
        var r = await env.Workflow.ExecuteAsync(o, new Progress<Core.Models.ExecutionProgress>(p => last = p), default);
        await Task.Delay(200);
        Assert.Equal(5000, r.BytesTransferred);
        Assert.NotNull(last);
        Assert.Equal(5, last!.CompletedOperations);
    }

    // ------------------------------------------------------------------ 14: caminhos UNC

    [Fact] // 14
    public void Unc_paths_are_recognized_and_normalized()
    {
        Assert.True(PathUtil.IsUnc(@"\\192.168.0.5\backup"));
        Assert.True(PathUtil.IsUnc(@"\\SERVIDOR\Financeiro\pasta"));
        Assert.False(PathUtil.IsUnc(@"C:\Temp"));
        Assert.Equal(@"\\servidor\share\dir", PathUtil.NormalizeRoot(@"\\servidor\share\dir\"));
        Assert.True(PathUtil.IsSameOrInside(@"\\srv\share\a", @"\\SRV\share\a\b"));
        Assert.False(PathUtil.IsSameOrInside(@"\\srv\share\a", @"\\srv\share\ab"));
    }

    [Fact]
    public async Task Preflight_rejects_recycle_bin_on_network_target_and_nested_roots()
    {
        using var env = new TestEnv();
        var pre = new PreflightValidator(env.Fs);

        var unc = env.Profile();
        unc.TargetPath = @"\\192.168.0.5\backup";
        unc.DeleteStrategy = DeleteStrategy.RecycleBin;
        var r1 = await pre.ValidateAsync(unc, false, default);
        Assert.Contains(r1.Errors, e => e.Contains("Lixeira"));

        var nested = env.Profile();
        nested.TargetPath = Path.Combine(env.Source, "inside");
        var r2 = await pre.ValidateAsync(nested, false, default);
        Assert.False(r2.IsValid);

        var safety = env.Profile();
        safety.SafetyFolderPath = Path.Combine(env.Target, "safety");
        var r3 = await pre.ValidateAsync(safety, false, default);
        Assert.Contains(r3.Errors, e => e.Contains("segurança"));
    }

    [Fact]
    public async Task Unreachable_unc_target_is_reported_as_error()
    {
        using var env = new TestEnv();
        var p = env.Profile();
        p.TargetPath = @"\\127.0.0.1\syncmanager-share-that-does-not-exist";
        var ex = await Assert.ThrowsAsync<SyncValidationException>(() => env.CompareAsync(p));
        Assert.Contains(ex.Errors, e => e.Contains("Target inacess"));
    }
}
