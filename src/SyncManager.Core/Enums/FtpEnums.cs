namespace SyncManager.Core.Enums;

/// <summary>
/// Protocolo de comunicação FTP / FTPS suportado.
/// </summary>
public enum FtpProtocol
{
    FtpsExplicit = 0, // TLS sobre a porta 21 (Recomendado)
    FtpsImplicit = 1, // TLS direto na porta 990
    FtpPlain = 2      // FTP em texto simples (inseguro)
}

/// <summary>
/// Modo de transferência do canal de dados FTP.
/// </summary>
public enum FtpTransferMode
{
    Passive = 0, // PASV (Recomendado para firewalls/NAT)
    Active = 1   // PORT (Ativo)
}

/// <summary>
/// Método de sincronização FTP / FTPS.
/// </summary>
public enum FtpSyncMethod
{
    UpdateTarget = 0,   // Atualizar destino (novos e modificados, sem exclusão remota)
    MirrorSource = 1,   // Espelhar origem (mantém destino idêntico à origem, com exclusão sob checklist)
    CopyNewOnly = 2,    // Copiar somente novos (ignora arquivos que já existem)
    DownloadServer = 3  // Baixar do servidor (download do remoto para a pasta local)
}

/// <summary>
/// Estratégia de comparação entre arquivos locais e remotos.
/// </summary>
public enum FtpComparisonStrategy
{
    SizeAndTimestamp = 0, // Tamanho e Data/Hora de Modificação
    SizeOnly = 1,         // Apenas Tamanho
    ChecksumIfSupported = 2 // Checksum / Hash se suportado pelo servidor
}

/// <summary>
/// Ação planejada proposta para cada arquivo no checklist pré-execução.
/// </summary>
public enum FtpPlannedAction
{
    Keep = 0,           // Manter (sem alteração)
    UploadNew = 1,      // Upload de arquivo novo
    UpdateExisting = 2, // Atualizar versão existente no destino
    DeleteRemote = 3,   // Exclusão remota no servidor
    IgnoreManual = 4,   // Ignorado/desmarcado manualmente no checklist
    DownloadNew = 5,    // Baixar arquivo novo do servidor para a pasta local
    UpdateLocal = 6,    // Atualizar versão local a partir do servidor
    DeleteLocal = 7     // Exclusão local (quando aplicável em espelhamento reverso sob autorização)
}

/// <summary>
/// Etapas do diagnóstico e teste de conexão FTP em 8 passos.
/// </summary>
public enum FtpDiagnosticStep
{
    DnsResolution = 1,
    TcpConnection = 2,
    TlsHandshake = 3,
    CertificateValidation = 4,
    Authentication = 5,
    PassiveMode = 6,
    DirectoryListing = 7,
    ModTimeSupport = 8
}

/// <summary>
/// Presets de frameworks web comuns para regras de exclusao e escopo de deploy.
/// </summary>
public enum WebFrameworkPreset
{
    Custom = 0,
    None = 0,
    Laravel = 1,
    WordPress = 2,
    ReactViteSpa = 3,
    NodeSpaReact = 3,
    AngularReactVue = 3,
    NodeJs = 4,
    AspNetCore = 5,
    DotNetGeneric = 5,
    DotNet = 5,
    StaticHtml = 6
}

/// <summary>
/// Canal ou formato de webhook para notificações pós-sincronização.
/// </summary>
public enum FtpWebhookType
{
    GenericJson = 0,
    Slack = 1,
    Discord = 2,
    Teams = 3,
    MicrosoftTeams = 3
}

/// <summary>
/// Estado de cada etapa do diagnóstico.
/// </summary>
public enum FtpDiagnosticStepStatus
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Warning = 3,
    Failed = 4
}

/// <summary>
/// Status da execução da sincronização FTP para auditoria.
/// </summary>
public enum FtpExecutionStatus
{
    Success = 0,
    Warning = 1,
    Failed = 2,
    Cancelled = 3
}
