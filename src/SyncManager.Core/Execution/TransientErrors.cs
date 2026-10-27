namespace SyncManager.Core.Execution;

/// <summary>Distingue falhas que costumam passar sozinhas (rede instável, bloqueio momentâneo) das definitivas.</summary>
public static class TransientErrors
{
    // Códigos Win32 (parte baixa do HRESULT) de rede e de arquivo temporariamente indisponível.
    private static readonly HashSet<int> Codes = new()
    {
        32,    // ERROR_SHARING_VIOLATION
        33,    // ERROR_LOCK_VIOLATION
        53,    // ERROR_BAD_NETPATH
        54,    // ERROR_NETWORK_BUSY
        55,    // ERROR_DEV_NOT_EXIST
        59,    // ERROR_UNEXP_NET_ERR
        64,    // ERROR_NETNAME_DELETED
        67,    // ERROR_BAD_NET_NAME
        71,    // ERROR_REQ_NOT_ACCEP
        121,   // ERROR_SEM_TIMEOUT
        1231,  // ERROR_NETWORK_UNREACHABLE
        1232,  // ERROR_HOST_UNREACHABLE
        1236   // ERROR_CONNECTION_ABORTED
    };

    public static bool IsTransient(Exception ex) =>
        ex is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or SourceChangedException)
        && Codes.Contains(ex.HResult & 0xFFFF);
}
