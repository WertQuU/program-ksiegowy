namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>
/// Okna dialogowe bez interfejsu — do kontroli uruchomienia i testów: potwierdzenia przyjmowane automatycznie,
/// odpowiedzi i ścieżki plików z kolejek, komunikaty zapisywane w dzienniku.
/// </summary>
public sealed class AutomaticDialogService : IDialogService
{
    public List<string> Infos { get; } = new();
    public List<string> Errors { get; } = new();
    public Queue<string?> Prompts { get; } = new();
    public Queue<string?> Files { get; } = new();
    public bool ConfirmResult { get; set; } = true;

    public Task InfoAsync(string title, string message) { Infos.Add($"{title}: {message}"); return Task.CompletedTask; }
    public Task ErrorAsync(string title, string message) { Errors.Add($"{title}: {message}"); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmResult);
    public Task<string?> PromptAsync(string title, string message, string? initial = null, bool password = false) => Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : initial);
    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    public Task ShowTextAsync(string title, string text) { Infos.Add($"{title}: {text}"); return Task.CompletedTask; }
    public void OpenInSystem(string path) => Infos.Add("Otwórz: " + path);
}
