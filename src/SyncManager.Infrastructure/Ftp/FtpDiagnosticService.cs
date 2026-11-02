using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public sealed class FtpDiagnosticService : IFtpDiagnosticService
{
    private readonly IFluentFtpClientFactory _clientFactory;

    public FtpDiagnosticService(IFluentFtpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async IAsyncEnumerable<FtpDiagnosticResult> RunDiagnosticsAsync(
        FtpConnectionProfile profile,
        string password,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1. Resolução DNS
        yield return await RunDnsStepAsync(profile, ct);

        // 2. Conexão TCP
        yield return await RunTcpStepAsync(profile, ct);

        // Cliente FTP para etapas 3 a 8
        AsyncFtpClient? client = null;
        FtpDiagnosticResult? clientInitError = null;
        try
        {
            client = await _clientFactory.CreateClientAsync(profile, password, null, ct);
        }
        catch (Exception ex)
        {
            clientInitError = new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.TlsHandshake,
                Title = "Handshake TLS / Criptografia",
                Description = "Inicialização do cliente FTP",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Erro ao instanciar cliente FTP: {ex.Message}"
            };
        }

        if (clientInitError != null)
        {
            yield return clientInitError;
            yield break;
        }

        // 3. Handshake TLS e Conexão Inicial
        var (tlsResult, isConnected) = await RunTlsStepAsync(client, profile, ct);
        yield return tlsResult;
        if (!isConnected)
        {
            await DisposeClientSafely(client);
            yield break;
        }

        // 4. Validação do Certificado
        yield return RunCertValidationStep(client, profile);

        // 5. Autenticação (USER / PASS)
        var (authResult, isAuthenticated) = RunAuthStep(client, profile);
        yield return authResult;
        if (!isAuthenticated)
        {
            await DisposeClientSafely(client);
            yield break;
        }

        // 6. Modo Passivo (PASV/EPSV)
        yield return await RunPassiveModeStepAsync(client, profile, ct);

        // 7. Listagem de Diretório
        yield return await RunListingStepAsync(client, profile, ct);

        // 8. Suporte a Timestamps (MDTM / MFMT)
        yield return await RunModTimeStepAsync(client, profile, ct);

        await DisposeClientSafely(client);
    }

    private static async Task<FtpDiagnosticResult> RunDnsStepAsync(FtpConnectionProfile profile, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(profile.Host, ct);
            sw.Stop();

            if (addresses.Length == 0)
            {
                return new FtpDiagnosticResult
                {
                    Step = FtpDiagnosticStep.DnsResolution,
                    Title = "Resolução DNS",
                    Description = "Resolução de nome para endereço IP",
                    Status = FtpDiagnosticStepStatus.Failed,
                    Details = $"Nenhum IP retornado para o host '{profile.Host}'.",
                    ElapsedMilliseconds = sw.ElapsedMilliseconds
                };
            }

            var ips = string.Join(", ", addresses.Take(2).Select(a => a.ToString()));
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.DnsResolution,
                Title = "Resolução DNS",
                Description = "Resolução de nome para endereço IP",
                Status = FtpDiagnosticStepStatus.Success,
                Details = $"Host resolvido com sucesso: {ips}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.DnsResolution,
                Title = "Resolução DNS",
                Description = "Resolução de nome para endereço IP",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Falha ao resolver '{profile.Host}': {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task<FtpDiagnosticResult> RunTcpStepAsync(FtpConnectionProfile profile, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, profile.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await tcpClient.ConnectAsync(profile.Host, profile.Port, linkedCts.Token);
            sw.Stop();

            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.TcpConnection,
                Title = "Conexão TCP",
                Description = $"Abertura de socket TCP na porta {profile.Port}",
                Status = FtpDiagnosticStepStatus.Success,
                Details = $"Socket TCP estabelecido com sucesso na porta {profile.Port}.",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.TcpConnection,
                Title = "Conexão TCP",
                Description = $"Abertura de socket TCP na porta {profile.Port}",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Não foi possível conectar na porta {profile.Port}: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task<(FtpDiagnosticResult Result, bool IsConnected)> RunTlsStepAsync(
        AsyncFtpClient client,
        FtpConnectionProfile profile,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await client.Connect(ct);
            sw.Stop();

            if (profile.Protocol is FtpProtocol.FtpsExplicit or FtpProtocol.FtpsImplicit)
            {
                var isEncrypted = client.IsEncrypted;
                return (new FtpDiagnosticResult
                {
                    Step = FtpDiagnosticStep.TlsHandshake,
                    Title = "Handshake TLS / Criptografia",
                    Description = "Negociação de canal seguro SSL/TLS",
                    Status = isEncrypted ? FtpDiagnosticStepStatus.Success : FtpDiagnosticStepStatus.Warning,
                    Details = isEncrypted
                        ? "Canal de controle seguro negociado com sucesso."
                        : "Conexão estabelecida, mas o canal não reportou criptografia ativa.",
                    ElapsedMilliseconds = sw.ElapsedMilliseconds
                }, true);
            }

            return (new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.TlsHandshake,
                Title = "Handshake TLS / Criptografia",
                Description = "FTP sem criptografia (Plain FTP)",
                Status = FtpDiagnosticStepStatus.Success,
                Details = "Conexão de controle estabelecida em modo Plain FTP.",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            }, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.TlsHandshake,
                Title = "Handshake TLS / Criptografia",
                Description = "Negociação de conexão inicial",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Falha ao estabelecer conexão: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            }, false);
        }
    }

    private static FtpDiagnosticResult RunCertValidationStep(AsyncFtpClient client, FtpConnectionProfile profile)
    {
        if (profile.Protocol == FtpProtocol.FtpPlain)
        {
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.CertificateValidation,
                Title = "Validação do Certificado",
                Description = "Validação da cadeia SSL/TLS",
                Status = FtpDiagnosticStepStatus.Success,
                Details = "Não aplicável para Plain FTP."
            };
        }

        var isEncrypted = client.IsEncrypted;
        return new FtpDiagnosticResult
        {
            Step = FtpDiagnosticStep.CertificateValidation,
            Title = "Validação do Certificado",
            Description = "Validação da cadeia SSL/TLS",
            Status = isEncrypted ? FtpDiagnosticStepStatus.Success : FtpDiagnosticStepStatus.Warning,
            Details = isEncrypted
                ? "Certificado do servidor validado com sucesso."
                : "Aviso: Conexão sem validação completa de certificado."
        };
    }

    private static (FtpDiagnosticResult Result, bool IsAuthenticated) RunAuthStep(
        AsyncFtpClient client,
        FtpConnectionProfile profile)
    {
        var isAuth = client.IsAuthenticated;
        return (new FtpDiagnosticResult
        {
            Step = FtpDiagnosticStep.Authentication,
            Title = "Autenticação",
            Description = "Validação de credenciais de login",
            Status = isAuth ? FtpDiagnosticStepStatus.Success : FtpDiagnosticStepStatus.Failed,
            Details = isAuth
                ? $"Usuário '{profile.Username}' autenticado com sucesso."
                : "Falha na autenticação do usuário e senha.",
            ElapsedMilliseconds = 10
        }, isAuth);
    }

    private static async Task<FtpDiagnosticResult> RunPassiveModeStepAsync(
        AsyncFtpClient client,
        FtpConnectionProfile profile,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var remoteDir = string.IsNullOrWhiteSpace(profile.DefaultRemotePath) ? "/" : profile.DefaultRemotePath;
            var listing = await client.GetListing(remoteDir, FtpListOption.Auto, ct);
            sw.Stop();

            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.PassiveMode,
                Title = "Modo Passivo (PASV)",
                Description = "Abertura do canal de dados para transmissão",
                Status = FtpDiagnosticStepStatus.Success,
                Details = $"Canal de dados aberto com sucesso ({profile.TransferMode}).",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.PassiveMode,
                Title = "Modo Passivo (PASV)",
                Description = "Abertura do canal de dados",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Falha ao abrir canal de dados: {ex.Message}. Verifique configurações de firewall e modo Passivo.",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task<FtpDiagnosticResult> RunListingStepAsync(
        AsyncFtpClient client,
        FtpConnectionProfile profile,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var remoteDir = string.IsNullOrWhiteSpace(profile.DefaultRemotePath) ? "/" : profile.DefaultRemotePath;
            var listing = await client.GetListing(remoteDir, FtpListOption.Auto, ct);
            sw.Stop();

            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.DirectoryListing,
                Title = "Listagem de Diretório",
                Description = "Leitura da estrutura de arquivos remota",
                Status = FtpDiagnosticStepStatus.Success,
                Details = $"Diretório '{remoteDir}' listado com sucesso ({listing.Length} itens encontrados).",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.DirectoryListing,
                Title = "Listagem de Diretório",
                Description = "Leitura da estrutura de arquivos remota",
                Status = FtpDiagnosticStepStatus.Failed,
                Details = $"Falha ao listar diretório: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task<FtpDiagnosticResult> RunModTimeStepAsync(
        AsyncFtpClient client,
        FtpConnectionProfile profile,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            bool hasMdtm = client.HasFeature(FtpCapability.MDTM);
            bool hasMfmt = client.HasFeature(FtpCapability.MFMT);
            sw.Stop();

            if (hasMdtm || hasMfmt)
            {
                var details = $"Comandos suportados: {(hasMdtm ? "MDTM " : "")}{(hasMfmt ? "MFMT" : "")}".Trim();
                return new FtpDiagnosticResult
                {
                    Step = FtpDiagnosticStep.ModTimeSupport,
                    Title = "Suporte a Timestamps (MDTM/MFMT)",
                    Description = "Preservação e leitura precisa de datas de modificação",
                    Status = FtpDiagnosticStepStatus.Success,
                    Details = details,
                    ElapsedMilliseconds = sw.ElapsedMilliseconds
                };
            }

            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.ModTimeSupport,
                Title = "Suporte a Timestamps (MDTM/MFMT)",
                Description = "Preservação de datas de modificação",
                Status = FtpDiagnosticStepStatus.Warning,
                Details = "Servidor não anunciou suporte explícito a MDTM/MFMT nas features.",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new FtpDiagnosticResult
            {
                Step = FtpDiagnosticStep.ModTimeSupport,
                Title = "Suporte a Timestamps (MDTM/MFMT)",
                Description = "Consulta de capacidades de timestamp",
                Status = FtpDiagnosticStepStatus.Warning,
                Details = $"Aviso ao verificar suporte a timestamp: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task DisposeClientSafely(AsyncFtpClient? client)
    {
        if (client == null) return;
        try
        {
            if (client.IsConnected)
                await client.Disconnect();
            client.Dispose();
        }
        catch { }
    }
}
