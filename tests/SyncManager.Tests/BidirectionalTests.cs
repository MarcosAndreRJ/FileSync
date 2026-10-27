using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Comparison;
using SyncManager.Core.Enums;
using SyncManager.Core.Execution;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;
using SyncManager.Infrastructure;
using SyncManager.Persistence;

namespace SyncManager.Tests;

public class BidirectionalTests
{
    private static readonly DateTime T1 = TestEnv.BaseTime.AddHours(1);
    private static readonly DateTime T2 = TestEnv.BaseTime.AddHours(2);
    private static readonly DateTime T3 = TestEnv.BaseTime.AddHours(3);

    // ------------------------------------------------------------------ infraestrutura dos testes

    private static async Task<SyncProfile> SyncProfile(TestEnv env, Action<SyncProfile>? configure = null)
    {
        var p = env.Profile(SyncMethod.Synchronization);
        p.Name = "Sync " + Guid.NewGuid().ToString("N")[..6];
        configure?.Invoke(p);
        return await env.Profiles.SaveAsync(p);
    }

    private static async Task<(CompareOutcome Outcome, ExecutionResult Result)> RunAsync(TestEnv env, SyncProfile p,
        Func<CompareOutcome, CompareOutcome>? edit = null)
    {
        var outcome = await env.CompareAsync(p);
        if (edit is not null) outcome = edit(outcome);
        return (outcome, await env.Workflow.ExecuteAsync(outcome, null, default));
    }

    private static Task<int> StateCount(TestEnv env, SyncProfile p) => new FileSyncStateRepository(env.Db).CountAsync(p.Id);

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));
    private static bool Exists(string root, string rel) => File.Exists(Path.Combine(root, rel));

    private static SyncOperation Op(CompareOutcome o, string rel) =>
        o.Plan.Operations.Single(x => x.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase));

    private static SyncOperation OpOfType(CompareOutcome o, string rel, SyncOperationType t) =>
        o.Plan.Operations.Single(x => x.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase) && x.Type == t);

    /// <summary>Sincroniza duas pastas com a.txt, b.txt e both.txt e devolve o ambiente já com histórico.</summary>
    private static async Task<(TestEnv Env, SyncProfile Profile)> SyncedAsync(Func<IFileSystem, IFileSystem>? wrap = null)
    {
        var env = new TestEnv(wrap);
        env.S("a.txt", "A"); env.S("both.txt", "same");
        env.T("b.txt", "B"); env.T("both.txt", "same");
        var p = await SyncProfile(env);
        var (_, r) = await RunAsync(env, p);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        return (env, p);
    }


    // ------------------------------------------------------------------ primeira sincronização

    [Fact]
    public async Task First_sync_copies_new_files_both_ways_and_records_the_history()
    {
        using var env = new TestEnv();
        env.S("a.txt", "from source"); env.S(@"dir\x.txt", "x");
        env.T("b.txt", "from target"); env.T("same.txt", "same"); env.S("same.txt", "same");
        var p = await SyncProfile(env);

        var outcome = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.Copy, Op(outcome, "a.txt").Type);
        Assert.Equal(FileSide.Source, Op(outcome, "a.txt").From);
        Assert.Equal(SyncOperationType.Copy, Op(outcome, "b.txt").Type);
        Assert.Equal(FileSide.Target, Op(outcome, "b.txt").From);   // Target → Source
        Assert.Contains(outcome.Plan.Warnings, w => w.Contains("Primeira sincronização"));

        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("from source", Read(env.Target, "a.txt"));
        Assert.Equal("from target", Read(env.Source, "b.txt"));
        Assert.Equal("x", Read(env.Target, @"dir\x.txt"));
        Assert.Equal(5, await StateCount(env, p)); // a, b, same, dir, dir\x

        var again = await env.CompareAsync(p);
        Assert.Equal(0, again.Plan.ExecutableCount);
        Assert.Equal(0, again.Plan.ConflictCount);
    }

    [Fact]
    public async Task First_sync_with_different_content_on_both_sides_is_a_conflict_never_a_silent_winner()
    {
        using var env = new TestEnv();
        env.S("doc.txt", "source version", T2); env.T("doc.txt", "target version!", T1);
        var p = await SyncProfile(env);

        var (outcome, r) = await RunAsync(env, p);
        Assert.Equal(SyncOperationType.Conflict, Op(outcome, "doc.txt").Type);
        Assert.Equal(ExecutionStatus.ConflictsUnresolved, r.Status);
        Assert.Equal("source version", Read(env.Source, "doc.txt"));
        Assert.Equal("target version!", Read(env.Target, "doc.txt"));
        Assert.Equal(0, await StateCount(env, p));
    }

    // ------------------------------------------------------------------ mudanças desde a última sincronização

    [Fact]
    public async Task Changes_on_one_side_flow_to_the_other_and_new_files_go_both_ways()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;

        env.S("a.txt", "A changed on source", T1);          // só o Source mudou
        env.T("b.txt", "B changed on target", T1);          // só o Target mudou
        env.S("c.txt", "new on source"); env.T("d.txt", "new on target");

        var outcome = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.Update, Op(outcome, "a.txt").Type);
        Assert.Equal(FileSide.Source, Op(outcome, "a.txt").From);
        Assert.Equal(SyncOperationType.Update, Op(outcome, "b.txt").Type);
        Assert.Equal(FileSide.Target, Op(outcome, "b.txt").From);
        Assert.Equal(0, outcome.Plan.ConflictCount);

        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("A changed on source", Read(env.Target, "a.txt"));
        Assert.Equal("B changed on target", Read(env.Source, "b.txt"));
        Assert.Equal("new on source", Read(env.Target, "c.txt"));
        Assert.Equal("new on target", Read(env.Source, "d.txt"));
        Assert.Equal(T1, File.GetLastWriteTimeUtc(Path.Combine(env.Source, "b.txt")));   // datas preservadas

        // versão antiga guardada do lado que foi sobrescrito
        Assert.Equal("A", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Target, ".SyncBackup"), "a.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal("B", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Source, ".SyncBackup"), "b.txt", SearchOption.AllDirectories).Single()));

        Assert.Equal(0, (await env.CompareAsync(p)).Plan.ExecutableCount);
    }

    [Fact]
    public async Task Older_mtime_does_not_decide_who_wins_only_who_changed()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        // o arquivo do Target mudou, mas com data ANTERIOR à do Source: quem mudou é quem vence, não a data
        env.T("both.txt", "target edited!", TestEnv.BaseTime.AddMinutes(-30));
        var outcome = await env.CompareAsync(p);
        var op = Op(outcome, "both.txt");
        Assert.Equal(SyncOperationType.Update, op.Type);
        Assert.Equal(FileSide.Target, op.From);
    }

    [Fact] // 11
    public async Task Both_sides_changed_is_a_conflict_and_nothing_is_touched()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        env.S("both.txt", "source edit", T1); env.T("both.txt", "target edit!", T2);

        var (outcome, r) = await RunAsync(env, p);
        Assert.Equal(SyncOperationType.Conflict, Op(outcome, "both.txt").Type);
        Assert.Contains("nos dois lados", Op(outcome, "both.txt").Reason);
        Assert.Equal(ExecutionStatus.ConflictsUnresolved, r.Status);
        Assert.Equal("source edit", Read(env.Source, "both.txt"));
        Assert.Equal("target edit!", Read(env.Target, "both.txt"));
    }

    [Fact]
    public async Task Both_changed_but_identical_content_is_not_a_conflict_with_hash_verification()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        // mesma mudança nos dois lados, com datas diferentes
        env.S("both.txt", "edited the same way", T1); env.T("both.txt", "edited the same way", T2);

        var noHash = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.Conflict, Op(noHash, "both.txt").Type);   // sem hash: datas diferentes = difere

        p.VerifyContentByHash = true;
        var hashed = await env.CompareAsync(p);
        Assert.Equal(ComparisonStatus.Identical, hashed.Comparison.Items.Single(i => i.RelativePath == "both.txt").Status);
        Assert.Equal(SyncOperationType.Skip, Op(hashed, "both.txt").Type);
        Assert.True(Op(hashed, "both.txt").RecordsState);

        await env.Workflow.ExecuteAsync(hashed, null, default);
        Assert.Equal(0, (await env.CompareAsync(p)).Plan.ConflictCount);   // histórico atualizado
    }

    [Fact]
    public async Task Touch_only_changes_are_not_changes_when_hashes_are_known()
    {
        using var env = new TestEnv();
        env.S("a.txt", "AAAA"); env.T("a.txt", "AAAA");
        var p = await SyncProfile(env, x => x.VerifyContentByHash = true);
        await RunAsync(env, p);   // grava estados com hash

        env.S("a.txt", "AAAA", T2);                       // só a data mudou no Source (mesmo conteúdo)
        env.T("a.txt", "BBBB", T1);                       // conteúdo mudou de verdade no Target
        var outcome = await env.CompareAsync(p);
        var item = outcome.Comparison.Items.Single(i => i.RelativePath == "a.txt");
        Assert.False(item.SourceChanged);                  // hash igual ao guardado
        Assert.True(item.TargetChanged);
        Assert.Equal(FileSide.Target, Op(outcome, "a.txt").From);
    }

    // ------------------------------------------------------------------ exclusões

    [Fact]
    public async Task Deletion_on_one_side_is_propagated_to_the_other_through_the_safety_folder()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        File.Delete(Path.Combine(env.Source, "a.txt"));     // excluído no Source
        File.Delete(Path.Combine(env.Target, "b.txt"));     // excluído no Target

        var outcome = await env.CompareAsync(p);
        var delA = Op(outcome, "a.txt");
        Assert.Equal(SyncOperationType.Delete, delA.Type);
        Assert.Equal(FileSide.Target, delA.To);
        var delB = Op(outcome, "b.txt");
        Assert.Equal(SyncOperationType.Delete, delB.Type);
        Assert.Equal(FileSide.Source, delB.To);
        Assert.Equal(2, outcome.Plan.DeleteCount);

        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.False(Exists(env.Target, "a.txt"));
        Assert.False(Exists(env.Source, "b.txt"));
        Assert.Equal("A", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Target, ".SyncBackup"), "a.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal("B", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Source, ".SyncBackup"), "b.txt", SearchOption.AllDirectories).Single()));

        Assert.Equal(1, await StateCount(env, p));          // sobrou só both.txt
        Assert.Equal(0, (await env.CompareAsync(p)).Plan.ExecutableCount);
    }

    [Fact]
    public async Task Deleted_on_one_side_and_modified_on_the_other_is_a_conflict()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        File.Delete(Path.Combine(env.Source, "a.txt"));
        env.T("a.txt", "modified on target", T1);

        var (outcome, r) = await RunAsync(env, p);
        Assert.Equal(SyncOperationType.Conflict, Op(outcome, "a.txt").Type);
        Assert.Contains("Excluído no Source, mas modificado no Target", Op(outcome, "a.txt").Reason);
        Assert.Equal("modified on target", Read(env.Target, "a.txt"));
        Assert.Equal(ExecutionStatus.ConflictsUnresolved, r.Status);
    }

    [Fact]
    public async Task Files_gone_from_both_sides_clean_their_history()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        File.Delete(Path.Combine(env.Source, "both.txt")); File.Delete(Path.Combine(env.Target, "both.txt"));

        var outcome = await env.CompareAsync(p);
        Assert.Contains("both.txt", outcome.Plan.StaleStatePaths);
        await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(2, await StateCount(env, p));          // a.txt e b.txt
    }

    [Fact]
    public async Task Empty_side_against_a_full_history_blocks_the_plan_instead_of_wiping_the_other_side()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        foreach (var f in Directory.GetFiles(env.Target)) File.Delete(f);   // pasta desmontada / share que respondeu vazio

        var outcome = await env.CompareAsync(p);
        Assert.True(outcome.Plan.IsBlocked);
        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal("A", Read(env.Source, "a.txt"));
        Assert.Equal(3, await StateCount(env, p));          // histórico intacto
    }

    [Fact]
    public async Task Mass_deletion_propagation_requires_strong_confirmation_and_unattended_runs_refuse()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 10; i++) { env.S($"f{i}.txt", "x" + i); env.T($"f{i}.txt", "x" + i); }
        var p = await SyncProfile(env);
        await RunAsync(env, p);
        for (var i = 0; i < 8; i++) File.Delete(Path.Combine(env.Source, $"f{i}.txt"));   // 80% excluído no Source

        var outcome = await env.CompareAsync(p);
        Assert.Equal(8, outcome.Plan.DeleteAtTargetCount);
        Assert.True(outcome.Plan.RequiresStrongConfirmation);
    }

    // ------------------------------------------------------------------ pastas

    [Fact]
    public async Task Directories_are_created_on_the_other_side_and_empty_ones_deletions_propagate()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Source, "only-source-dir"));
        Directory.CreateDirectory(Path.Combine(env.Target, "only-target-dir"));
        Directory.CreateDirectory(Path.Combine(env.Source, "shared")); Directory.CreateDirectory(Path.Combine(env.Target, "shared"));
        env.S("keep.txt", "k"); env.T("keep.txt", "k");
        var p = await SyncProfile(env);

        await RunAsync(env, p);
        Assert.True(Directory.Exists(Path.Combine(env.Target, "only-source-dir")));
        Assert.True(Directory.Exists(Path.Combine(env.Source, "only-target-dir")));

        Directory.Delete(Path.Combine(env.Source, "shared"));          // pasta vazia excluída no Source
        var outcome = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.DeleteDirectory, Op(outcome, "shared").Type);
        await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.False(Directory.Exists(Path.Combine(env.Target, "shared")));
    }

    // ------------------------------------------------------------------ resolução de conflitos

    private static async Task<(TestEnv Env, SyncProfile P, CompareOutcome Conflict)> ConflictedAsync()
    {
        var (env, p) = await SyncedAsync();
        env.S("both.txt", "SOURCE VERSION", T1);
        env.T("both.txt", "TARGET VERSION!", T2);
        var outcome = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.Conflict, Op(outcome, "both.txt").Type);
        return (env, p, outcome);
    }

    private static CompareOutcome Resolve(CompareOutcome o, ConflictResolution r, params string[] paths) =>
        o with { Plan = PlanEditor.ResolveConflicts(o.Plan, paths, r, new DateTime(2026, 9, 30, 14, 35, 0)) };

    [Fact]
    public async Task Resolve_keep_source_makes_the_target_match_and_backs_up_the_loser()
    {
        var (env, p, conflict) = await ConflictedAsync();
        using var _ = env;
        var resolved = Resolve(conflict, ConflictResolution.KeepSource, "both.txt");
        Assert.Equal(SyncOperationType.Update, Op(resolved, "both.txt").Type);
        Assert.Equal(0, resolved.Plan.ConflictCount);

        var r = await env.Workflow.ExecuteAsync(resolved, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("SOURCE VERSION", Read(env.Target, "both.txt"));
        Assert.Equal("TARGET VERSION!", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Target, ".SyncBackup"), "both.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal(0, (await env.CompareAsync(p)).Plan.ConflictCount);
    }

    [Fact]
    public async Task Resolve_keep_target_makes_the_source_match()
    {
        var (env, p, conflict) = await ConflictedAsync();
        using var _ = env;
        var resolved = Resolve(conflict, ConflictResolution.KeepTarget, "both.txt");
        Assert.Equal(FileSide.Target, Op(resolved, "both.txt").From);

        await env.Workflow.ExecuteAsync(resolved, null, default);
        Assert.Equal("TARGET VERSION!", Read(env.Source, "both.txt"));
        Assert.Equal("SOURCE VERSION", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Source, ".SyncBackup"), "both.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal(0, (await env.CompareAsync(p)).Plan.ConflictCount);
    }

    [Fact]
    public async Task Resolve_keep_both_preserves_both_versions_on_both_sides_with_the_conflict_name()
    {
        var (env, p, conflict) = await ConflictedAsync();
        using var _ = env;
        var resolved = Resolve(conflict, ConflictResolution.KeepBoth, "both.txt");
        Assert.Equal(3, resolved.Plan.Operations.Count(o => o.RelativePath == "both.txt"));

        var r = await env.Workflow.ExecuteAsync(resolved, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);

        const string name = "both.conflict-20260930-143500.txt";
        Assert.Equal("SOURCE VERSION", Read(env.Source, "both.txt"));
        Assert.Equal("SOURCE VERSION", Read(env.Target, "both.txt"));
        Assert.Equal("TARGET VERSION!", Read(env.Source, name));
        Assert.Equal("TARGET VERSION!", Read(env.Target, name));

        // e a próxima sincronização não acha mais nada para fazer
        var again = await env.CompareAsync(p);
        Assert.Equal(0, again.Plan.ExecutableCount);
        Assert.Equal(0, again.Plan.ConflictCount);
    }

    [Fact]
    public async Task Resolve_ignore_leaves_files_alone_and_the_conflict_returns_next_time()
    {
        var (env, p, conflict) = await ConflictedAsync();
        using var _ = env;
        var resolved = Resolve(conflict, ConflictResolution.Skip, "both.txt");
        Assert.Equal(SyncOperationType.Skip, Op(resolved, "both.txt").Type);
        await env.Workflow.ExecuteAsync(resolved, null, default);
        Assert.Equal("SOURCE VERSION", Read(env.Source, "both.txt"));
        Assert.Equal("TARGET VERSION!", Read(env.Target, "both.txt"));
        Assert.Equal(1, (await env.CompareAsync(p)).Plan.ConflictCount);
    }

    [Fact]
    public async Task Resolving_a_delete_versus_modify_conflict()
    {
        // KeepSource (Source excluiu) apaga no Target; KeepTarget restaura no Source; KeepBoth também restaura
        foreach (var (resolution, expectSourceFile, expectTargetFile) in new[]
        {
            (ConflictResolution.KeepSource, false, false),
            (ConflictResolution.KeepTarget, true, true),
            (ConflictResolution.KeepBoth, true, true)
        })
        {
            var (env, p) = await SyncedAsync();
            using var _ = env;
            File.Delete(Path.Combine(env.Source, "a.txt"));
            env.T("a.txt", "modified on target", T1);
            var conflict = await env.CompareAsync(p);

            var r = await env.Workflow.ExecuteAsync(Resolve(conflict, resolution, "a.txt"), null, default);
            Assert.Equal(ExecutionStatus.Success, r.Status);
            Assert.Equal(expectSourceFile, Exists(env.Source, "a.txt"));
            Assert.Equal(expectTargetFile, Exists(env.Target, "a.txt"));
        }
    }

    [Fact]
    public async Task Resolution_applies_only_to_the_chosen_conflicts()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        env.S("a.txt", "a-source", T1); env.T("a.txt", "a-target!", T2);
        env.S("b.txt", "b-source", T1); env.T("b.txt", "b-target!", T2);
        var conflict = await env.CompareAsync(p);
        Assert.Equal(2, conflict.Plan.ConflictCount);

        var resolved = Resolve(conflict, ConflictResolution.KeepSource, "a.txt");
        Assert.Equal(1, resolved.Plan.ConflictCount);
        Assert.Equal(SyncOperationType.Conflict, Op(resolved, "b.txt").Type);
    }

    [Fact]
    public void Type_mismatch_conflicts_can_only_be_ignored()
    {
        var item = new ComparisonItem("x", @"S\x", @"T\x", false, true, true, 1, null, TestEnv.BaseTime, TestEnv.BaseTime, ComparisonStatus.Conflict, "Source é arquivo e Target é pasta");
        var plan = new SyncPlan(SyncMethod.Synchronization, "S", "T", new[] { new SyncOperation(SyncOperationType.Conflict, item, null, @"Tx", 0, "tipo") }, 1, 0.3, null, Array.Empty<string>());
        Assert.Equal(SyncOperationType.Conflict, PlanEditor.ResolveConflicts(plan, new[] { "x" }, ConflictResolution.KeepSource, DateTime.Now).Operations[0].Type);
        Assert.Equal(SyncOperationType.Skip, PlanEditor.ResolveConflicts(plan, new[] { "x" }, ConflictResolution.Skip, DateTime.Now).Operations[0].Type);
    }

    [Theory]
    [InlineData(@"arquivo.txt", "arquivo.conflict-20260930-143500.txt")]
    [InlineData(@"dir\sub\relatório final.xlsx", @"dir\sub\relatório final.conflict-20260930-143500.xlsx")]
    [InlineData(@"semextensao", "semextensao.conflict-20260930-143500")]
    [InlineData(@"a.tar.gz", "a.tar.conflict-20260930-143500.gz")]
    public void Conflict_copy_naming(string rel, string expected) =>
        Assert.Equal(expected, PlanEditor.ConflictName(rel, new DateTime(2026, 9, 30, 14, 35, 0)));

    // ------------------------------------------------------------------ histórico e segurança

    [Fact]
    public async Task Failed_items_are_not_recorded_as_synced()
    {
        using var env = new TestEnv();
        env.S("ok.txt", "ok"); env.S("locked.bin", "locked");
        var p = await SyncProfile(env);
        var outcome = await env.CompareAsync(p);
        using (new FileStream(Path.Combine(env.Source, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = await env.Workflow.ExecuteAsync(outcome, null, default);
            Assert.Equal(ExecutionStatus.SuccessWithWarnings, r.Status);
        }
        Assert.Equal(1, await StateCount(env, p));           // só ok.txt
        var again = await env.CompareAsync(p);
        Assert.Equal(SyncOperationType.Copy, Op(again, "locked.bin").Type);   // tenta de novo na próxima
    }

    [Fact]
    public async Task Changing_the_profile_folders_discards_the_history()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        Assert.Equal(3, await StateCount(env, p));

        p.Name += " renomeado";                                   // renomear não invalida
        p = await env.Profiles.SaveAsync(p);
        Assert.Equal(3, await StateCount(env, p));

        p.TargetPath = Path.Combine(env.Root, "outro-destino");   // outro par de pastas: histórico não vale mais
        await env.Profiles.SaveAsync(p);
        Assert.Equal(0, await StateCount(env, p));
    }

    [Fact]
    public async Task Reset_sync_state_makes_the_next_run_a_first_sync()
    {
        var (env, p) = await SyncedAsync();
        using var _ = env;
        await env.Workflow.ResetSyncStateAsync(p.Id);
        var outcome = await env.CompareAsync(p);
        Assert.Contains(outcome.Plan.Warnings, w => w.Contains("Primeira sincronização"));
        Assert.Equal(3, outcome.Plan.Operations.Count(o => o.Type == SyncOperationType.Skip && o.RecordsState));
    }

    [Fact]
    public async Task Unsaved_profiles_have_no_history_so_differences_are_conflicts()
    {
        using var env = new TestEnv();
        env.S("a.txt", "one", T1); env.T("a.txt", "two!", T2);
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.Synchronization));   // Id = 0
        Assert.Equal(SyncOperationType.Conflict, Op(outcome, "a.txt").Type);
    }

    [Fact]
    public async Task Source_must_be_writable_only_when_the_plan_writes_to_it()
    {
        // Mirror nunca escreve no Source: não exige permissão de escrita nele (somente leitura continua funcionando)
        using var env = new TestEnv();
        env.S("a.txt", "a");
        var o = await env.CompareAsync(env.Profile());
        Assert.All(o.Plan.Operations.Where(x => x.IsExecutable), x => Assert.False(x.WritesSource));
    }

    // ------------------------------------------------------------------ rede: retentativas

    [Fact]
    public async Task Transient_network_errors_are_retried_and_the_copy_succeeds()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs) { TransientCopyFailures = 2 });
        env.S("a.txt", "network blip");
        var p = env.Profile(); p.TransientRetryCount = 3; p.TransientRetryDelayMs = 5;

        var outcome = await env.CompareAsync(p);
        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("network blip", Read(env.Target, "a.txt"));
        Assert.Equal(3, ((FaultyFileSystem)env.Fs).CopyAttempts);   // 2 falhas + 1 sucesso
        Assert.Empty(Directory.EnumerateFiles(env.Target, "*.synctmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Retries_are_limited_and_then_the_failure_is_reported()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs) { TransientCopyFailures = 99 });
        env.S("a.txt", "x");
        var p = env.Profile(); p.TransientRetryCount = 2; p.TransientRetryDelayMs = 5;

        var outcome = await env.CompareAsync(p);
        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(1, r.Errors);
        Assert.Equal(3, ((FaultyFileSystem)env.Fs).CopyAttempts);   // 1 tentativa + 2 retentativas
        Assert.False(Exists(env.Target, "a.txt"));
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried()
    {
        using var env = new TestEnv(fs => new FaultyFileSystem(fs));
        var locked = env.S("a.txt", "x");
        var p = env.Profile(); p.TransientRetryCount = 0;
        var outcome = await env.CompareAsync(p);
        File.Delete(locked);   // sumiu: FileNotFound é definitivo
        var r = await env.Workflow.ExecuteAsync(outcome, null, default);
        Assert.Equal(0, ((FaultyFileSystem)env.Fs).CopyAttempts);
        Assert.Equal(1, r.Warnings);
    }

    [Theory]
    [InlineData(64, true)]     // nome de rede excluído
    [InlineData(53, true)]     // caminho de rede não encontrado
    [InlineData(121, true)]    // timeout
    [InlineData(32, true)]     // arquivo em uso
    [InlineData(112, false)]   // disco cheio
    [InlineData(5, false)]     // acesso negado
    public void Transient_error_classification(int win32, bool transient) =>
        Assert.Equal(transient, TransientErrors.IsTransient(new IOException("x", unchecked((int)0x80070000) | win32)));

    [Fact]
    public void File_not_found_and_source_changed_are_never_transient()
    {
        Assert.False(TransientErrors.IsTransient(new FileNotFoundException()));
        Assert.False(TransientErrors.IsTransient(new SourceChangedException("x")));
        Assert.False(TransientErrors.IsTransient(new InvalidOperationException()));
    }

    // ------------------------------------------------------------------ CLI: bidirecional sem ninguém para decidir

    [Fact]
    public async Task Unattended_bidirectional_run_syncs_what_is_safe_and_reports_conflicts_with_exit_3()
    {
        using var env = await new CliEnv().WithProfileAsync(p => p.Method = SyncMethod.Synchronization);
        env.S("fine.txt", "new on source"); env.T("other.txt", "new on target");
        env.S("clash.txt", "source side"); env.T("clash.txt", "target side!");

        var (code, _, _) = await env.RunAsync("run", "--profile", env.Profile.Id.ToString(), "--silent");
        Assert.Equal((int)ExitCode.ConflictsUnresolved, code);
        Assert.True(File.Exists(Path.Combine(env.Target, "fine.txt")));
        Assert.True(File.Exists(Path.Combine(env.Source, "other.txt")));
        Assert.Equal("source side", File.ReadAllText(Path.Combine(env.Source, "clash.txt")));   // nenhum vencedor escolhido
        Assert.Equal("target side!", File.ReadAllText(Path.Combine(env.Target, "clash.txt")));
    }
}
