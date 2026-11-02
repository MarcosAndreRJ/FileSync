using SyncManager.Wpf.Mvvm;

namespace SyncManager.Wpf.ViewModels.Ftp;

public enum WizardStepState { Pending, Current, Done, Error }

/// <summary>Item da navegação lateral de um assistente: número, título e estado (pendente, atual, concluída ou com erro).</summary>
public sealed class WizardStepItem : ObservableObject
{
    private WizardStepState _state;

    public WizardStepItem(int number, string title) { Number = number; Title = title; }

    public int Number { get; }
    public string Title { get; }

    public WizardStepState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsCurrent));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(Marker));
        }
    }

    public bool IsCurrent => _state == WizardStepState.Current;
    public bool IsDone => _state == WizardStepState.Done;
    public bool HasError => _state == WizardStepState.Error;

    /// <summary>Marcador circular: número da etapa, ✓ (concluída) ou ! (com erro).</summary>
    public string Marker => _state switch
    {
        WizardStepState.Done => "✓",
        WizardStepState.Error => "!",
        _ => Number.ToString()
    };
}
