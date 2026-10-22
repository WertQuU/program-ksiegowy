using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    protected ViewModelBase(IDialogService dialogs) => Dialogs = dialogs;

    public IDialogService Dialogs { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Wykonuje operację z obsługą błędów: naruszenie reguły → komunikat; błąd techniczny → komunikat i wpis w logu.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await action();
            if (successMessage is not null) StatusMessage = successMessage;
            return true;
        }
        catch (DomainException ex)
        {
            await Dialogs.ErrorAsync(ex is AccessDeniedException ? "Brak uprawnień" : "Operacja niedozwolona", ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Anulowano.";
            return false;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            await Dialogs.ErrorAsync("Błąd", Describe(ex));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: { } inner } => "Baza danych odrzuciła zmianę: " + DatabaseConnector.Describe(inner),
        System.Data.Common.DbException => DatabaseConnector.Describe(ex),
        IOException or UnauthorizedAccessException => "Błąd dostępu do pliku: " + ex.Message,
        HttpRequestException => "Błąd połączenia sieciowego: " + ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}

/// <summary>Sekcja okna głównego.</summary>
public abstract partial class SectionViewModel : ViewModelBase
{
    protected SectionViewModel(AppSession session, IDialogService dialogs, string title, string icon) : base(dialogs)
    {
        Session = session;
        Title = title;
        Icon = icon;
    }

    public AppSession Session { get; }
    public string Title { get; }
    /// <summary>Krótki symbol wyświetlany w menu.</summary>
    public string Icon { get; }

    public bool Loaded { get; private set; }

    /// <summary>Wczytanie danych sekcji (przy pierwszym wyświetleniu i po „Odśwież”).</summary>
    protected abstract Task LoadCoreAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (await RunAsync(LoadCoreAsync)) Loaded = true;
    }

    /// <summary>Operacja zmieniająca dane: po powodzeniu odświeża sekcję.</summary>
    protected async Task ChangeAsync(Func<Task> action, string successMessage)
    {
        if (await RunAsync(action))
        {
            await RunAsync(LoadCoreAsync);
            StatusMessage = successMessage;
        }
    }
}
