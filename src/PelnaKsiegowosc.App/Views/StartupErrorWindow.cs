using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PelnaKsiegowosc.App.Views;

public enum StartupChoice { Retry, Emergency, Exit }

/// <summary>Brak połączenia z bazą przy starcie — świadomy wybór użytkownika, bez automatycznego przełączania.</summary>
public static class StartupErrorWindow
{
    public static async Task<StartupChoice> ShowAsync(Window owner, string message, bool offerEmergency)
    {
        var w = new Window { Title = "Brak dostępu do bazy danych", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        void Add(string text, StartupChoice c)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => { w.Tag = c; w.Close(); };
            buttons.Children.Add(b);
        }
        Add("Ponów próbę", StartupChoice.Retry);
        if (offerEmergency) Add("Tryb awaryjny (kopia robocza)…", StartupChoice.Emergency);
        Add("Zakończ", StartupChoice.Exit);
        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons }
        };
        var tcs = new TaskCompletionSource<StartupChoice>();
        w.Closed += (_, _) => tcs.TrySetResult(w.Tag is StartupChoice c ? c : StartupChoice.Exit);
        if (owner.IsVisible) await w.ShowDialog(owner); else w.Show();
        return await tcs.Task;
    }
}
