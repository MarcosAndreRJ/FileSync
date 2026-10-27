using System.Collections.ObjectModel;
using System.IO;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Wpf.Mvvm;
using SyncManager.Wpf.Services;

namespace SyncManager.Wpf.ViewModels;

public enum EditorSection { General, Method, Filters, Schedule, Advanced }

/// <summary>Um padrão de filtro editável na lista (o texto é editado direto na linha).</summary>
public sealed class FilterEntry : ObservableObject
{
    private string _text;
    public FilterEntry(string text) => _text = text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }
}

/// <summary>
/// Editor completo de um perfil (seções Geral, Método, Filtros, Agendamento e Avançado). Trabalha numa CÓPIA do perfil:
/// nada muda no banco até "Salvar"; "Cancelar" descarta. Só edita dados — não compara nem executa.
/// </summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly IProfileRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly IFileSystem _fs;

    private SyncProfile _profile = new();
    private EditorSection _section = EditorSection.General;
    private bool _dirty;
    private bool _loading;

    private string _name = "", _source = "", _target = "", _safety = "";
    private MethodOption _method = MethodCatalog.Methods[0];
    private DeleteOption _delete = MethodCatalog.DeleteOptions[0];
    private bool _subfolders, _timestamps, _attributes, _hash, _copyHash, _hidden, _system, _continue, _confirmDeletes, _backup, _caseSensitive, _wildcards;
    private int _tolerance, _parallel, _retries, _retryDelay, _massDeletePercent;
    private string _newExclude = "", _newInclude = "";
    private string _sourceTest = "", _targetTest = "";
    private System.Windows.Media.Brush _sourceTestBrush = Presentation.Gray, _targetTestBrush = Presentation.Gray;
    private bool _testing;
    private string _error = "";

    public ProfileEditorViewModel(IProfileRepository repo, IDialogService dialogs, IFileSystem fs, ScheduleViewModel schedule)
    {
        _repo = repo;
        _dialogs = dialogs;
        _fs = fs;
        Schedule = schedule;

        SelectSectionCommand = new RelayCommand<string>(s => Section = Enum.Parse<EditorSection>(s));
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => Closed?.Invoke(null));
        BrowseSourceCommand = new RelayCommand(() => Browse(p => Source = p, Source, "Pasta Source"));
        BrowseTargetCommand = new RelayCommand(() => Browse(p => Target = p, Target, "Pasta Target"));
        BrowseSafetyCommand = new RelayCommand(() => Browse(p => SafetyFolder = p, SafetyFolder, "Pasta de segurança"));
        TestConnectionsCommand = new AsyncRelayCommand(TestConnectionsAsync, () => !_testing);
        AddExcludeCommand = new RelayCommand(() => Add(Excludes, ref _newExclude, nameof(NewExclude)));
        AddIncludeCommand = new RelayCommand(() => Add(Includes, ref _newInclude, nameof(NewInclude)));
        RemoveFilterCommand = new RelayCommand<FilterEntry>(e => { Excludes.Remove(e); Includes.Remove(e); Touch(); });
        Excludes.CollectionChanged += (_, _) => Touch();
        Includes.CollectionChanged += (_, _) => Touch();
    }

    public IReadOnlyList<MethodOption> Methods => MethodCatalog.Methods;
    public IReadOnlyList<DeleteOption> DeleteOptions => MethodCatalog.DeleteOptions;
    public ObservableCollection<FilterEntry> Excludes { get; } = new();
    public ObservableCollection<FilterEntry> Includes { get; } = new();
    public ScheduleViewModel Schedule { get; }

    public RelayCommand<string> SelectSectionCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand BrowseSafetyCommand { get; }
    public AsyncRelayCommand TestConnectionsCommand { get; }
    public RelayCommand AddExcludeCommand { get; }
    public RelayCommand AddIncludeCommand { get; }
    public RelayCommand<FilterEntry> RemoveFilterCommand { get; }

    /// <summary>Fechou o editor: o perfil salvo, ou null se foi cancelado.</summary>
    public event Action<SyncProfile?>? Closed;

    public bool IsNew => _profile.Id == 0;
    public string Title => IsNew ? "Novo perfil" : $"Editar perfil — {_profile.Name}";
    public bool IsDirty => _dirty;
    /// <summary>Encerra a edição (salvou ou cancelou): sair da página não pede mais confirmação.</summary>
    public void ForgetChanges() { _dirty = false; OnPropertyChanged(nameof(IsDirty)); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    // ---- seções
    public EditorSection Section
    {
        get => _section;
        set
        {
            if (!SetProperty(ref _section, value)) return;
            OnPropertyChanged(nameof(IsGeneral)); OnPropertyChanged(nameof(IsMethod)); OnPropertyChanged(nameof(IsFilters));
            OnPropertyChanged(nameof(IsSchedule)); OnPropertyChanged(nameof(IsAdvanced));
            OnPropertyChanged(nameof(GeneralTag)); OnPropertyChanged(nameof(MethodTag)); OnPropertyChanged(nameof(FiltersTag));
            OnPropertyChanged(nameof(ScheduleTag)); OnPropertyChanged(nameof(AdvancedTag));
        }
    }
    public bool IsGeneral => _section == EditorSection.General;
    public bool IsMethod => _section == EditorSection.Method;
    public bool IsFilters => _section == EditorSection.Filters;
    public bool IsSchedule => _section == EditorSection.Schedule;
    public bool IsAdvanced => _section == EditorSection.Advanced;
    public string GeneralTag => IsGeneral ? "active" : "";
    public string MethodTag => IsMethod ? "active" : "";
    public string FiltersTag => IsFilters ? "active" : "";
    public string ScheduleTag => IsSchedule ? "active" : "";
    public string AdvancedTag => IsAdvanced ? "active" : "";
    public bool CanSchedule => !IsNew;
    public bool CannotSchedule => IsNew;

    // ---- Geral
    public string Name { get => _name; set => Edit(ref _name, value); }
    public string Source { get => _source; set => Edit(ref _source, value); }
    public string Target { get => _target; set => Edit(ref _target, value); }
    public bool IncludeSubfolders { get => _subfolders; set => Edit(ref _subfolders, value); }
    public bool PreserveTimestamps { get => _timestamps; set => Edit(ref _timestamps, value); }
    public bool PreserveAttributes { get => _attributes; set => Edit(ref _attributes, value); }
    public string SourceTestText { get => _sourceTest; private set => SetProperty(ref _sourceTest, value); }
    public string TargetTestText { get => _targetTest; private set => SetProperty(ref _targetTest, value); }
    public System.Windows.Media.Brush SourceTestBrush { get => _sourceTestBrush; private set => SetProperty(ref _sourceTestBrush, value); }
    public System.Windows.Media.Brush TargetTestBrush { get => _targetTestBrush; private set => SetProperty(ref _targetTestBrush, value); }

    // ---- Método
    public MethodOption SelectedMethod { get => _method; set { if (value is not null && Edit(ref _method, value)) OnPropertyChanged(nameof(MethodDescription)); } }
    public string MethodDescription => _method.Description;
    public DeleteOption SelectedDeleteOption { get => _delete; set { if (value is not null) Edit(ref _delete, value); } }
    public int ToleranceSeconds { get => _tolerance; set => Edit(ref _tolerance, Math.Max(0, value)); }
    public bool VerifyContentByHash { get => _hash; set => Edit(ref _hash, value); }
    public bool VerifyCopyByHash { get => _copyHash; set => Edit(ref _copyHash, value); }

    // ---- Filtros
    public string NewExclude { get => _newExclude; set => SetProperty(ref _newExclude, value); }
    public string NewInclude { get => _newInclude; set => SetProperty(ref _newInclude, value); }
    public bool IncludeHidden { get => _hidden; set => Edit(ref _hidden, value); }
    public bool IncludeSystem { get => _system; set => Edit(ref _system, value); }
    public bool FilterCaseSensitive { get => _caseSensitive; set => Edit(ref _caseSensitive, value); }
    public bool FilterWildcards { get => _wildcards; set => Edit(ref _wildcards, value); }

    // ---- Avançado
    public string SafetyFolder { get => _safety; set => Edit(ref _safety, value); }
    public bool BackupBeforeOverwrite { get => _backup; set => Edit(ref _backup, value); }
    public bool ContinueOnError { get => _continue; set => Edit(ref _continue, value); }
    public bool ConfirmDeletes { get => _confirmDeletes; set => Edit(ref _confirmDeletes, value); }
    public int MassDeletePercent { get => _massDeletePercent; set => Edit(ref _massDeletePercent, Math.Clamp(value, 1, 100)); }
    public int MaxParallel { get => _parallel; set => Edit(ref _parallel, Math.Clamp(value, 0, 32)); }
    public int RetryCount { get => _retries; set => Edit(ref _retries, Math.Clamp(value, 0, 10)); }
    public int RetryDelayMs { get => _retryDelay; set => Edit(ref _retryDelay, Math.Clamp(value, 100, 60000)); }

    private bool Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        Touch();
        return true;
    }

    private void Touch()
    {
        if (_loading || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void Browse(Action<string> set, string current, string title)
    {
        var p = _dialogs.BrowseFolder(current, title);
        if (p is not null) set(p);
    }

    private void Add(ObservableCollection<FilterEntry> list, ref string buffer, string property)
    {
        var text = buffer.Trim();
        if (text.Length > 0 && !list.Any(e => string.Equals(e.Text.Trim(), text, StringComparison.OrdinalIgnoreCase)))
            list.Add(new FilterEntry(text));
        buffer = "";
        OnPropertyChanged(property);
    }

    /// <summary>Carrega o perfil (cópia) no editor e, se já existe, o agendamento dele.</summary>
    public async Task LoadAsync(SyncProfile profile, EditorSection section = EditorSection.General)
    {
        _profile = profile.Clone();
        _loading = true;
        try
        {
            var p = _profile;
            Name = p.Name; Source = p.SourcePath; Target = p.TargetPath;
            IncludeSubfolders = p.IncludeSubfolders; PreserveTimestamps = p.PreserveTimestamps; PreserveAttributes = p.PreserveAttributes;
            SelectedMethod = Methods.FirstOrDefault(m => m.Method == p.Method) ?? Methods[0];
            SelectedDeleteOption = DeleteOptions.First(d => d.Strategy == p.DeleteStrategy);
            ToleranceSeconds = p.TimestampToleranceSeconds;
            VerifyContentByHash = p.VerifyContentByHash; VerifyCopyByHash = p.VerifyCopyByHash;
            IncludeHidden = p.IncludeHidden; IncludeSystem = p.IncludeSystem;
            FilterCaseSensitive = p.FilterCaseSensitive; FilterWildcards = p.FilterWildcards;
            SafetyFolder = p.SafetyFolderPath ?? "";
            BackupBeforeOverwrite = p.BackupBeforeOverwrite; ContinueOnError = p.ContinueOnError; ConfirmDeletes = p.ConfirmDeletes;
            MassDeletePercent = (int)Math.Round(p.MassDeleteThreshold * 100);
            MaxParallel = p.MaxParallelFileOperations; RetryCount = p.TransientRetryCount; RetryDelayMs = p.TransientRetryDelayMs;
            Excludes.Clear(); foreach (var e in p.ExcludePatterns) Excludes.Add(new FilterEntry(e));
            Includes.Clear(); foreach (var e in p.IncludePatterns) Includes.Add(new FilterEntry(e));
            NewExclude = ""; NewInclude = "";
            SourceTestText = ""; TargetTestText = ""; Error = "";
        }
        finally { _loading = false; }
        _dirty = false;
        Section = section;
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(IsNew)); OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanSchedule)); OnPropertyChanged(nameof(CannotSchedule));
        if (!IsNew) await Schedule.LoadAsync(_profile);
    }

    private SyncProfile Build()
    {
        var p = _profile.Clone();   // campos que o editor não mostra (se houver) são preservados
        p.Name = Name.Trim();
        p.SourcePath = Source.Trim(); p.TargetPath = Target.Trim();
        p.Method = SelectedMethod.Method;
        p.IncludeSubfolders = IncludeSubfolders; p.PreserveTimestamps = PreserveTimestamps; p.PreserveAttributes = PreserveAttributes;
        p.VerifyContentByHash = VerifyContentByHash; p.VerifyCopyByHash = VerifyCopyByHash;
        p.TimestampToleranceSeconds = ToleranceSeconds;
        p.IncludeHidden = IncludeHidden; p.IncludeSystem = IncludeSystem;
        p.ContinueOnError = ContinueOnError; p.ConfirmDeletes = ConfirmDeletes; p.BackupBeforeOverwrite = BackupBeforeOverwrite;
        p.DeleteStrategy = SelectedDeleteOption.Strategy;
        p.SafetyFolderPath = string.IsNullOrWhiteSpace(SafetyFolder) ? null : SafetyFolder.Trim();
        p.ExcludePatterns = Excludes.Select(e => e.Text.Trim()).Where(t => t.Length > 0).ToList();
        p.IncludePatterns = Includes.Select(e => e.Text.Trim()).Where(t => t.Length > 0).ToList();
        p.FilterCaseSensitive = FilterCaseSensitive; p.FilterWildcards = FilterWildcards;
        p.MaxParallelFileOperations = MaxParallel;
        p.TransientRetryCount = RetryCount; p.TransientRetryDelayMs = RetryDelayMs;
        p.MassDeleteThreshold = MassDeletePercent / 100.0;
        return p;
    }

    public SyncProfile GetProfile() => Build();

    public RelayCommand RestoreDefaultsCommand => new(() =>
    {
        IncludeSubfolders = true;
        PreserveTimestamps = true;
        PreserveAttributes = true;
        VerifyContentByHash = false;
        VerifyCopyByHash = false;
        ToleranceSeconds = 2;
        IncludeHidden = true;
        IncludeSystem = false;
        FilterCaseSensitive = false;
        FilterWildcards = true;
        SafetyFolder = "";
        BackupBeforeOverwrite = true;
        ContinueOnError = true;
        ConfirmDeletes = true;
        MassDeletePercent = 30;
        MaxParallel = 0;
        RetryCount = 3;
        RetryDelayMs = 500;
        Touch();
    });

    public async Task SaveAsync()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Informe o nome do perfil (seção Geral)."; Section = EditorSection.General; return; }
        if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Target))
        {
            Error = "Informe as pastas Source e Target (seção Geral).";
            Section = EditorSection.General;
            return;
        }

        try
        {
            var saved = await _repo.SaveAsync(Build());
            _dirty = false;
            OnPropertyChanged(nameof(IsDirty));
            Closed?.Invoke(saved);
        }
        catch (DuplicateProfileNameException ex)
        {
            Error = ex.Message;
            Section = EditorSection.General;
        }
        catch (Exception ex)
        {
            Error = "Não foi possível salvar o perfil: " + ex.Message;
        }
    }

    /// <summary>Confere o acesso às duas pastas (mesma checagem usada antes de comparar). Não altera nada.</summary>
    public async Task TestConnectionsAsync()
    {
        _testing = true;
        SourceTestText = "Testando..."; TargetTestText = "Testando...";
        SourceTestBrush = Presentation.Gray; TargetTestBrush = Presentation.Gray;
        try
        {
            var (st, sb) = await TestAsync(Source, false);
            var (tt, tb) = await TestAsync(Target, true);
            SourceTestText = st; SourceTestBrush = sb;
            TargetTestText = tt; TargetTestBrush = tb;
        }
        finally { _testing = false; }
    }

    private async Task<(string, System.Windows.Media.Brush)> TestAsync(string path, bool requireWrite)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("Informe o caminho.", Presentation.Orange);
        try
        {
            var r = await _fs.CheckRootAsync(SyncManager.Core.Paths.PathUtil.NormalizeRoot(path), requireWrite, CancellationToken.None);
            if (!r.Exists) return ($"Inacessível: {r.Error}", Presentation.Red);
            if (!r.CanRead) return ($"Sem permissão de leitura: {r.Error}", Presentation.Red);
            if (requireWrite && !r.CanWrite) return ($"Sem permissão de escrita: {r.Error}", Presentation.Red);
            return (requireWrite ? "Acessível (leitura e escrita)" : "Acessível (leitura)", Presentation.Green);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return ("Caminho inválido: " + ex.Message, Presentation.Red);
        }
    }
}
