using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Payroll;

public enum WorkScheduleDayKind { Praca, Choroba, Wolne, NieodplatnaNieobecnosc }
public sealed record WorkScheduleDay(DateOnly Date, decimal ScheduledHours, decimal WorkedHours, WorkScheduleDayKind Kind);
public sealed record ConfirmedWorkSchedule(IReadOnlyList<WorkScheduleDay> Days, string EvidenceReference,
    string SourceFingerprint, DateTime ConfirmedAtUtc, Guid ConfirmedByUserId);

public sealed partial class PayrollService
{
    private static string WorkScheduleKey(Guid listId, Guid itemId) => $"place.grafik.{listId:N}.{itemId:N}";

    private async Task<string> WorkScheduleFingerprintAsync(PayrollList list, ItemInput input, Employee employee, IReadOnlyList<WorkScheduleDay> days, CancellationToken ct)
    {
        var end = list.Month.AddMonths(1).AddDays(-1);
        var absences = await _db.Absences.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId &&
            a.EmployeeId == employee.Id && a.From <= end && a.To >= list.Month)
            .OrderBy(a => a.Id).Select(a => new { a.Id, a.From, a.To, a.Kind }).ToListAsync(ct);
        var json = JsonSerializer.Serialize(new { _user.CompanyId, list.Id, list.Month, list.PaymentDate,
            input, employee.WorkTimeFraction, employee.StartDate, employee.EndDate, employee.ContractType, absences, days = days.OrderBy(d => d.Date).ToArray() }, FingerprintJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private async Task<PayrollWorkHours> ValidateWorkScheduleAsync(PayrollList list, ItemInput input, Employee employee,
        IReadOnlyList<WorkScheduleDay> days, CancellationToken ct)
    {
        if (employee.ContractType != ContractType.UmowaOPrace || input.SickDays != 30 ||
            DateTime.DaysInMonth(list.Month.Year, list.Month.Month) != 31)
            throw new DomainException("Ewidencja 30/31 dotyczy umowy o pracę i dokładnie 30 dni choroby w miesiącu 31-dniowym.");
        var end = list.Month.AddMonths(1).AddDays(-1);
        if (employee.StartDate > list.Month || employee.EndDate < end)
            throw new DomainException("Przypadek zatrudnienia tylko przez część miesiąca wymaga odrębnego rozliczenia.");
        if (days.Count != 31 || days.Select(d => d.Date).Distinct().Count() != 31 ||
            days.Any(d => d.Date < list.Month || d.Date > end || !Enum.IsDefined(d.Kind) ||
                d.ScheduledHours < 0 || d.ScheduledHours > 24 || d.WorkedHours < 0 || d.WorkedHours > d.ScheduledHours ||
                (d.Kind != WorkScheduleDayKind.Praca && d.WorkedHours != 0) ||
                (d.Kind == WorkScheduleDayKind.Wolne && d.ScheduledHours != 0) ||
                (d.Kind == WorkScheduleDayKind.Praca && (d.ScheduledHours == 0 || d.WorkedHours == 0))))
            throw new DomainException("Grafik musi obejmować każdy dzień miesiąca dokładnie raz, prawidłowy rodzaj dnia i godziny 0–24. Pracę ponad grafik rozlicz odrębnie.");
        if (days.Sum(d => d.ScheduledHours) <= 0 || days.Count(d => d.Kind == WorkScheduleDayKind.Choroba) != 30)
            throw new DomainException("Grafik wymaga dodatniego wymiaru godzin i dokładnie 30 dni choroby.");
        var absences = await _db.Absences.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId &&
            a.EmployeeId == employee.Id && a.From <= end && a.To >= list.Month).ToListAsync(ct);
        foreach (var day in days)
        {
            var covering = absences.Where(a => a.From <= day.Date && a.To >= day.Date).ToArray();
            if (covering.Length > 1 || (day.Kind == WorkScheduleDayKind.Choroba &&
                (covering.Length != 1 || covering[0].Kind != AbsenceKind.Choroba)) ||
                (day.Kind != WorkScheduleDayKind.Choroba && covering.Any(a => a.Kind == AbsenceKind.Choroba)))
                throw new DomainException("Daty grafiku nie zgadzają się z udokumentowanymi okresami choroby w ewidencji nieobecności.");
            if (day.Kind == WorkScheduleDayKind.NieodplatnaNieobecnosc &&
                (covering.Length != 1 || covering[0].Kind is not (AbsenceKind.UrlopBezplatny or AbsenceKind.NieobecnoscNieusprawiedliwiona)))
                throw new DomainException("Nieodpłatna nieobecność wymaga zgodnego zapisu w ewidencji nieobecności.");
            if (day.Kind is WorkScheduleDayKind.Praca or WorkScheduleDayKind.Wolne && covering.Length != 0)
                throw new DomainException("Pozostały dzień ma inną nieobecność. Najpierw uzgodnij jej rodzaj; płatny urlop wymaga odrębnego rozliczenia.");
        }
        var unpaid = days.Count(d => d.Kind == WorkScheduleDayKind.NieodplatnaNieobecnosc && d.ScheduledHours > 0);
        if (input.UnpaidDays != unpaid)
            throw new DomainException("Liczba nieodpłatnych dni na liście nie zgadza się z grafikiem.");
        return new PayrollWorkHours(days.Sum(d => d.ScheduledHours), days.Sum(d => d.WorkedHours));
    }

    public async Task ConfirmWorkScheduleAsync(Guid listId, Guid itemId, IReadOnlyList<WorkScheduleDay> days,
        string evidenceReference, CancellationToken ct = default)
    {
        _user.Require(Permission.ManagePayrollData);
        _user.Require(Permission.ApproveDocuments);
        if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Trim().Length > 120)
            throw new DomainException("Podaj odnośnik do zatwierdzonego grafiku i ewidencji czasu pracy (1–120 znaków).");
        var list = await _db.PayrollLists.Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        if (list.Status != PayrollStatus.Robocza) throw new DomainException("Zaksięgowany grafik wymaga storna listy i nowej listy płac.");
        var input = (await StoredInputsAsync(list, ct)).Single(i => i.ItemId == itemId);
        var employee = await _db.Employees.AsNoTracking().SingleAsync(e => e.Id == input.EmployeeId && e.CompanyId == _user.CompanyId, ct);
        var hours = await ValidateWorkScheduleAsync(list, input, employee, days, ct);
        var value = new ConfirmedWorkSchedule(days.OrderBy(d => d.Date).ToArray(), evidenceReference.Trim(),
            await WorkScheduleFingerprintAsync(list, input, employee, days, ct), _clock.UtcNow, _user.UserId);
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, WorkScheduleKey(listId, itemId), value, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "GRAFIK_30_31_POTWIERDZONY", "PayrollItem", itemId.ToString(),
            $"{list.Number}; grafik {hours.Scheduled} h, praca {hours.Worked} h; dowód {value.EvidenceReference}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<PayrollWorkHours?> VerifiedWorkScheduleAsync(PayrollList list, ItemInput input, Employee employee, CancellationToken ct)
    {
        var value = await new SettingsService(_db, _clock).GetAsync<ConfirmedWorkSchedule?>(_user.CompanyId,
            WorkScheduleKey(list.Id, input.ItemId), () => null, ct);
        if (value is null || value.Days is null || string.IsNullOrWhiteSpace(value.EvidenceReference) ||
            value.SourceFingerprint != await WorkScheduleFingerprintAsync(list, input, employee, value.Days, ct)) return null;
        try { return await ValidateWorkScheduleAsync(list, input, employee, value.Days, ct); }
        catch (DomainException) { return null; }
    }
}
