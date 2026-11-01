using FluentFTP;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure.Ftp;

public sealed class FtpDirectoryBrowser : IFtpDirectoryBrowser
{
    private readonly IFluentFtpClientFactory _clientFactory;

    public FtpDirectoryBrowser(IFluentFtpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<IReadOnlyList<FtpRemoteItem>> ListDirectoryAsync(
        FtpConnectionProfile profile,
        string remotePath,
        CancellationToken ct = default)
    {
        var client = await _clientFactory.CreateClientAsync(profile, null, null, ct);
        try
        {
            await client.Connect(ct);

            var pathToList = string.IsNullOrWhiteSpace(remotePath) ? "/" : remotePath;
            var items = await client.GetListing(pathToList, FtpListOption.Auto, ct);

            var result = new List<FtpRemoteItem>();
            foreach (var item in items)
            {
                // Ignorar referências de diretório corrente e pai (. e ..)
                if (item.Name is "." or "..")
                    continue;

                result.Add(new FtpRemoteItem
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    Size = item.Size,
                    ModifiedTime = item.Modified != DateTime.MinValue ? item.Modified : null
                });
            }

            // Ordenar: pastas primeiro, depois arquivos alfabeticamente
            return result
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.Disconnect();
                client.Dispose();
            }
            catch { }
        }
    }
}
