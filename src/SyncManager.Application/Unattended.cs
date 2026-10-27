using SyncManager.Core.Enums;
using SyncManager.Core.Models;

namespace SyncManager.Application;

/// <summary>Códigos de saída da CLI (usados pelo Task Scheduler e por scripts).</summary>
public enum ExitCode
{
    Success = 0,
    SuccessWithWarnings = 1,
    Error = 2,
    ConflictsUnresolved = 3,
    ProfileNotFound = 4,
    /// <summary>O perfil já está sendo executado (por outra instância da CLI ou pela interface).</summary>
    AlreadyRunning = 5,
    Cancelled = 6
}

public static class ExitCodes
{
    public static ExitCode From(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Success => ExitCode.Success,
        ExecutionStatus.SuccessWithWarnings => ExitCode.SuccessWithWarnings,
        ExecutionStatus.ConflictsUnresolved => ExitCode.ConflictsUnresolved,
        ExecutionStatus.Cancelled => ExitCode.Cancelled,
        _ => ExitCode.Error
    };
}

public enum NotifyMode
{
    Never,
    /// <summary>Somente avisos, erros, conflitos, cancelamentos e falhas.</summary>
    OnProblems,
    Always
}

public enum NotificationLevel { Info, Warning, Error }

public interface INotificationService
{
    void Notify(string title, string message, NotificationLevel level);
}

public static class NotificationPolicy
{
    public static bool ShouldNotify(NotifyMode mode, ExecutionStatus status) => mode switch
    {
        NotifyMode.Never => false,
        NotifyMode.Always => true,
        _ => status != ExecutionStatus.Success
    };

    public static NotificationLevel LevelFor(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Success => NotificationLevel.Info,
        ExecutionStatus.Failed => NotificationLevel.Error,
        _ => NotificationLevel.Warning
    };

    public static (string Title, string Message) Describe(string profileName, ExecutionResult r)
    {
        var title = r.Status switch
        {
            ExecutionStatus.Success => $"{profileName}: concluído",
            ExecutionStatus.SuccessWithWarnings => $"{profileName}: concluído com avisos",
            ExecutionStatus.ConflictsUnresolved => $"{profileName}: há conflitos",
            ExecutionStatus.Cancelled => $"{profileName}: cancelado",
            _ => $"{profileName}: FALHOU"
        };
        var msg = r.Message ??
                  $"Copiados {r.FilesCopied}, atualizados {r.FilesUpdated}, excluídos {r.FilesDeleted}, " +
                  $"erros {r.Errors}, avisos {r.Warnings}, conflitos {r.Conflicts}.";
        return (title, msg);
    }
}

public sealed class ProfileBusyException : Exception
{
    public ProfileBusyException(string profileName)
        : base($"O perfil \"{profileName}\" já está em execução.") { }
}

public interface IProfileLockProvider
{
    /// <summary>Tenta reservar o perfil para execução. Null = já está em execução. Dispose libera.</summary>
    IDisposable? TryAcquire(int profileId);
}

/// <summary>
/// Trava entre processos (CLI agendada × interface) para que o mesmo perfil nunca rode duas vezes ao mesmo tempo.
/// Usa semáforo nomeado (sem afinidade de thread, ao contrário do Mutex, e some se o processo morrer).
/// O nome inclui o escopo (o banco de dados), pois o id de perfil só é único dentro de um banco.
/// </summary>
public sealed class ProfileLockProvider : IProfileLockProvider
{
    private readonly string _scopeHash;

    public ProfileLockProvider(string scope) =>
        _scopeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scope.ToUpperInvariant())))[..16];

    public IDisposable? TryAcquire(int profileId)
    {
        var semaphore = new Semaphore(1, 1, $@"Local\SyncManager.{_scopeHash}.Profile.{profileId}");
        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            return null;
        }
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private Semaphore? _semaphore;
        public Releaser(Semaphore s) => _semaphore = s;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _semaphore, null);
            if (s is null) return;
            s.Release();
            s.Dispose();
        }
    }
}
