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
        if (!string.IsNullOrWhiteSpace(e.Pesel) && !Identifiers.IsValidPesel(e.Pesel)) r.Error("PESEL jest nieprawidłowy.");
        if (e.Gross < 0) r.Error("Wynagrodzenie nie może być ujemne.");
        if (e.WorkTimeFraction is <= 0 or > 1) r.Error("Wymiar etatu podaj jako ułamek z przedziału (0; 1], np. 0,5.");
        if (e.EndDate is { } end && end < e.StartDate) r.Error("Data zakończenia umowy jest wcześniejsza niż data rozpoczęcia.");
        if (e.StudentUnder26 && e.ContractType != ContractType.UmowaZlecenie) r.Error("Zwolnienie ucznia/studenta do 26 lat dotyczy umowy zlecenia.");
        if (e.Costs == CostsOfIncome.Podwyzszone && e.ContractType != ContractType.UmowaOPrace) r.Error("Podwyższone koszty (300 zł) dotyczą umowy o pracę.");
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

    /// <summary>Wartości narastające danej osoby z list zaksięgowanych w roku wypłaty, wypłaconych przed wskazaną datą.</summary>
    private async Task<PayrollYearToDate> YearToDateAsync(Guid employeeId, DateOnly paymentDate, Guid? excludeList, CancellationToken ct)
    {
        var yearStart = new DateOnly(paymentDate.Year, 1, 1);
        var items = await (from i in _db.PayrollItems.AsNoTracking()
                           join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                           where i.EmployeeId == employeeId && l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana &&
                                 l.PaymentDate >= yearStart && l.PaymentDate <= paymentDate && l.Id != excludeList
                           select i).ToListAsync(ct);
        return new PayrollYearToDate(items.Sum(i => i.SocialBasis), items.Where(i => !i.FlatRateTax).Sum(i => i.TaxBase), items.Sum(i => i.ExemptRevenue), items.Sum(i => i.SickDaysEmployer));
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
        var rules = PayrollRules.For(paymentDate.Year);
        var accident = await AccidentRateAsync(ct);
        var hr = new HrService(_db, _user, _clock);
        var workingDays = Domain.Cit.PolishHolidays.WorkingDays(m.Year, m.Month);
        var inputs = new Dictionary<PayrollItem, (Employee Employee, PayrollAbsence? Absence)>();
        foreach (var e in employees)
        {
            // nieobecności z ewidencji kadrowej (choroba, urlop bezpłatny) — tylko umowa o pracę
            PayrollAbsence? absence = null;
            if (e.ContractType == ContractType.UmowaOPrace)
            {
                var (sick, unpaid) = await hr.MonthAbsencesAsync(e.Id, m, ct);
                if (sick > 0 || unpaid > 0) absence = new PayrollAbsence(sick, unpaid, workingDays);
            }
            var it = PayrollCalculator.Compute(e, e.Gross, await YearToDateAsync(e.Id, paymentDate, null, ct), rules, accident, absence,
                ownEmployee: IsOwnEmployee(e, employees));
            it.PayrollListId = list.Id;
            list.Items.Add(it);
            inputs[it] = (e, absence);
        }
        // Fundusz Pracy: osoba z kilkoma tytułami u płatnika (ten sam PESEL) — próg od łącznej podstawy
        foreach (var group in inputs.Where(x => !string.IsNullOrWhiteSpace(x.Value.Employee.Pesel)).GroupBy(x => x.Value.Employee.Pesel!.Trim()).Where(g => g.Count() > 1))
            foreach (var (item, (emp, abs)) in group.ToList())
            {
                var others = group.Where(x => x.Key != item).Sum(x => x.Key.SocialBasis);
                var fresh = PayrollCalculator.Compute(emp, emp.Gross, await YearToDateAsync(emp.Id, paymentDate, null, ct), rules, accident, abs, others, IsOwnEmployee(emp, employees));
                fresh.Id = item.Id;
                fresh.PayrollListId = list.Id;
                var i = list.Items.IndexOf(item);
                list.Items[i] = fresh;
            }
        _db.PayrollLists.Add(list);
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_UTWORZONA", "PayrollList", list.Id.ToString(), $"{list.Number}; {list.Items.Count} os.", ct);
        await _db.SaveChangesAsync(ct);
        return list;
    }

    /// <summary>Ta sama osoba (PESEL) ma w miesiącu umowę o pracę z płatnikiem.</summary>
    private static bool IsOwnEmployee(Employee e, IEnumerable<Employee> active) =>
        e.ContractType is ContractType.UmowaZlecenie or ContractType.UmowaODzielo && !string.IsNullOrWhiteSpace(e.Pesel)
        && active.Any(x => x.Id != e.Id && x.ContractType == ContractType.UmowaOPrace && x.Pesel == e.Pesel);

    public Task<PayrollItem> ChangeGrossAsync(Guid itemId, decimal gross, CancellationToken ct = default) => ChangeAsync(itemId, gross, 0, 0, ct);

    /// <summary>Przelicza pozycję: wynagrodzenie zasadnicze za pełny miesiąc oraz dni choroby i nieobecności nieodpłatnej (umowa o pracę).</summary>
    public async Task<PayrollItem> ChangeAsync(Guid itemId, decimal gross, int sickDays, int unpaidWorkingDays, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var it = await _db.PayrollItems.FirstAsync(i => i.Id == itemId, ct);
        var list = await _db.PayrollLists.FirstAsync(l => l.Id == it.PayrollListId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Lista płac jest zaksięgowana — zmiana wymaga storna zapisu i nowej listy.");
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == it.EmployeeId, ct);
        var absence = sickDays > 0 || unpaidWorkingDays > 0
            ? new PayrollAbsence(sickDays, unpaidWorkingDays, Domain.Cit.PolishHolidays.WorkingDays(list.Month.Year, list.Month.Month))
            : null;
        var others = string.IsNullOrWhiteSpace(e.Pesel) ? 0m : (await (from x in _db.PayrollItems
                                                                      join p in _db.Employees on x.EmployeeId equals p.Id
                                                                      where x.PayrollListId == list.Id && x.Id != it.Id && p.Pesel == e.Pesel
                                                                      select x.SocialBasis).ToListAsync(ct)).Sum();
        var monthEnd = list.Month.AddMonths(1).AddDays(-1);
        var sameMonth = await _db.Employees.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId && x.StartDate <= monthEnd && (x.EndDate == null || x.EndDate >= list.Month)).ToListAsync(ct);
        var fresh = PayrollCalculator.Compute(e, gross, await YearToDateAsync(e.Id, list.PaymentDate, list.Id, ct), PayrollRules.For(list.PaymentDate.Year), await AccidentRateAsync(ct), absence, others,
            IsOwnEmployee(e, sameMonth));
        fresh.Id = it.Id;
        fresh.PayrollListId = list.Id;
        _db.Entry(it).CurrentValues.SetValues(fresh);
        await _db.SaveChangesAsync(ct);
        return it;
    }

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
        // umowy cywilnoprawne bez kwoty należności z umowy — sposób opodatkowania (art. 30 ust. 1 pkt 5a) nieustalony
        var missingAmount = list.Items.Where(i => employees.TryGetValue(i.EmployeeId, out var emp) && emp.ContractType is ContractType.UmowaZlecenie or ContractType.UmowaODzielo
                                                  && emp.ContractTotalAmount is null).Select(i => i.EmployeeName).ToList();
        if (missingAmount.Count > 0)
            throw new DomainException("Uzupełnij kwotę należności określoną w umowie (Osoby → „kwota z umowy”) i przelicz pozycje: " + string.Join(", ", missingAmount) +
                                      " — od niej zależy ryczałt od umów do 200 zł (art. 30 ust. 1 pkt 5a ustawy o PIT).");
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
        foreach (var item in await _db.OpenItems.Where(o => o.JournalEntryId == entry.Id).ToListAsync(ct)) item.DueDate = list.PaymentDate;
        foreach (var item in _db.OpenItems.Local.Where(o => o.JournalEntryId == entry.Id)) item.DueDate = list.PaymentDate;
        await new AuditService(_db, _clock).AddAsync(_user, "LISTA_PLAC_ZAKSIEGOWANA", "PayrollList", list.Id.ToString(), $"{list.Number}; zapis {entry.JournalNumber}", ct);
        await _db.SaveChangesAsync(ct);
        return list;
    }
}
