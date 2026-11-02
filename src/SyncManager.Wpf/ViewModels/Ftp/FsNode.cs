using System.Collections.ObjectModel;
using System.IO;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Filtering;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

/// <summary>Carrega os filhos de um nó (sistema de arquivos local ou diretório remoto FTP).</summary>
public delegate Task<IReadOnlyList<FsNode>> FsNodeLoader(FsNode parent, CancellationToken ct);

/// <summary>
/// Nó de árvore de pastas/arquivos com carga preguiçosa. Usado no navegador FTP (local e remoto)
/// e na seleção persistente (com checkbox tri-state ligado às regras da tarefa).
/// </summary>
public sealed class FsNode : ObservableObject
{
    private static readonly FsNode Placeholder = new("…", string.Empty, string.Empty, false, null);

    private readonly FsNodeLoader? _loader;
    private readonly FtpSelectionModel? _selection;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoading;
    private bool _loaded;
    private string? _error;

    public FsNode(string name, string fullPath, string relativePath, bool isDirectory, FsNodeLoader? loader,
        FtpSelectionModel? selection = null, FsNode? parent = null, string info = "")
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        _loader = loader;
        _selection = selection;
        Parent = parent;
        Info = info;
        if (isDirectory && loader != null) Children.Add(Placeholder);
    }

    public string Name { get; }
    /// <summary>Caminho completo (local ou remoto).</summary>
    public string FullPath { get; }
    /// <summary>Caminho relativo à raiz da origem, com "/" (base das regras persistentes).</summary>
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public FsNode? Parent { get; }
    public string Info { get; }
    public string Glyph => IsDirectory ? "📁" : "📄";
    public ObservableCollection<FsNode> Children { get; } = new();

    public bool ShowCheckBox => _selection != null;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string? Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string StateText => _selection == null ? Info : IsChecked switch
    {
        true => "Incluída",
        null => "Parcial",
        _ => "Fora da tarefa"
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && IsDirectory) _ = EnsureLoadedAsync();
        }
    }

    /// <summary>Estado tri-state derivado das regras; alterar grava a regra (recursiva) e atualiza a árvore carregada.</summary>
    public bool? IsChecked
    {
        get => _selection == null ? false : _selection.GetState(RelativePath, IsDirectory);
        set
        {
            if (_selection == null) return;
            // clique: marcado -> desmarca; parcial ou desmarcado -> marca (o valor de ciclo do CheckBox é ignorado)
            var current = _selection.GetState(RelativePath, IsDirectory);
            _selection.SetChecked(RelativePath, current != true);   // o modelo avisa a árvore (Changed), que atualiza todos os nós carregados
        }
    }

    /// <summary>Notifica o estado de checkbox deste nó e dos descendentes já carregados.</summary>
    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(StateText));
        foreach (var c in Children)
            if (!ReferenceEquals(c, Placeholder)) c.RefreshState();
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded || _loader == null || IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            var items = await _loader(this, ct);
            Children.Clear();
            foreach (var item in items) Children.Add(item);
            _loaded = true;
        }
        catch (Exception ex)
        {
            Children.Clear();
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Permite nova tentativa após erro/desconexão.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _loaded = false;
        Children.Clear();
        await EnsureLoadedAsync(ct);
    }

    // ------------------------------------------------------------------ fábricas
    /// <summary>Raízes locais (filhos diretos de <paramref name="rootPath"/>). Lança se a pasta não existir.</summary>
    public static async Task<List<FsNode>> LoadLocalRootAsync(string rootPath, FtpSelectionModel? selection, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"Pasta local não encontrada: {rootPath}");
        var children = await LocalChildrenAsync(null, string.Empty, rootPath, selection, ct);
        return children.ToList();
    }

    private static Task<IReadOnlyList<FsNode>> LocalChildrenAsync(FsNode? parent, string parentRel, string dir, FtpSelectionModel? selection, CancellationToken ct) =>
        Task.Run<IReadOnlyList<FsNode>>(() =>
        {
            var list = new List<FsNode>();
            var di = new DirectoryInfo(dir);
            string Rel(string name) => parentRel.Length == 0 ? name : parentRel + "/" + name;

            foreach (var d in di.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // não segue links simbólicos
                list.Add(new FsNode(d.Name, d.FullName, Rel(d.Name), true,
                    (n, token) => LocalChildrenAsync(n, n.RelativePath, n.FullPath, selection, token), selection, parent, d.LastWriteTime.ToString("dd/MM/yyyy")));
            }
            foreach (var f in di.EnumerateFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(new FsNode(f.Name, f.FullName, Rel(f.Name), false, null, selection, parent, FormatSize(f.Length)));
            }
            return list;
        }, ct);

    /// <summary>Raízes remotas reais listadas via serviço de navegação FTP.</summary>
    public static async Task<List<FsNode>> LoadRemoteRootAsync(IFtpDirectoryBrowser browser, FtpConnectionProfile profile, string remotePath, CancellationToken ct = default, string? password = null)
    {
        var items = await browser.ListDirectoryAsync(profile, remotePath, ct, password);
        return items.Select(i => FromRemote(browser, profile, i, null, password)).ToList();
    }

    private static FsNode FromRemote(IFtpDirectoryBrowser browser, FtpConnectionProfile profile, FtpRemoteItem item, FsNode? parent, string? password)
    {
        FsNodeLoader? loader = item.IsDirectory
            ? async (n, ct) =>
            {
                var children = await browser.ListDirectoryAsync(profile, n.FullPath, ct, password);
                return children.Select(c => FromRemote(browser, profile, c, n, password)).ToList();
            }
            : null;
        var info = item.IsDirectory ? "diretório" : FormatSize(item.Size);
        return new FsNode(item.Name, item.FullPath, string.Empty, item.IsDirectory, loader, null, parent, info);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double d = bytes; var i = 0;
        while (d >= 1024 && i < units.Length - 1) { d /= 1024; i++; }
        return $"{d:0.#} {units[i]}";
    }
}
