using SyncManager.Application;
using SyncManager.Core.Comparison;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Planning;
using SyncManager.Infrastructure;

namespace SyncManager.Tests;

/// <summary>Setas ← → (copiar os itens selecionados) e Delete (remover da lista / excluir fisicamente): sempre via plano.</summary>
public class ManualOperationsTests
{
    private static readonly DateTime T1 = TestEnv.BaseTime.AddHours(1);
    private static readonly DateTime T2 = TestEnv.BaseTime.AddHours(2);

    private static IEnumerable<ComparisonItem> Pick(CompareOutcome o, params string[] paths) =>
        paths.Select(p => o.Comparison.Items.Single(i => i.RelativePath.Equals(p, StringComparison.OrdinalIgnoreCase)));

    private static CompareOutcome With(CompareOutcome o, SyncPlan plan) => o with { Plan = plan };

    // ------------------------------------------------------------------ → copiar do Source para o Target

    [Fact]
    public async Task Right_arrow_copies_selected_items_from_source_to_target_even_under_a_method_that_would_not()
    {
        using var env = new TestEnv();
        env.S("new.txt", "new on source");
        env.S("diff.txt", "source version", T1); env.T("diff.txt", "older target", TestEnv.BaseTime);
        env.S("same.txt", "same"); env.T("same.txt", "same");
        env.T("target-only.txt", "t");
        Directory.CreateDirectory(Path.Combine(env.Source, "newdir"));
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationRefresh));   // Refresh NÃO copiaria new.txt
        Assert.Equal(0, outcome.Plan.CopyCount);

        var manual = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "new.txt", "diff.txt", "same.txt", "target-only.txt", "newdir"), FileSide.Source);

        Assert.Equal(1, manual.Plan.CopyCount);
        Assert.Equal(1, manual.Plan.UpdateCount);
        Assert.Equal(1, manual.Plan.CreateDirectoryCount);
        Assert.Equal(1, manual.Overwrites);
        Assert.Equal(0, manual.OverwritesNewer);
        Assert.Equal(2, manual.NotApplicable.Count);                      // same.txt (igual) e target-only.txt (sem origem)
        Assert.Contains(manual.NotApplicable, m => m.StartsWith("target-only.txt"));

        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("new on source", File.ReadAllText(Path.Combine(env.Target, "new.txt")));
        Assert.Equal("source version", File.ReadAllText(Path.Combine(env.Target, "diff.txt")));
        Assert.True(Directory.Exists(Path.Combine(env.Target, "newdir")));
        Assert.True(File.Exists(Path.Combine(env.Target, "target-only.txt")));   // não selecionado/aplicável: intocado
        // o que foi sobrescrito está guardado
        Assert.Equal("older target", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Target, ".SyncBackup"), "diff.txt", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task Overwriting_a_newer_file_is_flagged_so_the_ui_can_warn()
    {
        using var env = new TestEnv();
        env.S("a.txt", "old source", TestEnv.BaseTime); env.T("a.txt", "NEWER target version", T2);
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));

        var right = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "a.txt"), FileSide.Source);
        Assert.Equal(1, right.OverwritesNewer);
        Assert.True(right.Plan.HasDestructiveOperations);
        Assert.Contains("MAIS NOVO", right.Plan.Operations.Single().Reason);

        var left = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "a.txt"), FileSide.Target);
        Assert.Equal(0, left.OverwritesNewer);   // no sentido contrário o destino (Source) é o mais antigo
    }

    // ------------------------------------------------------------------ ← copiar do Target para o Source

    [Fact]
    public async Task Left_arrow_copies_selected_items_from_target_to_source_preserving_dates_and_backing_up()
    {
        using var env = new TestEnv();
        env.T("only-target.txt", "from target", T1);
        env.S("both.txt", "source version"); env.T("both.txt", "target version wins", T2);
        env.T(@"sub\deep.txt", "deep", T1);
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.MirrorSourceToTarget));   // Mirror apagaria do Target!

        var manual = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "only-target.txt", "both.txt", @"sub", @"sub\deep.txt"), FileSide.Target);
        Assert.Equal(2, manual.Plan.CopyCount);
        Assert.Equal(1, manual.Plan.UpdateCount);
        Assert.Equal(1, manual.Plan.CreateDirectoryCount);
        Assert.Equal(0, manual.Plan.DeleteCount);
        Assert.All(manual.Plan.Operations, o => { Assert.Equal(FileSide.Target, o.From); Assert.Equal(FileSide.Source, o.To); });

        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("from target", File.ReadAllText(Path.Combine(env.Source, "only-target.txt")));
        Assert.Equal("target version wins", File.ReadAllText(Path.Combine(env.Source, "both.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(env.Source, "sub", "deep.txt")));
        Assert.Equal(T1, File.GetLastWriteTimeUtc(Path.Combine(env.Source, "only-target.txt")));
        // a versão do Source que foi sobrescrita ficou na pasta de segurança do SOURCE
        Assert.Equal("source version", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Source, ".SyncBackup"), "both.txt", SearchOption.AllDirectories).Single()));
        // o Target não foi tocado
        Assert.Equal("target version wins", File.ReadAllText(Path.Combine(env.Target, "both.txt")));
        Assert.False(Directory.Exists(Path.Combine(env.Target, ".SyncBackup")));
    }

    [Fact]
    public async Task Manual_copy_does_not_apply_to_errors_or_type_conflicts()
    {
        using var env = new TestEnv();
        env.S("x", "file on source");
        Directory.CreateDirectory(Path.Combine(env.Target, "x"));
        var outcome = await env.CompareAsync(env.Profile());

        var manual = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "x"), FileSide.Source);
        Assert.Empty(manual.Plan.Operations);
        Assert.Single(manual.NotApplicable);
    }

    [Fact]
    public async Task Manual_copy_revalidates_and_refuses_when_the_file_changed_after_the_comparison()
    {
        using var env = new TestEnv();
        env.S("a.txt", "v1");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));
        var manual = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "a.txt"), FileSide.Source);

        env.S("a.txt", "v2 changed meanwhile", T2);
        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.Equal(1, r.Warnings);
        Assert.False(File.Exists(Path.Combine(env.Target, "a.txt")));
    }

    // ------------------------------------------------------------------ atualizar a lista depois

    [Fact]
    public async Task Refresh_updates_only_the_touched_items_and_keeps_other_decisions()
    {
        var env = new TestEnv();
        using var _ = env;
        env.S("copy-me.txt", "c"); env.S("other.txt", "o");
        env.S("clash.txt", "s", T1); env.T("clash.txt", "t!", T2);
        // bidirecional sem histórico: clash.txt é conflito; copy-me/other são cópias
        var p = await env.Profiles.SaveAsync(env.Profile(SyncMethod.Synchronization));
        var outcome = await env.CompareAsync(p);
        Assert.Equal(1, outcome.Plan.ConflictCount);

        var manual = PlanEditor.CopyItems(outcome.Plan, Pick(outcome, "copy-me.txt"), FileSide.Source);
        await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);

        var refreshed = await env.Workflow.RefreshItemsAsync(outcome, new[] { "copy-me.txt" }, default);
        Assert.Equal(ComparisonStatus.Identical, refreshed.Comparison.Items.Single(i => i.RelativePath == "copy-me.txt").Status);
        Assert.Equal(SyncOperationType.Skip, refreshed.Plan.Operations.Single(o => o.RelativePath == "copy-me.txt").Type);
        Assert.Equal(SyncOperationType.Copy, refreshed.Plan.Operations.Single(o => o.RelativePath == "other.txt").Type);   // intocado
        Assert.Equal(1, refreshed.Plan.ConflictCount);
        Assert.Equal(outcome.Comparison.Items.Count, refreshed.Comparison.Items.Count);
        Assert.Equal(outcome.Comparison.TargetEntryCount + 1, refreshed.Comparison.TargetEntryCount);
    }

    [Fact]
    public async Task Refresh_drops_items_that_no_longer_exist_on_either_side()
    {
        using var env = new TestEnv();
        env.S("gone.txt", "g"); env.T("gone.txt", "g"); env.S("stay.txt", "s");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));
        File.Delete(Path.Combine(env.Source, "gone.txt")); File.Delete(Path.Combine(env.Target, "gone.txt"));

        var refreshed = await env.Workflow.RefreshItemsAsync(outcome, new[] { "gone.txt" }, default);
        Assert.DoesNotContain(refreshed.Comparison.Items, i => i.RelativePath == "gone.txt");
        Assert.DoesNotContain(refreshed.Plan.Operations, o => o.RelativePath == "gone.txt");
        Assert.Equal(outcome.Comparison.SourceEntryCount - 1, refreshed.Comparison.SourceEntryCount);
    }

    // ------------------------------------------------------------------ Delete: remover da lista / excluir fisicamente

    [Fact]
    public async Task Remove_from_list_only_turns_the_items_into_skips_and_never_touches_disk()
    {
        using var env = new TestEnv();
        env.S("keep.txt", "k");
        env.T("extra1.txt", "x"); env.T("extra2.txt", "y");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.MirrorSourceToTarget));
        Assert.Equal(2, outcome.Plan.DeleteCount);

        var edited = PlanEditor.RemoveFromPlan(outcome.Plan, new[] { "extra1.txt" });
        Assert.Equal(1, edited.DeleteCount);
        Assert.Equal(SyncOperationType.Skip, edited.Operations.Single(o => o.RelativePath == "extra1.txt").Type);
        Assert.Contains("Removido da lista", edited.Operations.Single(o => o.RelativePath == "extra1.txt").Reason);

        await env.Workflow.ExecuteAsync(With(outcome, edited), null, default);
        Assert.True(File.Exists(Path.Combine(env.Target, "extra1.txt")));    // protegido por ter sido removido da lista
        Assert.False(File.Exists(Path.Combine(env.Target, "extra2.txt")));
    }

    [Fact]
    public async Task Physical_delete_at_each_side_uses_the_safety_folder_of_that_side()
    {
        using var env = new TestEnv();
        env.S("both.txt", "s"); env.T("both.txt", "s");
        env.S("src-only.txt", "so"); env.T("tgt-only.txt", "to");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));

        // só no Source
        var atSource = PlanEditor.DeleteItems(outcome.Plan, Pick(outcome, "src-only.txt", "tgt-only.txt"), atSource: true, atTarget: false);
        Assert.Equal(1, atSource.Plan.DeleteCount);
        Assert.Single(atSource.NotApplicable);                                // tgt-only não existe no Source

        // ambos os lados
        var both = PlanEditor.DeleteItems(outcome.Plan, Pick(outcome, "both.txt"), atSource: true, atTarget: true);
        Assert.Equal(2, both.Plan.DeleteCount);
        Assert.Equal(1, both.Plan.DeleteAtSourceCount);
        Assert.Equal(1, both.Plan.DeleteAtTargetCount);

        var r1 = await env.Workflow.ExecuteAsync(With(outcome, atSource.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r1.Status);
        Assert.False(File.Exists(Path.Combine(env.Source, "src-only.txt")));
        Assert.Equal("so", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(env.Source, ".SyncBackup"), "src-only.txt", SearchOption.AllDirectories).Single()));

        var r2 = await env.Workflow.ExecuteAsync(With(outcome, both.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r2.Status);
        Assert.False(File.Exists(Path.Combine(env.Source, "both.txt")));
        Assert.False(File.Exists(Path.Combine(env.Target, "both.txt")));
        Assert.True(File.Exists(Path.Combine(env.Target, "tgt-only.txt")));   // intocado
        Assert.NotNull(r2.SafetyFolder);
    }

    [Fact]
    public async Task Physical_delete_honors_the_permanent_strategy()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a"); env.T("a.txt", "a");
        var p = env.Profile(SyncMethod.ReplicationAugment); p.DeleteStrategy = DeleteStrategy.Permanent;
        var outcome = await env.CompareAsync(p);

        var manual = PlanEditor.DeleteItems(outcome.Plan, Pick(outcome, "a.txt"), true, true);
        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Null(r.SafetyFolder);
        Assert.False(Directory.Exists(Path.Combine(env.Source, ".SyncBackup")));
        Assert.False(Directory.Exists(Path.Combine(env.Target, ".SyncBackup")));
    }

    [Fact]
    public async Task Deleting_a_folder_with_its_files_removes_children_first()
    {
        using var env = new TestEnv();
        env.T(@"d\inner.txt", "i");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));

        var manual = PlanEditor.DeleteItems(outcome.Plan, Pick(outcome, "d", @"d\inner.txt"), false, true);
        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.False(Directory.Exists(Path.Combine(env.Target, "d")));
    }

    [Fact]
    public async Task Deleting_only_the_folder_row_keeps_a_folder_that_still_has_files()
    {
        using var env = new TestEnv();
        env.T(@"d\inner.txt", "i");
        var outcome = await env.CompareAsync(env.Profile(SyncMethod.ReplicationAugment));
        var manual = PlanEditor.DeleteItems(outcome.Plan, Pick(outcome, "d"), false, true);
        var r = await env.Workflow.ExecuteAsync(With(outcome, manual.Plan), null, default);
        Assert.True(File.Exists(Path.Combine(env.Target, "d", "inner.txt")));   // nunca exclui recursivamente
        Assert.Equal(1, r.Warnings);
    }

    // ------------------------------------------------------------------ desempenho / escala (100.000+ itens)

    [Fact]
    public async Task One_hundred_fifty_thousand_items_compare_and_plan_quickly_with_history()
    {
        const int n = 150_000;
        var tol = TimeSpan.FromSeconds(2);
        var baseTime = TestEnv.BaseTime;
        var sourceEntries = new List<FileEntry>(n);
        var targetEntries = new List<FileEntry>(n);
        var states = new Dictionary<string, FileSyncState>(n, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < n; i++)
        {
            var rel = $@"d{i % 500}\sub{i % 7}\file{i}.dat";
            var size = 1000 + i % 100;
            states[rel] = new FileSyncState(1, rel, false, size, size, baseTime, baseTime, null, null, DateTimeOffset.UtcNow);

            var sourceTime = i % 150 == 0 ? baseTime.AddHours(1) : baseTime;       // 1.000 mudaram só no Source
            var keepInSource = i % 300 != 7;                                       // 500 excluídos no Source
            var keepInTarget = i % 1000 != 3;                                      // 150 excluídos no Target
            if (keepInSource) sourceEntries.Add(new FileEntry(rel, @"S:\" + rel, false, size, sourceTime, FileAttributes.Normal));
            if (keepInTarget) targetEntries.Add(new FileEntry(rel, @"T:\" + rel, false, size, baseTime, FileAttributes.Normal));
        }
        for (var i = 0; i < 2000; i++)                                              // 2.000 novos no Target
            targetEntries.Add(new FileEntry($@"new\n{i}.dat", $@"T:\new\n{i}.dat", false, 10, baseTime, FileAttributes.Normal));

        var source = new ScanResult("S:\\", sourceEntries, Array.Empty<UnreliablePath>(), 0);
        var target = new ScanResult("T:\\", targetEntries, Array.Empty<UnreliablePath>(), 0);
        var comparer = new DirectoryComparer(new FileComparer(new PhysicalFileSystem()));   // sem hash: nunca toca o disco
        var options = new CompareOptions("S:\\", "T:\\", tol, false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var comparison = await comparer.CompareAsync(source, target, options, null, default, states);
        var plan = new SyncPlanner().Build(comparison, new SyncProfile { Method = SyncMethod.Synchronization }, states.Count);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"lento demais: {sw.Elapsed}");
        Assert.Equal(n + 2000, comparison.Analyzed);
        Assert.Equal(1000, plan.UpdateCount);                                        // Source mudou → Target
        Assert.Equal(2000, plan.CopyCount);                                          // novos no Target → Source
        Assert.Equal(500 + 150, plan.DeleteCount);                                   // exclusões propagadas (500 no Source, 150 no Target)
        Assert.Empty(plan.StaleStatePaths);
        Assert.Equal(0, plan.ConflictCount);
    }

}
