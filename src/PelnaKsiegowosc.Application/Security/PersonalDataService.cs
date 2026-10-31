using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Security;

public sealed record RetentionDecision(bool CanErase, DateOnly KeepUntil, string Basis);

/// <summary>
/// Żądania usunięcia danych osobowych (RODO art. 17). Prawo do usunięcia nie ma zastosowania, gdy przetwarzanie jest
/// niezbędne do wypełnienia obowiązku prawnego (art. 17 ust. 3 lit. b): dowody księgowe przechowuje się 5 lat od początku
/// roku następującego po roku obrotowym (art. 74 ust. 2 UoR), dokumentację podatkową — do upływu przedawnienia
/// zobowiązania (art. 86 § 1 i art. 70 § 1 Ordynacji podatkowej: 5 lat od końca roku, w którym upłynął termin płatności
/// podatku — za rok Y zwykle do końca roku Y+6), dokumentację pracowniczą i płacową — 10 lat od końca roku, w którym
/// ustał stosunek pracy (art. 94 pkt 9b i art. 51u Kodeksu pracy). Po upływie tych okresów dane osoby są anonimizowane
/// w kartotece i na dokumentach; zapisy księgowe (kwoty, daty, numery) pozostają, a każde żądanie trafia do dziennika zdarzeń.
/// </summary>
public sealed class PersonalDataService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    /// <summary>Ścieżki plików załączników objętych usunięciem (usuwa je warstwa magazynu plików po zapisie zmian).</summary>
    public List<string> PendingAttachmentRemovals { get; } = new();

    public PersonalDataService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public static string HoldKey(Guid subjectId) => $"rodo.wstrzymanie.{subjectId}";
    public sealed record LegalHold(DateOnly Until, string Reason);

    /// <summary>
    /// Wstrzymanie usuwania danych (np. zawieszenie lub przerwanie biegu przedawnienia — art. 70 § 2–6 Ordynacji, kontrola
    /// podatkowa, postępowanie sądowe, szczególny obowiązek archiwizacyjny). Do wskazanej daty żądania usunięcia są odrzucane.
    /// </summary>
    public async Task SetLegalHoldAsync(Guid subjectId, DateOnly until, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCompany);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj podstawę wstrzymania (np. kontrola podatkowa, spór sądowy, zawieszenie biegu przedawnienia).");
        await new Setup.SettingsService(_db, _clock).SetAsync<LegalHold?>(_user.CompanyId, HoldKey(subjectId), new LegalHold(until, reason.Trim()), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "RODO_WSTRZYMANIE_USUNIECIA", "Subject", subjectId.ToString(), $"do {until.ToPl()}: {reason.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<RetentionDecision> WithHoldAsync(Guid subjectId, RetentionDecision d, CancellationToken ct)
    {
        var hold = await new Setup.SettingsService(_db, _clock).GetAsync<LegalHold?>(_user.CompanyId, HoldKey(subjectId), () => null, ct);
        if (hold is null || hold.Until < _clock.Today) return d;
        var until = hold.Until > d.KeepUntil || d.KeepUntil == DateOnly.MaxValue ? hold.Until : d.KeepUntil;
        return new(false, d.KeepUntil == DateOnly.MaxValue ? DateOnly.MaxValue : until, d.Basis + $" Wstrzymanie usuwania do {hold.Until.ToPl()}: {hold.Reason}.");
    }

    public async Task<RetentionDecision> CounterpartyRetentionAsync(Guid counterpartyId, CancellationToken ct = default) =>
        await WithHoldAsync(counterpartyId, await BaseCounterpartyRetentionAsync(counterpartyId, ct), ct);

    private async Task<RetentionDecision> BaseCounterpartyRetentionAsync(Guid counterpartyId, CancellationToken ct)
    {
        var docDates = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == _user.CompanyId && d.CounterpartyId == counterpartyId)
            .Select(d => d.IssueDate).ToListAsync(ct);
        var entryDates = await (from l in _db.JournalLines.AsNoTracking()
                                join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                                where l.CounterpartyId == counterpartyId && e.CompanyId == _user.CompanyId
                                select e.PostingDate).ToListAsync(ct);
        if (await _db.OpenItems.AnyAsync(o => o.CounterpartyId == counterpartyId && o.Status != OpenItemStatus.Rozliczona, ct))
            return new(false, DateOnly.MaxValue, "Kontrahent ma nierozliczone należności lub zobowiązania — dane są niezbędne do rozliczeń (RODO art. 6 ust. 1 lit. b i c).");
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.CounterpartyId == counterpartyId && e.CompanyId == _user.CompanyId, ct);
        if (employee is not null) return await BaseEmployeeRetentionAsync(employee.Id, ct);
        var last = docDates.Concat(entryDates).DefaultIfEmpty(DateOnly.MinValue).Max();
        if (last == DateOnly.MinValue) return new(true, _clock.Today, "Brak dokumentów i zapisów — dane można usunąć.");
        var keepUntil = new DateOnly(last.Year + 6, 12, 31);
        return new(_clock.Today > keepUntil, keepUntil,
            $"Ostatni dokument/zapis: {last.ToPl()}. Okres przechowywania: art. 74 ust. 2 UoR (5 lat) i art. 70 § 1 w zw. z art. 86 § 1 Ordynacji podatkowej (przedawnienie) — do {keepUntil.ToPl()}.");
    }

    public async Task<RetentionDecision> EmployeeRetentionAsync(Guid employeeId, CancellationToken ct = default) =>
        await WithHoldAsync(employeeId, await BaseEmployeeRetentionAsync(employeeId, ct), ct);

    private async Task<RetentionDecision> BaseEmployeeRetentionAsync(Guid employeeId, CancellationToken ct)
    {
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == employeeId && x.CompanyId == _user.CompanyId, ct);
        if (e.IsActive || e.EndDate is null)
            return new(false, DateOnly.MaxValue, "Zatrudnienie (umowa) trwa — dane są niezbędne do wykonania umowy i obowiązków pracodawcy.");
        var keepUntil = new DateOnly(e.EndDate.Value.Year + 10, 12, 31);
        return new(_clock.Today > keepUntil, keepUntil,
            $"Umowa zakończona {e.EndDate.Value.ToPl()}. Dokumentacja pracownicza i płacowa: 10 lat od końca roku ustania zatrudnienia (art. 94 pkt 9b, art. 51u KP) — do {keepUntil.ToPl()}.");
    }

    /// <summary>Realizacja żądania usunięcia danych kontrahenta (osoby fizycznej): anonimizacja po upływie okresów przechowywania albo odmowa z podstawą prawną.</summary>
    public async Task<RetentionDecision> EraseCounterpartyAsync(Guid counterpartyId, string request, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCompany);
        if (string.IsNullOrWhiteSpace(request)) throw new DomainException("Opisz żądanie (data wpływu, osoba, sposób przekazania).");
        var decision = await CounterpartyRetentionAsync(counterpartyId, ct);
        var cp = await _db.Counterparties.FirstAsync(c => c.Id == counterpartyId && c.CompanyId == _user.CompanyId, ct);
        if (!decision.CanErase)
        {
            await new AuditService(_db, _clock).AddAsync(_user, "RODO_USUNIECIE_ODMOWA", "Counterparty", cp.Id.ToString(), $"{request.Trim()} — {decision.Basis}", ct);
            await _db.SaveChangesAsync(ct);
            return decision;
        }
        var label = $"Osoba usunięta (RODO) {cp.Code}";
        cp.Name = label;
        cp.Nip = cp.Regon = cp.Pesel = cp.Street = cp.BuildingNumber = cp.ApartmentNumber = cp.PostalCode = cp.City = null;
        cp.Email = cp.Phone = cp.BankAccountIban = cp.Notes = cp.VatEuNumber = cp.WhiteListRequestId = null;
        cp.IsActive = false;
        // historia danych identyfikacyjnych (poprzednie NIP) — także usuwana
        _db.CounterpartyIdentityChanges.RemoveRange(await _db.CounterpartyIdentityChanges.Where(h => h.CounterpartyId == counterpartyId).ToListAsync(ct));
        var docs = await _db.Documents.Where(d => d.CounterpartyId == counterpartyId && d.CompanyId == _user.CompanyId).ToListAsync(ct);
        foreach (var d in docs)
        {
            d.CounterpartyName = label;
            d.CounterpartyNip = null;
            d.CounterpartyAddress = null;
        }
        // załączniki (skany dokumentów) po upływie okresu przechowywania — do usunięcia plików przez wywołującego (magazyn plików)
        var docIds = docs.Select(d => d.Id).ToList();
        var attachments = await _db.Attachments.Where(a => a.DocumentId != null && docIds.Contains(a.DocumentId.Value)).ToListAsync(ct);
        PendingAttachmentRemovals.AddRange(attachments.Select(a => a.StoragePath));
        _db.Attachments.RemoveRange(attachments);
        await new AuditService(_db, _clock).AddAsync(_user, "RODO_USUNIECIE_WYKONANE", "Counterparty", cp.Id.ToString(),
            $"{request.Trim()} — dane zanonimizowane po upływie okresu przechowywania ({decision.KeepUntil.ToPl()}); zapisy księgowe zachowane; załączników do usunięcia: {PendingAttachmentRemovals.Count}; kopie zapasowe — usunięcie wraz z ich rotacją", ct);
        await _db.SaveChangesAsync(ct);
        return decision;
    }

    public async Task<RetentionDecision> EraseEmployeeAsync(Guid employeeId, string request, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCompany);
        if (string.IsNullOrWhiteSpace(request)) throw new DomainException("Opisz żądanie (data wpływu, osoba, sposób przekazania).");
        var decision = await EmployeeRetentionAsync(employeeId, ct);
        var e = await _db.Employees.FirstAsync(x => x.Id == employeeId && x.CompanyId == _user.CompanyId, ct);
        if (!decision.CanErase)
        {
            await new AuditService(_db, _clock).AddAsync(_user, "RODO_USUNIECIE_ODMOWA", "Employee", e.Id.ToString(), $"{request.Trim()} — {decision.Basis}", ct);
            await _db.SaveChangesAsync(ct);
            return decision;
        }
        e.FirstName = "Osoba usunięta (RODO)";
        e.LastName = e.Id.ToString()[..8];
        e.Pesel = e.Address = e.Street = e.HouseNumber = e.FlatNumber = e.PostalCode = e.City = null;
        e.Voivodeship = e.County = e.Commune = e.BankAccountIban = e.TaxOfficeCode = null;
        e.BirthDate = null;
        e.MedicalExamUntil = e.BhpTrainingUntil = null;
        e.IsActive = false;
        await new AuditService(_db, _clock).AddAsync(_user, "RODO_USUNIECIE_WYKONANE", "Employee", e.Id.ToString(),
            $"{request.Trim()} — dane zanonimizowane po upływie okresu przechowywania ({decision.KeepUntil.ToPl()})", ct);
        await _db.SaveChangesAsync(ct);
        if (e.CounterpartyId != Guid.Empty && await _db.Counterparties.AnyAsync(c => c.Id == e.CounterpartyId, ct))
        {
            var cp = await _db.Counterparties.FirstAsync(c => c.Id == e.CounterpartyId, ct);
            cp.Name = $"Osoba usunięta (RODO) {cp.Code}";
            cp.Nip = cp.Pesel = cp.Street = cp.BuildingNumber = cp.ApartmentNumber = cp.PostalCode = cp.City = cp.Email = cp.Phone = cp.BankAccountIban = cp.Notes = null;
            cp.IsActive = false;
            await _db.SaveChangesAsync(ct);
        }
        return decision;
    }
}
