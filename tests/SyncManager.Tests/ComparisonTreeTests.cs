using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.ViewModels;
using Xunit;

namespace SyncManager.Tests;

public class ComparisonTreeTests
{
    private static SyncOperation MakeDummyOp(string relPath, bool isDir, SyncOperationType type, ComparisonStatus status)
    {
        var item = new ComparisonItem(
            relPath,
            isDir ? $"C:\\src\\{relPath}" : $"C:\\src\\{relPath}",
            isDir ? $"C:\\dst\\{relPath}" : $"C:\\dst\\{relPath}",
            isDir,
            true,
            true,
            isDir ? null : 1024,
            isDir ? null : 1024,
            DateTime.UtcNow,
            DateTime.UtcNow,
            status);

        return new SyncOperation(type, item, item.SourcePath, item.TargetPath, 1024, "Teste", FileSide.Source, FileSide.Target);
    }

    [Fact]
    public void HierarchicalComparisonManager_BuildsTreeStructureCorrectly()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("pasta\\arquivo1.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("pasta\\sub\\arquivo2.txt", false, SyncOperationType.Update, ComparisonStatus.SourceNewer),
            MakeDummyOp("raiz.txt", false, SyncOperationType.Skip, ComparisonStatus.Identical)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 3, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();

        manager.Build(plan);

        Assert.NotEmpty(manager.FlatVisibleRows);
        var pastaNode = manager.FindByPath("pasta");
        Assert.NotNull(pastaNode);
        Assert.True(pastaNode.IsDirectory);

        var subNode = manager.FindByPath("pasta\\sub");
        Assert.NotNull(subNode);
        Assert.True(subNode.IsDirectory);

        var file1 = manager.FindByPath("pasta\\arquivo1.txt");
        Assert.NotNull(file1);
        Assert.False(file1.IsDirectory);
    }

    [Fact]
    public void ExpansionAndCollapse_UpdatesFlatVisibleRows()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("pasta\\arquivo1.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("raiz.txt", false, SyncOperationType.Skip, ComparisonStatus.Identical)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 2, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);

        var initialCount = manager.FlatVisibleRows.Count;

        var pastaNode = manager.FindByPath("pasta");
        Assert.NotNull(pastaNode);

        // Recolher pasta
        pastaNode.IsExpanded = false;
        Assert.True(manager.FlatVisibleRows.Count < initialCount);
        Assert.DoesNotContain(manager.FlatVisibleRows, n => n.RelativePath == "pasta\\arquivo1.txt");

        // Expandir pasta novamente
        pastaNode.IsExpanded = true;
        Assert.Equal(initialCount, manager.FlatVisibleRows.Count);
        Assert.Contains(manager.FlatVisibleRows, n => n.RelativePath == "pasta\\arquivo1.txt");
    }

    [Fact]
    public void TristateCheckbox_PropagatesToChildrenAndUpdatesParent()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("pasta\\arquivo1.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("pasta\\arquivo2.txt", false, SyncOperationType.Update, ComparisonStatus.SourceNewer)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 2, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);

        var pastaNode = manager.FindByPath("pasta")!;
        var file1 = manager.FindByPath("pasta\\arquivo1.txt")!;
        var file2 = manager.FindByPath("pasta\\arquivo2.txt")!;

        // Desmarcar pasta pai deve desmarcar os dois filhos
        pastaNode.IsChecked = false;
        Assert.False(file1.IsChecked);
        Assert.False(file2.IsChecked);

        // Marcar apenas um filho deixa a pasta pai como indeterminada (null)
        file1.IsChecked = true;
        Assert.Null(pastaNode.IsChecked);

        // Marcar o segundo filho deixa a pasta pai como true
        file2.IsChecked = true;
        Assert.True(pastaNode.IsChecked);
    }

    [Fact]
    public void HideNode_RemovesFromVisibleWithoutLosingNodeInstance()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("arquivo1.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("arquivo2.txt", false, SyncOperationType.Skip, ComparisonStatus.Identical)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 2, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);

        var node1 = manager.FindByPath("arquivo1.txt")!;
        manager.HideNode(node1);

        Assert.Equal(1, manager.HiddenCount);
        Assert.DoesNotContain(manager.FlatVisibleRows, n => n.RelativePath == "arquivo1.txt");

        manager.RestoreHidden();
        Assert.Equal(0, manager.HiddenCount);
        Assert.Contains(manager.FlatVisibleRows, n => n.RelativePath == "arquivo1.txt");
    }

    [Fact]
    public void Search_AutoExpandsParentsOfMatchingNodes()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("pasta\\sub\\item_especial.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("outro.txt", false, SyncOperationType.Skip, ComparisonStatus.Identical)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 2, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);
        manager.CollapseAll();

        var pasta = manager.FindByPath("pasta")!;
        var sub = manager.FindByPath("pasta\\sub")!;
        Assert.False(pasta.IsExpanded);
        Assert.False(sub.IsExpanded);

        manager.SearchText = "especial";

        Assert.True(pasta.IsExpanded);
        Assert.True(sub.IsExpanded);
        Assert.Contains(manager.FlatVisibleRows, n => n.RelativePath == "pasta\\sub\\item_especial.txt");
    }

    [Fact]
    public void DisplayNames_ShowDashWhenFileMissingOnOneSide()
    {
        var sourceOnlyItem = new ComparisonItem(
            "origem_so.txt", "C:\\src\\origem_so.txt", "C:\\dst\\origem_so.txt",
            false, true, false, 500, null, DateTime.UtcNow, null, ComparisonStatus.SourceOnly);
        var targetOnlyItem = new ComparisonItem(
            "destino_so.txt", "C:\\src\\destino_so.txt", "C:\\dst\\destino_so.txt",
            false, false, true, null, 700, null, DateTime.UtcNow, ComparisonStatus.TargetOnly);

        var ops = new List<SyncOperation>
        {
            new SyncOperation(SyncOperationType.Copy, sourceOnlyItem, sourceOnlyItem.SourcePath, sourceOnlyItem.TargetPath, 500, "Copia", FileSide.Source, FileSide.Target),
            new SyncOperation(SyncOperationType.Delete, targetOnlyItem, targetOnlyItem.SourcePath, targetOnlyItem.TargetPath, 700, "Exclui", FileSide.Target, FileSide.Target)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 2, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);

        var nodeSourceOnly = manager.FindByPath("origem_so.txt")!;
        Assert.Equal("origem_so.txt", nodeSourceOnly.SourceNameDisplay);
        Assert.Equal("—", nodeSourceOnly.TargetNameDisplay);

        var nodeTargetOnly = manager.FindByPath("destino_so.txt")!;
        Assert.Equal("—", nodeTargetOnly.SourceNameDisplay);
        Assert.Equal("destino_so.txt", nodeTargetOnly.TargetNameDisplay);
    }

    [Fact]
    public void Counters_ReflectComparisonStatusesAccurately()
    {
        var ops = new List<SyncOperation>
        {
            MakeDummyOp("arq1.txt", false, SyncOperationType.Copy, ComparisonStatus.SourceOnly),
            MakeDummyOp("arq2.txt", false, SyncOperationType.Update, ComparisonStatus.SourceNewer),
            MakeDummyOp("arq3.txt", false, SyncOperationType.Skip, ComparisonStatus.Identical)
        };

        var plan = new SyncPlan(SyncMethod.MirrorSourceToTarget, "C:\\src", "C:\\dst", ops, 3, 0.3, null, Array.Empty<string>());
        var manager = new HierarchicalComparisonManager();
        manager.Build(plan);

        Assert.Equal(3, manager.TotalAnalyzed);
        Assert.Equal(1, manager.IdenticalCount);
        Assert.Equal(2, manager.DifferentCount);
        Assert.Equal(1, manager.SourceOnlyCount);
        Assert.Equal(2, manager.ExecutableCheckedCount);
    }
}
