using Avalonia.Controls;

namespace PelnaKsiegowosc.App.Views;

public partial class MainWindow : Window
{
    private bool _closingDone;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>Wywoływane raz przy zamykaniu okna (np. kopia zapasowa przy zamknięciu programu).</summary>
    public event Func<Task>? ClosingAccepted;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingDone || ClosingAccepted is null) return;
        e.Cancel = true;
        _closingDone = true;
        IsEnabled = false;
        Title += " — zapisywanie kopii zapasowej…";
        try { await ClosingAccepted.Invoke(); }
        finally { Close(); }
    }
}
