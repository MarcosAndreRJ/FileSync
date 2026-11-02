using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Application.Ftp;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.ViewModels.Ftp;
using SyncManager.Wpf.Views;
using SyncManager.Wpf.Views.Ftp;
using Xunit;

namespace SyncManager.Tests;

/// <summary>Assistentes de conexão (3 etapas) e de tarefa (6 etapas) + renderização das telas reais para evidência.</summary>
public class FtpWizardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SyncManagerFtpWiz", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;

    public FtpWizardTests()
    {
        _sp = App.BuildServices(Path.Combine(_root, "db", "w.db"), Path.Combine(_root, "logs"));
        _sp.GetRequiredService<Database>().MigrateAsync().GetAwaiter().GetResult();
    }

    public void Dispose() { _sp.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void App_starts_on_home_screen()
    {
        Assert.Equal(AppScreen.Home, NewMain().CurrentScreen);
    }

    private MainViewModel NewMain() => new(
        _sp.GetRequiredService<SyncWorkflow>(), new FakeDialogs(), _sp.GetRequiredService<ILogService>(),
        _sp.GetRequiredService<IProfileRepository>(), _sp.GetRequiredService<SettingsService>(), _sp);

    [Fact]
    public async Task New_connection_button_command_raises_the_dialog_request_and_view_opens_wizard()
    {
        await UiThread.Run(() =>
        {
            var vm = _sp.GetRequiredService<FtpConnectionsViewModel>();
            var view = new FtpConnectionsView { DataContext = vm };
            // o diálogo modal bloquearia o teste: a View só deve ter assinado o evento exatamente uma vez
            FtpConnectionEditViewModel? received = null;
            var before = 0;
            vm.RequestEditConnectionDialog += e => received = e;   // inscrição extra do teste
            // reatribuir o DataContext não pode duplicar inscrições da View
            view.DataContext = null; view.DataContext = vm;
            var field = typeof(FtpConnectionsViewModel).GetField("RequestEditConnectionDialog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var del = (Delegate?)field.GetValue(vm);
            before = del!.GetInvocationList().Length;
            Assert.Equal(2, before);   // View (1) + teste (1)
            Assert.True(vm.NewConnectionCommand.CanExecute(null));
            Assert.NotNull(view.FindName("NewConnectionButton"));
            Assert.Null(received);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Connection_wizard_validates_per_step_keeps_state_and_saves()
    {
        var vm = _sp.GetRequiredService<Func<FtpConnectionProfile, FtpConnectionEditViewModel>>()(new FtpConnectionProfile());
        Assert.Equal(1, vm.Step);
        vm.NavigateCommand.Execute(3);                      // navegação lateral não contorna validações
        Assert.Equal(1, vm.Step);
        Assert.True(vm.HasNameError);
        vm.NextCommand.Execute(null);                       // nome vazio: não avança
        Assert.Equal(1, vm.Step);
        Assert.True(vm.HasValidationMessage);

        vm.Name = "Servidor teste"; vm.Host = "127.0.0.1"; vm.Port = 21; vm.Username = "user";
        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.Step);

        vm.CertificatePinning = true;                       // pinning sem thumbprint bloqueia
        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.Step);
        vm.CertificatePinning = false; vm.TimeoutSeconds = 7;
        vm.NextCommand.Execute(null);                       // "Testar conexão →"
        Assert.Equal(3, vm.Step);
        Assert.NotNull(vm.Diagnostics);
        Assert.All(vm.Steps.Take(2), s => Assert.True(s.IsDone));

        vm.BackCommand.Execute(null); vm.BackCommand.Execute(null);
        Assert.Equal(1, vm.Step);
        Assert.Equal("Servidor teste", vm.Name);            // estado preservado
        Assert.Equal(7, vm.TimeoutSeconds);

        vm.Password = "segredo";
        Assert.False(vm.TestPassed);                         // nunca apresentada como validada sem teste real
        vm.SaveCommand.Execute(null);
        await Task.Delay(300);
        var svc = _sp.GetRequiredService<IFtpConnectionService>();
        Assert.Empty(await svc.GetAllAsync());               // 1º clique: pede confirmação consciente
        Assert.Contains("NÃO validada", vm.ValidationMessage);
        Assert.Equal(3, vm.Step);
        vm.SaveCommand.Execute(null);                        // 2º clique confirma salvar sem validação
        for (var i = 0; i < 50 && (await svc.GetAllAsync()).Count == 0; i++) await Task.Delay(100);
        var saved = (await svc.GetAllAsync()).Single();
        Assert.Equal("Servidor teste", saved.Name);
        Assert.Equal(7, saved.TimeoutSeconds);
        Assert.Equal("segredo", await svc.GetPasswordAsync(saved.Id));
    }

    [Fact]
    public async Task Connection_wizard_cancel_does_not_persist()
    {
        var vm = _sp.GetRequiredService<Func<FtpConnectionProfile, FtpConnectionEditViewModel>>()(new FtpConnectionProfile { Name = "x", Host = "h", Username = "u" });
        var cancelled = false; vm.RequestCancel += () => cancelled = true;
        vm.CancelCommand.Execute(null);
        Assert.True(cancelled);
        Assert.Empty(await _sp.GetRequiredService<IFtpConnectionService>().GetAllAsync());
    }

    private async Task<(FtpConnectionProfile conn, string src)> SeedAsync()
    {
        var conn = new FtpConnectionProfile { Name = "srv", Host = "127.0.0.1", Port = 1, Username = "u" };
        await _sp.GetRequiredService<IFtpConnectionService>().SaveAsync(conn, "pw");
        var src = Path.Combine(_root, "origem");
        Directory.CreateDirectory(Path.Combine(src, "app"));
        File.WriteAllText(Path.Combine(src, "app", "a.txt"), "a");
        return (conn, src);
    }

    [Fact]
    public async Task Task_wizard_six_steps_requires_selection_preserves_state_and_saves_rules()
    {
        var (conn, src) = await SeedAsync();
        var vm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(new FtpSyncTask());
        for (var i = 0; i < 50 && vm.AvailableProfiles.Count == 0; i++) await Task.Delay(50);

        vm.NextCommand.Execute(null); Assert.Equal(1, vm.Step);          // sem nome
        vm.Name = "Deploy"; vm.SelectedProfile = vm.AvailableProfiles.First(); vm.SourceLocalPath = Path.Combine(_root, "nao-existe"); vm.TargetRemotePath = "/site";
        vm.NextCommand.Execute(null); Assert.Equal(2, vm.Step);          // dados da etapa 1 completos
        vm.NextCommand.Execute(null); Assert.Equal(2, vm.Step);          // pasta local inexistente bloqueia a etapa 2
        Assert.True(vm.HasSourceError);
        vm.SourceLocalPath = src;
        vm.NextCommand.Execute(null); Assert.Equal(3, vm.Step);
        vm.SyncMethod = FtpSyncMethod.CopyNewOnly;
        vm.NextCommand.Execute(null);
        Assert.Equal(4, vm.Step);
        await vm.LoadLocalTreeAsync(true);
        vm.NextCommand.Execute(null);                                    // seleção vazia: bloqueia
        Assert.Equal(4, vm.Step);
        Assert.Contains("seleção vazia", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        await UiThread.Run(() => { vm.LocalRoots.Single(r => r.Name == "app").IsChecked = true; return Task.CompletedTask; });
        vm.NextCommand.Execute(null); Assert.Equal(5, vm.Step);
        vm.NextCommand.Execute(null); Assert.Equal(6, vm.Step);
        Assert.Contains(vm.ReviewSections.SelectMany(s => s.Lines), l => l.Label == "Incluídos" && l.Value == "app");
        vm.BackCommand.Execute(null); vm.BackCommand.Execute(null); vm.BackCommand.Execute(null); vm.BackCommand.Execute(null);
        Assert.Equal(2, vm.Step);
        Assert.Equal("Deploy", vm.Name); Assert.Equal(src, vm.SourceLocalPath); Assert.Equal(FtpSyncMethod.CopyNewOnly, vm.SyncMethod);

        vm.SaveCommand.Execute(null);
        var svc = _sp.GetRequiredService<IFtpTaskService>();
        for (var i = 0; i < 100 && vm.Step == 2 && (await svc.GetAllAsync()).Count == 0; i++) await Task.Delay(100);
        // o caminho remoto não é alcançável (porta 1): o usuário é informado e a gravação exige confirmação explícita
        Assert.Empty(await svc.GetAllAsync());
        Assert.Contains("Não foi possível confirmar", vm.ValidationMessage);
        Assert.Contains("NÃO confirmado", vm.ValidationMessage);
        vm.SaveCommand.Execute(null);
        for (var i = 0; i < 100 && (await svc.GetAllAsync()).Count == 0; i++) await Task.Delay(100);
        var saved = (await svc.GetAllAsync()).Single();
        Assert.Equal(["app"], saved.IncludedRelativePaths);
        Assert.Equal(FtpSyncMethod.CopyNewOnly, saved.Method);
        Assert.Equal(conn.Id, saved.ConnectionProfileId);
    }

    [Fact]
    public async Task Task_wizard_edit_loads_existing_rules_and_cancel_keeps_stored_task_untouched()
    {
        var (conn, src) = await SeedAsync();
        var svc = _sp.GetRequiredService<IFtpTaskService>();
        await svc.SaveAsync(new FtpSyncTask { Name = "T", ConnectionProfileId = conn.Id, SourceLocalPath = src, TargetRemotePath = "/s",
            IncludedRelativePaths = ["app"], ExcludedRelativePaths = ["app/cache"] });
        var stored = (await svc.GetAllAsync()).Single();

        var vm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(stored.Clone());
        Assert.Equal(["app"], vm.Selection.Includes);
        Assert.Equal(["app/cache"], vm.Selection.Excludes);
        vm.Selection.Clear(); vm.Name = "Alterado";
        vm.CancelCommand.Execute(null);
        var again = (await svc.GetAllAsync()).Single();
        Assert.Equal("T", again.Name);
        Assert.Equal(["app"], again.IncludedRelativePaths);
    }

    [Fact]
    public async Task Task_wizard_step1_holds_connection_and_paths_and_shows_inline_errors()
    {
        var (_, src) = await SeedAsync();
        var vm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(new FtpSyncTask());
        for (var i = 0; i < 50 && vm.AvailableProfiles.Count == 0; i++) await Task.Delay(50);
        Assert.Equal(vm.AvailableProfiles.Single(), vm.SelectedProfile);   // única conexão: já selecionada

        vm.NextCommand.Execute(null);
        Assert.True(vm.HasNameError); Assert.Equal(WizardStepState.Current, vm.Steps[0].State);
        vm.Name = "T";
        Assert.False(vm.HasValidationMessage);                              // erro some quando o campo é corrigido
        vm.NextCommand.Execute(null);
        Assert.True(vm.HasSourceError);
        vm.SourceLocalPath = src;
        vm.TargetRemotePath = "";
        vm.NextCommand.Execute(null);
        Assert.True(vm.HasTargetError);
        vm.TargetRemotePath = "/site";
        vm.NavigateCommand.Execute(4);                                       // pular etapas sem seleção não é permitido
        Assert.NotEqual(5, vm.Step);
        Assert.True(vm.Step <= 4);
    }

    [Fact]
    public async Task Task_wizard_method_cards_exclusions_preview_and_review_use_real_data()
    {
        var (_, src) = await SeedAsync();
        File.WriteAllText(Path.Combine(src, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(src, "app", "note.md"), "x");
        var vm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(new FtpSyncTask { Name = "T", SourceLocalPath = src, TargetRemotePath = "/s", ExclusionPatterns = [] });
        for (var i = 0; i < 50 && vm.SelectedProfile == null; i++) await Task.Delay(50);

        vm.MethodChoices.Single(m => m.Method == FtpSyncMethod.MirrorSource).IsSelected = true;
        Assert.Equal(FtpSyncMethod.MirrorSource, vm.SyncMethod);
        Assert.True(vm.MethodChoices.Single(m => m.Method == FtpSyncMethod.MirrorSource).IsSelected);
        Assert.False(vm.MethodChoices.Single(m => m.Method == FtpSyncMethod.UpdateTarget).IsSelected);
        Assert.Contains("excluir", vm.MethodWarning, StringComparison.OrdinalIgnoreCase);

        vm.Selection.SetChecked("app", true);
        await vm.UpdatePreviewAsync();
        var withoutRules = vm.PreviewIncluded;
        vm.NewExclusionText = "**/*.md";
        vm.AddExclusionCommand.Execute(null);
        await vm.UpdatePreviewAsync();
        Assert.Equal(withoutRules - 1, vm.PreviewIncluded);                 // a exceção realmente tira o .md da seleção
        Assert.Equal(1, vm.PreviewIgnored);
        Assert.Contains(vm.Exclusions, e => e.Text == "**/*.md" && !e.IsPath);
        Assert.True(vm.SensitiveIsRisk);                                    // .env não protegido: aviso real
        vm.NewExclusionText = ".env"; vm.AddExclusionCommand.Execute(null);
        Assert.False(vm.SensitiveIsRisk);

        vm.Selection.SetChecked("app/sub", false);
        Assert.Contains(vm.Exclusions, e => e.IsPath && e.Text == "app/sub");
        vm.RemoveExclusionCommand.Execute(vm.Exclusions.First(e => e.IsPath));
        Assert.Empty(vm.Selection.Excludes);

        Assert.Contains(vm.ReviewSections.SelectMany(s => s.Lines), l => l.Label == "Método" && l.Value == "Espelhar origem");
        Assert.Contains(vm.ReviewWarnings, w => w.Contains("Espelhamento"));
        Assert.Contains(vm.ReviewWarnings, w => w.Contains("destino remoto ainda não foi confirmado"));
    }

    [Fact]
    public async Task Task_wizard_footer_saves_only_on_last_step()
    {
        var (_, src) = await SeedAsync();
        var vm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(new FtpSyncTask { Name = "T", SourceLocalPath = src, TargetRemotePath = "/s" });
        for (var i = 0; i < 50 && vm.SelectedProfile == null; i++) await Task.Delay(50);
        vm.Selection.SetChecked("app", true);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.IsNotLastStep);
        await UiThread.Run(async () => { vm.NavigateCommand.Execute(6); await Task.Delay(500); });
        Assert.Equal(6, vm.Step);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.IsNotLastStep);
        Assert.Empty(await _sp.GetRequiredService<IFtpTaskService>().GetAllAsync());   // nada gravado antes de Salvar
    }

    [Fact]
    public async Task Home_empty_states_follow_the_real_collections()
    {
        await UiThread.Run(async () =>
        {
            var mvm = NewMain();
            var w = new MainWindow(mvm); w.Show();
            await mvm.Home.LoadAsync(); await UiThread.Settle();
            var view = UiFind.ByName<SyncManager.Wpf.Views.Pages.HomeView>(w, "HomePage");
            Assert.False(mvm.Home.HasProfiles);
            Assert.Contains(AllText(view), t => t == "Nenhum perfil salvo ainda.");

            await _sp.GetRequiredService<IProfileRepository>().SaveAsync(new SyncProfile { Name = "Real", SourcePath = @"C:\a", TargetPath = @"C:\b" });
            await mvm.Home.LoadAsync(); await UiThread.Settle();
            Assert.True(mvm.Home.HasProfiles);
            Assert.DoesNotContain(VisibleText(view), t => t == "Nenhum perfil salvo ainda.");
            Assert.Contains(VisibleText(view), t => t == "Real");
            w.Close();
        });
    }

    private static IEnumerable<string> AllText(DependencyObject root) =>
        Descendants(root).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text);
    private static IEnumerable<string> VisibleText(DependencyObject root) =>
        Descendants(root).OfType<System.Windows.Controls.TextBlock>().Where(t => t.IsVisible).Select(t => t.Text);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        yield return d;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            foreach (var c in Descendants(VisualTreeHelper.GetChild(d, i))) yield return c;
    }

    // ----------------------------------------------------------------- evidência visual (renderização real do XAML)
    private static void Snap(FrameworkElement root, string file)
    {
        root.UpdateLayout();
        var w = (int)Math.Max(1, root.ActualWidth); var h = (int)Math.Max(1, root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        // fundo da tela principal simulado sob o modal translúcido
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)), null, new Rect(0, 0, w, h));
            dc.DrawImage(rtb, new Rect(0, 0, w, h));
        }
        var outRtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        outRtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(outRtb));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var fs = File.Create(file); enc.Save(fs);
    }

    private static string EvidenceDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SyncManager.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, "docs", "evidence");
    }

    [Fact]
    public async Task Render_real_windows_to_png_evidence()
    {
        var conn = new FtpConnectionProfile { Name = "SmartKonta", Host = "ftp.homolog.test", Port = 21, Username = "deploy", DefaultRemotePath = "/api.smartkonta.com.br/" };
        await _sp.GetRequiredService<IFtpConnectionService>().SaveAsync(conn, "pw");
        var src = Path.Combine(_root, "smartkonta-api");
        foreach (var d in new[] { "app", "bootstrap", "config", "database", "lang", "public", "resources", "routes", "storage", "tests", "vendor" })
        {
            Directory.CreateDirectory(Path.Combine(src, d));
            File.WriteAllText(Path.Combine(src, d, "index.php"), "<?php");
        }
        File.WriteAllText(Path.Combine(src, ".env"), "APP_KEY=x"); File.WriteAllText(Path.Combine(src, "artisan"), "x"); File.WriteAllText(Path.Combine(src, "composer.json"), "{}");
        var dir = EvidenceDir();

        await UiThread.Run(async () =>
        {
            // conexão: 3 etapas
            var cvm = _sp.GetRequiredService<Func<FtpConnectionProfile, FtpConnectionEditViewModel>>()(new FtpConnectionProfile
                { Name = "SmartKonta", Host = "127.0.0.1", Port = 1, Username = "deploy", DefaultRemotePath = "/api.smartkonta.com.br/" });
            var cd = new FtpConnectionEditDialog(cvm); cd.Show(); await Task.Delay(200);
            Snap(cd, Path.Combine(dir, "conexao_1_geral.png"));
            cvm.NextCommand.Execute(null); await Task.Delay(200); Snap(cd, Path.Combine(dir, "conexao_2_seguranca.png"));
            cvm.NextCommand.Execute(null);                                  // dispara o diagnóstico real (porta 1: falha real)
            for (var i = 0; i < 100 && cvm.Diagnostics?.IsCompleted != true; i++) await Task.Delay(100);
            await Task.Delay(300);
            Snap(cd, Path.Combine(dir, "conexao_3_teste_falha_real.png"));
            cd.Close();

            // tarefa: 6 etapas
            var task = new FtpSyncTask { Name = "Atualizar API SmartKonta", Description = "Sincronizar somente as pastas selecionadas do projeto.", ConnectionProfileId = conn.Id,
                SourceLocalPath = src, TargetRemotePath = "/api.smartkonta.com.br/",
                IncludedRelativePaths = ["app", "bootstrap", "config", "database", "lang", "public", "resources", "routes"],
                ExcludedRelativePaths = ["app/cache"], ExclusionPatterns = ["**/node_modules/**", "**/.git/**", "**/*.log"] };
            var tvm = _sp.GetRequiredService<Func<FtpSyncTask, FtpTaskEditViewModel>>()(task);
            var td = new FtpTaskEditDialog(tvm); td.Show(); await Task.Delay(300);
            Snap(td, Path.Combine(dir, "tarefa_1_geral.png"));
            tvm.NextCommand.Execute(null); await Task.Delay(300); Snap(td, Path.Combine(dir, "tarefa_2_conexao_caminhos.png"));
            tvm.NextCommand.Execute(null); await Task.Delay(200); Snap(td, Path.Combine(dir, "tarefa_3_metodo.png"));
            tvm.NextCommand.Execute(null); await Task.Delay(1200);
            tvm.LocalRoots.FirstOrDefault(r => r.Name == "app")!.IsExpanded = true; await Task.Delay(300);
            Snap(td, Path.Combine(dir, "tarefa_4_pastas_selecionadas.png"));
            tvm.IsTabPreview = true; await Task.Delay(500); Snap(td, Path.Combine(dir, "tarefa_4b_previa_regras.png")); tvm.IsTabFiles = true;
            tvm.NextCommand.Execute(null); await Task.Delay(500); Snap(td, Path.Combine(dir, "tarefa_5_filtros.png"));
            tvm.NextCommand.Execute(null); await Task.Delay(500); Snap(td, Path.Combine(dir, "tarefa_6_revisao.png"));
            td.Close();

            var mvm = NewMain();
            var mw = new MainWindow(mvm); mw.Width = 1280; mw.Height = 760; mw.Show();
            await mvm.Home.LoadAsync(); await Task.Delay(300);
            Snap(mw, Path.Combine(dir, "janela_principal_inicial.png"));
            mw.Close();
        });
        Assert.True(File.Exists(Path.Combine(dir, "tarefa_6_revisao.png")));
    }
}