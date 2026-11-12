using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels;
using Xunit;

namespace SyncManager.Tests;

/// <summary>Fluxo "salvar a comparação (origem/destino/método) como perfil" na tela Comparação.</summary>
public class SaveComparisonProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerSaveCmp", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;
    private readonly FakeDialogs _dialogs = new();

    public SaveComparisonProfileTests()
    {
        _sp = App.BuildServices(Path.Combine(_root, "db", "s.db"), Path.Combine(_root, "logs"));
        _sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.Combine(_root, "origem")); Directory.CreateDirectory(Path.Combine(_root, "destino"));
    }

    public void Dispose() { _sp.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private MainViewModel NewVm()
    {
        var vm = new MainViewModel(_sp.GetRequiredService<SyncWorkflow>(), _dialogs, _sp.GetRequiredService<ILogService>(),
            _sp.GetRequiredService<IProfileRepository>(), _sp.GetRequiredService<SettingsService>(), _sp);
        vm.SourcePath = Path.Combine(_root, "origem");
        vm.TargetPath = Path.Combine(_root, "destino");
        return vm;
    }

    private Task<IReadOnlyList<SyncManager.Core.Models.SyncProfile>> Stored() => _sp.GetRequiredService<IProfileRepository>().GetAllAsync();

    [Fact]
    public async Task Requires_source_and_target_before_saving()
    {
        var vm = NewVm(); vm.SourcePath = "";
        await vm.SaveComparisonAsProfileAsync(false);
        Assert.Single(_dialogs.Errors);
        Assert.Empty(_dialogs.TextPrompts);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task First_save_asks_name_and_stores_paths_and_method()
    {
        var vm = NewVm();
        vm.SelectedMethod = vm.Methods.First(m => m.Method == SyncManager.Core.Enums.SyncMethod.ReplicationAugment);
        _dialogs.TextAnswer = "Meu perfil";
        await vm.SaveComparisonAsProfileAsync(false);

        var saved = Assert.Single(await Stored());
        Assert.Equal("Meu perfil", saved.Name);
        Assert.Equal(Path.Combine(_root, "origem"), saved.SourcePath);
        Assert.Equal(Path.Combine(_root, "destino"), saved.TargetPath);
        Assert.Equal(SyncManager.Core.Enums.SyncMethod.ReplicationAugment, saved.Method);
        Assert.Equal("Meu perfil", vm.ProfileName);
        Assert.Equal(saved.Id, vm.SelectedProfileItem?.Id);                    // o seletor "Perfil ativo" já mostra o perfil
        Assert.Contains(vm.AvailableProfiles, p => p.Id == saved.Id);
        Assert.False(vm.IsDirty);
        Assert.Contains("origem", _dialogs.TextPrompts.Single());              // sugestão de nome vem das pastas
    }

    [Fact]
    public async Task Cancelling_the_name_prompt_saves_nothing()
    {
        var vm = NewVm(); _dialogs.TextAnswer = null;
        await vm.SaveComparisonAsProfileAsync(false);
        Assert.Empty(await Stored());
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Saving_again_updates_the_active_profile_without_asking_the_name()
    {
        var vm = NewVm(); _dialogs.TextAnswer = "P1";
        await vm.SaveComparisonAsProfileAsync(false);
        var id = (await Stored()).Single().Id;

        Directory.CreateDirectory(Path.Combine(_root, "outro"));
        vm.TargetPath = Path.Combine(_root, "outro");
        await vm.SaveComparisonAsProfileAsync(false);

        var all = await Stored();
        Assert.Single(all);
        Assert.Equal(id, all[0].Id);
        Assert.Equal(Path.Combine(_root, "outro"), all[0].TargetPath);
        Assert.Single(_dialogs.TextPrompts);                                    // só perguntou na primeira vez
    }

    [Fact]
    public async Task Save_as_creates_a_new_profile_and_keeps_the_original()
    {
        var vm = NewVm(); _dialogs.TextAnswer = "P1";
        await vm.SaveComparisonAsProfileAsync(false);
        _dialogs.TextAnswer = "P2";
        vm.TargetPath = Path.Combine(_root, "origem");
        await vm.SaveComparisonAsProfileAsync(true);

        var all = (await Stored()).OrderBy(p => p.Name).ToList();
        Assert.Equal(["P1", "P2"], all.Select(p => p.Name).ToList());
        Assert.Equal(Path.Combine(_root, "destino"), all[0].TargetPath);        // original intacto
        Assert.Equal(Path.Combine(_root, "origem"), all[1].TargetPath);
        Assert.Equal("P2", vm.ProfileName);
    }

    [Fact]
    public async Task Duplicate_name_reports_error_and_restores_previous_state()
    {
        var vm = NewVm(); _dialogs.TextAnswer = "P1";
        await vm.SaveComparisonAsProfileAsync(false);
        _dialogs.TextAnswer = "P1";                                              // mesmo nome em "Salvar como"
        await vm.SaveComparisonAsProfileAsync(true);

        Assert.Single(await Stored());
        Assert.NotEmpty(_dialogs.Errors);
        Assert.Equal("P1", vm.ProfileName);
        Assert.Equal((await Stored()).Single().Id, vm.SelectedProfileItem?.Id);
    }
}
