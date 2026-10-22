using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.App.Services;

/// <summary>Okna dialogowe Avalonia (Windows i Linux) — komunikaty, pytania, wybór plików i katalogów.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _owner;

    public DialogService(Func<Window?> owner) => _owner = owner;

    private Window Owner => _owner() ?? throw new InvalidOperationException("Brak okna programu.");

    private static Window Build(string title, Control content, double width = 520) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        MaxHeight = 800,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = true,
        ShowInTaskbar = false,
        Content = new Border { Padding = new Thickness(18), Child = content }
    };

    private async Task<T> ShowAsync<T>(Window w, T cancelValue)
    {
        var owner = _owner();
        if (owner is null || !owner.IsVisible)
        {
            var tcs = new TaskCompletionSource<T>();
            w.Closed += (_, _) => tcs.TrySetResult(w.Tag is T t ? t : cancelValue);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            return await tcs.Task;
        }
        await w.ShowDialog(owner);
        return w.Tag is T r ? r : cancelValue;
    }

    private static Button Btn(string text, Window w, object? result, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => { w.Tag = result; w.Close(); };
        return b;
    }

    private static Control Message(string text, IBrush? color = null) => new SelectableTextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Brushes.Black, Margin = new Thickness(0, 0, 0, 16)
    };

    public Task InfoAsync(string title, string message) => MessageAsync(title, message, null);

    public Task ErrorAsync(string title, string message) => MessageAsync(title, message, new SolidColorBrush(Color.Parse("#a40e26")));

    private async Task MessageAsync(string title, string message, IBrush? color)
    {
        var panel = new StackPanel();
        var w = Build(title, panel);
        panel.Children.Add(new ScrollViewer { MaxHeight = 600, Content = Message(message, color) });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { Btn("OK", w, true, true, true) } });
        await ShowAsync(w, true);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var panel = new StackPanel();
        var w = Build(title, panel);
        panel.Children.Add(new ScrollViewer { MaxHeight = 600, Content = Message(message) });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { Btn("Tak", w, true, isDefault: true), Btn("Nie", w, false, isCancel: true) }
        });
        return await ShowAsync(w, false);
    }

    public async Task<string?> PromptAsync(string title, string message, string? initial = null, bool password = false)
    {
        var panel = new StackPanel();
        var w = Build(title, panel);
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 0, 0, 16), AcceptsReturn = false };
        if (password) box.PasswordChar = '•';
        panel.Children.Add(Message(message));
        panel.Children.Add(box);
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) => { w.Tag = box.Text ?? ""; w.Close(); };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, Btn("Anuluj", w, null, isCancel: true) } });
        w.Opened += (_, _) => box.Focus();
        return await ShowAsync<string?>(w, null);
    }

    public async Task ShowTextAsync(string title, string text)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var w = Build(title, grid, 900);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = 640;
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,Liberation Mono,monospace") };
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        grid.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Kopiuj do schowka", Margin = new Thickness(6, 0, 0, 0) };
        copy.Click += async (_, _) => { if (w.Clipboard is { } c) await c.SetTextAsync(text); };
        buttons.Children.Add(copy);
        buttons.Children.Add(Btn("Zamknij", w, true, true, true));
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        await ShowAsync(w, true);
    }

    private static List<FilePickerFileType> Types(FileFilter[] filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();

    public async Task<string?> OpenFileAsync(string title, params FileFilter[] filters)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = Types(filters) });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters)
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, ShowOverwritePrompt = true,
            FileTypeChoices = filters.Length > 0 ? Types(filters) : null,
            DefaultExtension = filters.FirstOrDefault()?.Patterns.FirstOrDefault()?.TrimStart('*', '.')
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public void OpenInSystem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = ErrorAsync("Otwieranie", $"Nie udało się otworzyć {path}: {ex.Message}");
        }
    }
}
