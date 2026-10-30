using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Ledger;

/// <summary>Cel nowego konta opisany prostymi słowami — program dobiera numer, typ i powiązanie ze sprawozdaniem.</summary>
public sealed record AccountPurpose(string Key, string Label, string Explanation, string Parent, AccountType Type,
    string? BalanceDebitLine, string? BalanceCreditLine, string? ProfitLossLine, bool IsCost, bool IsRevenue, CitClassification DefaultCit, bool Settlement = false);

/// <summary>Odpowiedź na pytanie „czy to koszt / przychód podatkowy?”.</summary>
public enum TaxAnswer { Tak, Nie, NieWiem }

/// <summary>
/// Kreator konta: użytkownik wybiera, co chce ewidencjonować („koszt — usługi obce”, „rachunek bankowy”…) i podaje nazwę.
/// Program nadaje kolejny numer analityczny, typ konta, pozycję bilansu/RZiS i klasyfikację CIT. Jeśli brakuje konta
/// nadrzędnego — dodaje je z szablonu.
/// </summary>
public sealed class AccountWizard
{
    public static readonly IReadOnlyList<AccountPurpose> Purposes = new AccountPurpose[]
    {
        new("koszt-uslugi", "Koszt: usługa od firmy zewnętrznej", "np. abonament, sprzątanie, ochrona, szkolenia, wynajem sprzętu", "402", AccountType.WynikoweKosztowe, null, null, "R.B.III", true, false, CitClassification.KosztPodatkowy),
        new("koszt-materialy", "Koszt: materiały, paliwo, energia", "zakupy zużywane w firmie: materiały biurowe, części, paliwo, prąd, gaz", "401", AccountType.WynikoweKosztowe, null, null, "R.B.II", true, false, CitClassification.KosztPodatkowy),
        new("koszt-inne", "Koszt: inny koszt działalności", "np. ubezpieczenia majątkowe, podróże służbowe, składki członkowskie", "409", AccountType.WynikoweKosztowe, null, null, "R.B.VII", true, false, CitClassification.KosztPodatkowy),
        new("koszt-pozostaly", "Koszt nietypowy (poza zwykłą działalnością)", "np. kary, darowizny, spisane należności, szkody", "761", AccountType.WynikoweKosztowe, null, null, "R.E.III", true, false, CitClassification.DoAnalizy),
        new("koszt-finansowy", "Koszt finansowy", "np. odsetki od pożyczek, prowizje od kredytów", "751", AccountType.WynikoweKosztowe, null, null, "R.H.IV", true, false, CitClassification.DoAnalizy),
        new("przychod-uslugi", "Przychód ze sprzedaży usług lub produktów", "osobne konto dla rodzaju sprzedaży, np. „Usługi doradcze”, „Sprzedaż licencji”", "700", AccountType.WynikowePrzychodowe, null, null, "R.A.I", false, true, CitClassification.PrzychodPodatkowy),
        new("przychod-pozostaly", "Przychód nietypowy", "np. odszkodowania, otrzymane kary, sprzedaż środka trwałego", "760", AccountType.WynikowePrzychodowe, null, null, "R.D.IV", false, true, CitClassification.PrzychodPodatkowy),
        new("przychod-finansowy", "Przychód finansowy", "np. odsetki od lokat", "750", AccountType.WynikowePrzychodowe, null, null, "R.G.V", false, true, CitClassification.DoAnalizy),
        new("bank", "Rachunek bankowy", "kolejny rachunek w banku (np. drugi bank, lokata, rachunek walutowy)", "130", AccountType.Aktywne, "B.III.1.c", null, null, false, false, CitClassification.NieDotyczy),
        new("publiczne", "Rozliczenie z urzędem (podatek, opłata)", "np. podatek od nieruchomości, PFRON, opłata środowiskowa", "220", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g", null, false, false, CitClassification.NieDotyczy),
    };

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public AccountWizard(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public static CitClassification Classify(AccountPurpose p, TaxAnswer answer) => (p.IsCost, p.IsRevenue, answer) switch
    {
        (true, _, TaxAnswer.Tak) => CitClassification.KosztPodatkowy,
        (true, _, TaxAnswer.Nie) => CitClassification.KosztNiepodatkowy,
        (_, true, TaxAnswer.Tak) => CitClassification.PrzychodPodatkowy,
        (_, true, TaxAnswer.Nie) => CitClassification.PrzychodNiepodatkowy,
        (true, _, _) or (_, true, _) => CitClassification.DoAnalizy,
        _ => p.DefaultCit
    };

    /// <summary>Kolejny wolny numer analityczny konta nadrzędnego (01, 02…, z pominięciem 99 — „pozostałe”).</summary>
    public async Task<string> NextNumberAsync(Guid chartId, string parent, CancellationToken ct = default)
    {
        var numbers = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chartId && a.Number.StartsWith(parent + "-")).Select(a => a.Number).ToListAsync(ct);
        var used = numbers.Select(n => n[(parent.Length + 1)..]).Where(s => !s.Contains('-') && int.TryParse(s, out _)).Select(int.Parse).Where(n => n != 99).ToList();
        var next = used.Count == 0 ? 1 : used.Max() + 1;
        if (next == 99) next = 100;
        return $"{parent}-{next:00}";
    }

    public async Task<Account> CreateAsync(Guid chartId, string purposeKey, string name, TaxAnswer tax, CancellationToken ct = default)
    {
        var p = Purposes.FirstOrDefault(x => x.Key == purposeKey) ?? throw new DomainException("Wybierz, do czego ma służyć konto.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Podaj nazwę konta, np. „Ochrona obiektu”.");
        var charts = new ChartService(_db, _user, _clock);
        var parent = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ChartId == chartId && a.Number == p.Parent, ct);
        if (parent is null)
        {
            var t = ChartTemplate.Accounts.FirstOrDefault(a => a.Number == p.Parent) ?? throw new DomainException($"Brak konta {p.Parent} w szablonie.");
            await charts.AddAccountAsync(chartId, t.Number, t.Name, t.Type, t.BalanceDebitLine, t.BalanceCreditLine, t.ProfitLossLine, t.Settlement, t.Cit, ct);
        }
        else if (await _db.JournalLines.AnyAsync(l => l.AccountId == parent.Id, ct))
            throw new DomainException($"Konto {p.Parent} ({parent.Name}) ma już zapisy i nie ma podkont — nowe konto analityczne można dodać dopiero po przeksięgowaniu jego salda. " +
                                      "Poproś księgowego albo użyj istniejącego konta.");
        var number = await NextNumberAsync(chartId, p.Parent, ct);
        return await charts.AddAccountAsync(chartId, number, name.Trim(), p.Type, p.BalanceDebitLine, p.BalanceCreditLine, p.ProfitLossLine, p.Settlement, Classify(p, tax), ct);
    }
}
