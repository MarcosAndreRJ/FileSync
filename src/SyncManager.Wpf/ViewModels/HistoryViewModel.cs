using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

public sealed class HistoryRow
{
    public HistoryRow(ExecutionHistory h)
    {
        History = h;
        StartedText = h.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        StatusText = Presentation.StatusName(h.Status);
        StatusBrush = h.Status switch
        {
            ExecutionStatus.Success => Presentation.Green,
            ExecutionStatus.Failed => Presentation.Red,
            ExecutionStatus.Cancelled => Presentation.Gray,
            _ => Presentation.Orange
        };
        DurationText = h.Duration.ToString(@"hh\:mm\:ss");
        BytesText = Presentation.Bytes(h.BytesTransferred);
        MethodText = Presentation.MethodName(h.Method);
    }

    public ExecutionHistory History { get; }
    public string StartedText { get; }
    public string StatusText { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
    public string DurationText { get; }
    public string BytesText { get; }
    public string MethodText { get; }
    public string ProfileName => History.ProfileName;
    public int Copied => History.FilesCopied;
    public int Updated => History.FilesUpdated;
    public int Deleted => History.FilesDeleted;
    public int Skipped => History.FilesSkipped;
    public int Conflicts => History.Conflicts;
    public int Errors => History.Errors;
    public int Warnings => History.Warnings;
}

public sealed record HistoryItemRow(string Operation, string Path, string Outcome, string Message, string Size, System.Windows.Media.Brush Brush);

public sealed class HistoryViewModel : ObservableObject
{
    private const string AllProfiles = "(todos os perfis)";
    private readonly IExecutionHistoryRepository _repo;
    private readonly IDialogService _dialogs;
    private HistoryRow? _selected;
    private string _profileFilter = AllProfiles;
    private string _detailTitle = "Selecione uma execução para ver os detalhes.";

    public HistoryViewModel(IExecutionHistoryRepository repo, IDialogService dialogs)
    {
        _repo = repo;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenSafetyFolderCommand = new RelayCommand(
            () => _dialogs.OpenFolder(Selected!.History.SafetyFolder!),
            () => Selected?.History.SafetyFolder is { } p && Directory.Exists(p));
        OpenLogsCommand = new RelayCommand(() => _dialogs.OpenLogFolder());
        RerunCommand = new RelayCommand(() => RerunRequested?.Invoke(Selected!.History.ProfileId!.Value), () => Selected?.History.ProfileId is not null);

        View = new ListCollectionView(Rows) { Filter = o => _profileFilter == AllProfiles || ((HistoryRow)o).ProfileName == _profileFilter };
    }

    public ObservableCollection<HistoryRow> Rows { get; } = new();
    public ObservableCollection<HistoryItemRow> Items { get; } = new();
    public ObservableCollection<string> ProfileNames { get; } = new();
    public ICollectionView View { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenSafetyFolderCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    /// <summary>"Executar novamente": roda o fluxo normal (comparar, plano, confirmar) do perfil desta execução.</summary>
    public RelayCommand RerunCommand { get; }
    public event Action<int>? RerunRequested;
    public bool HasSelection => _selected is not null;

    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }

    public string ProfileFilter
    {
        get => _profileFilter;
        set { if (SetProperty(ref _profileFilter, value)) View.Refresh(); }
    }

    public HistoryRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); _ = LoadItemsAsync(); }
        }
    }

    public async Task LoadAsync()
    {
        var keep = Selected?.History.Id;
        var list = await _repo.ListAsync(500);
        Rows.Clear();
        foreach (var h in list) Rows.Add(new HistoryRow(h));

        ProfileNames.Clear();
        ProfileNames.Add(AllProfiles);
        foreach (var n in list.Select(h => h.ProfileName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            ProfileNames.Add(n);
        if (!ProfileNames.Contains(_profileFilter)) ProfileFilter = AllProfiles;

        Selected = Rows.FirstOrDefault(r => r.History.Id == keep) ?? Rows.FirstOrDefault();
        if (Rows.Count == 0) DetailTitle = "Nenhuma execução registrada ainda.";
    }

    private async Task LoadItemsAsync()
    {
        Items.Clear();
        var row = Selected;
        if (row is null) return;

        var h = row.History;
        var items = await _repo.GetItemsAsync(h.Id, 5000);
        if (!ReferenceEquals(row, Selected)) return; // seleção mudou enquanto carregava

        foreach (var i in items)
        {
            var brush = i.Outcome switch
            {
                "Failed" => Presentation.Red,
                "Warning" => Presentation.Orange,
                "Cancelled" => Presentation.Gray,
                _ => Presentation.Dark
            };
            Items.Add(new HistoryItemRow(i.OperationType, i.RelativePath, i.Outcome, i.Message ?? "",
                i.Bytes > 0 ? Presentation.Bytes(i.Bytes) : "", brush));
        }

        DetailTitle = $"{h.ProfileName} — {h.SourcePath} → {h.TargetPath}" +
                      (h.Message is null ? "" : $"   |   {h.Message}") +
                      (items.Count >= 5000 ? "   |   (exibindo os primeiros 5.000 itens)" : "");
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
