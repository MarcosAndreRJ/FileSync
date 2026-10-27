using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Infrastructure;

namespace SyncManager.Cli;

/// <summary>
/// Execução sem interface: <c>SyncManager.Cli run --profile &lt;id|nome&gt; [--silent]</c>.
/// Usa o mesmo SyncWorkflow da interface (COMPARE → PLAN → EXECUTE) e devolve códigos de saída estáveis.
/// </summary>
public sealed class CliApp
{
    public const string Usage = """
        SyncManager.Cli — execução de perfis do SyncManager sem interface gráfica

        Uso:
          SyncManager.Cli run --profile <id|nome> [opções]   Compara, planeja e executa o perfil
          SyncManager.Cli list                               Lista os perfis e seus agendamentos
          SyncManager.Cli help                               Mostra esta ajuda

        Opções do "run":
          --silent              Sem saída no console (uso em tarefas agendadas); notifica conforme as configurações
          --dry-run             Só compara e mostra o plano; não altera nenhum arquivo
          --allow-mass-delete   Permite executar, sem confirmação humana, um plano que exclui muito do Target
          --no-notify           Não mostra notificação do Windows
        Opção global:
          --db <caminho>        Usa outro arquivo de banco de dados (padrão: %LocalAppData%\SyncManager\syncmanager.db)

        Códigos de saída:
          0 sucesso            1 sucesso com avisos     2 erro (inclui plano bloqueado/abortado por segurança)
          3 conflitos não resolvidos                    4 perfil inexistente
          5 perfil já em execução                       6 cancelado
        """;

    private readonly IServiceProvider _services;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly INotificationService _notifier;

    public CliApp(IServiceProvider services, TextWriter stdout, TextWriter stderr, INotificationService notifier)
    {
        _services = services;
        _out = stdout;
        _err = stderr;
        _notifier = notifier;
    }

    private sealed record Options(
        string Command, string? Profile, bool Silent, bool DryRun, bool AllowMassDelete, bool NoNotify);

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        Options options;
        try { options = Parse(args); }
        catch (ArgumentException ex)
        {
            await _err.WriteLineAsync(ex.Message);
            await _err.WriteLineAsync("Use \"help\" para ver as opções.");
            return (int)ExitCode.Error;
        }

        switch (options.Command)
        {
            case "help":
                await _out.WriteLineAsync(Usage);
                return (int)ExitCode.Success;
            case "list":
                return await ListAsync(ct);
            case "run":
                return await RunProfileAsync(options, ct);
            default:
                await _err.WriteLineAsync($"Comando desconhecido: {options.Command}");
                await _err.WriteLineAsync(Usage);
                return (int)ExitCode.Error;
        }
    }

    private static Options Parse(string[] args)
    {
        if (args.Length == 0) return new Options("help", null, false, false, false, false);

        var command = args[0].ToLowerInvariant();
        if (command is "--help" or "-h" or "/?") command = "help";

        string? profile = null;
        bool silent = false, dry = false, mass = false, noNotify = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--profile" or "-p":
                    profile = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--profile exige um valor.");
                    break;
                case "--silent" or "-s": silent = true; break;
                case "--dry-run": dry = true; break;
                case "--allow-mass-delete": mass = true; break;
                case "--no-notify": noNotify = true; break;
                case "--db": i++; break; // tratado em Program (antes de montar os serviços)
                default: throw new ArgumentException($"Opção desconhecida: {args[i]}");
            }
        }
        return new Options(command, profile, silent, dry, mass, noNotify);
    }

    // ------------------------------------------------------------------ list

    private async Task<int> ListAsync(CancellationToken ct)
    {
        var profiles = await _services.GetRequiredService<IProfileRepository>().GetAllAsync(ct);
        var schedules = await _services.GetRequiredService<IScheduleRepository>().GetAllAsync(ct);
        if (profiles.Count == 0)
        {
            await _out.WriteLineAsync("Nenhum perfil salvo.");
            return (int)ExitCode.Success;
        }
        foreach (var p in profiles)
        {
            var schedule = schedules.TryGetValue(p.Id, out var s) ? s.Describe() : "Sem agendamento";
            await _out.WriteLineAsync($"{p.Id,4}  {p.Name}  [{p.Method}]  {p.SourcePath} → {p.TargetPath}  |  {schedule}");
        }
        return (int)ExitCode.Success;
    }

    // ------------------------------------------------------------------ run

    private async Task<int> RunProfileAsync(Options o, CancellationToken ct)
    {
        var log = _services.GetRequiredService<ILogService>();
        var settings = await _services.GetRequiredService<SettingsService>().LoadAsync(ct);
        var notifyMode = o.NoNotify || !o.Silent ? NotifyMode.Never : settings.NotifyMode;

        if (string.IsNullOrWhiteSpace(o.Profile))
        {
            await _err.WriteLineAsync("Informe o perfil: run --profile <id|nome>");
            return (int)ExitCode.Error;
        }

        var profile = await FindProfileAsync(o.Profile, ct);
        if (profile is null)
        {
            log.Error("CLI RUN", $"Perfil não encontrado: {o.Profile}");
            if (!o.Silent) await _err.WriteLineAsync($"Perfil não encontrado: {o.Profile}");
            return (int)ExitCode.ProfileNotFound;
        }

        if (!o.Silent) log.EntryWritten += Echo;
        try
        {
            log.Info("CLI RUN", $"{profile.Name} (#{profile.Id}) | silent={o.Silent} dryRun={o.DryRun}");
            return await ExecuteAsync(profile, o, notifyMode, log, ct);
        }
        catch (OperationCanceledException)
        {
            log.Warning("CLI RUN", "Cancelado");
            return (int)ExitCode.Cancelled;
        }
        finally
        {
            if (!o.Silent) log.EntryWritten -= Echo;
        }
    }

    private void Echo(LogEntry e) => _out.WriteLine(FileLogService.Format(e));

    private async Task<int> ExecuteAsync(SyncProfile profile, Options o, NotifyMode notifyMode, ILogService log, CancellationToken ct)
    {
        var workflow = _services.GetRequiredService<SyncWorkflow>();

        CompareOutcome outcome;
        try
        {
            outcome = await workflow.CompareAsync(profile, null, ct);
        }
        catch (SyncValidationException ex)
        {
            return Fail(profile, "Não foi possível comparar: " + string.Join(" ", ex.Errors), notifyMode, log);
        }
        catch (IOException ex)
        {
            return Fail(profile, "Não foi possível ler as pastas: " + ex.Message, notifyMode, log);
        }

        var plan = outcome.Plan;
        if (!o.Silent) await _out.WriteLineAsync(PlanSummary(plan));

        if (o.DryRun)
        {
            if (!o.Silent)
            {
                foreach (var op in plan.Operations.Where(x => x.Type != SyncOperationType.Skip).Take(500))
                    await _out.WriteLineAsync($"  {op.Type.ToString().ToUpperInvariant(),-16} {op.RelativePath}");
                await _out.WriteLineAsync("(dry-run: nenhum arquivo foi alterado)");
            }
            return (int)ExitCode.Success;
        }

        if (plan.IsBlocked)
            return Fail(profile, "Plano bloqueado: " + plan.BlockedReason, notifyMode, log);

        // Sem humano para confirmar: exclusão em massa só com autorização explícita na linha de comando.
        if (plan.RequiresStrongConfirmation && !o.AllowMassDelete)
            return Fail(profile,
                $"Execução abortada por segurança: o plano exclui {plan.DeleteCount:N0} de {plan.TargetEntryCount:N0} itens do Target " +
                $"({plan.DeleteRatio:P0}). Revise na interface ou use --allow-mass-delete.", notifyMode, log);

        ExecutionResult result;
        try
        {
            result = await workflow.ExecuteAsync(outcome, null, ct);
        }
        catch (ProfileBusyException ex)
        {
            log.Warning("CLI RUN", ex.Message);
            if (!o.Silent) await _err.WriteLineAsync(ex.Message);
            return (int)ExitCode.AlreadyRunning;
        }

        if (!o.Silent) await _out.WriteLineAsync(ResultSummary(result));

        if (NotificationPolicy.ShouldNotify(notifyMode, result.Status))
        {
            var (title, message) = NotificationPolicy.Describe(profile.Name, result);
            SafeNotify(title, message, NotificationPolicy.LevelFor(result.Status), log);
        }
        return (int)ExitCodes.From(result.Status);
    }

    private int Fail(SyncProfile profile, string message, NotifyMode notifyMode, ILogService log)
    {
        log.Error("CLI RUN", message);
        _err.WriteLine(message);
        if (NotificationPolicy.ShouldNotify(notifyMode, ExecutionStatus.Failed))
            SafeNotify($"{profile.Name}: FALHOU", message, NotificationLevel.Error, log);
        return (int)ExitCode.Error;
    }

    private void SafeNotify(string title, string message, NotificationLevel level, ILogService log)
    {
        try { _notifier.Notify(title, message, level); }
        catch (Exception ex) { log.Warning("NOTIFY", "Não foi possível mostrar a notificação: " + ex.Message); }
    }

    private async Task<SyncProfile?> FindProfileAsync(string idOrName, CancellationToken ct)
    {
        var repo = _services.GetRequiredService<IProfileRepository>();
        if (int.TryParse(idOrName, out var id) && await repo.GetAsync(id, ct) is { } byId) return byId;
        return (await repo.GetAllAsync(ct)).FirstOrDefault(p => p.Name.Equals(idOrName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string PlanSummary(SyncPlan p) =>
        $"Plano: copiar {p.CopyCount:N0}, atualizar {p.UpdateCount:N0}, excluir {p.DeleteCount:N0}, " +
        $"ignorar {p.SkipCount:N0}, conflitos {p.ConflictCount:N0}, erros {p.ErrorCount:N0}";

    private static string ResultSummary(ExecutionResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"Resultado: {r.Status} | copiados {r.FilesCopied:N0}, atualizados {r.FilesUpdated:N0}, excluídos {r.FilesDeleted:N0}, ");
        sb.Append($"conflitos {r.Conflicts:N0}, erros {r.Errors:N0}, avisos {r.Warnings:N0} | {r.Duration:hh\\:mm\\:ss}");
        if (r.Message is not null) sb.Append(" | ").Append(r.Message);
        return sb.ToString();
    }
}
