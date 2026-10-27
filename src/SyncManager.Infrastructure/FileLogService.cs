using System.Text;
using Microsoft.Extensions.Logging;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;

namespace SyncManager.Infrastructure;

/// <summary>Log estruturado em arquivo diário (uma linha por evento) e notificação para a UI.</summary>
public sealed class FileLogService : ILogService, IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger<FileLogService>? _diagnostics;
    private StreamWriter? _writer;
    private string? _writerDate;

    public FileLogService(ILogger<FileLogService>? diagnostics = null, string? directory = null)
    {
        _diagnostics = diagnostics;
        LogDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncManager", "Logs");
    }

    public string LogDirectory { get; }
    public event Action<LogEntry>? EntryWritten;

    public static string Format(LogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" | ").Append(e.Severity.ToString().ToUpperInvariant().PadRight(7));
        sb.Append(" | ").Append(e.Event);
        if (!string.IsNullOrEmpty(e.Detail)) sb.Append(" | ").Append(e.Detail.ReplaceLineEndings(" "));
        return sb.ToString();
    }

    public void Write(LogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                var date = entry.Time.ToLocalTime().ToString("yyyyMMdd");
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(LogDirectory);
                    _writer = new StreamWriter(new FileStream(Path.Combine(LogDirectory, $"syncmanager-{date}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = date;
                }
                _writer.WriteLine(Format(entry));
            }
        }
        catch (Exception ex)
        {
            // Falha de log nunca pode derrubar a sincronização.
            _diagnostics?.LogWarning(ex, "Falha ao gravar o log em arquivo");
        }

        EntryWritten?.Invoke(entry);
    }

    /// <summary>Apaga arquivos de log mais antigos que <paramref name="days"/> dias (0 = não apaga nada).</summary>
    public int PurgeOlderThan(int days)
    {
        if (days <= 0 || !Directory.Exists(LogDirectory)) return 0;
        var limit = DateTime.UtcNow.AddDays(-days);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "syncmanager-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}
