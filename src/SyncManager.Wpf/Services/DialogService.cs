using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SyncManager.Core.Abstractions;
using SyncManager.Core.Models;
using SyncManager.Persistence;
using SyncManager.Wpf.ViewModels;
using SyncManager.Wpf.Views;

namespace SyncManager.Wpf.Services;

public enum DeleteChoice { Cancel, RemoveFromList, DeleteSource, DeleteTarget, DeleteBoth }

/// <param name="StrategyText">Como as exclusões serão feitas (pasta de segurança, Lixeira ou permanente), já em texto.</param>
public sealed record DeleteChoiceRequest(int Count, bool AnySource, bool AnyTarget, string StrategyText, string Sample);

public interface IDialogService
{
    string? BrowseFolder(string? initialPath, string title);
    bool Confirm(string title, string message, bool isWarning);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? AskSaveFile(string title, string suggestedName, string filter);
    /// <summary>Pede um texto curto (ex.: nome do perfil). Devolve null se cancelado.</summary>
    string? AskText(string title, string prompt, string? initial);
    DeleteChoice AskDeleteChoice(DeleteChoiceRequest request);
    void OpenFolder(string path);
    void OpenLogFolder();
}

public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    // Resolve as janelas sob demanda (IServiceProvider) para não criar ciclo de dependência com os ViewModels.
    public DialogService(IServiceProvider services) => _services = services;

    // A janela ativa (ex.: Perfis) é a dona do próximo diálogo; senão a principal.
    private static Window? Owner =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;

    public string? BrowseFolder(string? initialPath, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.InitialDirectory = initialPath;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message, bool isWarning) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo,
            isWarning ? MessageBoxImage.Warning : MessageBoxImage.Question,
            isWarning ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? AskSaveFile(string title, string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? AskText(string title, string prompt, string? initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial ?? "", Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(6, 5, 6, 5) };
        var ok = new System.Windows.Controls.Button { Content = "Salvar", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5) };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box); panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner, ShowInTaskbar = false
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public DeleteChoice AskDeleteChoice(DeleteChoiceRequest request)
    {
        var window = new DeleteChoiceWindow(request) { Owner = Owner };
        window.ShowDialog();
        return window.Choice;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            ShowError("Pasta não encontrada", path);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void OpenLogFolder()
    {
        var dir = _services.GetRequiredService<ILogService>().LogDirectory;
        Directory.CreateDirectory(dir);
        OpenFolder(dir);
    }
}
