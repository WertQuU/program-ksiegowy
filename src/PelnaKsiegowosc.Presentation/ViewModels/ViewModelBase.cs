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

    /// <summary>Krótkie objaśnienie sekcji w prostym języku (wyświetlane nad zawartością).</summary>
    public string Guide => Guides.GetValueOrDefault(GetType().Name, "");

    private static readonly Dictionary<string, string> Guides = new()
    {
        ["DashboardViewModel"] = "Tu widzisz, co wymaga uwagi. Pracuj od góry: najpierw pozycje „BŁĄD”, potem „UWAGA”. Przycisk „Przejdź” prowadzi tam, gdzie problem się naprawia.",
        ["CompanyViewModel"] = "Dane firmy, osoby odpowiedzialne i polityka rachunkowości. Politykę zatwierdza kierownik jednostki — bez tego program nie pozwoli księgować.",
        ["DocumentsViewModel"] = "Wszystkie dowody księgowe. Dokument przechodzi drogę: roboczy → zatwierdzony → zaksięgowany. Zaksięgowanego nie zmienia się — wystawia się korektę.",
        ["SalesViewModel"] = "Wystawianie faktur dla klientów. „Zapisz i zaksięguj” robi wszystko naraz; fakturę dla firmy wyślij potem do KSeF.",
        ["PurchasesViewModel"] = "Wydatki firmy. Wybierz rodzaj wydatku (np. paliwo, telefon, hotel) — program sam dobierze konto, odliczenie VAT i część nieuznawaną przez fiskusa.",
        ["LedgerViewModel"] = "Dziennik zapisów księgowych. Zapis ręczny (polecenie księgowania) przyda się rzadko — zwykle zapisy tworzą się z dokumentów. Błąd poprawia się stornem.",
        ["ChartViewModel"] = "Lista kont, na których program zapisuje operacje. Zwykle wystarczy plan z kreatora; nowe konto dodasz tu.",
        ["CounterpartiesViewModel"] = "Klienci i dostawcy. Wpisz NIP i kliknij „Pobierz dane z białej listy” — reszta wypełni się sama. Zakładka „Rozrachunki” pokazuje, kto komu ile winien.",
        ["BankViewModel"] = "Import wyciągów i rozliczanie płatności. Wpływ od klienta lub zapłatę za fakturę dopasuj do faktury; inną operację (opłata, podatek) — wybierz z listy „Co to za operacja?”.",
        ["VatViewModel"] = "Rejestry VAT i plik JPK_V7M. Najprościej rozliczyć VAT kreatorem „Rozlicz miesiąc” (sekcja Zamknięcie).",
        ["CitViewModel"] = "Podatek dochodowy spółki: zaliczki i różnice między wynikiem księgowym a podatkowym.",
        ["FixedAssetsViewModel"] = "Sprzęt, maszyny i auta droższe niż 10 000 zł netto. Po przyjęciu (OT) program co miesiąc nalicza amortyzację.",
        ["CapitalViewModel"] = "Wkłady wspólników, podział zysku, dywidendy i pożyczki od właścicieli. Księgowanie dopiero po wskazaniu uchwały lub umowy.",
        ["ReportsViewModel"] = "Zestawienia z ksiąg: bilans, rachunek zysków i strat, rejestry, rozrachunki. Każdy raport zapiszesz do PDF, Excela lub CSV.",
        ["ClosingViewModel"] = "Kreator „Rozlicz miesiąc” prowadzi krok po kroku: dokumenty, bank, VAT i JPK, CIT, kopia, zamknięcie. Rok zamyka się raz w roku, po rozliczeniu wszystkich miesięcy.",
        ["ArchiveViewModel"] = "Załączniki i dziennik zdarzeń (kto, co i kiedy zmienił). Kontrola sum potwierdza, że nic nie zostało zmienione.",
        ["BackupsViewModel"] = "Kopie zapasowe ksiąg. Klucz odzyskiwania przechowuj poza komputerem — bez niego kopii nie da się odtworzyć.",
        ["IntegrationsViewModel"] = "Połączenie z KSeF i kopią w chmurze OVHcloud. Tokeny i klucze są przechowywane bezpiecznie w systemie.",
        ["SettingsViewModel"] = "Gdzie są przechowywane księgi (ten komputer albo serwer) i przeniesienie ich na serwer.",
        ["HelpViewModel"] = "Informacje o programie, licencja i pełna kontrola integralności ksiąg.",
    };

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
