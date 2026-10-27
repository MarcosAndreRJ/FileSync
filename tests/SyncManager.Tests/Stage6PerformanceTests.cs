using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views.Pages;

namespace SyncManager.Tests;

/// <summary>Etapa 6: a lista do plano continua leve com 100 mil operações (virtualização de linhas e colunas).</summary>
public class Stage6PerformanceTests
{
    private sealed record PlanHost(PlanPreviewViewModel Plan);

    private static SyncPlan BigPlan(int count)
    {
        var ops = new List<SyncOperation>(count);
        var modified = TestEnv.BaseTime;
        for (var i = 0; i < count; i++)
        {
            var rel = $"pasta{i % 500:000}\\sub{i % 37}\\arquivo_{i:000000}.dat";
            var status = i % 10 == 0 ? ComparisonStatus.Identical : ComparisonStatus.SourceOnly;
            var item = new ComparisonItem(rel, @"C:\S\" + rel, @"C:\T\" + rel, false, true, status != ComparisonStatus.TargetOnly && true,
                1000 + i, status == ComparisonStatus.Identical ? 1000 + i : null, modified, null, status);
            ops.Add(status == ComparisonStatus.Identical
                ? new SyncOperation(SyncOperationType.Skip, item, item.SourcePath, item.TargetPath, 0, "igual")
                : new SyncOperation(SyncOperationType.Copy, item, item.SourcePath, item.TargetPath, 1000 + i, "só no Source"));
        }
        return new SyncPlan(SyncMethod.MirrorSourceToTarget, @"C:\S", @"C:\T", ops, count, 0.3, null, Array.Empty<string>());
    }

    [Fact]
    public async Task Plan_page_with_100k_operations_is_virtualized_and_filters_quickly()
    {
        const int N = 100_000;
        var plan = BigPlan(N);

        await UiThread.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var vm = new PlanPreviewViewModel(plan);
            var buildMs = sw.ElapsedMilliseconds;
            Assert.Equal(N - N / 10, vm.VisibleCount);                   // filtro padrão: sem SKIP
            Assert.True(buildMs < 10_000, $"montar o plano levou {buildMs} ms");

            var view = new PlanView { DataContext = new PlanHost(vm) };
            var w = new Window { Content = view, Width = 1200, Height = 700, ShowInTaskbar = false };
            w.Show();
            await UiThread.Settle(); await Task.Delay(300); await UiThread.Settle();
            var grid = (DataGrid)LogicalTreeHelper.FindLogicalNode(view, "PlanGrid")!;
            var rows = Descendants<DataGridRow>(grid).Count();
            Assert.InRange(rows, 1, 200);                               // só o que cabe na tela (+ reserva), nunca 90 mil

            sw.Restart();
            vm.Search = "arquivo_05";                                   // pesquisa sobre 100 mil itens
            var searchMs = sw.ElapsedMilliseconds;
            Assert.Equal(9_000, vm.VisibleCount);                        // arquivo_050000..059999 = 10 mil itens; 10% são SKIP
            Assert.True(searchMs < 5_000, $"pesquisar levou {searchMs} ms");

            sw.Restart();
            vm.SelectedFilter = vm.Filters.First(f => f.Label == "Todas");
            vm.Search = "";
            Assert.Equal(N, vm.VisibleCount);
            Assert.True(sw.ElapsedMilliseconds < 5_000, $"trocar filtro levou {sw.ElapsedMilliseconds} ms");

            var csvStart = sw.ElapsedMilliseconds;
            sw.Restart();
            var csv = vm.ToCsv();
            Assert.True(csv.Length > N * 40);
            Assert.True(sw.ElapsedMilliseconds < 10_000, $"exportar CSV levou {sw.ElapsedMilliseconds} ms ({csvStart})");
            w.Close();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
