using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using SyncManager.Application;
using SyncManager.Infrastructure;
using SyncManager.Persistence;

namespace SyncManager.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var dbPath = Database.DefaultPath;
        var dbIndex = Array.FindIndex(args, a => a.Equals("--db", StringComparison.OrdinalIgnoreCase));
        if (dbIndex >= 0)
        {
            if (dbIndex + 1 >= args.Length)
            {
                await Console.Error.WriteLineAsync("--db exige um caminho.");
                return (int)ExitCode.Error;
            }
            dbPath = Path.GetFullPath(args[dbIndex + 1]);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            using var services = new ServiceCollection().AddSyncManagerEngine(dbPath).BuildServiceProvider();
            await services.GetRequiredService<Database>().MigrateAsync(cts.Token);
            var app = new CliApp(services, Console.Out, Console.Error, new BalloonNotifier());
            return await app.RunAsync(args, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return (int)ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Erro inesperado: " + ex.Message);
            return (int)ExitCode.Error;
        }
    }
}

/// <summary>
/// Notificação do Windows via balão da bandeja (NotifyIcon): funciona em aplicativo não empacotado, sem registro prévio.
/// Em sessões sem área de trabalho interativa simplesmente não aparece.
/// </summary>
public sealed class BalloonNotifier : INotificationService
{
    public void Notify(string title, string message, NotificationLevel level)
    {
        var thread = new Thread(() => Show(title, message, level)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
    }

    private static void Show(string title, string message, NotificationLevel level)
    {
        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            BalloonTipTitle = Truncate(title, 60),
            BalloonTipText = Truncate(message, 250),
            BalloonTipIcon = level switch
            {
                NotificationLevel.Error => ToolTipIcon.Error,
                NotificationLevel.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info
            }
        };
        icon.ShowBalloonTip(10000);

        // o shell precisa de um laço de mensagens por alguns segundos para exibir o balão
        var end = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < end)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(100);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
