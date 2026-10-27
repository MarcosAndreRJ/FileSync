using SyncManager.Application;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;

namespace SyncManager.Tests;

/// <summary>Opções novas do refator visual: filtros com maiúsculas/curingas opcionais e configurações de interface.</summary>
public class Stage5CoreTests
{
    [Theory]
    [InlineData("*.tmp", "A.TMP", false, true)]      // padrão: ignora maiúsculas, curinga ligado
    [InlineData("*.tmp", "A.TMP", true, false)]      // diferenciar maiúsculas
    [InlineData("*.tmp", "a.tmp", true, true)]
    [InlineData("Thumbs.db", "thumbs.db", true, false)]
    public void Case_sensitivity_is_optional(string pattern, string path, bool caseSensitive, bool expected) =>
        Assert.Equal(expected, new PathFilter(new[] { pattern }, null, caseSensitive).IsExcluded(path, false));

    [Theory]
    [InlineData("*.tmp", "a.tmp", false)]            // sem curingas, '*' é texto literal
    [InlineData("*.tmp", "*.tmp", true)]
    [InlineData("a?c.txt", "abc.txt", false)]
    [InlineData("a?c.txt", "a?c.txt", true)]
    public void Wildcards_can_be_turned_off(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new PathFilter(new[] { pattern }, null, false, wildcards: false).IsExcluded(path, false));

    [Fact]
    public void Include_list_honours_the_same_options()
    {
        var f = new PathFilter(Array.Empty<string>(), new[] { "*.DOC" }, caseSensitive: true);
        Assert.True(f.IsExcluded("a.doc", false));       // não casa com *.DOC → fora
        Assert.False(f.IsExcluded("a.DOC", false));
    }

    [Fact]
    public async Task New_profile_and_settings_fields_round_trip_and_old_data_keeps_defaults()
    {
        using var env = new TestEnv();
        var saved = await env.Profiles.SaveAsync(new SyncProfile { Name = "F", SourcePath = "s", TargetPath = "t", FilterCaseSensitive = true, FilterWildcards = false });
        var loaded = (await env.Profiles.GetAsync(saved.Id))!;
        Assert.True(loaded.FilterCaseSensitive);
        Assert.False(loaded.FilterWildcards);

        var defaults = new SyncProfile();
        Assert.False(defaults.FilterCaseSensitive);
        Assert.True(defaults.FilterWildcards);

        Assert.True((await env.Settings.LoadAsync()).ConfirmBeforeExecute);
        Assert.False((await env.Settings.LoadAsync()).StartMinimized);
        await env.Settings.SaveAsync(new AppSettings { StartMinimized = true, ConfirmBeforeExecute = false });
        var s = await env.Settings.LoadAsync();
        Assert.True(s.StartMinimized);
        Assert.False(s.ConfirmBeforeExecute);
    }

    [Fact]
    public async Task Case_sensitive_filter_reaches_the_comparison()
    {
        using var env = new TestEnv();
        env.S("A.TMP", "x"); env.S("b.tmp", "y"); env.S("keep.txt", "z");
        var p = env.Profile();
        p.ExcludePatterns = new List<string> { "*.tmp" };
        p.FilterCaseSensitive = true;
        var o = await env.CompareAsync(p);
        var paths = o.Plan.Operations.Select(op => op.RelativePath).ToList();
        Assert.Contains("A.TMP", paths);                 // não casou (maiúsculas) → continua na comparação
        Assert.DoesNotContain("b.tmp", paths);
    }
}
