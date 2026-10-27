using SyncManager.Core.Enums;

namespace SyncManager.Core.Models;

/// <summary>Todas as configurações necessárias para comparar, planejar e executar uma sincronização.</summary>
public sealed class SyncProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "Sem nome";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public SyncMethod Method { get; set; } = SyncMethod.MirrorSourceToTarget;

    public bool IncludeSubfolders { get; set; } = true;
    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;
    /// <summary>Verifica conteúdo por SHA-256 (somente quando tamanho/data não bastam).</summary>
    public bool VerifyContentByHash { get; set; }
    /// <summary>Confere o hash da cópia temporária contra o Source antes de substituir o destino.</summary>
    public bool VerifyCopyByHash { get; set; }
    public int TimestampToleranceSeconds { get; set; } = 2;
    public bool IncludeHidden { get; set; } = true;
    public bool IncludeSystem { get; set; }
    public bool ContinueOnError { get; set; } = true;
    public bool ConfirmDeletes { get; set; } = true;
    /// <summary>Guarda a versão antiga do Target na pasta de segurança antes de sobrescrevê-la.</summary>
    public bool BackupBeforeOverwrite { get; set; } = true;

    public DeleteStrategy DeleteStrategy { get; set; } = DeleteStrategy.SafetyFolder;
    /// <summary>Pasta de segurança personalizada. Vazio = &lt;Target&gt;\.SyncBackup.</summary>
    public string? SafetyFolderPath { get; set; }

    public List<string> ExcludePatterns { get; set; } = new();
    public List<string> IncludePatterns { get; set; } = new();
    /// <summary>Filtros diferenciam maiúsculas de minúsculas (padrão: não).</summary>
    public bool FilterCaseSensitive { get; set; }
    /// <summary>Filtros interpretam * e ? como curingas (padrão: sim). Desligado, os padrões são texto literal.</summary>
    public bool FilterWildcards { get; set; } = true;

    /// <summary>0 = automático (4 em disco local, 2 quando algum lado é caminho de rede).</summary>
    public int MaxParallelFileOperations { get; set; }
    /// <summary>Tentativas extras diante de erro temporário (rede instável, arquivo momentaneamente em uso).</summary>
    public int TransientRetryCount { get; set; } = 2;
    /// <summary>Espera base entre tentativas; cresce a cada nova tentativa (1x, 2x, 3x...).</summary>
    public int TransientRetryDelayMs { get; set; } = 1000;
    /// <summary>Fração de exclusões sobre o lado afetado acima da qual a confirmação é reforçada.</summary>
    public double MassDeleteThreshold { get; set; } = 0.30;

    public SyncProfile Clone()
    {
        var c = (SyncProfile)MemberwiseClone();
        c.ExcludePatterns = new List<string>(ExcludePatterns);
        c.IncludePatterns = new List<string>(IncludePatterns);
        return c;
    }
}
