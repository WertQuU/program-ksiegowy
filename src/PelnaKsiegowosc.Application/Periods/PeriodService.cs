using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Application.Periods;

public enum CheckSeverity
{
    /// <summary>Uniemożliwia zamknięcie.</summary>
    Blokujace,
    /// <summary>Zamknięcie możliwe po świadomym potwierdzeniu z uzasadnieniem (zapisywanym w audycie).</summary>
    WymagaPotwierdzenia,
    Informacja
}

public sealed record CheckItem(string Code, string Title, bool Passed, CheckSeverity Severity, string Details);

public sealed class CloseChecklist
{
    public List<CheckItem> Items { get; } = new();
    public bool HasBlocking => Items.Any(i => !i.Passed && i.Severity == CheckSeverity.Blokujace);
    public bool NeedsAcknowledgement => Items.Any(i => !i.Passed && i.Severity == CheckSeverity.WymagaPotwierdzenia);
    public string ToJson() => JsonSerializer.Serialize(Items);
}

public sealed record YearClosePreview(CloseChecklist Checklist, decimal NetResult, IReadOnlyList<LineInput> ClosingLines);

/// <summary>Kontrola okresów: zamknięcie z listą kontrolną, kontrolowane ponowne otwarcie, zamknięcie roku i bilans otwarcia.</summary>
public sealed class PeriodService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public PeriodService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    public async Task<CloseChecklist> BuildChecklistAsync(Guid periodId, CancellationToken ct = default)
    {
        var period = await _db.Periods.FirstAsync(p => p.Id == periodId && p.CompanyId == _user.CompanyId, ct);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == period.FiscalYearId, ct);
        var c = new CloseChecklist();
        var cid = period.CompanyId;

        var earlierOpen = fy.Periods.Where(p => p.Number < period.Number && p.Status == PeriodStatus.Otwarty).Select(p => p.Name).ToList();
        c.Items.Add(new("POPRZEDNIE", "Poprzednie okresy zamknięte", earlierOpen.Count == 0, CheckSeverity.Blokujace,
            earlierOpen.Count == 0 ? "OK" : "Otwarte okresy: " + string.Join(", ", earlierOpen)));

        var drafts = await _db.JournalEntries.CountAsync(e => e.PeriodId == periodId && e.Status == EntryStatus.Bufor, ct);
        c.Items.Add(new("BUFOR", "Brak zapisów w buforze", drafts == 0, CheckSeverity.Blokujace,
            drafts == 0 ? "OK" : $"{drafts} zapis(ów) w buforze — zaksięguj lub usuń."));

        var pendingDocs = await _db.Documents.CountAsync(d => d.CompanyId == cid && d.OperationDate >= period.StartDate && d.OperationDate <= period.EndDate &&
                                                               (d.Status == DocumentStatus.Zatwierdzony || d.Status == DocumentStatus.Roboczy), ct);
        c.Items.Add(new("DOKUMENTY", "Dokumenty okresu zaksięgowane", pendingDocs == 0, CheckSeverity.WymagaPotwierdzenia,
            pendingDocs == 0 ? "OK" : $"{pendingDocs} dokument(ów) z datą operacji w okresie nie zostało zaksięgowanych."));

        var integrity = new IntegrityService(_db, _clock);
        var problems = await integrity.CheckLedgerChainAsync(fy.Id, ct);
        c.Items.Add(new("INTEGRALNOSC", "Integralność dziennika (numeracja, skróty, bilansowanie)", problems.Count == 0, CheckSeverity.Blokujace,
            problems.Count == 0 ? "OK" : string.Join("; ", problems.Take(5))));

        // amortyzacja
        var assetsToDepreciate = await _db.FixedAssets.AsNoTracking().Include(a => a.Operations)
            .Where(a => a.CompanyId == cid && a.Status == FixedAssetStatus.WEwidencji).ToListAsync(ct);
        var due = assetsToDepreciate.Count(a => DepreciationCalculator.AmountFor(a, DepreciationBasis.Bilansowa, period.StartDate.Year, period.StartDate.Month) > 0);
        var run = await _db.DepreciationRuns.AnyAsync(r => r.CompanyId == cid && r.Year == period.StartDate.Year && r.Month == period.StartDate.Month && !r.IsReversed, ct);
        c.Items.Add(new("AMORTYZACJA", "Amortyzacja naliczona", due == 0 || run, CheckSeverity.WymagaPotwierdzenia,
            due == 0 ? "Brak składników do amortyzacji w okresie." : run ? "OK" : $"{due} składnik(ów) wymaga naliczenia amortyzacji za {period.Name}."));

        // bank
        var unmatched = await _db.MoneyTransactions.CountAsync(t => t.CompanyId == cid && t.BookingDate >= period.StartDate && t.BookingDate <= period.EndDate &&
                                                                    (t.Status == TransactionStatus.Nierozliczona || t.Status == TransactionStatus.Dopasowana), ct);
        c.Items.Add(new("BANK", "Operacje bankowe/kasowe zaksięgowane", unmatched == 0, CheckSeverity.WymagaPotwierdzenia,
            unmatched == 0 ? "OK" : $"{unmatched} operacji nie zaksięgowano."));

        var bankRecon = await new Banking.BankReconciliation(_db).ReconcileAsync(cid, fy.Id, period.EndDate, ct);
        var bankIssues = bankRecon.Where(r => r.Difference is { } d && d != 0).ToList();
        c.Items.Add(new("UZGODNIENIE_BANK", "Uzgodnienie sald bankowych z księgami", bankIssues.Count == 0, CheckSeverity.WymagaPotwierdzenia,
            bankIssues.Count == 0 ? (bankRecon.Count == 0 ? "Brak rachunków do uzgodnienia." : "OK") :
            string.Join("; ", bankIssues.Select(b => $"{b.AccountName}: wyciąg {Money.Format(b.StatementBalance ?? 0)} vs księgi {Money.Format(b.LedgerBalance)}"))));

        // VAT
        var vat = await VatLedgerDifferenceAsync(fy.Id, period, ct);
        c.Items.Add(new("UZGODNIENIE_VAT", "Zgodność rejestrów VAT z kontami VAT", vat.Count == 0, CheckSeverity.WymagaPotwierdzenia,
            vat.Count == 0 ? "OK" : string.Join("; ", vat)));

        // rozrachunki vs księgi
        var oi = await integrity.CheckOpenItemsAgainstLedgerAsync(fy.Id, period.EndDate, ct);
        c.Items.Add(new("ROZRACHUNKI", "Zgodność rozrachunków z saldami kont", oi.Count == 0, CheckSeverity.Blokujace,
            oi.Count == 0 ? "OK" : string.Join("; ", oi.Take(5))));

        // kasa ujemna
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fy.Id, period.StartDate, period.EndDate, ct);
        var posting = await new SettingsService(_db, _clock).GetAsync(cid, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var cash = tb.FirstOrDefault(t => t.Account.Number == posting.Cash);
        c.Items.Add(new("KASA", "Saldo kasy nieujemne", cash is null || cash.Balance >= 0, CheckSeverity.Blokujace,
            cash is null ? "Brak konta kasy." : $"Saldo kasy: {Money.Format(cash.Balance)}"));

        // CIT
        var citTaxpayer = await _db.Companies.Where(x => x.Id == fy.CompanyId).Select(x => x.LegalForm).FirstAsync(ct) is var lf && LegalForms.IsCitTaxpayer(lf);
        if (citTaxpayer && fy.Periods.Count > 0)
        {
            var advance = await _db.CitAdvances.AnyAsync(a => a.FiscalYearId == fy.Id && a.PeriodNumber == period.Number, ct);
            c.Items.Add(new("CIT", "Zaliczka CIT wyliczona", advance, CheckSeverity.Informacja,
                advance ? "OK" : "Nie zapisano wyliczenia zaliczki CIT za okres (CIT → Zaliczki)."));
        }
        return c;
    }

    private async Task<List<string>> VatLedgerDifferenceAsync(Guid fyId, AccountingPeriod period, CancellationToken ct)
    {
        var cid = period.CompanyId;
        var posting = await new SettingsService(_db, _clock).GetAsync(cid, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var vatPeriod = new DateOnly(period.StartDate.Year, period.StartDate.Month, 1);
        var regSales = (await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == cid && v.Register == VatRegisterType.Sprzedaz && v.Period == vatPeriod)
            .Select(v => v.VatPln).ToListAsync(ct)).Sum();
        var regPurch = (await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == cid && v.Register == VatRegisterType.Zakup && v.Period == vatPeriod)
            .Select(v => v.DeductibleVatPln).ToListAsync(ct)).Sum();

        var docIds = await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == cid && v.Period == vatPeriod).Select(v => v.DocumentId).Distinct().ToListAsync(ct);
        var lines = await (from l in _db.JournalLines.AsNoTracking()
                           join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                           where e.Status == EntryStatus.Zaksiegowany && e.SourceDocumentId != null && docIds.Contains(e.SourceDocumentId.Value)
                                 && (l.AccountNumber == posting.VatOutput || l.AccountNumber == posting.VatInput)
                           select new { l.AccountNumber, l.Debit, l.Credit }).ToListAsync(ct);
        var ledgerOut = lines.Where(x => x.AccountNumber == posting.VatOutput).Sum(x => x.Credit - x.Debit);
        var ledgerIn = lines.Where(x => x.AccountNumber == posting.VatInput).Sum(x => x.Debit - x.Credit);
        var r = new List<string>();
        if (regSales != ledgerOut) r.Add($"VAT należny: rejestr {Money.Format(regSales)} ≠ konto {posting.VatOutput} {Money.Format(ledgerOut)}");
        if (regPurch != ledgerIn) r.Add($"VAT naliczony: rejestr {Money.Format(regPurch)} ≠ konto {posting.VatInput} {Money.Format(ledgerIn)}");
        return r;
    }

    public async Task<CloseChecklist> ClosePeriodAsync(Guid periodId, string? acknowledgementReason, CancellationToken ct = default)
    {
        _user.Require(Permission.ClosePeriods);
        var period = await _db.Periods.FirstAsync(p => p.Id == periodId && p.CompanyId == _user.CompanyId, ct);
        if (period.Status == PeriodStatus.Zamkniety) throw new DomainException($"Okres {period.Name} jest już zamknięty.");
        var checklist = await BuildChecklistAsync(periodId, ct);
        if (checklist.HasBlocking)
            throw new DomainException("Nie można zamknąć okresu — niespełnione warunki:" + Environment.NewLine +
                string.Join(Environment.NewLine, checklist.Items.Where(i => !i.Passed && i.Severity == CheckSeverity.Blokujace).Select(i => $"• {i.Title}: {i.Details}")));
        if (checklist.NeedsAcknowledgement && string.IsNullOrWhiteSpace(acknowledgementReason))
            throw new DomainException("Zamknięcie wymaga potwierdzenia ostrzeżeń z uzasadnieniem:" + Environment.NewLine +
                string.Join(Environment.NewLine, checklist.Items.Where(i => !i.Passed && i.Severity == CheckSeverity.WymagaPotwierdzenia).Select(i => $"• {i.Title}: {i.Details}")));
        period.Status = PeriodStatus.Zamkniety;
        period.ClosedAtUtc = _clock.UtcNow;
        period.ClosedByUserId = _user.UserId;
        _db.PeriodStatusChanges.Add(new PeriodStatusChange
        {
            CompanyId = period.CompanyId,
            PeriodId = period.Id,
            FromStatus = PeriodStatus.Otwarty,
            ToStatus = PeriodStatus.Zamkniety,
            UserId = _user.UserId,
            AtUtc = _clock.UtcNow,
            Reason = acknowledgementReason ?? "",
            ChecklistJson = checklist.ToJson()
        });
        await _audit.AddAsync(_user, "OKRES_ZAMKNIETY", "AccountingPeriod", period.Id.ToString(), $"{period.Name}; potwierdzenie: {acknowledgementReason}", ct);
        await _db.SaveChangesAsync(ct);
        return checklist;
    }

    /// <summary>Kontrolowane ponowne otwarcie — tylko ostatni zamknięty okres otwartego roku, z uzasadnieniem i uprawnieniem.</summary>
    public async Task ReopenPeriodAsync(Guid periodId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ReopenPeriods);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10) throw new DomainException("Podaj uzasadnienie ponownego otwarcia (min. 10 znaków).");
        var period = await _db.Periods.FirstAsync(p => p.Id == periodId && p.CompanyId == _user.CompanyId, ct);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == period.FiscalYearId, ct);
        if (fy.Status != FiscalYearStatus.Otwarty) throw new DomainException("Rok obrotowy jest zamknięty — ponowne otwarcie okresu jest niedozwolone (art. 12 UoR: po zamknięciu ksiąg nie dokonuje się w nich zapisów).");
        if (period.Status != PeriodStatus.Zamkniety) throw new DomainException("Okres nie jest zamknięty.");
        if (fy.Periods.Any(p => p.Number > period.Number && p.Status == PeriodStatus.Zamkniety))
            throw new DomainException("Najpierw otwórz późniejsze zamknięte okresy (otwieranie w odwrotnej kolejności).");
        period.Status = PeriodStatus.Otwarty;
        period.ReopenCount++;
        _db.PeriodStatusChanges.Add(new PeriodStatusChange
        {
            CompanyId = period.CompanyId,
            PeriodId = period.Id,
            FromStatus = PeriodStatus.Zamkniety,
            ToStatus = PeriodStatus.Otwarty,
            UserId = _user.UserId,
            AtUtc = _clock.UtcNow,
            Reason = reason.Trim()
        });
        await _audit.AddAsync(_user, "OKRES_OTWARTY_PONOWNIE", "AccountingPeriod", period.Id.ToString(), $"{period.Name}; uzasadnienie: {reason}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Podgląd zamknięcia roku: lista kontrolna, wynik finansowy i zapisy przeksięgowania kont wynikowych na 860.</summary>
    public async Task<YearClosePreview> PreviewYearCloseAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        var c = new CloseChecklist();
        var open = fy.Periods.Where(p => p.Status == PeriodStatus.Otwarty).Select(p => p.Name).ToList();
        c.Items.Add(new("OKRESY", "Wszystkie okresy zamknięte", open.Count == 0, CheckSeverity.Blokujace,
            open.Count == 0 ? "OK" : "Otwarte okresy: " + string.Join(", ", open)));
        c.Items.Add(new("STATUS", "Rok otwarty", fy.Status == FiscalYearStatus.Otwarty, CheckSeverity.Blokujace, fy.Status.ToString()));
        var drafts = await _db.JournalEntries.CountAsync(e => e.FiscalYearId == fy.Id && e.Status == EntryStatus.Bufor, ct);
        c.Items.Add(new("BUFOR", "Brak zapisów w buforze w roku", drafts == 0, CheckSeverity.Blokujace, drafts == 0 ? "OK" : $"{drafts} zapis(ów)"));
        var integrity = new IntegrityService(_db, _clock);
        var problems = await integrity.CheckLedgerChainAsync(fy.Id, ct);
        c.Items.Add(new("INTEGRALNOSC", "Integralność dziennika", problems.Count == 0, CheckSeverity.Blokujace, problems.Count == 0 ? "OK" : string.Join("; ", problems.Take(5))));
        var oi = await integrity.CheckOpenItemsAgainstLedgerAsync(fy.Id, fy.EndDate, ct);
        c.Items.Add(new("ROZRACHUNKI", "Rozrachunki zgodne z saldami", oi.Count == 0, CheckSeverity.Blokujace, oi.Count == 0 ? "OK" : string.Join("; ", oi.Take(5))));

        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fy.Id, fy.StartDate, fy.EndDate, ct);
        var posting = await new SettingsService(_db, _clock).GetAsync(fy.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var resultAccount = tb.FirstOrDefault(t => t.Account.Number == posting.FinancialResult);
        c.Items.Add(new("KONTO_860", "Konto wyniku finansowego istnieje", resultAccount is not null, CheckSeverity.Blokujace, posting.FinancialResult));
        var lines = new List<LineInput>();
        decimal resultCredit = 0;
        foreach (var t in tb.Where(t => t.IsLeaf && t.Account.IsResultAccount && t.Balance != 0))
        {
            // zamknięcie konta: saldo przeciwnie
            if (t.Balance > 0) lines.Add(new LineInput(t.Account.Number, 0, t.Balance, Description: "Przeksięgowanie na wynik finansowy"));
            else lines.Add(new LineInput(t.Account.Number, -t.Balance, 0, Description: "Przeksięgowanie na wynik finansowy"));
            resultCredit -= t.Balance;
        }
        if (lines.Count > 0)
        {
            if (resultCredit > 0) lines.Add(new LineInput(posting.FinancialResult, 0, resultCredit, Description: "Wynik finansowy — zysk"));
            else if (resultCredit < 0) lines.Add(new LineInput(posting.FinancialResult, -resultCredit, 0, Description: "Wynik finansowy — strata"));
            else lines.Add(new LineInput(posting.FinancialResult, 0, 0, Description: "Wynik zerowy"));
        }
        lines.RemoveAll(l => l.Debit == 0 && l.Credit == 0);
        var taxAdvanceInfo = await _db.CitAdvances.AnyAsync(a => a.FiscalYearId == fy.Id, ct);
        var form = await _db.Companies.Where(x => x.Id == fy.CompanyId).Select(x => x.LegalForm).FirstAsync(ct);
        if (LegalForms.IsCitTaxpayer(form)) c.Items.Add(new("CIT", "Wyliczenia CIT za rok", taxAdvanceInfo, CheckSeverity.WymagaPotwierdzenia,
            taxAdvanceInfo ? "OK" : "Brak zapisanych wyliczeń CIT — sprawdź ujęcie podatku dochodowego (konto 870) przed zamknięciem."));
        // Standardowe ostateczne zamknięcie ksiąg kontynuującej działalność jednostki: po zatwierdzeniu SF
        // (art. 12 ust. 4 UoR). Samo niezachowanie terminu zatwierdzenia ani oświadczenie operatora
        // nie stanowi podstawy obejścia wymogu. Zdarzenia z art. 12 ust. 2 (np. likwidacja) wymagają
        // odrębnego procesu zamykania na właściwy dzień, nie tej ścieżki tworzącej następny rok.
        var settings = new SettingsService(_db, _clock);
        var approval = await settings.GetAsync<Reports.FinancialStatementApprovals.StatementApproval?>(fy.CompanyId, Reports.FinancialStatementApprovals.ApprovalKey(fy.Name), () => null, ct);
        var exception = await settings.GetAsync<FinalCloseException?>(fy.CompanyId, FinalCloseExceptionKey(fy.Name), () => null, ct);
        c.Items.Add(new("SF_ZATWIERDZONE", "Sprawozdanie finansowe zatwierdzone (ostateczne zamknięcie ksiąg — art. 12 ust. 4 UoR)", approval is not null, CheckSeverity.Blokujace,
            approval is { } a
                ? $"Zatwierdzone {a.ApprovedOn.ToPl()} ({a.Body}, {a.Reference}); termin ostatecznego zamknięcia: {a.ApprovedOn.AddDays(15).ToPl()}" + (_clock.Today > a.ApprovedOn.AddDays(15) ? " — PRZEKROCZONY" : "")
                : exception is { } x
                    ? $"UDOKUMENTOWANO ZDARZENIE SZCZEGÓLNE ({x.Basis}) — {x.DecidedOn.ToPl()} ({x.DecidedBy}), dokument: {x.Document}; {x.Reason}. To NIE zastępuje zatwierdzenia SF i NIE odblokowuje standardowego, nieodwracalnego zamknięcia roku. Dla likwidacji lub innego zdarzenia z art. 12 ust. 2 UoR należy zastosować odrębny tryb."
                    : "Brak zapisu zatwierdzenia sprawozdania. Sprawozdanie sporządza się na podstawie ksiąg na dzień bilansowy; ostateczne zamknięcie ksiąg jednostki kontynuującej działalność następuje po zatwierdzeniu SF (art. 12 ust. 4 UoR). Zarejestruj uchwałę w Raportach. Nie można obejść blokady przez samo niezatwierdzenie SF w terminie."));
        return new YearClosePreview(c, resultCredit, lines);
    }

    /// <summary>Dokumentacja okoliczności szczególnych — NIE odblokowuje standardowego zamknięcia ksiąg.</summary>
    public enum FinalCloseExceptionBasis
    {
        /// <summary>Zakończenie działalności, likwidacja, upadłość, połączenie / przekształcenie (zamknięcie ksiąg w związku z tymi zdarzeniami — art. 12 ust. 2 UoR).</summary>
        ZakonczenieDzialalnosci,
        /// <summary>Organ zatwierdzający nie zatwierdził sprawozdania w terminie ustawowym (art. 53 ust. 1 UoR) — dalsze postępowanie wymaga oceny prawnej.</summary>
        NiezatwierdzoneWTerminie
    }

    public sealed record FinalCloseException(string Reason, DateOnly DecidedOn, string DecidedBy, FinalCloseExceptionBasis Basis = FinalCloseExceptionBasis.NiezatwierdzoneWTerminie, string Document = "");
    public static string FinalCloseExceptionKey(string year) => $"rok.zamkniecie.bezZatwierdzenia.{year}";

    /// <summary>
    /// Rejestruje okoliczności szczególne (np. likwidacja, brak zatwierdzenia w terminie) do audytu.
    /// To wyłącznie dokumentacja; nie uprawnia do zamknięcia roku w standardowej ścieżce
    /// jednostki kontynuującej działalność i nie stanowi samodzielnej podstawy prawnej.
    /// </summary>
    public async Task RecordFinalCloseExceptionAsync(Guid fiscalYearId, FinalCloseExceptionBasis basis, string document, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        var fy = await _db.FiscalYears.FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        if (string.IsNullOrWhiteSpace(document))
            throw new DomainException("Wskaż dokument źródłowy (np. uchwała o rozwiązaniu i otwarciu likwidacji, postanowienie sądu, protokół zgromadzenia bez zatwierdzenia sprawozdania).");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 20)
            throw new DomainException("Opisz szczegółowo okoliczności i podstawę prawną (co najmniej 20 znaków). Program nie potwierdza dopuszczalności zamknięcia w trybie wyjątkowym — decyzja należy do kierownika jednostki.");
        await new SettingsService(_db, _clock).SetAsync<FinalCloseException?>(fy.CompanyId, FinalCloseExceptionKey(fy.Name),
            new FinalCloseException(reason.Trim(), _clock.Today, _user.Login, basis, document.Trim()), ct);
        await _audit.AddAsync(_user, "ROK_ZAMKNIECIE_TRYB_WYJATKOWY", "FiscalYear", fy.Id.ToString(), $"{basis}; dokument: {document.Trim()}; {reason.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Zamknięcie roku obrotowego (operacja nieodwracalna): przeksięgowanie kont wynikowych na 860, zamknięcie ksiąg,
    /// utworzenie kolejnego roku z planem kont i bilansem otwarcia (saldo 860 przenoszone na 820). Wykonywane w transakcji.
    /// </summary>
    public async Task<FiscalYear> CloseYearAsync(Guid fiscalYearId, string confirmation, string? acknowledgementReason, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        // Ta ścieżka zawsze kontynuuje działalność i otwiera kolejny rok.
        // Brak zatwierdzenia SF nie daje prawa do ostatecznego zamknięcia art. 12 ust. 4 UoR.
        var expected = $"ZAMKNIJ {fy.Name}";
        if (confirmation != expected) throw new DomainException($"Aby potwierdzić nieodwracalne zamknięcie roku, wpisz: {expected}");
        var preview = await PreviewYearCloseAsync(fiscalYearId, ct);
        if (preview.Checklist.HasBlocking)
            throw new DomainException("Nie można zamknąć roku:" + Environment.NewLine +
                string.Join(Environment.NewLine, preview.Checklist.Items.Where(i => !i.Passed && i.Severity == CheckSeverity.Blokujace).Select(i => $"• {i.Title}: {i.Details}")));
        if (preview.Checklist.NeedsAcknowledgement && string.IsNullOrWhiteSpace(acknowledgementReason))
            throw new DomainException("Zamknięcie roku wymaga potwierdzenia ostrzeżeń z uzasadnieniem.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var ledger = new LedgerService(_db, _user, _clock) { SkipReadinessCheck = true };
        fy.Status = FiscalYearStatus.WZamknieciu;
        await _db.SaveChangesAsync(ct);
        if (preview.ClosingLines.Count > 0)
        {
            await ledger.CreateAndPostAsync(new EntryInput("ZAM", fy.EndDate, fy.EndDate, fy.EndDate, $"Zamknięcie roku obrotowego {fy.Name} — przeksięgowanie kont wynikowych",
                "PK", $"PK/ZAM/{fy.Name}", preview.ClosingLines, Kind: EntryKind.Zamkniecie), ct);
            await _db.SaveChangesAsync(ct);
        }
        fy.Status = FiscalYearStatus.Zamkniety;
        fy.ClosedAtUtc = _clock.UtcNow;
        fy.ClosedByUserId = _user.UserId;
        await _audit.AddAsync(_user, "ROK_ZAMKNIETY", "FiscalYear", fy.Id.ToString(), $"{fy.Name}; wynik: {Money.Format(preview.NetResult)}; potwierdzenie: {acknowledgementReason}", ct);
        await _db.SaveChangesAsync(ct);

        var next = await EnsureNextYearAsync(fy, ct);
        await PostOpeningBalanceAsync(fy, next, ct);
        await tx.CommitAsync(ct);
        return next;
    }

    private async Task<FiscalYear> EnsureNextYearAsync(FiscalYear fy, CancellationToken ct)
    {
        var start = fy.EndDate.AddDays(1);
        var next = await _db.FiscalYears.Include(f => f.Periods).FirstOrDefaultAsync(f => f.CompanyId == fy.CompanyId && f.StartDate == start, ct);
        if (next is null)
        {
            next = new FiscalYear
            {
                CompanyId = fy.CompanyId,
                StartDate = start,
                EndDate = start.AddMonths(12).AddDays(-1),
                Name = start.Month == 1 ? start.Year.ToString() : $"{start:MM.yyyy}–{start.AddMonths(12).AddDays(-1):MM.yyyy}",
                PreviousYearId = fy.Id
            };
            next.GenerateMonthlyPeriods();
            _db.FiscalYears.Add(next);
            await _db.SaveChangesAsync(ct);
        }
        if (!await _db.Charts.AnyAsync(c => c.FiscalYearId == next.Id, ct))
        {
            var src = await _db.Charts.Where(c => c.FiscalYearId == fy.Id).OrderByDescending(c => c.Version).FirstAsync(ct);
            var chart = await new ChartService(_db, _user, _clock).CopyChartToYearAsync(src.Id, next.Id, ct);
            chart.IsApproved = src.IsApproved;
            chart.ApprovedOn = src.ApprovedOn;
            await _db.SaveChangesAsync(ct);
        }
        return next;
    }

    /// <summary>
    /// Bilans otwarcia = bilans zamknięcia poprzedniego roku (zasada ciągłości, art. 5 ust. 1 UoR).
    /// Rozrachunki przenoszone są per kontrahent i per pozycja rozrachunkowa.
    /// </summary>
    private async Task PostOpeningBalanceAsync(FiscalYear closed, FiscalYear next, CancellationToken ct)
    {
        if (await _db.JournalEntries.AnyAsync(e => e.FiscalYearId == next.Id && e.Kind == EntryKind.BilansOtwarcia && e.Status == EntryStatus.Zaksiegowany, ct))
            throw new DomainException($"Rok {next.Name} ma już zaksięgowany bilans otwarcia.");
        var posting = await new SettingsService(_db, _clock).GetAsync(closed.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var queries = new LedgerQueries(_db);
        var tb = await queries.TrialBalanceAsync(closed.Id, closed.StartDate, closed.EndDate, ct);
        var itemBalances = await new IntegrityService(_db, _clock).OpenItemBalancesAsync(closed.Id, closed.EndDate, ct);
        var itemIds = itemBalances.ByItem.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToList();
        var openItems = await _db.OpenItems.Where(o => itemIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        var lines = new List<LineInput>();
        decimal resultBalance = 0;
        foreach (var t in tb.Where(t => t.IsLeaf && t.Balance != 0))
        {
            var a = t.Account;
            if (a.Type == AccountType.Pozabilansowe) continue;
            if (a.IsResultAccount) throw new DomainException($"Konto wynikowe {a.Number} ma saldo po zamknięciu roku — niespójność.");
            if (a.Number == posting.FinancialResult)
            {
                resultBalance += t.Balance;
                continue;
            }
            if (a.IsSettlementAccount)
            {
                foreach (var (itemId, bal) in itemBalances.ByItem.Where(kv => kv.Value != 0 && openItems.ContainsKey(kv.Key) && openItems[kv.Key].AccountNumber == a.Number))
                {
                    var item = openItems[itemId];
                    lines.Add(bal > 0
                        ? new LineInput(a.Number, bal, 0, item.CounterpartyId, $"BO: {item.Reference}", item.Id)
                        : new LineInput(a.Number, 0, -bal, item.CounterpartyId, $"BO: {item.Reference}", item.Id));
                }
                continue;
            }
            lines.Add(t.Balance > 0 ? new LineInput(a.Number, t.Balance, 0, Description: "BO") : new LineInput(a.Number, 0, -t.Balance, Description: "BO"));
        }
        if (resultBalance != 0)
            lines.Add(resultBalance < 0
                ? new LineInput(posting.RetainedEarnings, 0, -resultBalance, Description: $"BO: wynik finansowy roku {closed.Name} (do podziału)")
                : new LineInput(posting.RetainedEarnings, resultBalance, 0, Description: $"BO: strata roku {closed.Name} (do pokrycia)"));

        var dsum = lines.Sum(l => l.Debit);
        var csum = lines.Sum(l => l.Credit);
        if (dsum != csum) throw new DomainException($"Bilans otwarcia nie bilansuje się (Wn {Money.Format(dsum)} ≠ Ma {Money.Format(csum)}) — sprawdź zgodność rozrachunków z kontami.");
        if (lines.Count == 0) return;
        var ledger = new LedgerService(_db, _user, _clock) { SkipReadinessCheck = true };
        await ledger.CreateAndPostAsync(new EntryInput("BO", next.StartDate, closed.EndDate, next.StartDate, $"Bilans otwarcia {next.Name}", "BO", $"BO/{next.Name}",
            lines, Kind: EntryKind.BilansOtwarcia), ct);
        next.OpeningBalanceApproved = true;
        await _audit.AddAsync(_user, "BILANS_OTWARCIA", "FiscalYear", next.Id.ToString(), $"{next.Name}; pozycji: {lines.Count}; suma {Money.Format(dsum)}", ct);
        await _db.SaveChangesAsync(ct);
    }
}
