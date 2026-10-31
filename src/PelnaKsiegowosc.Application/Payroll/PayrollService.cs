using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Application.Payroll;

/// <summary>
/// Kadry i płace: ewidencja osób (umowa o pracę, zlecenie, dzieło, powołanie), listy płac z wyliczeniem ZUS/PIT
/// i księgowanie: Wn 404 / Ma 230 (netto per osoba — rozrachunek do wypłaty), Ma 220-03 (ZUS i zdrowotna pracownika),
/// Ma 220-02 (zaliczki PIT); Wn 405 / Ma 220-03 (składki pracodawcy, FP, FGŚP).
/// </summary>
public sealed class PayrollService
{
    public const string AccidentRateKey = "place.stopa-wypadkowa";
    public const decimal DefaultAccidentRate = 1.67m;
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public PayrollService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public Task<decimal> AccidentRateAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, AccidentRateKey, () => DefaultAccidentRate, ct);

    public async Task SetAccidentRateAsync(decimal rate, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (rate is < 0.67m or > 3.33m) throw new DomainException("Stopa składki wypadkowej musi mieścić się w przedziale 0,67–3,33% (zawiadomienie ZUS).");
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, AccidentRateKey, rate, ct);
        await _db.SaveChangesAsync(ct);
    }

    private static void Validate(Employee e)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(e.FirstName) || string.IsNullOrWhiteSpace(e.LastName)) r.Error("Podaj imię i nazwisko.");
        if (!string.IsNullOrWhiteSpace(e.Pesel))
        {
            if (!Identifiers.IsValidPesel(e.Pesel) || Identifiers.BirthDateFromPesel(e.Pesel) is not { } encodedBirth)
                r.Error("PESEL jest nieprawidłowy (suma kontrolna lub zakodowana data urodzenia).");
            else if (e.BirthDate is { } birth && birth != encodedBirth)
                r.Error("Data urodzenia nie zgadza się z datą zakodowaną w numerze PESEL — wyjaśnij rozbieżność przed naliczeniem PIT/ZUS.");
        }
        if (e.Gross < 0) r.Error("Wynagrodzenie nie może być ujemne.");
        if (e.WorkTimeFraction is <= 0 or > 1) r.Error("Wymiar etatu podaj jako ułamek z przedziału (0; 1], np. 0,5.");
        if (e.EndDate is { } end && end < e.StartDate) r.Error("Data zakończenia umowy jest wcześniejsza niż data rozpoczęcia.");
        if (e.StudentUnder26 && e.ContractType != ContractType.UmowaZlecenie) r.Error("Zwolnienie ucznia/studenta do 26 lat dotyczy umowy zlecenia.");
        if (e.Costs == CostsOfIncome.Podwyzszone && e.ContractType != ContractType.UmowaOPrace) r.Error("Podwyższone koszty (300 zł) dotyczą umowy o pracę.");
        if (e.Costs == CostsOfIncome.Autorskie50)
        {
            if (string.IsNullOrWhiteSpace(e.AuthorCostsBasis))
                r.Error("Koszty 50%: wskaż podstawę (umowa z postanowieniami o prawach autorskich, ewidencja utworów, przeniesienie praw — art. 22 ust. 9 pkt 1–3 i ust. 9b ustawy o PIT).");
            if (e.AuthorSharePercent is <= 0 or > 100) r.Error("Koszty 50%: udział wynagrodzenia autorskiego podaj w procentach z przedziału (0; 100].");
        }
        r.ThrowIfInvalid();
    }

    public async Task<Employee> AddEmployeeAsync(Employee e, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        Validate(e);
        var code = "P" + (await _db.Employees.CountAsync(x => x.CompanyId == _user.CompanyId, ct) + 1).ToString("000");
        while (await _db.Counterparties.AnyAsync(c => c.CompanyId == _user.CompanyId && c.Code == code, ct)) code += "A";
        var cp = new Counterparty
        {
            CompanyId = _user.CompanyId, Code = code, Name = e.FullName, Kind = CounterpartyKind.OsobaFizyczna, Pesel = e.Pesel,
            BankAccountIban = e.BankAccountIban, DefaultPaymentDays = 0, CreatedAtUtc = _clock.UtcNow
        };
        cp.Validate().ThrowIfInvalid();
        _db.Counterparties.Add(cp);
        e.CompanyId = _user.CompanyId;
        e.CounterpartyId = cp.Id;
        e.CreatedAtUtc = _clock.UtcNow;
        _db.Employees.Add(e);
        await new AuditService(_db, _clock).AddAsync(_user, "PRACOWNIK_DODANY", "Employee", e.Id.ToString(), $"{e.FullName}; {e.ContractType}", ct);
        await _db.SaveChangesAsync(ct);
        return e;
    }

    public async Task UpdateEmployeeAsync(Employee changed, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        Validate(changed);
        var e = await _db.Employees.FirstAsync(x => x.Id == changed.Id && x.CompanyId == _user.CompanyId, ct);
        _db.Entry(e).CurrentValues.SetValues(changed);
        e.CompanyId = _user.CompanyId;
        var cp = await _db.Counterparties.FirstAsync(c => c.Id == e.CounterpartyId, ct);
        cp.Name = e.FullName;
        cp.Pesel = e.Pesel;
        cp.BankAccountIban = e.BankAccountIban;
        await new AuditService(_db, _clock).AddAsync(_user, "PRACOWNIK_ZMIENIONY", "Employee", e.Id.ToString(), e.FullName, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Limity PIT, ZUS i ulgi dla młodych liczone wg roku WYPŁATY.
    /// Dni wynagrodzenia chorobowego pracodawcy (art. 92 KP) liczone niezależnie
    /// wg roku kalendarzowego NIEZDOLNOŚCI DO PRACY (miesiąca listy).
    /// Wypłata grudniowej listy w styczniu nie zużywa styczniowego limitu 33/14 dni.
    /// </summary>
    private async Task<PayrollYearToDate> YearToDateAsync(IReadOnlyCollection<Guid> ids, DateOnly paymentDate, DateOnly serviceMonth, Guid? excludeList, CancellationToken ct)
    {
        var yearStart = new DateOnly(paymentDate.Year, 1, 1);
        var items = await (from i in _db.PayrollItems.AsNoTracking()
                           join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                           where ids.Contains(i.EmployeeId) && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                 l.PaymentDate >= yearStart && l.PaymentDate <= paymentDate && l.Id != excludeList
                           select i).ToListAsync(ct);
        var employment = items.Where(i => i.ContractType == ContractType.UmowaOPrace).ToList();
        var sickYearStart = new DateOnly(serviceMonth.Year, 1, 1);
        var sickDays = await (from i in _db.PayrollItems.AsNoTracking()
                              join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                              where ids.Contains(i.EmployeeId) && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                    l.Month >= sickYearStart && l.Month <= serviceMonth && l.Id != excludeList &&
                                    i.ContractType == ContractType.UmowaOPrace
                              select i.SickDaysEmployer).ToListAsync(ct);
        return new PayrollYearToDate(items.Sum(i => i.SocialBasis), employment.Where(i => !i.FlatRateTax).Sum(i => i.TaxBase), items.Sum(i => i.ExemptRevenue),
            sickDays.Sum(), items.Sum(i => i.AuthorCosts));
    }

    /// <summary>
    /// Bezpieczny podzbiór art. 36–43 ustawy zasiłkowej: pełne miesiące nieprzerwanego
    /// zatrudnienia przed chorobą, najwyżej 12, z jednym zaksięgowanym wynagrodzeniem
    /// zasadniczym w miesiącu. Brak historii, premie, wcześniejsze absencje i zmiana
    /// warunków płacy wymagają indywidualnego wyliczenia; nie wolno zgadywać podstawy.
    /// Wynik jest kwotą BRUTTO — kalkulator odejmuje 13,71% składek.
    /// </summary>
    private async Task<(decimal? Basis, string Problem)> VerifiedSickBasisAsync(Employee e, PayrollList list, CancellationToken ct)
    {
        var firstFullMonth = new DateOnly(e.StartDate.Year, e.StartDate.Month, 1);
        if (e.StartDate.Day > 1) firstFullMonth = firstFullMonth.AddMonths(1);
        var startMonth = list.Month.AddMonths(-12);
        if (firstFullMonth > startMonth) startMonth = firstFullMonth;
        if (startMonth >= list.Month)
            return (null, "brak pełnego miesiąca ubezpieczenia przed zachorowaniem — potrzebna indywidualna podstawa z art. 37");

        // Inna umowa tej samej osoby u pracodawcy może zwiększać podstawę chorobowego.
        // Bez historii składników nie wolno przyjmować samej pensji zasadniczej.
        if (!string.IsNullOrWhiteSpace(e.Pesel) &&
            await _db.Employees.AsNoTracking().AnyAsync(x => x.CompanyId == _user.CompanyId && x.Id != e.Id && x.Pesel == e.Pesel, ct))
            return (null, "wiele umów tej samej osoby — wymagana kwalifikacja składników wynagrodzenia");

        var history = await (from i in _db.PayrollItems.AsNoTracking()
                             join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                             where i.EmployeeId == e.Id && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                   l.Month >= startMonth && l.Month < list.Month
                             select new { l.Month, i.BaseGross, i.SocialBasis, i.SickDays, i.UnpaidDays, i.ContractType, i.PensionEmployee, i.DisabilityEmployee, i.SicknessEmployee }).ToListAsync(ct);
        var months = new List<decimal>();
        for (var month = startMonth; month < list.Month; month = month.AddMonths(1))
        {
            var entries = history.Where(x => x.Month == month).ToList();
            if (entries.Count != 1)
                return (null, $"brak jednoznacznej, zaksięgowanej historii za {month:MM.yyyy} (oczekiwano jednej wypłaty)");
            var item = entries[0];
            if (item.ContractType != ContractType.UmowaOPrace || item.SickDays != 0 || item.UnpaidDays != 0 ||
                item.BaseGross is null || item.SocialBasis <= 0 || item.BaseGross.Value != item.SocialBasis)
                return (null, $"w {month:MM.yyyy} wystąpiła nieobecność lub składniki wymagające uzupełnienia/kwalifikacji");
            var rules = PayrollRules.For(month.Year);
            static decimal Round2(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (item.PensionEmployee != Round2(item.SocialBasis * rules.PensionEmployee / 100m) ||
                item.DisabilityEmployee != Round2(item.SocialBasis * rules.DisabilityEmployee / 100m) ||
                item.SicknessEmployee != Round2(item.SocialBasis * rules.Sickness / 100m))
                return (null, $"w {month:MM.yyyy} potrącone składki nie odpowiadają standardowym 13,71% (np. limit 30-krotności)");
            months.Add(item.SocialBasis);
        }

        // Zmiana kwoty może oznaczać zmianę etatu lub składników zmiennych, których
        // historycznego rodzaju system nie przechowuje. Nie wyliczamy jej automatycznie.
        if (months.Any(x => x != months[0]) || months[0] != e.Gross)
            return (null, "zmiana wynagrodzenia/etatu w okresie podstawy — potrzebne wyliczenie z historii i kwalifikacja składników");
        return (months.Average(), "");
    }

    /// <summary>Dane wejściowe pozycji listy: wynagrodzenie zasadnicze za pełny miesiąc i dni nieobecności.</summary>
    private sealed record ItemInput(Guid ItemId, Guid EmployeeId, decimal Gross, int SickDays, int UnpaidDays);

    private static string PersonKey(Employee e) => string.IsNullOrWhiteSpace(e.Pesel) ? e.Id.ToString() : e.Pesel.Trim();

    /// <summary>
    /// Wylicza wszystkie pozycje listy (bez zapisu) — osobno dla każdej osoby (PESEL), w kolejności: umowa o pracę, powołanie, umowy
    /// cywilnoprawne wg daty zawarcia. Uwzględnia: limity roczne narastająco (także z innych umów osoby na liście), Fundusz Pracy od
    /// łącznej podstawy, zbieg tytułów zleceń, kwotę zmniejszającą PIT-2 raz w miesiącu wypłaty (łącznie z innymi zaksięgowanymi
    /// listami), wpłaty PPK pracodawcy przekazane do instytucji finansowej (przychód w wypłacie po przekazaniu).
    /// </summary>
    private async Task<Dictionary<Guid, PayrollItem>> ComputeItemsAsync(PayrollList list, IReadOnlyList<ItemInput> inputs, CancellationToken ct)
    {
        var employees = await _db.Employees.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId).ToDictionaryAsync(x => x.Id, ct);
        var monthEnd = list.Month.AddMonths(1).AddDays(-1);
        var sameMonth = employees.Values.Where(x => x.StartDate <= monthEnd && (x.EndDate == null || x.EndDate >= list.Month)).ToList();
        var rules = PayrollRules.For(list.PaymentDate.Year);
        var accident = await AccidentRateAsync(ct);
        var workingDays = Domain.Cit.PolishHolidays.WorkingDays(list.Month.Year, list.Month.Month);
        var payMonthStart = new DateOnly(list.PaymentDate.Year, list.PaymentDate.Month, 1);
        var payMonthEnd = payMonthStart.AddMonths(1).AddDays(-1);
        var result = new Dictionary<Guid, PayrollItem>();
        var sickBases = new Dictionary<Guid, decimal?>();
        foreach (var group in inputs.GroupBy(i => PersonKey(employees[i.EmployeeId])))
        {
            int Rank(Employee e) => e.ContractType switch { ContractType.UmowaOPrace => 0, ContractType.Powolanie => 1, _ => 2 };
            var ordered = group.OrderBy(i => Rank(employees[i.EmployeeId])).ThenBy(i => employees[i.EmployeeId].StartDate)
                .ThenBy(i => employees[i.EmployeeId].CreatedAtUtc).ToList();
            var personIds = employees.Values.Where(x => PersonKey(x) == group.Key).Select(x => x.Id).ToList();
            var baseYtd = await YearToDateAsync(personIds, list.PaymentDate, list.Month, list.Id, ct);
            // PIT-2: kwota zmniejszająca wykorzystana już w miesiącu wypłaty na innych zaksięgowanych listach tej osoby
            var reductionLimit = ordered.Select(i => employees[i.EmployeeId].Pit2Reduction).DefaultIfEmpty(0m).Max();
            var reductionUsed = await (from i in _db.PayrollItems.AsNoTracking()
                                       join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                                       where personIds.Contains(i.EmployeeId) && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                             l.Id != list.Id && l.PaymentDate >= payMonthStart && l.PaymentDate <= payMonthEnd
                                       select i.TaxReduction).ToListAsync(ct);
            // PPK: wpłaty pracodawcy z zaksięgowanych list, przekazane do instytucji finansowej do dnia wypłaty (w roku wypłaty),
            // jeszcze nieopodatkowane (albo opodatkowane w tej liście przy ponownym przeliczeniu)
            var ppkSources = await (from i in _db.PayrollItems.AsNoTracking()
                                    join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                                    where personIds.Contains(i.EmployeeId) && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                          l.Id != list.Id && l.PpkTransferredOn != null && l.PpkTransferredOn <= list.PaymentDate &&
                                          l.PpkTransferredOn >= new DateOnly(list.PaymentDate.Year, 1, 1) && i.PpkEmployer > 0 &&
                                          (i.PpkTaxedInListId == null || i.PpkTaxedInListId == list.Id)
                                    select new { i.EmployeeId, i.PpkEmployer }).ToListAsync(ct);
            var bases = new Dictionary<Guid, decimal>();
            for (var pass = 0; pass < 2; pass++)
            {
                decimal pension = 0, youth = 0, employmentTax = 0, author = 0;
                var reductionLeft = Math.Max(0, reductionLimit - reductionUsed.Sum());
                decimal? civilMandatoryBasis = null;
                var ppkLeft = ppkSources.GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(x => x.PpkEmployer));
                foreach (var input in ordered)
                {
                    var e = employees[input.EmployeeId];
                    decimal? sickBasis = null;
                    if (e.ContractType == ContractType.UmowaOPrace && input.SickDays > 0)
                    {
                        if (!sickBases.TryGetValue(e.Id, out sickBasis))
                        {
                            (sickBasis, _) = await VerifiedSickBasisAsync(e, list, ct);
                            sickBases[e.Id] = sickBasis;
                        }
                    }
                    var absence = e.ContractType == ContractType.UmowaOPrace && (input.SickDays > 0 || input.UnpaidDays > 0)
                        ? new PayrollAbsence(input.SickDays, input.UnpaidDays, workingDays,
                            // Bez potwierdzonej historii 0 oznacza WYŁĄCZNIE wstępne
                            // przeliczenie składników; PostAsync nadal blokuje taki zapis.
                            // Nie podstawiamy bieżącej pensji jako rzekomej podstawy chorobowego.
                            SickBasis: input.SickDays > 0 ? sickBasis ?? 0m : null,
                            CalendarDaysInMonth: DateTime.DaysInMonth(list.Month.Year, list.Month.Month), SickYear: list.Month.Year) : null;
                    var ytd = baseYtd with { PensionBasis = baseYtd.PensionBasis + pension, YouthExemptRevenue = baseYtd.YouthExemptRevenue + youth,
                        TaxBase = baseYtd.TaxBase + employmentTax, AuthorCosts = baseYtd.AuthorCosts + author };
                    var others = pass == 0 ? 0m : bases.Where(b => b.Key != input.ItemId).Sum(b => b.Value);
                    var own = IsOwnEmployee(e, sameMonth);
                    bool? civil = null;
                    var independentCivil = e.ContractType == ContractType.UmowaZlecenie && !own && !e.WorkForOwnEmployer && !e.StudentUnder26;
                    if (independentCivil) civil = PayrollCalculator.CivilMandatory(e, civilMandatoryBasis, rules);
                    var ppkTaxable = ppkLeft.Remove(e.Id, out var p) ? p : 0m;
                    var fresh = PayrollCalculator.Compute(e, input.Gross, ytd, rules, accident, absence, others, own, civil,
                        e.Pit2Reduction > 0 ? reductionLeft : 0m, ppkTaxable, list.PaymentDate);
                    fresh.BaseGross = input.Gross;
                    if (input.SickDays > 0 && sickBasis is null)
                        fresh.Notes = (fresh.Notes ?? "") + "; UWAGA: podstawa chorobowego jest NIEUSTALONA — kwota świadczenia = 0 WYŁĄCZNIE W ROBOCZEJ LIŚCIE WSTĘPNA; księgowanie tej listy zablokowane";
                    if (independentCivil && civil != false) civilMandatoryBasis = (civilMandatoryBasis ?? 0m) + fresh.SocialBasis;
                    reductionLeft -= fresh.TaxReduction;
                    bases[input.ItemId] = fresh.SocialBasis;
                    pension += fresh.SocialBasis;
                    youth += fresh.ExemptRevenue;
                    author += fresh.AuthorCosts;
                    if (e.ContractType == ContractType.UmowaOPrace && !fresh.FlatRateTax) employmentTax += fresh.TaxBase;
                    fresh.Id = input.ItemId;
                    fresh.PayrollListId = list.Id;
                    result[input.ItemId] = fresh;
                }
            }
        }
        return result;
    }

    /// <summary>Tworzy listę płac za miesiąc dla osób z umową aktywną w tym miesiącu (kwoty z ewidencji; można je zmienić przed zaksięgowaniem).</summary>
    public async Task<PayrollList> CreateListAsync(DateOnly month, DateOnly paymentDate, string? description = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var m = new DateOnly(month.Year, month.Month, 1);
        var end = m.AddMonths(1).AddDays(-1);
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == _user.CompanyId && e.IsActive && e.StartDate <= end && (e.EndDate == null || e.EndDate >= m))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync(ct);
        if (employees.Count == 0) throw new DomainException("Brak osób z umową aktywną w tym miesiącu — dodaj pracowników w zakładce „Osoby”.");
        var count = await _db.PayrollLists.CountAsync(l => l.CompanyId == _user.CompanyId && l.PaymentDate.Year == paymentDate.Year, ct);
        var list = new PayrollList
        {
            CompanyId = _user.CompanyId, Month = m, PaymentDate = paymentDate, Number = $"LP/{count + 1}/{paymentDate.Year}",
            Description = description ?? $"Lista płac za {m:MM.yyyy}", RulesVersion = PayrollRules.Version, CreatedAtUtc = _clock.UtcNow
        };
        var hr = new HrService(_db, _user, _clock);
        var inputs = new List<ItemInput>();
        foreach (var e in employees)
        {
            // nieobecności z ewidencji kadrowej (choroba, urlop bezpłatny) — tylko umowa o pracę
            var (sick, unpaid) = e.ContractType == ContractType.UmowaOPrace ? await hr.MonthAbsencesAsync(e.Id, m, ct) : (0, 0);
            inputs.Add(new ItemInput(Guid.CreateVersion7(), e.Id, e.Gross, sick, unpaid));
        }
        var computed = await ComputeItemsAsync(list, inputs, ct);
        foreach (var input in inputs) list.Items.Add(computed[input.ItemId]);
        _db.PayrollLists.Add(list);
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_UTWORZONA", "PayrollList", list.Id.ToString(), $"{list.Number}; {list.Items.Count} os.", ct);
        await _db.SaveChangesAsync(ct);
        return list;
    }

    /// <summary>Ta sama osoba (PESEL) ma w miesiącu umowę o pracę z płatnikiem (art. 8 ust. 2a ustawy o sus; art. 30 ust. 1 pkt 5a ustawy o PIT).</summary>
    private static bool IsOwnEmployee(Employee e, IEnumerable<Employee> active) =>
        e.ContractType is ContractType.UmowaZlecenie or ContractType.UmowaODzielo && !string.IsNullOrWhiteSpace(e.Pesel)
        && active.Any(x => x.Id != e.Id && x.ContractType == ContractType.UmowaOPrace && x.Pesel == e.Pesel);

    /// <summary>Zmienia wynagrodzenie zasadnicze pozycji — dni choroby i nieobecności nieodpłatnej pozostają bez zmian.</summary>
    public Task<PayrollItem> ChangeGrossAsync(Guid itemId, decimal gross, CancellationToken ct = default) => ChangeAsync(itemId, gross, null, null, ct);

    /// <summary>
    /// Przelicza pozycję: wynagrodzenie zasadnicze za pełny miesiąc oraz dni choroby i nieobecności nieodpłatnej (umowa o pracę).
    /// Wartość null — bez zmiany (zachowane dotychczasowe dane pozycji).
    /// </summary>
    public async Task<PayrollItem> ChangeAsync(Guid itemId, decimal? gross, int? sickDays, int? unpaidWorkingDays, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var it = await _db.PayrollItems.FirstAsync(i => i.Id == itemId, ct);
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == it.PayrollListId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Lista płac jest zaksięgowana — zmiana wymaga storna zapisu i nowej listy.");
        var inputs = await StoredInputsAsync(list, ct);
        var i = inputs.FindIndex(x => x.ItemId == itemId);
        inputs[i] = inputs[i] with { Gross = gross ?? inputs[i].Gross, SickDays = sickDays ?? inputs[i].SickDays, UnpaidDays = unpaidWorkingDays ?? inputs[i].UnpaidDays };
        if (inputs[i].Gross < 0) throw new DomainException("Kwota wynagrodzenia nie może być ujemna.");
        await ApplyAsync(list, await ComputeItemsAsync(list, inputs, ct));
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_POZYCJA_ZMIENIONA", "PayrollItem", it.Id.ToString(),
            $"{list.Number}; {it.EmployeeName}; brutto {inputs[i].Gross:N2}; choroba {inputs[i].SickDays} dni; nieodpłatna {inputs[i].UnpaidDays} dni", ct);
        await _db.SaveChangesAsync(ct);
        return it;
    }

    /// <summary>Przelicza całą roboczą listę według aktualnych danych osób, limitów i innych zaksięgowanych list (kwoty i dni pozycji bez zmian).</summary>
    public async Task<PayrollList> RecalculateAsync(Guid listId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Lista płac jest zaksięgowana — zmiana wymaga storna zapisu i nowej listy.");
        await ApplyAsync(list, await ComputeItemsAsync(list, await StoredInputsAsync(list, ct), ct));
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_PRZELICZONA", "PayrollList", list.Id.ToString(), list.Number, ct);
        await _db.SaveChangesAsync(ct);
        return list;
    }

    /// <summary>Dane wejściowe zapisanych pozycji: wynagrodzenie bazowe (null — z ewidencji osoby; 0 — świadomie zero) i dni nieobecności.</summary>
    private async Task<List<ItemInput>> StoredInputsAsync(PayrollList list, CancellationToken ct)
    {
        var ids = list.Items.Select(i => i.EmployeeId).Distinct().ToList();
        var gross = await _db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Gross, ct);
        return list.Items.Select(i => new ItemInput(i.Id, i.EmployeeId, i.BaseGross ?? gross[i.EmployeeId], i.SickDays, i.UnpaidDays)).ToList();
    }

    private Task ApplyAsync(PayrollList list, IReadOnlyDictionary<Guid, PayrollItem> computed)
    {
        foreach (var item in list.Items)
        {
            var fresh = computed[item.Id];
            fresh.PpkTaxedInListId = item.PpkTaxedInListId;
            _db.Entry(item).CurrentValues.SetValues(fresh);
        }
        return Task.CompletedTask;
    }

    /// <summary>Kwoty pozycji istotne dla księgowania i deklaracji — porównywane przed zaksięgowaniem z ponownym wyliczeniem.</summary>
    private static string Fingerprint(PayrollItem i) => string.Join("|", i.Gross, i.SocialBasis, i.PensionEmployee, i.DisabilityEmployee, i.SicknessEmployee,
        i.HealthBase, i.Health, i.CostsOfIncome, i.AuthorCosts, i.ExemptRevenue, i.TaxBase, i.TaxReduction, i.TaxAdvance, i.FlatRateTax, i.Net,
        i.PpkEmployee, i.PpkEmployer, i.PpkEmployerTaxed, i.PensionEmployer, i.DisabilityEmployer, i.Accident, i.LaborFund, i.Fgsp, i.SickPay,
        i.SickDaysEmployer, i.InsuranceUndetermined, i.ContractType);

    public async Task DeleteListAsync(Guid listId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Nie można usunąć zaksięgowanej listy płac.");
        _db.PayrollLists.Remove(list);
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_USUNIETA", "PayrollList", list.Id.ToString(), list.Number, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Guid> PpkCounterpartyAsync(CancellationToken ct)
    {
        var cp = await _db.Counterparties.FirstOrDefaultAsync(c => c.CompanyId == _user.CompanyId && c.Code == "PPK", ct);
        if (cp is not null) return cp.Id;
        cp = new Counterparty { CompanyId = _user.CompanyId, Code = "PPK", Name = "Instytucja finansowa PPK", Kind = CounterpartyKind.Firma, CreatedAtUtc = _clock.UtcNow };
        _db.Counterparties.Add(cp);
        await _db.SaveChangesAsync(ct);
        return cp.Id;
    }

    public async Task<PayrollList> PostAsync(Guid listId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Lista płac jest już zaksięgowana.");
        if (list.Items.Count == 0) throw new DomainException("Lista płac jest pusta.");
        var employees = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId).ToDictionaryAsync(e => e.Id, ct);
        foreach (var item in list.Items.Where(i => i.ContractType == ContractType.UmowaOPrace && i.SickDays > 0))
        {
            var employee = employees[item.EmployeeId];
            if ((employee.BirthDate ?? Identifiers.BirthDateFromPesel(employee.Pesel)) is null)
                throw new DomainException($"Nie można zaksięgować chorobowego dla {item.EmployeeName}: brak potwierdzonej daty urodzenia. " +
                    "Bez wieku nie da się ustalić limitu 33/14 dni pracodawcy z art. 92 KP. Uzupełnij datę urodzenia lub PESEL i przelicz listę.");
            var (basis, problem) = await VerifiedSickBasisAsync(employee, list, ct);
            if (basis is null)
                throw new DomainException($"Nie można zaksięgować wynagrodzenia chorobowego dla {item.EmployeeName}: {problem}. " +
                    "Ustal i udokumentuj podstawę według art. 36–43 ustawy zasiłkowej; program nie obsługuje jeszcze ręcznego zatwierdzania przypadków szczególnych.");
        }
        // aktualność wyliczenia: dane osób (kwota z umowy, PIT-2, zbieg tytułów, PPK…), limity roczne i inne zaksięgowane listy
        // mogły zmienić się po wyliczeniu — lista musi odpowiadać ponownemu wyliczeniu z bieżących danych
        var fresh = await ComputeItemsAsync(list, await StoredInputsAsync(list, ct), ct);
        var stale = list.Items.Where(i => Fingerprint(i) != Fingerprint(fresh[i.Id])).Select(i => i.EmployeeName).ToList();
        if (stale.Count > 0)
            throw new DomainException("Lista płac jest nieaktualna — od wyliczenia zmieniły się dane osób, limity roczne lub inne listy płac (" +
                                      string.Join(", ", stale) + "). Użyj „Przelicz listę”, sprawdź kwoty i zaksięguj ponownie.");
        var undetermined = list.Items.Where(i => i.InsuranceUndetermined).Select(i => i.EmployeeName).ToList();
        if (undetermined.Count > 0)
            throw new DomainException("Nie ustalono zbiegu tytułów do ubezpieczeń (art. 9 ustawy o sus) dla umów zlecenia: " + string.Join(", ", undetermined) +
                                      " — uzupełnij w danych osoby inne tytuły (etat, inne zlecenia, działalność) i ich podstawę, potem przelicz listę.");
        // umowy cywilnoprawne bez kwoty należności z umowy — sposób opodatkowania (art. 30 ust. 1 pkt 5a) nieustalony
        var missingAmount = list.Items.Where(i => employees.TryGetValue(i.EmployeeId, out var emp) && emp.ContractType is ContractType.UmowaZlecenie or ContractType.UmowaODzielo
                                                  && emp.ContractTotalAmount is null).Select(i => i.EmployeeName).ToList();
        if (missingAmount.Count > 0)
            throw new DomainException("Uzupełnij kwotę należności określoną w umowie (Osoby → „kwota z umowy”) i przelicz pozycje: " + string.Join(", ", missingAmount) +
                                      " — od niej zależy ryczałt od umów do 200 zł (art. 30 ust. 1 pkt 5a ustawy o PIT).");
        // PIT art. 21 ust. 1 pkt 148: zwolnienie tylko do 26. urodzin włącznie, według daty wypłaty.
        // SUS art. 6 ust. 4: zwolnienie ucznia/studenta ze zlecenia kończy się w dniu 26. urodzin
        // (okres wykonywania umowy, NIE data wypłaty). Nie wolno księgować ulgi bez potwierdzenia wieku.
        var ageUnknown = list.Items.Where(i => employees.TryGetValue(i.EmployeeId, out var e) &&
            ((e.YouthRelief && i.ExemptRevenue > 0) ||
             (e.StudentUnder26 && e.ContractType == ContractType.UmowaZlecenie && i.Gross > 0 && i.SocialBasis == 0)) &&
            (e.BirthDate ?? Identifiers.BirthDateFromPesel(e.Pesel)) is null)
            .Select(i => i.EmployeeName).Distinct().ToList();
        if (ageUnknown.Count > 0)
            throw new DomainException("Nie można potwierdzić wieku osoby korzystającej z ulgi dla młodych lub zwolnienia studenta ze składek ZUS: " +
                                      string.Join(", ", ageUnknown) + ". Uzupełnij datę urodzenia lub poprawny PESEL przed zaksięgowaniem listy.");
        // W miesiącu ukończenia 26 lat jeden znacznik StudentUnder26 nie pozwala ustalić
        // odrębnych okresów podlegania ZUS; nie wolno zwolnić automatycznie całej wypłaty.
        var monthEnd = list.Month.AddMonths(1).AddDays(-1);
        var studentAgeConflict = list.Items.Where(i => employees.TryGetValue(i.EmployeeId, out var e) &&
            e.StudentUnder26 && e.ContractType == ContractType.UmowaZlecenie && i.Gross > 0 && i.SocialBasis == 0 &&
            (e.BirthDate ?? Identifiers.BirthDateFromPesel(e.Pesel)) is { } birth &&
            birth.AddYears(26) <= monthEnd)
            .Select(i => i.EmployeeName).Distinct().ToList();
        if (studentAgeConflict.Count > 0)
            throw new DomainException("Zwolnienie studenta ze składek ZUS wymaga ustalenia okresu wykonywania zlecenia: " +
                                      string.Join(", ", studentAgeConflict) +
                                      ". Osoba ukończyła 26 lat przed końcem miesiąca listy; zweryfikuj daty i rozdziel wynagrodzenie na okresy przed/od 26. urodzin oraz przelicz listę.");
        var lines = new List<LineInput>
        {
            new("404", list.Items.Sum(i => i.Gross), 0, Description: "Wynagrodzenia brutto")
        };
        foreach (var i in list.Items.Where(i => i.Net != 0))
            lines.Add(new LineInput("230", 0, i.Net, employees[i.EmployeeId].CounterpartyId, $"Wynagrodzenie netto — {i.EmployeeName}"));
        var zusEmployee = list.Items.Sum(i => i.SocialEmployee + i.Health);
        var pit = list.Items.Sum(i => i.TaxAdvance);
        var employer = list.Items.Sum(i => i.EmployerCost);
        if (zusEmployee != 0) lines.Add(new LineInput("220-03", 0, zusEmployee, Description: "Składki ZUS i zdrowotna potrącone z wynagrodzeń"));
        if (pit != 0) lines.Add(new LineInput("220-02", 0, pit, Description: "Zaliczki na PIT / podatek zryczałtowany"));
        if (employer != 0)
        {
            lines.Add(new LineInput("405", employer, 0, Description: "Składki ZUS pracodawcy, FP, FGŚP"));
            lines.Add(new LineInput("220-03", 0, employer, Description: "Składki ZUS pracodawcy, FP, FGŚP"));
        }
        var ppk = list.Items.Sum(i => i.PpkEmployee + i.PpkEmployer);
        if (ppk != 0)
        {
            var ppkCp = await PpkCounterpartyAsync(ct);
            if (list.Items.Sum(i => i.PpkEmployer) is var er and not 0) lines.Add(new LineInput("405", er, 0, Description: "Wpłaty PPK finansowane przez pracodawcę"));
            lines.Add(new LineInput("240", 0, ppk, ppkCp, "Wpłaty PPK do instytucji finansowej"));
        }
        var date = list.Month.AddMonths(1).AddDays(-1);
        if (list.PaymentDate < date) date = list.PaymentDate;
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, list.PaymentDate, date,
            $"{list.Description} ({list.Number})", "LP", list.Number, lines), ct);
        list.Status = PayrollStatus.Zaksiegowana;
        list.JournalEntryId = entry.Id;
        // wpłaty PPK pracodawcy opodatkowane w tej liście — oznaczenie źródłowych pozycji (nie zostaną opodatkowane ponownie)
        if (list.Items.Any(i => i.PpkEmployerTaxed > 0))
        {
            var personIds = list.Items.Where(i => i.PpkEmployerTaxed > 0).Select(i => i.EmployeeId).ToList();
            var sources = await (from i in _db.PayrollItems
                                 join l in _db.PayrollLists on i.PayrollListId equals l.Id
                                 where personIds.Contains(i.EmployeeId) && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana && l.Id != list.Id &&
                                       l.PpkTransferredOn != null && l.PpkTransferredOn <= list.PaymentDate && l.PpkTransferredOn >= new DateOnly(list.PaymentDate.Year, 1, 1) &&
                                       i.PpkEmployer > 0 && i.PpkTaxedInListId == null
                                 select i).ToListAsync(ct);
            foreach (var src in sources) src.PpkTaxedInListId = list.Id;
        }
        foreach (var item in await _db.OpenItems.Where(o => o.JournalEntryId == entry.Id).ToListAsync(ct)) item.DueDate = list.PaymentDate;
        foreach (var item in _db.OpenItems.Local.Where(o => o.JournalEntryId == entry.Id)) item.DueDate = list.PaymentDate;
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_ZAKSIEGOWANA", "PayrollList", list.Id.ToString(), $"{list.Number}; zapis {entry.JournalNumber}", ct);
        await _db.SaveChangesAsync(ct);
        return list;
    }

    /// <summary>
    /// Rejestruje datę przekazania wpłat PPK z zaksięgowanej listy do instytucji finansowej. Od tego dnia wpłata pracodawcy jest
    /// przychodem pracownika (art. 11 ust. 1 ustawy o PIT) — zostanie opodatkowana w najbliższej liście płac z wypłatą w tym dniu lub później.
    /// </summary>
    public async Task RecordPpkTransferAsync(Guid listId, DateOnly transferredOn, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Zaksiegowana) throw new DomainException("Datę przekazania wpłat PPK rejestruje się dla zaksięgowanej listy płac.");
        if (list.Items.Sum(i => i.PpkEmployer + i.PpkEmployee) == 0) throw new DomainException("Lista płac nie zawiera wpłat PPK.");
        if (transferredOn < list.Month) throw new DomainException("Data przekazania wpłat PPK nie może być wcześniejsza niż miesiąc listy płac.");
        if (list.Items.Any(i => i.PpkTaxedInListId != null))
            throw new DomainException("Wpłaty PPK pracodawcy z tej listy zostały już opodatkowane w innej liście płac — zmiana daty przekazania wymaga korekty tamtej listy.");
        list.PpkTransferredOn = transferredOn;
        await new AuditService(_db, _clock).AddAsync(_user, "PPK_PRZEKAZANO", "PayrollList", list.Id.ToString(), $"{list.Number}; {transferredOn:yyyy-MM-dd}", ct);
        await _db.SaveChangesAsync(ct);
    }
}
