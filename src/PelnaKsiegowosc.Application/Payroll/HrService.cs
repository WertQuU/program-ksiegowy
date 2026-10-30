using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Payroll;

/// <summary>Stan urlopu wypoczynkowego w roku: wymiar (proporcjonalny w roku podjęcia/ustania pracy), zaległy, wykorzystany, pozostały.</summary>
public sealed record LeaveBalance(Guid EmployeeId, string Employee, int Year, decimal Entitlement, decimal CarriedOver, decimal Used, decimal OnDemandUsed)
{
    public decimal Remaining => Entitlement + CarriedOver - Used;
}

/// <summary>Termin kadrowy do pilnowania (badania, BHP, koniec umowy).</summary>
public sealed record HrReminder(Guid EmployeeId, string Employee, string What, DateOnly Date);

/// <summary>
/// Kadry: ewidencja nieobecności (urlopy, choroby, urlop bezpłatny), stan urlopów (art. 154–155¹ KP — wymiar proporcjonalny
/// w roku zatrudnienia, przeniesienie niewykorzystanego), terminy badań lekarskich, szkoleń BHP i końca umów.
/// </summary>
public sealed class HrService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public HrService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    /// <summary>Dni robocze (pon–pt bez świąt) w przedziale, włącznie.</summary>
    public static int WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !PolishHolidays.IsHoliday(d)) n++;
        return n;
    }

    public async Task<Absence> AddAbsenceAsync(Guid employeeId, AbsenceKind kind, DateOnly from, DateOnly to, string? notes = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (to < from) throw new DomainException("Data „do” jest wcześniejsza niż „od”.");
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == employeeId && x.CompanyId == _user.CompanyId, ct);
        if (await _db.Absences.AnyAsync(a => a.EmployeeId == employeeId && a.From <= to && a.To >= from, ct))
            throw new DomainException("Nieobecność nakłada się na już zarejestrowaną.");
        if (kind is AbsenceKind.UrlopWypoczynkowy or AbsenceKind.UrlopNaZadanie)
        {
            if (e.ContractType != ContractType.UmowaOPrace) throw new DomainException("Urlop wypoczynkowy przysługuje pracownikom (umowa o pracę).");
            var days = WorkingDays(from, to);
            var bal = await LeaveBalanceAsync(employeeId, from.Year, ct);
            if (days > bal.Remaining) throw new DomainException($"Urlop {days} dni przekracza pozostały wymiar ({bal.Remaining:0.##} dni).");
            if (kind == AbsenceKind.UrlopNaZadanie && bal.OnDemandUsed + days > 4) throw new DomainException("Urlop na żądanie: najwyżej 4 dni w roku (art. 167² KP).");
        }
        var a = new Absence { CompanyId = _user.CompanyId, EmployeeId = employeeId, Kind = kind, From = from, To = to, Notes = notes?.Trim(), CreatedAtUtc = _clock.UtcNow };
        _db.Absences.Add(a);
        await new AuditService(_db, _clock).AddAsync(_user, "NIEOBECNOSC", "Absence", a.Id.ToString(), $"{e.FullName}; {kind}; {from}–{to}", ct);
        await _db.SaveChangesAsync(ct);
        return a;
    }

    public async Task DeleteAbsenceAsync(Guid id, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var a = await _db.Absences.FirstAsync(x => x.Id == id && x.CompanyId == _user.CompanyId, ct);
        _db.Absences.Remove(a);
        await new AuditService(_db, _clock).AddAsync(_user, "NIEOBECNOSC_USUNIETA", "Absence", a.Id.ToString(), $"{a.Kind}; {a.From}–{a.To}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<Absence>> AbsencesAsync(int year, CancellationToken ct = default) =>
        _db.Absences.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.From.Year <= year && a.To.Year >= year).OrderBy(a => a.From).ToListAsync(ct);

    /// <summary>Wymiar w roku: pełny albo proporcjonalny do miesięcy zatrudnienia (art. 155¹ KP, zaokrąglenie w górę do pełnego dnia), z uwzględnieniem etatu.</summary>
    public static decimal Entitlement(Employee e, int year)
    {
        var start = e.StartDate.Year == year ? e.StartDate.Month : e.StartDate.Year < year ? 1 : 13;
        var end = e.EndDate is { } d && d.Year == year ? d.Month : e.EndDate is { } d2 && d2.Year < year ? 0 : 12;
        var months = Math.Max(0, end - start + 1);
        if (months == 0) return 0;
        var full = e.AnnualLeaveDays * e.WorkTimeFraction;
        return months == 12 ? Math.Ceiling(full) : Math.Ceiling(full * months / 12m);
    }

    public async Task<LeaveBalance> LeaveBalanceAsync(Guid employeeId, int year, CancellationToken ct = default)
    {
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == employeeId, ct);
        var leaves = await _db.Absences.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && (a.Kind == AbsenceKind.UrlopWypoczynkowy || a.Kind == AbsenceKind.UrlopNaZadanie)).ToListAsync(ct);
        decimal carried = e.LeaveCarriedOver;
        for (var y = e.StartDate.Year; y < year; y++)
            carried = Entitlement(e, y) + carried - leaves.Where(a => a.From.Year == y).Sum(a => WorkingDays(a.From, a.To));
        var thisYear = leaves.Where(a => a.From.Year == year).ToList();
        return new LeaveBalance(e.Id, e.FullName, year, Entitlement(e, year), Math.Max(0, carried),
            thisYear.Sum(a => WorkingDays(a.From, a.To)), thisYear.Where(a => a.Kind == AbsenceKind.UrlopNaZadanie).Sum(a => WorkingDays(a.From, a.To)));
    }

    public async Task<List<LeaveBalance>> LeaveBalancesAsync(int year, CancellationToken ct = default)
    {
        var ids = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId && e.ContractType == ContractType.UmowaOPrace && e.StartDate.Year <= year)
            .OrderBy(e => e.LastName).Select(e => e.Id).ToListAsync(ct);
        var list = new List<LeaveBalance>();
        foreach (var id in ids) list.Add(await LeaveBalanceAsync(id, year, ct));
        return list;
    }

    /// <summary>Dni choroby (kalendarzowe) i urlopu bezpłatnego / nieobecności nieusprawiedliwionej (robocze) w miesiącu — do listy płac.</summary>
    public async Task<(int SickDays, int UnpaidWorkingDays)> MonthAbsencesAsync(Guid employeeId, DateOnly month, CancellationToken ct = default)
    {
        var m0 = new DateOnly(month.Year, month.Month, 1);
        var m1 = m0.AddMonths(1).AddDays(-1);
        var list = await _db.Absences.AsNoTracking().Where(a => a.EmployeeId == employeeId && a.From <= m1 && a.To >= m0).ToListAsync(ct);
        int sick = 0, unpaid = 0;
        foreach (var a in list)
        {
            var f = a.From < m0 ? m0 : a.From;
            var t = a.To > m1 ? m1 : a.To;
            if (a.Kind == AbsenceKind.Choroba) sick += t.DayNumber - f.DayNumber + 1;
            if (a.Kind is AbsenceKind.UrlopBezplatny or AbsenceKind.NieobecnoscNieusprawiedliwiona) unpaid += WorkingDays(f, t);
        }
        return (sick, unpaid);
    }

    /// <summary>Terminy kadrowe w horyzoncie (domyślnie 45 dni) oraz przeterminowane.</summary>
    public async Task<List<HrReminder>> RemindersAsync(int horizonDays = 45, CancellationToken ct = default)
    {
        var limit = _clock.Today.AddDays(horizonDays);
        var employees = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId && e.IsActive).ToListAsync(ct);
        var r = new List<HrReminder>();
        foreach (var e in employees)
        {
            if (e.ContractType == ContractType.UmowaOPrace)
            {
                if (e.MedicalExamUntil is null) r.Add(new HrReminder(e.Id, e.FullName, "brak daty ważności badań lekarskich", _clock.Today));
                else if (e.MedicalExamUntil <= limit) r.Add(new HrReminder(e.Id, e.FullName, "kończą się badania lekarskie okresowe", e.MedicalExamUntil.Value));
                if (e.BhpTrainingUntil is { } b && b <= limit) r.Add(new HrReminder(e.Id, e.FullName, "kończy się szkolenie okresowe BHP", b));
            }
            if (e.EndDate is { } end && end >= _clock.Today && end <= limit) r.Add(new HrReminder(e.Id, e.FullName, "kończy się umowa", end));
        }
        return r.OrderBy(x => x.Date).ToList();
    }
}
