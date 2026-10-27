using System.Text.Json;
using SyncManager.Core.Abstractions;

namespace SyncManager.Application;

/// <summary>Configurações globais do aplicativo (tabela Settings).</summary>
public sealed class AppSettings
{
    public static readonly string DefaultExcludes = string.Join(Environment.NewLine,
        "*.tmp", "*.log", "Thumbs.db", "desktop.ini", ".git\\", "node_modules\\", "bin\\", "obj\\");

    /// <summary>Operações de arquivo em paralelo quando Source e Target são locais.</summary>
    public int MaxParallelLocal { get; set; } = 4;
    /// <summary>Operações de arquivo em paralelo quando algum lado é caminho de rede.</summary>
    public int MaxParallelNetwork { get; set; } = 2;
    /// <summary>Logs mais antigos que isso são apagados ao iniciar (0 = nunca apagar).</summary>
    public int LogRetentionDays { get; set; } = 30;
    /// <summary>Quando a CLI agendada (modo silencioso) mostra uma notificação do Windows.</summary>
    public NotifyMode NotifyMode { get; set; } = NotifyMode.OnProblems;
    /// <summary>Padrões de exclusão aplicados a novos perfis (um por linha).</summary>
    public string DefaultExcludePatterns { get; set; } = DefaultExcludes;
    /// <summary>Inicia o aplicativo minimizado na barra de tarefas.</summary>
    public bool StartMinimized { get; set; }
    /// <summary>Pede confirmação antes de executar planos sem exclusões (com exclusões, vale a opção do perfil).</summary>
    public bool ConfirmBeforeExecute { get; set; } = true;

    public AppSettings Normalized()
    {
        MaxParallelLocal = Math.Clamp(MaxParallelLocal, 1, 32);
        MaxParallelNetwork = Math.Clamp(MaxParallelNetwork, 1, 32);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 0, 3650);
        DefaultExcludePatterns ??= "";
        return this;
    }
}

public sealed class SettingsService
{
    private const string Key = "AppSettings";
    private readonly ISettingsRepository _repo;

    public SettingsService(ISettingsRepository repo) => _repo = repo;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _repo.GetAsync(Key, ct);
            if (!string.IsNullOrWhiteSpace(json))
                return (JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings()).Normalized();
        }
        catch (JsonException) { /* configuração corrompida: volta aos padrões */ }
        return new AppSettings();
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) =>
        _repo.SetAsync(Key, JsonSerializer.Serialize(settings.Normalized()), ct);
}
