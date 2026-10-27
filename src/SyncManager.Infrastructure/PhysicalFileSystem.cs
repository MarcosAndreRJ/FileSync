using System.IO.Enumeration;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Infrastructure;

public sealed class PhysicalFileSystem : IFileSystem
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan RootCheckTimeout = TimeSpan.FromSeconds(20);

    public async Task<RootStatus> CheckRootAsync(string path, bool requireWrite, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => CheckRoot(path, requireWrite), ct).WaitAsync(RootCheckTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new RootStatus(false, false, false, "Tempo esgotado ao acessar o caminho (rede indisponível?).");
        }
    }

    private static RootStatus CheckRoot(string path, bool requireWrite)
    {
        try
        {
            if (!Directory.Exists(path)) return new RootStatus(false, false, false, "A pasta não existe ou não está acessível.");
        }
        catch (Exception ex) { return new RootStatus(false, false, false, ex.Message); }

        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new RootStatus(true, false, false, "Sem permissão de leitura: " + ex.Message);
        }

        if (!requireWrite) return new RootStatus(true, true, false, null);

        var probe = Path.Combine(path, ".syncmanager-write-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return new RootStatus(true, true, true, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new RootStatus(true, true, false, "Sem permissão de escrita: " + ex.Message);
        }
    }

    public IEnumerable<FileSystemItem> EnumerateDirectory(string path)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        return new FileSystemEnumerable<FileSystemItem>(path, static (ref FileSystemEntry e) =>
            new FileSystemItem(
                e.FileName.ToString(),
                e.ToFullPath(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.Attributes,
                e.Attributes.HasFlag(FileAttributes.ReparsePoint)), options);
    }

    public FileSystemItem? GetItem(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists)
            return new FileSystemItem(fi.Name, fi.FullName, false, fi.Length, fi.LastWriteTimeUtc, fi.Attributes, fi.Attributes.HasFlag(FileAttributes.ReparsePoint));

        var di = new DirectoryInfo(path);
        if (di.Exists)
            return new FileSystemItem(di.Name, di.FullName, true, 0, di.LastWriteTimeUtc, di.Attributes, di.Attributes.HasFlag(FileAttributes.ReparsePoint));

        return null;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public async Task CopyFileAsync(string source, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(read);
            }
            await output.FlushAsync(ct);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void MoveFile(string source, string destination, bool overwrite)
    {
        if (overwrite && File.Exists(destination))
        {
            var attrs = File.GetAttributes(destination);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
        }
        File.Move(source, destination, overwrite);
    }

    public void DeleteFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    public void SendToRecycleBin(string path)
    {
        if (PathUtil.IsUnc(path))
            throw new NotSupportedException("A Lixeira não está disponível em caminhos de rede; o arquivo seria excluído permanentemente.");
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    public async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    public void SetModifiedUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);
}
