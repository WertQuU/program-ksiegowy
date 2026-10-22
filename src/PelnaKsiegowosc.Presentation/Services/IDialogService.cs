namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>Filtr plików w oknie wyboru, np. („Wyciąg MT940”, ["*.sta", "*.mt940"]).</summary>
public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>Okna dialogowe — implementacja w aplikacji okienkowej, w testach wersja skryptowa.</summary>
public interface IDialogService
{
    Task InfoAsync(string title, string message);
    Task ErrorAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Pytanie o tekst; <c>null</c> = anulowano.</summary>
    Task<string?> PromptAsync(string title, string message, string? initial = null, bool password = false);
    Task<string?> OpenFileAsync(string title, params FileFilter[] filters);
    Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters);
    Task<string?> PickFolderAsync(string title);
    Task ShowTextAsync(string title, string text);
    /// <summary>Otwiera plik lub katalog w domyślnym programie systemu.</summary>
    void OpenInSystem(string path);
}
