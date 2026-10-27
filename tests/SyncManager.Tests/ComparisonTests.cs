using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;

namespace SyncManager.Tests;

public class ComparisonTests
{
    [Fact] // 1
    public async Task Identical_folders_produce_only_identical_and_skip()
    {
        using var env = new TestEnv();
        env.S("a.txt", "hello"); env.T("a.txt", "hello");
        env.S(@"sub\b.txt", "world"); env.T(@"sub\b.txt", "world");

        var o = await env.CompareAsync(env.Profile());

        Assert.Equal(3, o.Comparison.Analyzed); // a.txt, sub, sub\b.txt
        Assert.Equal(3, o.Comparison.Count(ComparisonStatus.Identical));
        Assert.Equal(0, o.Plan.ExecutableCount);
        Assert.Equal(3, o.Plan.SkipCount);
    }

    [Fact] // 2
    public async Task File_only_in_source()
    {
        using var env = new TestEnv();
        env.S("novo.txt", "x");
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.SourceOnly, TestEnv.StatusOf(o, "novo.txt"));
        Assert.Equal(SyncOperationType.Copy, TestEnv.OpOf(o, "novo.txt"));
    }

    [Fact] // 3
    public async Task File_only_in_target()
    {
        using var env = new TestEnv();
        env.S("keep.txt", "k"); env.T("keep.txt", "k");
        env.T("extra.txt", "x");
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.TargetOnly, TestEnv.StatusOf(o, "extra.txt"));
    }

    [Fact] // 4
    public async Task Source_newer()
    {
        using var env = new TestEnv();
        env.S("a.txt", "new content", TestEnv.BaseTime.AddHours(1));
        env.T("a.txt", "old", TestEnv.BaseTime);
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.SourceNewer, TestEnv.StatusOf(o, "a.txt"));
    }

    [Fact] // 5
    public async Task Target_newer()
    {
        using var env = new TestEnv();
        env.S("a.txt", "old", TestEnv.BaseTime);
        env.T("a.txt", "newer content", TestEnv.BaseTime.AddHours(1));
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.TargetNewer, TestEnv.StatusOf(o, "a.txt"));
    }

    [Fact] // 6
    public async Task Same_timestamp_and_size_but_different_content_is_detected_only_with_hash()
    {
        using var env = new TestEnv();
        env.S("a.bin", "AAAA"); env.T("a.bin", "BBBB");

        var fast = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.Identical, TestEnv.StatusOf(fast, "a.bin")); // sem hash: iguais por tamanho+data

        var p = env.Profile(); p.VerifyContentByHash = true;
        var strict = await env.CompareAsync(p);
        Assert.Equal(ComparisonStatus.DifferentContent, TestEnv.StatusOf(strict, "a.bin"));
        Assert.Equal(SyncOperationType.Update, TestEnv.OpOf(strict, "a.bin"));
    }

    [Fact]
    public async Task Timestamp_difference_within_tolerance_is_identical()
    {
        using var env = new TestEnv();
        env.S("a.txt", "same", TestEnv.BaseTime.AddSeconds(1));
        env.T("a.txt", "same", TestEnv.BaseTime);
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.Identical, TestEnv.StatusOf(o, "a.txt"));
    }

    [Fact]
    public async Task Different_size_with_same_timestamp()
    {
        using var env = new TestEnv();
        env.S("a.txt", "short"); env.T("a.txt", "much longer content");
        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.DifferentSize, TestEnv.StatusOf(o, "a.txt"));
    }

    [Fact]
    public async Task Filtered_files_are_invisible_on_both_sides_and_never_deleted()
    {
        using var env = new TestEnv();
        env.S("a.txt", "a"); env.T("a.txt", "a");
        env.T("lixo.tmp", "tmp");
        env.T(@"node_modules\pkg\x.js", "js");
        var p = env.Profile();
        p.ExcludePatterns.AddRange(new[] { "*.tmp", "node_modules\\" });

        var o = await env.CompareAsync(p);
        Assert.DoesNotContain(o.Comparison.Items, i => i.RelativePath.EndsWith("lixo.tmp"));
        Assert.DoesNotContain(o.Comparison.Items, i => i.RelativePath.Contains("node_modules"));
        Assert.Equal(0, o.Plan.DeleteCount);
    }

    [Theory]
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", "x\\y\\a.TMP", false, true)]
    [InlineData("*.tmp", "a.txt", false, false)]
    [InlineData("Thumbs.db", "d\\thumbs.db", false, true)]
    [InlineData("bin\\", "src\\bin", true, true)]
    [InlineData("bin\\", "src\\bin", false, false)]
    [InlineData("\\build", "build", true, true)]
    [InlineData("\\build", "src\\build", true, false)]
    [InlineData("src\\obj", "a\\src\\obj", true, true)]
    [InlineData("*.log", "a.log.txt", false, false)]
    public void PathFilter_patterns(string pattern, string path, bool isDir, bool expected)
    {
        Assert.Equal(expected, new PathFilter(new[] { pattern }).IsExcluded(path, isDir));
    }

    [Fact]
    public async Task Type_mismatch_is_a_conflict_and_is_never_executed()
    {
        using var env = new TestEnv();
        env.S("x", "i am a file");
        Directory.CreateDirectory(Path.Combine(env.Target, "x"));
        File.WriteAllText(Path.Combine(env.Target, "x", "inner.txt"), "inner");

        var o = await env.CompareAsync(env.Profile());
        Assert.Equal(ComparisonStatus.Conflict, TestEnv.StatusOf(o, "x"));
        Assert.Equal(SyncOperationType.Conflict, TestEnv.OpOf(o, "x"));

        var r = await env.Workflow.ExecuteAsync(o, null, default);
        Assert.True(File.Exists(Path.Combine(env.Target, "x", "inner.txt")));
        Assert.Contains(r.Status, new[] { ExecutionStatus.ConflictsUnresolved, ExecutionStatus.SuccessWithWarnings });
        Assert.Equal(1, r.Conflicts);
    }

    [Fact] // 15
    public async Task Thousands_of_files_are_compared_and_planned_correctly()
    {
        using var env = new TestEnv();
        const int n = 3000;
        for (var i = 0; i < n; i++)
        {
            var rel = Path.Combine($"d{i % 50}", $"f{i}.txt");
            env.S(rel, "data" + i);
            if (i % 3 != 0) env.T(rel, "data" + i);          // 1000 só no Source
            if (i % 10 == 0) env.T(Path.Combine($"d{i % 50}", $"extra{i}.txt"), "x"); // 300 só no Target
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = await env.CompareAsync(env.Profile());
        sw.Stop();

        Assert.Equal(1000, o.Comparison.Count(ComparisonStatus.SourceOnly));
        Assert.Equal(300, o.Comparison.Count(ComparisonStatus.TargetOnly));
        Assert.Equal(1000, o.Plan.CopyCount);
        Assert.Equal(300, o.Plan.DeleteCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"lento demais: {sw.Elapsed}");
    }
}
