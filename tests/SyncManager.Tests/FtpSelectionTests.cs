using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;
using SyncManager.Infrastructure.Ftp;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels.Ftp;
using Xunit;

namespace SyncManager.Tests;

/// <summary>Seleção persistente da tarefa FTP: comportamento real sobre arquivos em disco e banco SQLite temporário.</summary>
public class FtpSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerFtpSel", Guid.NewGuid().ToString("N"));
    private readonly string _src;

    public FtpSelectionTests()
    {
        _src = Path.Combine(_root, "src");
        Write("app/a.txt"); Write("app/sub/b.txt"); Write("app/cache/c.tmp"); Write("config/x.json"); Write("root.txt");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string rel)
    {
        var p = Path.Combine(_src, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, rel);
    }

    private static List<string> Scan(string root, IReadOnlyList<string> inc, IReadOnlyList<string> exc) =>
        FtpComparisonEngine.ScanLocalFiles(root, inc, exc, PathFilter.Empty).Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void A_selecting_a_folder_includes_all_its_files()
    {
        var files = Scan(_src, ["app"], []);
        Assert.Equal(["app/a.txt", "app/cache/c.tmp", "app/sub/b.txt"], files);
    }

    [Fact]
    public void B_excluding_a_subfolder_inside_an_included_folder()
    {
        var files = Scan(_src, ["app"], ["app/cache"]);
        Assert.Equal(["app/a.txt", "app/sub/b.txt"], files);
    }

    [Fact]
    public void B2_more_specific_include_inside_excluded_folder_wins_and_single_files_can_be_included()
    {
        var files = Scan(_src, ["app", "root.txt"], ["app/cache", "app/sub"]);
        Assert.Equal(["app/a.txt", "root.txt"], files);
        files = Scan(_src, ["app", "app/cache/c.tmp"], ["app/cache"]);
        Assert.Contains("app/cache/c.tmp", files);
    }

    [Fact]
    public void C_new_file_in_selected_folder_is_included_automatically_on_next_scan()
    {
        var before = Scan(_src, ["app"], ["app/cache"]);
        Write("app/sub/novo.txt"); Write("app/cache/ignorado.txt"); Write("config/novo-fora.json");
        var after = Scan(_src, ["app"], ["app/cache"]);
        Assert.DoesNotContain("app/sub/novo.txt", before);
        Assert.Contains("app/sub/novo.txt", after);
        Assert.DoesNotContain("app/cache/ignorado.txt", after);
        Assert.DoesNotContain("config/novo-fora.json", after);
    }

    [Fact]
    public async Task D_rules_survive_restart_persisted_in_sqlite()
    {
        var dbPath = Path.Combine(_root, "db", "sel.db");
        FtpSyncTask task;
        Guid connId;
        using (var sp = App.BuildServices(dbPath, Path.Combine(_root, "logs")))
        {
            await sp.GetRequiredService<Database>().MigrateAsync();
            var conn = new FtpConnectionProfile { Name = "srv", Host = "localhost", Username = "u" };
            await sp.GetRequiredService<IFtpConnectionService>().SaveAsync(conn, "pw");
            connId = conn.Id;
            task = new FtpSyncTask { Name = "t", ConnectionProfileId = connId, SourceLocalPath = _src, TargetRemotePath = "/x", Method = FtpSyncMethod.MirrorSource,
                IncludedRelativePaths = ["app", "root.txt"], ExcludedRelativePaths = ["app/cache"] };
            await sp.GetRequiredService<IFtpTaskService>().SaveAsync(task);
        }
        // "reinício": novo contêiner sobre o mesmo arquivo de banco
        using (var sp2 = App.BuildServices(dbPath, Path.Combine(_root, "logs")))
        {
            await sp2.GetRequiredService<Database>().MigrateAsync();
            var loaded = (await sp2.GetRequiredService<IFtpTaskService>().GetAllAsync()).Single();
            Assert.Equal(["app", "root.txt"], loaded.IncludedRelativePaths);
            Assert.Equal(["app/cache"], loaded.ExcludedRelativePaths);
            Assert.Equal(FtpSyncMethod.MirrorSource, loaded.Method);
            Assert.Equal(["app/a.txt", "app/sub/b.txt", "root.txt"], Scan(_src, loaded.IncludedRelativePaths, loaded.ExcludedRelativePaths));
        }
    }

    [Fact]
    public void E_unchecking_in_a_run_checklist_does_not_change_the_permanent_selection()
    {
        var task = new FtpSyncTask { Name = "t", IncludedRelativePaths = ["app"], ExcludedRelativePaths = ["app/cache"] };
        var items = new List<FtpChecklistItem>
        {
            new() { RelativePath = "app/a.txt", ProposedAction = FtpPlannedAction.UploadNew, IsSelected = true },
            new() { RelativePath = "app/sub/b.txt", ProposedAction = FtpPlannedAction.UploadNew, IsSelected = true },
        };
        var vm = new FtpChecklistViewModel(task, items, (_, _) => throw new InvalidOperationException("não executa"));
        vm.Items[0].IsSelected = false;
        vm.SelectNoneCommand.Execute(null);
        Assert.False(items[1].IsSelected);
        Assert.Equal(["app"], task.IncludedRelativePaths);
        Assert.Equal(["app/cache"], task.ExcludedRelativePaths);
        Assert.NotNull(vm);
    }

    [Fact]
    public async Task F_empty_selection_never_authorizes_the_whole_source()
    {
        Assert.Empty(Scan(_src, [], []));
        Assert.Empty(Scan(_src, ["  ", "/"], []));
        var engine = new FtpComparisonEngine(null!);
        var task = new FtpSyncTask { Name = "t", SourceLocalPath = _src, TargetRemotePath = "/x" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CompareAsync(task, new FtpConnectionProfile()));
    }

    [Fact]
    public void Included_path_cannot_escape_the_source_root()
    {
        Write("../fora.txt");
        Assert.Empty(Scan(_src, ["../fora.txt", ".."], []));
    }

    [Fact]
    public void Model_tristate_and_recursive_toggle()
    {
        var m = new FtpSelectionModel();
        Assert.False(m.GetState("app", true));
        m.SetChecked("app", true);
        Assert.True(m.GetState("app", true));
        Assert.True(m.GetState("app/sub/b.txt", false));
        m.SetChecked("app/cache", false);
        Assert.Null(m.GetState("app", true));          // parcial
        Assert.False(m.GetState("app/cache", true));
        m.SetChecked("app/cache", true);               // volta: remove a exceção
        Assert.True(m.GetState("app", true));
        Assert.Empty(m.Excludes);
        m.SetChecked("app", false);
        Assert.False(m.GetState("app", true));
        Assert.False(m.HasSelection);
    }

    [Fact]
    public async Task Tree_node_checkbox_edits_rules_and_refreshes_children()
    {
        await UiThread.Run(async () =>
        {
            var model = new FtpSelectionModel();
            var roots = await FsNode.LoadLocalRootAsync(_src, model);
            var app = roots.Single(r => r.Name == "app");
            await app.EnsureLoadedAsync();
            var cache = app.Children.Single(c => c.Name == "cache");

            app.IsChecked = true;
            Assert.True(cache.IsChecked);
            cache.IsChecked = false;
            Assert.Null(app.IsChecked);
            Assert.Equal(["app"], model.Includes);
            Assert.Equal(["app/cache"], model.Excludes);
        });
    }
}
