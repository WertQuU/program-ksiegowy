using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Assistant;

public enum IssueSeverity
{
    /// <summary>Błąd — księgi lub rozliczenia mogą być nieprawidłowe; wymaga działania.</summary>
    Blad,
    /// <summary>Ostrzeżenie — prawdopodobny problem albo zaległość.</summary>
    Ostrzezenie,
    /// <summary>Wskazówka — warto sprawdzić.</summary>
    Wskazowka
}

/// <summary>Miejsce w programie, w którym usuwa się problem (sekcja okna głównego).</summary>
public enum AppArea
{
    Pulpit, Jednostka, Dokumenty, Sprzedaz, Zakupy, Ksiegowosc, PlanKont, Kontrahenci, Bank, Vat, Cit, SrodkiTrwale, Kapital,
    Raporty, Zamkniecie, Archiwum, Backupy, Integracje, Ustawienia, Pomoc, Przepisy, Place, Deklaracje, Magazyn
}

/// <summary>Wykryty problem: co jest nie tak, dlaczego to ważne i jak to naprawić.</summary>
public sealed record Issue(string Code, IssueSeverity Severity, string Title, string Details, string HowToFix, AppArea Area, Guid? ObjectId = null);

/// <summary>Termin ustawowy z informacją, czy obowiązek został już wykonany w programie.</summary>
public sealed record Deadline(DateOnly Date, string Title, string Details, bool Done, AppArea Area, bool Informational = false)
{
    /// <summary>Termin informacyjny — program nie wie, czy obowiązek wykonano poza nim (np. złożenie CIT-8), więc nie oznacza go jako zaległego.</summary>
    public bool IsOverdue(DateOnly today) => !Done && !Informational && Date < today;
    public int DaysLeft(DateOnly today) => Date.DayNumber - today.DayNumber;
}

/// <summary>
/// Asystent kontroli ksiąg: automatycznie wykrywa błędy, niezgodności i zaległości (dokumenty, VAT, KSeF, rozrachunki,
/// salda kont, bank, kasa, amortyzacja, zamknięcia, kopie) oraz wylicza najbliższe terminy ustawowe.
/// Każdy problem ma opis w prostym języku i wskazówkę naprawy. Kontrole nie zmieniają danych.
/// Podstawy prawne podane w opisach mają charakter informacyjny i wymagają weryfikacji z aktualnym brzmieniem przepisów.
/// </summary>
public sealed class BookkeepingAssistant
{
    /// <summary>Od tego dnia faktury krajowe B2B wystawia się w KSeF (obowiązek dla wszystkich podatników poza wyjątkami).</summary>
    public static readonly DateOnly KsefMandatoryFrom = new(2026, 4, 1);
    public const decimal CashPaymentLimit = 15000m;
    public const decimal SplitPaymentThreshold = 15000m;

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public BookkeepingAssistant(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<List<Issue>> CheckAsync(Guid fiscalYearId, DateTime? lastBackupUtc, CancellationToken ct = default)
    {
        var issues = new List<Issue>();
        var today = _clock.Today;
        var companyId = _user.CompanyId;
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId, ct);
        var fy = await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
        void Add(string code, IssueSeverity s, string title, string details, string fix, AppArea area, Guid? id = null) =>
            issues.Add(new Issue(code, s, title, details, fix, area, id));

        // --- Konfiguracja ---
        foreach (var (item, ok, details) in await new SetupService(_db, _clock).ReadinessAsync(companyId, ct))
        {
            if (ok) continue;
            var area = item.Contains("kopia", StringComparison.OrdinalIgnoreCase) ? AppArea.Backupy
                : item.Contains("Plan kont") ? AppArea.PlanKont : AppArea.Jednostka;
            var blocking = item.StartsWith("Plan kont") || item.StartsWith("Polityka");
            Add("GOTOWOSC", blocking ? IssueSeverity.Blad : IssueSeverity.Ostrzezenie, $"Nie spełniono: {item}", details,
                blocking ? "Bez tego księgowanie jest zablokowane. Uzupełnij i zatwierdź." : "Uzupełnij brakujące dane.", area);
        }

        // --- Dokumenty ---
        var docs = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && d.Status != DocumentStatus.Anulowany).ToListAsync(ct);
        var drafts = docs.Where(d => d.Status == DocumentStatus.Roboczy && d.IssueDate <= today.AddDays(-7)).ToList();
        if (drafts.Count > 0)
            Add("DOK_ROBOCZE", IssueSeverity.Ostrzezenie, $"{drafts.Count} dokument(ów) roboczych starszych niż 7 dni",
                string.Join(", ", drafts.Take(8).Select(d => d.Number)), "Sprawdź je, zatwierdź i zaksięguj albo anuluj błędne.", AppArea.Dokumenty);
        var unposted = docs.Where(d => d.Status == DocumentStatus.Zatwierdzony).ToList();
        static DateOnly BooksDue(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(14);
        var unpostedOld = unposted.Concat(docs.Where(d => d.Status == DocumentStatus.Roboczy)).Where(d => today > BooksDue(d.IssueDate)).ToList();
        if (unpostedOld.Count > 0)
            Add("DOK_NIEZAKSIEGOWANE", IssueSeverity.Blad, $"{unpostedOld.Count} dokumentów z minionych miesięcy nie zaksięgowano w terminie",
                string.Join(", ", unpostedOld.Take(8).Select(d => d.Number)) +
                ". Księgi prowadzi się bieżąco (art. 24 ust. 5 UoR) — przyjęty termin ujęcia operacji miesiąca to 15. dzień następnego miesiąca (sprawdź politykę rachunkowości).",
                "Użyj kreatora „Rozlicz miesiąc” albo zaksięguj je w sekcji Dokumenty.", AppArea.Dokumenty);
        else if (unposted.Count > 0)
            Add("DOK_DO_KSIEGOWANIA", IssueSeverity.Wskazowka, $"{unposted.Count} zatwierdzonych dokumentów czeka na zaksięgowanie",
                string.Join(", ", unposted.Take(8).Select(d => d.Number)), "Zaksięguj je (Dokumenty → Zaksięguj).", AppArea.Dokumenty);

        foreach (var d in docs.Where(d => d.IsPurchase && d.Status == DocumentStatus.Roboczy && d.VatAmount != 0 && !d.VatDeductionConfirmed && !d.ExcludedFromVatRegister))
            Add("ZAKUP_ODLICZENIE", IssueSeverity.Ostrzezenie, $"Zakup {d.Number} ({d.CounterpartyName}): nie ustalono odliczenia VAT",
                "Nie każdy wydatek daje prawo do odliczenia VAT (np. samochód osobowy — zwykle 50%, gastronomia i noclegi — brak odliczenia).",
                "Zakupy → zaznacz dokument → wybierz kategorię wydatku lub wpisz procent odliczenia → „Potwierdź odliczenie VAT”.", AppArea.Zakupy, d.Id);

        foreach (var d in docs.Where(d => d.IssueDate > today.AddDays(1)))
            Add("DATA_PRZYSZLA", IssueSeverity.Ostrzezenie, $"Dokument {d.Number} ma datę z przyszłości ({d.IssueDate.ToPl()})", "Prawdopodobnie błąd w dacie.",
                "Popraw datę (dokument roboczy) albo wystaw korektę.", d.IsSales ? AppArea.Sprzedaz : AppArea.Dokumenty, d.Id);

        foreach (var d in docs.Where(d => d.IsInvoice && d.Currency != "PLN" && string.IsNullOrWhiteSpace(d.ExchangeRateTable) && d.Status != DocumentStatus.Zaksiegowany))
            Add("KURS_NBP", IssueSeverity.Ostrzezenie, $"Dokument {d.Number} w {d.Currency} bez numeru tabeli kursu NBP",
                "Kwoty w walucie przelicza się po kursie średnim NBP z ostatniego dnia roboczego przed dniem operacji (art. 31a ustawy o VAT, art. 30 UoR).",
                "Otwórz dokument (Edytuj roboczą) i kliknij „Pobierz kurs NBP”.", d.IsSales ? AppArea.Sprzedaz : AppArea.Zakupy, d.Id);

        // KSeF
        var noKsef = docs.Where(d => d.Kind is DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy && d.Status is DocumentStatus.Zatwierdzony or DocumentStatus.Zaksiegowany
                                     && d.IssueDate >= KsefMandatoryFrom && string.IsNullOrEmpty(d.KsefNumber)
                                     && (d.CounterpartyCountry ?? "PL") == "PL" && !string.IsNullOrEmpty(d.CounterpartyNip)).ToList();
        if (noKsef.Count > 0)
            Add("KSEF_BRAK", IssueSeverity.Ostrzezenie, $"{noKsef.Count} faktur sprzedaży dla firm bez numeru KSeF",
                string.Join(", ", noKsef.Take(8).Select(d => d.Number)) + ". Od 2026 r. faktury dla firm krajowych wystawia się w KSeF (art. 106ga ustawy o VAT): najwięksi podatnicy od 1.02.2026, pozostali od 1.04.2026; podatnicy ze sprzedażą do 10 000 zł miesięcznie mogą do końca 2026 r. wystawiać faktury poza KSeF.",
                "Sprzedaż → zaznacz fakturę → „Wyślij do KSeF”. Jeżeli faktura była wystawiona w trybie awaryjnym/offline — dośli ją w terminie.", AppArea.Sprzedaz);
        foreach (var d in docs.Where(d => d.KsefStatus == KsefStatus.Odrzucona))
            Add("KSEF_ODRZUCONA", IssueSeverity.Blad, $"KSeF odrzucił fakturę {d.Number}", "Faktura nie została wystawiona w KSeF.",
                "Sprawdź komunikat w Integracje → Dziennik integracji, popraw dane i wyślij ponownie.", AppArea.Sprzedaz, d.Id);

        // Duplikaty zakupów
        foreach (var g in docs.Where(d => d.IsPurchase && !string.IsNullOrEmpty(d.ExternalNumber)).GroupBy(d => (d.CounterpartyId, d.ExternalNumber, d.Kind)).Where(g => g.Count() > 1))
            Add("ZAKUP_DUPLIKAT", IssueSeverity.Blad, $"Faktura {g.Key.ExternalNumber} od {g.First().CounterpartyName} zarejestrowana {g.Count()} razy",
                string.Join(", ", g.Select(d => d.Number)), "Anuluj zbędny dokument (jeżeli niezaksięgowany) albo wystornuj zapis.", AppArea.Zakupy);

        // Przelewy > 15 000 zł na rachunek spoza białej listy (art. 15d ust. 1 pkt 2 ustawy o CIT, art. 117ba Ordynacji podatkowej)
        var cpWl = await _db.Counterparties.AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id, ct);
        foreach (var d in docs.Where(d => d.IsPurchase && d.PaymentMethod == PaymentMethod.Przelew && d.GrossAmountPln > CashPaymentLimit && fy.Contains(d.IssueDate)
                                          && d.CounterpartyId is { } cid && cpWl.TryGetValue(cid, out var c) && c.CountryCode == "PL" && !string.IsNullOrWhiteSpace(c.Nip)
                                          && (c.WhiteListAccountOk != true || c.WhiteListCheckedOn is null || c.WhiteListCheckedOn < d.IssueDate)))
        {
            var c = cpWl[d.CounterpartyId!.Value];
            Add("BIALA_LISTA_15000", IssueSeverity.Ostrzezenie, $"{d.Number}: przelew ponad 15 000 zł — sprawdź rachunek {c.Name} na białej liście",
                c.WhiteListAccountOk == false
                    ? $"Ostatnie sprawdzenie ({c.WhiteListCheckedOn?.ToPl()}): rachunku NIE MA w wykazie."
                    : $"Brak sprawdzenia rachunku na dzień zlecenia przelewu (ostatnie: {c.WhiteListCheckedOn?.ToPl() ?? "nigdy"}).",
                "Kontrahenci → „Sprawdź rachunek” w dniu zlecenia przelewu (identyfikator zapytania zachowaj jako dowód). Rachunek spoza wykazu: zapłać na rachunek z wykazu albo złóż ZAW-NR w ciągu 7 dni — inaczej kwota nie jest kosztem podatkowym i grozi solidarna odpowiedzialność za VAT.",
                AppArea.Kontrahenci, d.Id);
        }

        // Płatności gotówkowe > 15 000 zł (art. 15d ustawy o CIT, art. 19 ustawy Prawo przedsiębiorców)
        foreach (var d in docs.Where(d => d.IsPurchase && d.PaymentMethod == PaymentMethod.Gotowka && d.GrossAmountPln > CashPaymentLimit && fy.Contains(d.IssueDate)))
            Add("GOTOWKA_LIMIT", IssueSeverity.Ostrzezenie, $"Zakup {d.Number} za {Money.Format(d.GrossAmountPln)} zł opłacony gotówką",
                "Transakcje z firmami powyżej 15 000 zł należy regulować przelewem; zapłata gotówką powoduje wyłączenie kwoty z kosztów podatkowych (art. 15d ustawy o CIT).",
                "Sprawdź formę płatności; jeżeli zapłata była gotówkowa — wyłącz wydatek z KUP (CIT → korekta NKUP).", AppArea.Zakupy, d.Id);
        foreach (var d in docs.Where(d => d.IsInvoice && !d.SplitPayment && d.GrossAmountPln > SplitPaymentThreshold && d.Status != DocumentStatus.Roboczy
                                          && (d.CounterpartyCountry ?? "PL") == "PL" && d.IssueDate >= today.AddDays(-45)))
            Add("MPP_SPRAWDZ", IssueSeverity.Wskazowka, $"Faktura {d.Number} powyżej 15 000 zł bez oznaczenia podzielonej płatności",
                "Mechanizm podzielonej płatności jest obowiązkowy, gdy faktura obejmuje towary lub usługi z załącznika nr 15 do ustawy o VAT.",
                "Sprawdź, czy pozycje faktury są w załączniku nr 15; jeżeli tak — oznacz „Mechanizm podzielonej płatności”.", d.IsSales ? AppArea.Sprzedaz : AppArea.Zakupy, d.Id);

        // --- Księgowość ---
        var buffer = await _db.JournalEntries.AsNoTracking().CountAsync(e => e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Bufor && e.PostingDate < new DateOnly(today.Year, today.Month, 1), ct);
        if (buffer > 0)
            Add("BUFOR", IssueSeverity.Ostrzezenie, $"{buffer} zapisów w buforze z poprzednich miesięcy", "Zapis w buforze nie jest zapisem w księgach.",
                "Księgowość → zaznacz „Bufor” → zaksięguj albo usuń zapisy.", AppArea.Ksiegowosc);

        foreach (var p in await new IntegrityService(_db, _clock).CheckLedgerChainAsync(fiscalYearId, ct))
            Add("INTEGRALNOSC", IssueSeverity.Blad, "Naruszona integralność dziennika", p,
                "Zapisy mogły zostać zmienione poza programem. Odtwórz bazę z ostatniej poprawnej kopii i skontaktuj się z księgowym.", AppArea.Pomoc);

        var asOf = today < fy.EndDate ? (today < fy.StartDate ? fy.StartDate : today) : fy.EndDate;
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        var posting = await new SettingsService(_db, _clock).GetAsync(companyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        foreach (var t in tb.Where(t => t.IsLeaf && t.HasActivity && t.Balance != 0))
        {
            var a = t.Account;
            var numberPrefix = a.Number.Split('-')[0];
            if (numberPrefix == posting.Cash.Split('-')[0] && t.Balance < 0)
                Add("KASA_UJEMNA", IssueSeverity.Blad, $"Ujemne saldo kasy {a.Number}: {Money.Format(t.Balance)} zł",
                    "Kasa nie może mieć ujemnego stanu — zwykle brakuje zapisu wpłaty (KP) albo rozchód zaksięgowano dwukrotnie.",
                    "Sprawdź raport kasowy i zapisy na koncie (Raporty → Księga główna).", AppArea.Bank);
            else if (a.Type == AccountType.Aktywne && t.Balance < 0 && !a.Name.Contains("umorzen", StringComparison.OrdinalIgnoreCase))
                Add("SALDO_STRONA", IssueSeverity.Ostrzezenie, $"Konto aktywów {a.Number} {a.Name} ma saldo Ma ({Money.Format(-t.Balance)} zł)",
                    numberPrefix.StartsWith("13") ? "Saldo Ma rachunku bankowego oznacza debet — w bilansie wykazuje się go jako zobowiązanie (kredyt)." : "Konto aktywów powinno mieć saldo Wn lub zerowe.",
                    "Sprawdź zapisy na koncie — często to pomylona strona dekretu albo brakujący zapis.", AppArea.Ksiegowosc);
            else if (a.Type == AccountType.Pasywne && t.Balance > 0 && numberPrefix is not ("820" or "821"))
                Add("SALDO_STRONA", IssueSeverity.Ostrzezenie, $"Konto pasywów {a.Number} {a.Name} ma saldo Wn ({Money.Format(t.Balance)} zł)",
                    "Konto pasywów powinno mieć saldo Ma lub zerowe.", "Sprawdź zapisy na koncie — często to pomylona strona dekretu.", AppArea.Ksiegowosc);
            else if (a.Type == AccountType.WynikoweKosztowe && t.Balance < 0)
                Add("SALDO_KOSZT", IssueSeverity.Ostrzezenie, $"Konto kosztów {a.Number} {a.Name} ma saldo Ma", "Koszty zwykle mają saldo Wn — saldo Ma może oznaczać zbyt duże storno lub błędny dekret.",
                    "Sprawdź zapisy na koncie.", AppArea.Ksiegowosc);
            else if (a.Type == AccountType.WynikowePrzychodowe && t.Balance > 0)
                Add("SALDO_PRZYCHOD", IssueSeverity.Ostrzezenie, $"Konto przychodów {a.Number} {a.Name} ma saldo Wn", "Przychody zwykle mają saldo Ma.",
                    "Sprawdź zapisy na koncie.", AppArea.Ksiegowosc);
        }

        // Bilans: aktywa = pasywa, konta bez pozycji sprawozdania
        if (fy.Periods.Count > 0)
        {
            var sb = new StatementBuilder(_db);
            var bs = await sb.BalanceSheetAsync(fiscalYearId, asOf, ct);
            foreach (var w in bs.Warnings)
                Add("BILANS", IssueSeverity.Ostrzezenie, "Uwaga do bilansu", w, "Sprawdź plan kont (pozycje bilansu) i zapisy.", AppArea.PlanKont);
            foreach (var (acc, bal) in bs.Unmapped.Take(10))
                Add("BILANS_MAPOWANIE", IssueSeverity.Ostrzezenie, $"Konto {acc} (saldo {Money.Format(bal)}) nie ma pozycji w bilansie",
                    "Saldo nie trafi do sprawozdania finansowego.", "Plan kont → zaznacz konto → uzupełnij „Bilans Wn / Ma” albo „RZiS”.", AppArea.PlanKont);
        }

        // --- Rozrachunki ---
        var items = await _db.OpenItems.AsNoTracking().Where(o => o.CompanyId == companyId && o.Status != OpenItemStatus.Rozliczona).ToListAsync(ct);
        var cps = await _db.Counterparties.AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        foreach (var o in items.Where(o => o.Side == OpenItemSide.Zobowiazanie && o.DueDate is { } d && today.DayNumber - d.DayNumber > 90 && o.DocumentId is not null))
            Add("ZOBOWIAZANIE_90", IssueSeverity.Blad, $"Niezapłacona faktura {o.Reference} ({cps.GetValueOrDefault(o.CounterpartyId)}) — ponad 90 dni po terminie",
                $"Pozostało {Money.Format(o.RemainingPln)} zł. Nabywca ma obowiązek skorygować odliczony VAT (art. 89b ustawy o VAT); w CIT podstawę opodatkowania zwiększa się o niezapłaconą kwotę netto w okresie, w którym upłynął 90. dzień — również w zaliczce (art. 18f ustawy o CIT; program dolicza ją automatycznie w module CIT).",
                "Jeżeli faktura jest zapłacona — zaksięguj płatność. Jeżeli nie — ujmij korektę VAT w JPK; zwiększenie dochodu CIT (art. 18f) program wylicza sam w uzgodnieniu i zaliczce.", AppArea.Kontrahenci, o.Id);
        foreach (var o in items.Where(o => o.Side == OpenItemSide.Naleznosc && o.DueDate is { } d && today.DayNumber - d.DayNumber > 90))
            Add("NALEZNOSC_90", IssueSeverity.Wskazowka, $"Klient {cps.GetValueOrDefault(o.CounterpartyId)} nie zapłacił {o.Reference} od ponad 90 dni",
                $"Pozostało {Money.Format(o.RemainingPln)} zł. Możliwa „ulga na złe długi” w VAT (art. 89a) i CIT (art. 18f) oraz odpis aktualizujący (art. 35b UoR).",
                "Wyślij wezwanie do zapłaty; rozważ ulgę na złe długi po konsultacji z księgowym.", AppArea.Kontrahenci, o.Id);

        // --- Bank i kasa ---
        var oldTx = await _db.MoneyTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.Status == TransactionStatus.Nierozliczona && t.BookingDate < today.AddDays(-14)).CountAsync(ct);
        if (oldTx > 0)
            Add("BANK_NIEROZLICZONE", IssueSeverity.Ostrzezenie, $"{oldTx} operacji bankowych/kasowych czeka na rozliczenie ponad 14 dni", "Nierozliczone operacje nie są ujęte w księgach.",
                "Bank i kasa → zaznacz operację → dopasuj do faktury lub konta → Zaksięguj.", AppArea.Bank);
        var matched = await _db.MoneyTransactions.AsNoTracking().CountAsync(t => t.CompanyId == companyId && t.Status == TransactionStatus.Dopasowana, ct);
        if (matched > 0)
            Add("BANK_DOPASOWANE", IssueSeverity.Wskazowka, $"{matched} dopasowanych operacji czeka na zaksięgowanie", "",
                "Bank i kasa → „Zaksięguj wszystkie dopasowane”.", AppArea.Bank);
        var accounts = await _db.MoneyAccounts.AsNoTracking().Where(m => m.CompanyId == companyId && m.IsActive && m.Kind == MoneyAccountKind.RachunekBankowy).ToListAsync(ct);
        foreach (var m in accounts)
        {
            var last = await _db.MoneyStatements.AsNoTracking().Where(s => s.MoneyAccountId == m.Id).MaxAsync(s => (DateOnly?)s.DateTo, ct);
            var hasLedgerMovement = tb.Any(t => t.Account.Number == m.LedgerAccountNumber && t.HasActivity);
            if (hasLedgerMovement && (last is null || last < today.AddDays(-35)))
                Add("BANK_WYCIAG", IssueSeverity.Ostrzezenie, $"Brak wyciągu z rachunku „{m.Name}” za ostatni miesiąc",
                    last is null ? "Nie zaimportowano żadnego wyciągu." : $"Ostatni wyciąg do {last.ToPl()}.",
                    "Pobierz wyciąg (MT940 lub CSV) z bankowości internetowej i zaimportuj go w sekcji Bank i kasa.", AppArea.Bank);
        }

        // --- Środki trwałe ---
        var prevMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var activeAssets = await _db.FixedAssets.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.Status == FixedAssetStatus.WEwidencji
            && a.BookMethod != DepreciationMethod.Brak && a.PlacedInServiceDate < prevMonth, ct);
        if (activeAssets && fy.Contains(prevMonth) &&
            !await _db.DepreciationRuns.AnyAsync(r => r.CompanyId == companyId && r.Year == prevMonth.Year && r.Month == prevMonth.Month && !r.IsReversed, ct))
            Add("AMORTYZACJA", IssueSeverity.Ostrzezenie, $"Nie naliczono amortyzacji za {prevMonth:MM.yyyy}", "Odpisy amortyzacyjne nalicza się co miesiąc (zgodnie z polityką).",
                $"Środki trwałe → miesiąc {prevMonth:MM.yyyy} → „Nalicz i zaksięguj amortyzację”.", AppArea.SrodkiTrwale);

        // --- VAT ---
        if (company.VatStatus == VatStatus.CzynnyPodatnikVat && fy.Contains(prevMonth))
        {
            foreach (var p in await new VatService(_db, _user, _clock).ValidateRegistersAsync(prevMonth, ct))
                Add("VAT_REJESTR", IssueSeverity.Blad, $"Rejestr VAT {prevMonth:MM.yyyy}: niezgodność", p, "VAT → okres → „Kontrola rejestrów”.", AppArea.Vat);
        }

        // --- Okresy ---
        var openOld = fy.Periods.Where(p => p.Status == PeriodStatus.Otwarty && p.EndDate < today.AddMonths(-2)).ToList();
        if (openOld.Count > 0)
            Add("OKRESY_OTWARTE", IssueSeverity.Wskazowka, $"{openOld.Count} zakończonych miesięcy nie jest zamkniętych", string.Join(", ", openOld.Select(p => p.Name)),
                "Zamknięcie okresu chroni przed przypadkowymi zmianami. Użyj przycisku „Rozlicz miesiąc” na Pulpicie.", AppArea.Zamkniecie);

        // --- Przepisy ---
        var changes = (await new Legal.LegalUpdates(_db, _clock).ListAsync(ct)).Where(c => !c.Seen).ToList();
        if (changes.Count > 0)
            Add("PRZEPISY_ZMIANY", IssueSeverity.Ostrzezenie, $"Zmiany w przepisach: {changes.Count} nowych aktów do przejrzenia",
                string.Join("; ", changes.Take(4).Select(c => $"{c.ActName}: {c.Title}")),
                "Przepisy → przejrzyj zmiany; jeżeli dotyczą reguł programu, sprawdź dostępność aktualizacji programu i skonsultuj się z księgowym.", AppArea.Przepisy);
        var age = today.DayNumber - Domain.Legal.LegalRules.VerifiedOn.DayNumber;
        if (age > 120)
            Add("PRZEPISY_WERYFIKACJA", IssueSeverity.Wskazowka, $"Rejestr przepisów programu zweryfikowano {age} dni temu",
                $"Wersja {Domain.Legal.LegalRules.Version} z {Domain.Legal.LegalRules.VerifiedOn.ToPl()}.", "Sprawdź, czy jest nowsza wersja programu.", AppArea.Przepisy);

        // --- Kopie ---
        if (lastBackupUtc is null || _clock.UtcNow - lastBackupUtc.Value > TimeSpan.FromDays(7))
            Add("KOPIA", IssueSeverity.Ostrzezenie, lastBackupUtc is null ? "Nie wykonano jeszcze kopii zapasowej" : $"Ostatnia kopia zapasowa: {lastBackupUtc.Value.ToPlDateTime()}",
                "Kopia chroni księgi przed utratą (awaria dysku, kradzież, błąd).", "Backupy → „Wykonaj kopię teraz”; włącz kopie automatyczne.", AppArea.Backupy);

        // --- Kadry ---
        foreach (var h in await new Payroll.HrService(_db, _user, _clock).RemindersAsync(30, ct))
            Add("KADRY", h.Date < today ? IssueSeverity.Ostrzezenie : IssueSeverity.Wskazowka, $"{h.Employee}: {h.What} ({h.Date.ToPl()})",
                "Termin z kartoteki pracownika.", "Zaktualizuj datę w sekcji Płace → Osoby po wykonaniu badania / szkolenia / przedłużeniu umowy.", AppArea.Place, h.EmployeeId);

        return issues.OrderBy(i => i.Severity).ThenBy(i => i.Area).ToList();
    }

    /// <summary>Terminy ustawowe od 40 dni wstecz do 60 dni naprzód, z oznaczeniem wykonania na podstawie danych programu.</summary>
    public async Task<List<Deadline>> DeadlinesAsync(CancellationToken ct = default)
    {
        var today = _clock.Today;
        var companyId = _user.CompanyId;
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId, ct);
        var settings = new SettingsService(_db, _clock);
        var list = new List<Deadline>();
        var from = today.AddDays(-40);
        var to = today.AddDays(60);
        var years = await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).Where(f => f.CompanyId == companyId).ToListAsync(ct);
        var advances = await _db.CitAdvances.AsNoTracking().Where(a => a.CompanyId == companyId).ToListAsync(ct);
        var docs = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && (d.Status == DocumentStatus.Zatwierdzony || d.Status == DocumentStatus.Roboczy)).Select(d => d.IssueDate).ToListAsync(ct);
        var payDates = await _db.PayrollLists.AsNoTracking().Where(l => l.CompanyId == companyId && l.Status == Domain.Payroll.PayrollStatus.Zaksiegowana).Select(l => l.PaymentDate).ToListAsync(ct);
        var zusDay = LegalForms.IsCitTaxpayer(company.LegalForm) ? 15 : 20;

        for (var m = new DateOnly(from.Year, from.Month, 1).AddMonths(-1); m <= to; m = m.AddMonths(1))
        {
            var monthEnd = m.AddMonths(1).AddDays(-1);
            var fy = years.FirstOrDefault(y => y.Contains(m));
            if (fy is null) continue;
            var period = fy.Periods.FirstOrDefault(p => p.Contains(m));

            // Ujęcie operacji w księgach (bieżące prowadzenie ksiąg, art. 24 ust. 5 UoR; przyjęty termin: 15. dzień)
            var booksDue = m.AddMonths(1).AddDays(14);
            var booksDone = period?.Status == PeriodStatus.Zamkniety || !docs.Any(d => d >= m && d <= monthEnd);
            list.Add(new Deadline(booksDue, $"Zaksięgowanie operacji za {m:MM.yyyy}",
                "Księgi prowadzi się bieżąco (art. 24 ust. 5 UoR); przyjęto termin ujęcia operacji miesiąca do 15. dnia następnego miesiąca.", booksDone && monthEnd < today, AppArea.Dokumenty));

            if (company.VatStatus == VatStatus.CzynnyPodatnikVat && company.VatPeriod == VatSettlementPeriod.Miesieczny)
            {
                var jpkDue = Domain.Cit.CitCalculator.ShiftToBusinessDay(new DateOnly(m.Year, m.Month, 25).AddMonths(1));
                var settled = await settings.GetAsync<VatService.VatSettlementSnapshot?>(companyId, $"vat.settlement.{m:yyyy-MM}", () => null, ct) is { JournalEntryId: not null };
                var jpk = await settings.GetAsync<DateTime?>(companyId, $"jpk.wygenerowano.{m:yyyy-MM}", () => null, ct) is not null;
                list.Add(new Deadline(jpkDue, $"JPK_V7M i zapłata VAT za {m:MM.yyyy}",
                    "Plik JPK_V7M (ewidencja + deklaracja) i wpłata podatku do 25. dnia następnego miesiąca (art. 99 ust. 1, art. 103 ust. 1 ustawy o VAT). " +
                    (settled ? "Rozliczenie VAT zaksięgowane. " : "Rozliczenie VAT niezaksięgowane. ") + (jpk ? "Plik JPK wygenerowany." : "Plik JPK niewygenerowany."),
                    settled && jpk, AppArea.Vat));
            }

            if (company.VatStatus == VatStatus.CzynnyPodatnikVat && company.VatPeriod == VatSettlementPeriod.Kwartalny)
            {
                var due = Domain.Cit.CitCalculator.ShiftToBusinessDay(new DateOnly(m.Year, m.Month, 25).AddMonths(1));
                var quarterEnd = m.Month % 3 == 0;
                var jpk = await settings.GetAsync<DateTime?>(companyId, $"jpk.wygenerowano.{m:yyyy-MM}", () => null, ct) is not null;
                var settled = !quarterEnd || await settings.GetAsync<VatService.VatSettlementSnapshot?>(companyId, $"vat.settlement.{m:yyyy-MM}", () => null, ct) is { JournalEntryId: not null };
                list.Add(new Deadline(due, $"JPK_V7K za {m:MM.yyyy}" + (quarterEnd ? " (z deklaracją kwartalną i zapłatą VAT)" : " (ewidencja)"),
                    "Plik JPK_V7K składa się co miesiąc do 25. dnia (ewidencja); deklarację i zapłatę — za kwartał, w pliku za ostatni miesiąc kwartału (art. 99 ust. 2, art. 103 ust. 2 ustawy o VAT). " +
                    (jpk ? "Plik JPK wygenerowany." : "Plik JPK niewygenerowany.") + (quarterEnd ? settled ? " Rozliczenie kwartału zaksięgowane." : " Rozliczenie kwartału niezaksięgowane." : ""),
                    jpk && settled, AppArea.Vat));
            }

            if (payDates.Any(d => d >= m && d <= monthEnd))
            {
                list.Add(new Deadline(Domain.Cit.CitCalculator.ShiftToBusinessDay(new DateOnly(m.Year, m.Month, zusDay).AddMonths(1)), $"ZUS (DRA/RCA) za {m:MM.yyyy}",
                    $"Deklaracja rozliczeniowa i składki do {zusDay}. dnia następnego miesiąca (art. 47 ust. 1 ustawy o sus). Zestawienie: Płace → ZUS DRA / RCA.", false, AppArea.Place, Informational: true));
                list.Add(new Deadline(Domain.Cit.CitCalculator.ShiftToBusinessDay(new DateOnly(m.Year, m.Month, 20).AddMonths(1)), $"Zaliczki PIT od wynagrodzeń za {m:MM.yyyy}",
                    "Wpłata pobranych zaliczek i podatku zryczałtowanego do 20. dnia następnego miesiąca (art. 38 ust. 1, art. 42 ust. 1 ustawy o PIT).", false, AppArea.Place, Informational: true));
            }

            if (LegalForms.IsCitTaxpayer(company.LegalForm) && period is not null &&
                (company.CitAdvanceMethod == CitAdvanceMethod.Miesieczne || period.Number % 3 == 0))
            {
                var due = Domain.Cit.CitCalculator.AdvanceDeadline(period.EndDate);
                var adv = advances.FirstOrDefault(a => a.FiscalYearId == fy.Id && a.PeriodNumber == period.Number);
                list.Add(new Deadline(due, $"Zaliczka CIT za {(company.CitAdvanceMethod == CitAdvanceMethod.Miesieczne ? $"{m:MM.yyyy}" : $"kwartał kończący się {period.EndDate:MM.yyyy}")}",
                    "Wpłata zaliczki do 20. dnia następnego miesiąca (art. 25 ust. 1 ustawy o CIT)" + (adv is null ? "; zaliczki nie wyliczono." : $"; do zapłaty {Money.Format(adv.AdvanceDue)} zł."),
                    adv?.IsApproved == true, AppArea.Cit));
            }
        }

        foreach (var y in payDates.Select(d => d.Year).Distinct())
        {
            var due = Domain.Cit.CitCalculator.ShiftToBusinessDay(new DateOnly(y + 1, 1, 31));
            if (due >= from && due <= to)
                list.Add(new Deadline(due, $"PIT-4R, PIT-8AR i PIT-11 za {y}", "Deklaracje roczne płatnika i informacje PIT-11 do urzędu do końca stycznia; PIT-11 podatnikowi do końca lutego. Zestawienia: Płace → PIT-4R / PIT-11.", false, AppArea.Place, Informational: true));
        }

        foreach (var fy in years)
        {
            void Y(DateOnly date, string title, string details, bool done, AppArea area)
            {
                if (date >= from && date <= to) list.Add(new Deadline(date, title, details, done, area, Informational: !done));
            }
            var closed = fy.Status is FiscalYearStatus.Zamkniety or FiscalYearStatus.Zarchiwizowany;
            DateOnly After(int months) => fy.EndDate.AddDays(1).AddMonths(months).AddDays(-1);
            Y(After(3), $"Sporządzenie sprawozdania finansowego za {fy.Name}", "W ciągu 3 miesięcy od dnia bilansowego (art. 52 ust. 1 UoR); podpisuje kierownik jednostki i osoba sporządzająca. Najpierw zamknij rok obrotowy.", closed, AppArea.Raporty);
            if (LegalForms.IsCitTaxpayer(company.LegalForm))
            {
                Y(Domain.Cit.CitCalculator.AnnualReturnDeadline(fy.EndDate), $"Zeznanie CIT-8 za {fy.Name}", "Do końca 3. miesiąca roku następnego (art. 27 ust. 1 ustawy o CIT); zapłata podatku w tym terminie. Zeznanie składa się poza programem.", false, AppArea.Cit);
                if (JpkCitRequired(company, fy.StartDate) is { } basis)
                {
                    var jpkCit = await settings.GetAsync<DateTime?>(companyId, $"jpk-cit.wygenerowano.{fy.Name}", () => null, ct) is not null;
                    Y(Domain.Cit.CitCalculator.ShiftToBusinessDay(After(7)), $"JPK_CIT (księgi z oznaczeniami podatkowymi) za {fy.Name}",
                        $"Księgi w strukturze JPK_KR_PD do końca 7. miesiąca po roku podatkowym (art. 9 ust. 1e ustawy o CIT). {basis} Generator: sekcja CIT.",
                        jpkCit, AppArea.Cit);
                }
            }
            Y(After(6), $"Zatwierdzenie sprawozdania finansowego za {fy.Name}", "Nie później niż 6 miesięcy od dnia bilansowego (art. 53 ust. 1 UoR); następnie złożenie w KRS w ciągu 15 dni (art. 69 UoR) — dotyczy jednostek wpisanych do KRS.", false, AppArea.Raporty);
        }
        return list.OrderBy(d => d.Date).ToList();
    }

    /// <summary>
    /// Harmonogram obowiązku JPK_CIT (przepisy przejściowe): lata podatkowe rozpoczęte od 2025 r. — podatkowe grupy
    /// kapitałowe i najwięksi podatnicy (przychody ponad 50 mln euro; program nie ustala tego automatycznie); od 2026 r. —
    /// podatnicy składający JPK_V7M (VAT miesięcznie); od 2027 r. — podatnicy składający JPK_V7K (VAT kwartalnie)
    /// oraz pozostali podatnicy CIT. Zwraca podstawę obowiązku albo null, gdy rok jeszcze nie jest objęty.
    /// </summary>
    public static string? JpkCitRequired(Company company, DateOnly taxYearStart)
    {
        var vatPayer = company.VatStatus == VatStatus.CzynnyPodatnikVat;
        if (vatPayer && company.VatPeriod == VatSettlementPeriod.Miesieczny && taxYearStart.Year >= 2026)
            return "Obowiązek od lat podatkowych rozpoczętych po 31.12.2025 r. — podatnik składa JPK_V7M.";
        if (taxYearStart.Year >= 2027)
            return vatPayer
                ? "Obowiązek od lat podatkowych rozpoczętych po 31.12.2026 r. — podatnik składa JPK_V7K."
                : "Obowiązek od lat podatkowych rozpoczętych po 31.12.2026 r. — pozostali podatnicy CIT (bez JPK_VAT).";
        return null;
    }
}
