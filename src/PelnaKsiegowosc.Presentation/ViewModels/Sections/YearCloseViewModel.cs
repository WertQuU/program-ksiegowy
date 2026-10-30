using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>
/// Kreator zamknięcia roku: kolejność czynności końca roku (inwentaryzacja, wycena, podatki, zamknięcie ksiąg, sprawozdanie,
/// deklaracje roczne) — każdy krok ze stanem i przyciskiem prowadzącym do właściwego miejsca w programie.
/// </summary>
public sealed partial class YearCloseViewModel : ViewModelBase
{
    private readonly AppSession _session;

    public YearCloseViewModel(AppSession session, IDialogService dialogs) : base(dialogs)
    {
        _session = session;
        Task Go(AppArea a) { _session.Navigate(a); return Task.CompletedTask; }
        Steps = new List<CloseStep>
        {
            new(1, "Rozlicz i zamknij wszystkie miesiące roku",
                "Każdy miesiąc rozliczasz kreatorem „Rozlicz miesiąc” (dokumenty, bank, amortyzacja, VAT, zaliczka CIT). Grudzień — po wykonaniu kroków 2–5.",
                "Rozlicz miesiąc", () => Go(AppArea.Zamkniecie)),
            new(2, "Inwentaryzacja na koniec roku",
                "Spis z natury (magazyn, kasa), potwierdzenia sald z kontrahentami, weryfikacja pozostałych pozycji (art. 26 UoR). Program przygotowuje wykazy i protokoły.",
                "Wykazy inwentaryzacyjne", () => Go(AppArea.Deklaracje)),
            new(3, "Odpisy na należności i rezerwy",
                "Zakładka „Odpisy i rezerwy”: zaksięguj proponowane odpisy na przeterminowane należności oraz rezerwy (urlopy — z ewidencji urlopów, badanie sprawozdania, inne).",
                "Odpisy i rezerwy", () => Go(AppArea.Zamkniecie)),
            new(4, "Amortyzacja za grudzień",
                "Nalicz amortyzację środków trwałych za ostatni miesiąc roku.", "Środki trwałe", () => Go(AppArea.SrodkiTrwale)),
            new(5, "Podatek dochodowy za rok i podatek odroczony",
                "W sekcji CIT: przelicz wynik podatkowy za cały rok, zaksięguj podatek należny (870) i „Podatek odroczony…” (841/842/872).",
                "Sekcja CIT", () => Go(AppArea.Cit)),
            new(6, "Zamknięcie roku obrotowego",
                "Podgląd i zamknięcie roku: przeksięgowanie wyniku, zablokowanie ksiąg, bilans otwarcia następnego roku (z kopią zapasową).",
                "Zamknij rok", () => Go(AppArea.Zamkniecie)),
            new(7, "Sprawozdanie finansowe",
                "Bilans, rachunek zysków i strat, informacja dodatkowa (i dla jednostek pełnych: zmiany w kapitale, przepływy). Plik e-Sprawozdania podpisuje zarząd w aplikacji MF.",
                "Raporty", () => Go(AppArea.Raporty)),
            new(8, "Deklaracje roczne i uchwały",
                "CIT-8 (koniec 3. miesiąca), PIT-11 / PIT-4R / PIT-8AR (koniec stycznia), IFT-2R, uchwały zatwierdzające sprawozdanie i podział wyniku, złożenie w KRS.",
                "Deklaracje", () => Go(AppArea.Deklaracje)),
        };
    }

    public List<CloseStep> Steps { get; }
    [ObservableProperty] private string _summary = "";

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async () =>
    {
        var y = _session.CurrentYear;
        await using var db = _session.OpenDb();
        var openMonths = y.Periods.Count(p => p.Status != PeriodStatus.Zamkniety);
        Steps[0].Done = openMonths == 0;
        Steps[0].Status = openMonths == 0 ? "Wszystkie miesiące zamknięte." : $"Otwartych miesięcy: {openMonths}.";
        var cit = LegalForms.IsCitTaxpayer(_session.Company!.LegalForm);
        Steps[4].Applicable = cit;
        if (cit)
        {
            try
            {
                var d = await new DeferredTaxService(db, _session.User, _session.Clock).ComputeAsync(y.Id, y.EndDate);
                Steps[4].Done = d.ProvisionChange == 0 && d.AssetChange == 0;
                Steps[4].Status = Steps[4].Done ? "Podatek odroczony ujęty w aktualnej wysokości." : $"Do zaksięgowania zmiana podatku odroczonego: {Money.Format(d.ResultCharge)} zł.";
            }
            catch (DomainException ex) { Steps[4].Status = ex.Message; }
        }
        else Steps[4].Status = "Jednostka nie jest podatnikiem CIT.";
        var provisions = await db.Provisions.AsNoTracking().AnyAsync(p => p.CompanyId == _session.CompanyId && p.Date >= y.StartDate && p.Date <= y.EndDate);
        Steps[2].Status = provisions ? "Rezerwy w tym roku zaksięgowano — sprawdź, czy są kompletne." : "Brak rezerw w tym roku — sprawdź, czy są potrzebne.";
        Steps[5].Done = y.Status == FiscalYearStatus.Zamkniety;
        Steps[5].Status = Steps[5].Done ? "Rok zamknięty." : "Rok otwarty.";
        foreach (var i in new[] { 1, 2, 3, 6, 7 }) if (string.IsNullOrEmpty(Steps[i].Status)) Steps[i].Status = "Sprawdź i wykonaj.";
        var left = Steps.Count(s => s.Applicable && !s.Done);
        Summary = $"Rok {y.Name}: wykonuj kroki po kolei. Automatycznie sprawdzane: zamknięcie miesięcy, podatek odroczony i zamknięcie roku (pozostało: {left}).";
    });
}
