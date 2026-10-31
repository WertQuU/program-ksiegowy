using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Cit;

/// <summary>
/// Różnica przejściowa: dodatnia — do opodatkowania w przyszłości (rezerwa), ujemna — do odliczenia (aktywo).
/// Stawka — przewidywana na rok odwrócenia różnicy (art. 37 ust. 6 UoR); źródło „zyski kapitałowe” zawsze 19%.
/// </summary>
public sealed record TemporaryDifference(string Item, string Basis, decimal BookValue, decimal TaxValue, decimal Difference,
    DateOnly ReversalPeriodStart = default, bool CapitalGains = false, decimal RatePercent = 0m, bool Unconfirmed = false)
{
    public decimal Tax => Money.Round(Math.Abs(Difference) * RatePercent / 100m);
    /// <summary>Rok kalendarzowy początku roku podatkowego, w którym różnica się odwraca (do prezentacji).</summary>
    public int ReversalYear => ReversalPeriodStart.Year;
    /// <summary>Rok odwrócenia nieustalony (niewyjaśniona część różnicy — do weryfikacji).</summary>
    public bool ReversalUnknown => ReversalPeriodStart == default;
}

public sealed record DeferredTaxCalculation(DateOnly AsOf, decimal RatePercent, IReadOnlyList<TemporaryDifference> Differences,
    decimal TaxableDifferences, decimal DeductibleDifferences, decimal Provision, decimal Asset, decimal BookedProvision, decimal BookedAsset,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Podatek od różnic opartych na niepotwierdzonych założeniach (stawka, kalendarz lat podatkowych, rok odwrócenia).</summary>
    public decimal UnconfirmedTax => Differences.Where(d => d.Unconfirmed).Sum(d => d.Tax);
    public decimal ProvisionChange => Provision - BookedProvision;
    public decimal AssetChange => Asset - BookedAsset;
    /// <summary>Obciążenie wyniku podatkiem odroczonym (dodatnie — koszt, ujemne — zmniejszenie obciążenia).</summary>
    public decimal ResultCharge => ProvisionChange - AssetChange;
}

/// <summary>
/// Podatek odroczony (art. 37 UoR): rezerwa (841) i aktywo (842) od różnic przejściowych między wartością bilansową
/// a podatkową — środki trwałe (amortyzacja bilansowa ≠ podatkowa), odpisy aktualizujące należności i rezerwy na
/// zobowiązania (koszty niepodatkowe do czasu realizacji), straty podatkowe do rozliczenia. Zmiana obciąża wynik (872,
/// RZiS „podatek dochodowy”). Jednostki małe/mikro mogą nie ustalać podatku odroczonego (art. 37 ust. 10 UoR).
/// </summary>
public sealed class DeferredTaxService
{
    public const string ProvisionAccount = "841";
    public const string AssetAccount = "842";
    public const string ExpenseAccount = "872";

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    /// <summary>Prognoza stawki dla roku podatkowego rozpoczynającego się w dniu <paramref name="taxYearStart"/>.</summary>
    public static string ExpectedRateKey(DateOnly taxYearStart) => $"cit.prognozaStawki.{taxYearStart:yyyy-MM-dd}";

    /// <summary>Prognozowana stawka CIT (inne źródła) na rok odwrócenia różnic — decyzja kierownika jednostki z uzasadnieniem.</summary>
    public async Task SetExpectedRateAsync(DateOnly taxYearStart, decimal ratePercent, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (ratePercent is not (CitCalculator.ReducedRate or CitCalculator.StandardRate)) throw new DomainException("Stawka CIT: 9% albo 19%.");
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Uzasadnij prognozę (np. przewidywane przychody i status małego podatnika w roku odwrócenia różnic).");
        await new Setup.SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, ExpectedRateKey(taxYearStart), ratePercent, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_PROGNOZA_STAWKI", "AppSetting", ExpectedRateKey(taxYearStart), $"rok podatkowy od {taxYearStart.ToPl()}: {ratePercent}% — {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public const string CalendarKey = "cit.kalendarzLatPodatkowych";
    public const string MaterialityKey = "cit.podatekOdroczony.progIstotnosci";
    public static string ReversalKey(Guid fixedAssetId) => $"cit.odwrocenieRoznicy.{fixedAssetId}";

    /// <summary>Potwierdzenie kalendarza lat podatkowych dla prognozy: kolejne lata to 12 miesięcy (art. 8 ust. 1 ustawy o CIT) do dnia <c>ConfirmedThrough</c>.</summary>
    public sealed record TaxYearCalendar(DateOnly ConfirmedThrough, string Justification, DateOnly ConfirmedOn);
    /// <summary>Udokumentowane ustalenie roku odwrócenia niewyjaśnionej części różnicy środka trwałego (np. planowana sprzedaż/likwidacja).</summary>
    public sealed record ReversalDecision(DateOnly TaxYearStart, string Justification, DateOnly DecidedOn);

    /// <summary>
    /// Potwierdzenie, że do dnia <paramref name="confirmedThrough"/> nie planuje się zmiany roku podatkowego (lata 12-miesięczne
    /// następujące po ostatnim roku założonym w programie). Planowaną zmianę roku podatkowego (art. 8 ust. 6–7) wprowadza się,
    /// zakładając w programie rok obrotowy o zmienionych granicach.
    /// </summary>
    public async Task ConfirmTaxYearCalendarAsync(DateOnly confirmedThrough, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Uzasadnij potwierdzenie (np. brak planowanej zmiany roku podatkowego — uchwała / umowa spółki).");
        await new Setup.SettingsService(_db, _clock).SetAsync<TaxYearCalendar?>(_user.CompanyId, CalendarKey, new TaxYearCalendar(confirmedThrough, justification.Trim(), _clock.Today), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_KALENDARZ_LAT", "AppSetting", CalendarKey, $"lata 12-miesięczne do {confirmedThrough.ToPl()}: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Ustalenie roku odwrócenia części różnicy środka trwałego niewynikającej z planów amortyzacji (sprzedaż, likwidacja, nieruchomość niepodlegająca amortyzacji).</summary>
    public async Task SetReversalYearAsync(Guid fixedAssetId, DateOnly taxYearStart, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Uzasadnij rok odwrócenia (np. planowana sprzedaż środka trwałego, decyzja o likwidacji).");
        await _db.FixedAssets.FirstAsync(a => a.Id == fixedAssetId && a.CompanyId == _user.CompanyId, ct);
        await new Setup.SettingsService(_db, _clock).SetAsync<ReversalDecision?>(_user.CompanyId, ReversalKey(fixedAssetId), new ReversalDecision(taxYearStart, justification.Trim(), _clock.Today), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "POD_ODROCZONY_ROK_ODWROCENIA", "FixedAsset", fixedAssetId.ToString(), $"rok podatkowy od {taxYearStart.ToPl()}: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public static string QualificationKey(Guid sourceId) => $"cit.kwalifikacjaRoznicy.{sourceId}";

    /// <summary>
    /// Kwalifikacja rezerwy lub odpisu aktualizującego: <c>Temporary</c> = false — koszt nie będzie KUP także po realizacji (np. kary
    /// umowne z art. 16 ust. 1 pkt 22, należność niespełniająca warunków art. 16 ust. 1 pkt 25–26) — różnica trwała, bez podatku
    /// odroczonego; true — różnica przejściowa odwracająca się w roku podatkowym od <c>ReversalTaxYearStart</c>.
    /// </summary>
    public sealed record DifferenceQualification(bool Temporary, DateOnly? ReversalTaxYearStart, string Justification, DateOnly QualifiedOn);

    /// <summary>Udokumentowana kwalifikacja rezerwy / odpisu aktualizującego należności dla podatku odroczonego (art. 37 ust. 1 i 4 UoR — zasada ostrożności).</summary>
    public async Task QualifyDifferenceAsync(Guid sourceId, bool temporary, DateOnly? reversalTaxYearStart, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification))
            throw new DomainException("Uzasadnij kwalifikację (np. termin wypłaty świadczeń, przewidywane zakończenie sporu, spełnienie warunków art. 16 ust. 1 pkt 25–26 ustawy o CIT).");
        var exists = await _db.Provisions.AnyAsync(p => p.Id == sourceId && p.CompanyId == _user.CompanyId, ct)
                     || await _db.ReceivableAllowances.AnyAsync(a => a.OpenItemId == sourceId && a.CompanyId == _user.CompanyId, ct);
        if (!exists) throw new DomainException("Nie znaleziono rezerwy ani odpisu aktualizującego.");
        await new Setup.SettingsService(_db, _clock).SetAsync<DifferenceQualification?>(_user.CompanyId, QualificationKey(sourceId),
            new DifferenceQualification(temporary, temporary ? reversalTaxYearStart : null, justification.Trim(), _clock.Today), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "POD_ODROCZONY_KWALIFIKACJA", "Valuation", sourceId.ToString(),
            $"{(temporary ? "różnica przejściowa" + (reversalTaxYearStart is { } r ? $", odwrócenie w roku od {r.ToPl()}" : "") : "różnica trwała")}: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Próg istotności (zł podatku) dla różnic opartych na niepotwierdzonych założeniach — powyżej progu księgowanie jest blokowane.</summary>
    public async Task SetMaterialityAsync(decimal threshold, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (threshold < 0) throw new DomainException("Próg nie może być ujemny.");
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Wskaż podstawę progu (polityka rachunkowości — zasada istotności, art. 4 ust. 4 UoR).");
        await new Setup.SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, MaterialityKey, threshold, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "POD_ODROCZONY_PROG", "AppSetting", MaterialityKey, $"{Money.Xml(threshold)} zł: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public static string LossAssessmentKey(Guid lossId) => $"cit.aktywoStrata.{lossId}";
    public sealed record LossAssessment(bool Recoverable, decimal? RecoverableAmount, string Justification, DateOnly AssessedOn);

    /// <summary>
    /// Udokumentowana ocena możliwości wykorzystania straty (art. 37 ust. 4 UoR, zasada ostrożności): aktywo ze straty ujmuje się
    /// tylko w kwocie, co do której prawdopodobne jest osiągnięcie dochodu do jej odliczenia (np. prognoza wyników, kontrakty).
    /// </summary>
    public async Task AssessLossAsync(Guid lossId, bool recoverable, decimal? recoverableAmount, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Opisz podstawę oceny (prognoza dochodów, zawarte umowy, plan finansowy).");
        if (recoverableAmount is < 0) throw new DomainException("Kwota nie może być ujemna.");
        await _db.TaxLosses.FirstAsync(l => l.Id == lossId && l.CompanyId == _user.CompanyId, ct);
        await new Setup.SettingsService(_db, _clock).SetAsync<LossAssessment?>(_user.CompanyId, LossAssessmentKey(lossId),
            new LossAssessment(recoverable, recoverableAmount, justification.Trim(), _clock.Today), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "POD_ODROCZONY_OCENA_STRATY", "TaxLoss", lossId.ToString(),
            $"{(recoverable ? "do wykorzystania" : "niepewna")}{(recoverableAmount is { } a ? $" do {Money.Xml(a)}" : "")}: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public DeferredTaxService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    /// <summary>
    /// Kolejne lata podatkowe od dnia bilansowego: bieżący (gdy <paramref name="asOf"/> jest przed jego końcem), lata już utworzone
    /// w programie (w tym rok zmieniony — art. 8 ust. 6–7 ustawy o CIT), dalej — 12 kolejnych miesięcy (art. 8 ust. 1), bez
    /// przenoszenia długości bieżącego roku (pierwszy lub zmieniony rok może być krótszy albo dłuższy). <paramref name="projectedFrom"/>
    /// — początek pierwszego roku, którego granice są wyłącznie prognozą (brak w programie i poza potwierdzonym kalendarzem).
    /// </summary>
    public static List<(DateOnly Start, DateOnly End)> FuturePeriods(Domain.Periods.FiscalYear fy, DateOnly asOf, IReadOnlyList<(DateOnly Start, DateOnly End)> years,
        out DateOnly? projectedFrom, DateOnly? calendarConfirmedThrough = null)
    {
        projectedFrom = null;
        var periods = new List<(DateOnly Start, DateOnly End)>();
        if (asOf < fy.EndDate) periods.Add((fy.StartDate, fy.EndDate));
        var next = fy.EndDate.AddDays(1);
        for (var i = 0; i < 60; i++)
        {
            var known = years.FirstOrDefault(y => y.Start == next);
            var end = known != default ? known.End : next.AddMonths(12).AddDays(-1);
            if (known == default && projectedFrom is null && (calendarConfirmedThrough is not { } through || end > through)) projectedFrom = next;
            periods.Add((next, end));
            next = end.AddDays(1);
        }
        return periods;
    }

    public static List<(DateOnly Start, DateOnly End)> FuturePeriods(Domain.Periods.FiscalYear fy, DateOnly asOf, IReadOnlyList<(DateOnly Start, DateOnly End)> years) =>
        FuturePeriods(fy, asOf, years, out _);

    /// <summary>
    /// Odwrócenie istniejącej różnicy <paramref name="difference"/> w kolejnych latach na podstawie przyszłych zmian różnicy
    /// (odpis bilansowy − odpis podatkowy): różnica istniejąca odwraca się dopiero wtedy, gdy narastająco spada poniżej
    /// dotychczasowego minimum — nowe różnice powstające po dniu bilansowym (np. wyższa amortyzacja podatkowa) nie są
    /// odwróceniem różnicy istniejącej. Część, której plany amortyzacji nie odwracają (np. sprzedaż, likwidacja, wartość
    /// nieamortyzowana, rozbieżność planu z odpisami zaksięgowanymi), nie jest przypisywana do żadnego roku — zwracana jako
    /// <c>Unexplained</c> do weryfikacji. Wyjątek: różnica zaokrągleń poniżej 1 zł — w ostatnim roku z przepływem.
    /// </summary>
    public static List<((DateOnly Start, DateOnly End) Period, decimal Amount)> ExistingDifferenceReversal(decimal difference,
        IReadOnlyList<((DateOnly Start, DateOnly End) Period, decimal Reversal)> flows) => ExistingDifferenceReversal(difference, flows, out _);

    public static List<((DateOnly Start, DateOnly End) Period, decimal Amount)> ExistingDifferenceReversal(decimal difference,
        IReadOnlyList<((DateOnly Start, DateOnly End) Period, decimal Reversal)> flows, out decimal unexplained)
    {
        unexplained = 0m;
        var result = new List<((DateOnly, DateOnly), decimal)>();
        if (difference == 0) return result;
        if (flows.Count == 0) { unexplained = difference; return result; }
        var sign = Math.Sign(difference);
        var existing = Math.Abs(difference);
        var running = existing;
        foreach (var (period, reversal) in flows)
        {
            running -= sign * reversal;
            var stillExisting = Math.Min(existing, Math.Max(0m, running));
            var reversed = existing - stillExisting;
            if (reversed > 0) result.Add((period, sign * reversed));
            existing = stillExisting;
            if (existing == 0) break;
        }
        if (existing > 0)
        {
            var last = flows.LastOrDefault(f => f.Reversal != 0);
            if (existing < 1m && last != default)
            {
                var i = result.FindIndex(r => r.Item1 == last.Period);
                if (i >= 0) result[i] = (result[i].Item1, result[i].Item2 + sign * existing);
                else result.Add((last.Period, sign * existing));
            }
            else unexplained = sign * existing;
        }
        return result;
    }

    public async Task<DeferredTaxCalculation> ComputeAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == fy.CompanyId, ct);
        if (!LegalForms.IsCitTaxpayer(company.LegalForm))
            throw new DomainException("Jednostka nie jest podatnikiem CIT — podatku odroczonego nie ustala się (podatek rozliczają wspólnicy / właściciel).");
        var warnings = new List<string>();
        var settings = new Setup.SettingsService(_db, _clock);
        var years = (await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == fy.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct))
            .Select(y => (y.StartDate, y.EndDate)).ToList();
        var calendar = await settings.GetAsync<TaxYearCalendar?>(fy.CompanyId, CalendarKey, () => null, ct);
        var periods = FuturePeriods(fy, asOf, years, out var projectedFrom, calendar?.ConfirmedThrough);
        var first = periods[0];
        var projectedWarned = new HashSet<DateOnly>();
        // rok odwrócenia poza latami w programie i poza potwierdzonym kalendarzem — granice roku są tylko prognozą
        bool ProjectedYear(DateOnly start)
        {
            if (projectedFrom is not { } pf || start < pf) return false;
            if (projectedWarned.Add(start))
            {
                var p = periods.First(x => x.Start == start);
                warnings.Add($"Rok podatkowy {p.Start.ToPl()}–{p.End.ToPl()} przyjęto jako 12 kolejnych miesięcy (art. 8 ust. 1 ustawy o CIT) — nie jest założony w programie ani potwierdzony. Potwierdź kalendarz lat podatkowych albo załóż rok o zmienionych granicach.");
            }
            return true;
        }
        // stawka bieżącego roku (wyliczenie CIT) — dla odwrócenia w bieżącym roku i jako punkt wyjścia, gdy brak prognozy
        decimal current;
        var currentConfirmed = true;
        try
        {
            var fyFull = await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
            var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fyFull.Periods.Count, ct);
            current = rec.Rate.RateMissing ? CitCalculator.StandardRate : rec.Rate.RatePercent;
            if (rec.Rate.RateMissing) currentConfirmed = false;
        }
        catch (DomainException) { current = CitCalculator.StandardRate; currentConfirmed = false; }
        if (!currentConfirmed) warnings.Add($"Nie ustalono stawki CIT bieżącego roku (brak danych do limitu 2 mln euro) — przyjęto {current:0}%.");
        var expectedCache = new Dictionary<DateOnly, (decimal Rate, bool Confirmed)>();
        async Task<(decimal Rate, bool Confirmed)> RateForAsync(DateOnly periodStart, bool capital)
        {
            if (capital) return (CitCalculator.StandardRate, true);
            if (periodStart == fy.StartDate) return (current, currentConfirmed);
            if (expectedCache.TryGetValue(periodStart, out var r)) return r;
            var expected = await settings.GetAsync<decimal?>(fy.CompanyId, ExpectedRateKey(periodStart), () => null, ct);
            var confirmed = expected is not null;
            if (expected is null)
            {
                var label = periods.FirstOrDefault(p => p.Start == periodStart);
                warnings.Add($"Brak prognozy stawki CIT na rok podatkowy {label.Start.ToPl()}–{label.End.ToPl()} (odwrócenie różnic) — przyjęto {current:0}% jak w bieżącym roku. Potwierdź prognozę (art. 37 ust. 6 UoR): 9% tylko przy spełnieniu warunków art. 19 w tym roku.");
                expected = current;
            }
            expectedCache[periodStart] = (expected.Value, confirmed);
            return expectedCache[periodStart];
        }
        async Task<TemporaryDifference> DifferenceAsync(string item, string basis, decimal book, decimal tax, decimal amount, DateOnly reversalStart, bool capital)
        {
            var (r, ok) = await RateForAsync(reversalStart, capital);
            var projected = !capital && ProjectedYear(reversalStart);
            return new TemporaryDifference(item, basis, book, tax, amount, reversalStart, capital, r, !ok || projected);
        }
        var (rate, _) = await RateForAsync(first.Start, false);
        var diffs = new List<TemporaryDifference>();

        // środki trwałe: różnica = wartość bilansowa netto − podatkowa netto; odwraca się w latach, w których przyszłe odpisy
        // bilansowe i podatkowe (wg planów amortyzacji, z ulepszeniami i metodą degresywną) się różnią: odwrócenie w roku =
        // odpis bilansowy − odpis podatkowy; część nieodwracana planami — w roku zbycia/likwidacji (zarejestrowanego albo
        // udokumentowanej decyzji), a bez tego jako „niewyjaśniona — do weryfikacji” (bez przypisywania do ostatniego roku)
        var assets = await _db.FixedAssets.AsNoTracking().Include(a => a.Operations).Where(a => a.CompanyId == fy.CompanyId && a.PlacedInServiceDate <= asOf
                                                                     && (a.DisposalDate == null || a.DisposalDate > asOf)).ToListAsync(ct);
        var charges = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.CompanyId == fy.CompanyId && c.JournalEntryId != null).ToListAsync(ct))
            .Where(c => new DateOnly(c.Year, c.Month, 1) <= asOf).GroupBy(c => c.FixedAssetId)
            .ToDictionary(g => g.Key, g => (Book: g.Sum(c => c.BookAmount), Tax: g.Sum(c => c.TaxAmount)));
        var asOfMonth = new DateOnly(asOf.Year, asOf.Month, 1);
        foreach (var a in assets)
        {
            var c = charges.GetValueOrDefault(a.Id);
            var book = a.InitialValueBook - a.OpeningAccumulatedBook - c.Book;
            var tax = a.InitialValueTax - a.OpeningAccumulatedTax - c.Tax;
            if (book == tax) continue;
            var total = Money.Round(book - tax);
            var bookPlan = Domain.FixedAssets.DepreciationCalculator.Plan(a, Domain.FixedAssets.DepreciationBasis.Bilansowa).Where(r => new DateOnly(r.Year, r.Month, 1) > asOfMonth).ToList();
            var taxPlan = Domain.FixedAssets.DepreciationCalculator.Plan(a, Domain.FixedAssets.DepreciationBasis.Podatkowa).Where(r => new DateOnly(r.Year, r.Month, 1) > asOfMonth).ToList();
            decimal In(IEnumerable<Domain.FixedAssets.DepreciationPlanRow> rows, (DateOnly Start, DateOnly End) p) =>
                rows.Where(r => new DateOnly(r.Year, r.Month, 1) >= new DateOnly(p.Start.Year, p.Start.Month, 1) && new DateOnly(r.Year, r.Month, 1) <= p.End).Sum(r => r.Amount);
            var flows = periods.Select(p => (Period: p, Reversal: Money.Round(In(bookPlan, p) - In(taxPlan, p)))).ToList();
            var reversals = ExistingDifferenceReversal(total, flows, out var unexplained);
            foreach (var (p, part) in reversals)
                diffs.Add(await DifferenceAsync($"Środek trwały {a.InventoryNumber} {a.Name} — odwrócenie w roku {p.Start.ToPl()}–{p.End.ToPl()}",
                    "wartość bilansowa ≠ podatkowa (plan amortyzacji)", book, tax, part, p.Start, false));
            if (unexplained == 0) continue;
            var decision = await settings.GetAsync<ReversalDecision?>(fy.CompanyId, ReversalKey(a.Id), () => null, ct);
            // zbycie/likwidacja zarejestrowane z datą po dniu bilansowym (plany kończą się na zbyciu) — reszta odwraca się w roku zbycia
            var disposalPeriod = a.DisposalDate is { } d2 ? periods.FirstOrDefault(p => p.Start <= d2 && d2 <= p.End) : default;
            if (disposalPeriod != default)
                diffs.Add(await DifferenceAsync($"Środek trwały {a.InventoryNumber} {a.Name} — odwrócenie przy zbyciu/likwidacji {a.DisposalDate!.Value.ToPl()}",
                    "wartość bilansowa ≠ podatkowa (zbycie/likwidacja)", book, tax, unexplained, disposalPeriod.Start, false));
            else if (decision is not null)
                diffs.Add(await DifferenceAsync($"Środek trwały {a.InventoryNumber} {a.Name} — odwrócenie w roku od {decision.TaxYearStart.ToPl()} (ustalenie z {decision.DecidedOn.ToPl()})",
                    $"wartość bilansowa ≠ podatkowa: {decision.Justification}", book, tax, unexplained, decision.TaxYearStart, false));
            else
            {
                diffs.Add(new TemporaryDifference($"Środek trwały {a.InventoryNumber} {a.Name} — część niewyjaśniona planem amortyzacji (do weryfikacji)",
                    "rok odwrócenia nieustalony — sprawdź: planowana sprzedaż/likwidacja, wartość nieamortyzowana, różnica trwała (np. limit dla samochodu), zgodność planu z odpisami",
                    book, tax, unexplained, default, false, current, Unconfirmed: true));
                warnings.Add($"Środek trwały {a.InventoryNumber} {a.Name}: {Money.Format(Math.Abs(unexplained))} zł różnicy nie odwraca się według planów amortyzacji — ustal rok odwrócenia (sprzedaż, likwidacja) albo ujmij jako różnicę trwałą. Do czasu ustalenia przyjęto stawkę bieżącą {current:0}%.");
            }
        }

        // odpisy aktualizujące należności i rezerwy (NKUP do czasu realizacji): każda pozycja osobno. Bez udokumentowanej
        // kwalifikacji przyjmuje się odwrócenie w najbliższym roku podatkowym tylko dla rezerw urlopowych i na koszty (rozliczanych
        // zwykle w kolejnym roku); pozostałe (odpisy na należności — warunki art. 16 ust. 1 pkt 25–26, rezerwy emerytalne,
        // gwarancyjne, na sprawy sporne i kary — możliwa różnica trwała lub odwrócenie po latach) — do kwalifikacji (niezatwierdzone).
        // Różnica trwała — bez podatku odroczonego.
        async Task ValuationDifferenceAsync(Guid id, string item, string basis, decimal amount, bool defaultConfirmed)
        {
            if (amount == 0) return;
            var q = await settings.GetAsync<DifferenceQualification?>(fy.CompanyId, QualificationKey(id), () => null, ct);
            if (q is { Temporary: false }) return;
            var start = q?.ReversalTaxYearStart is { } rs && rs > first.Start ? rs : first.Start;
            var d = await DifferenceAsync(item + (q is null ? "" : $" (kwalifikacja z {q.QualifiedOn.ToPl()})"), q is null ? basis : $"{basis}; {q.Justification}", -amount, 0m, -amount, start, false);
            if (q is null && !defaultConfirmed)
            {
                d = d with { Unconfirmed = true };
                warnings.Add($"{item}: brak kwalifikacji (różnica przejściowa czy trwała, rok odwrócenia) — przyjęto odwrócenie w najbliższym roku; zakwalifikuj przed księgowaniem (art. 37 ust. 1 i 4 UoR).");
            }
            diffs.Add(d);
        }
        // odpisy na należność — saldo odpisów (utworzenie − rozwiązanie) dla każdej należności; kwalifikacja wg należności
        var allowances = (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.Date <= asOf)
                .Select(a => new { a.OpenItemId, a.Amount }).ToListAsync(ct))
            .GroupBy(a => a.OpenItemId).Select(g => (Item: g.Key, Amount: g.Sum(a => a.Amount))).Where(x => x.Amount != 0).ToList();
        var itemIds = allowances.Select(a => a.Item).ToList();
        var refs = await _db.OpenItems.AsNoTracking().Where(o => itemIds.Contains(o.Id)).Select(o => new { o.Id, o.Reference }).ToListAsync(ct);
        foreach (var (item, amount) in allowances)
            await ValuationDifferenceAsync(item, $"Odpis aktualizujący należność {refs.FirstOrDefault(r => r.Id == item)?.Reference}", "art. 16 ust. 1 pkt 26a ustawy o CIT", amount, false);
        var provisions = await _db.Provisions.AsNoTracking().Where(p => p.CompanyId == fy.CompanyId && p.Date <= asOf).ToListAsync(ct);
        foreach (var p in provisions.Where(p => p.Remaining != 0))
            await ValuationDifferenceAsync(p.Id, $"Rezerwa {p.Kind}: {p.Description}", "art. 16 ust. 1 pkt 27 ustawy o CIT", p.Remaining,
                p.Kind is Domain.Settlements.ProvisionKind.Urlopowa or Domain.Settlements.ProvisionKind.NaKoszty);

        // straty podatkowe — wyłącznie z rejestru strat: okres odliczenia ze wspólnego mechanizmu lat podatkowych; aktywo tylko
        // po udokumentowanej ocenie możliwości wykorzystania (art. 37 ust. 4 UoR); zyski kapitałowe po 19%
        var losses = await _db.TaxLosses.AsNoTracking().Where(l => l.CompanyId == fy.CompanyId && l.YearEnd <= asOf).ToListAsync(ct);
        var usedByLoss = (await _db.TaxLossDeductions.AsNoTracking().Where(d => d.CompanyId == fy.CompanyId).ToListAsync(ct))
            .GroupBy(d => d.TaxLossId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        foreach (var l in losses)
        {
            var left = l.Amount - usedByLoss.GetValueOrDefault(l.Id);
            if (left <= 0) continue;
            var until = TaxLossRules.WindowEnd(l, years);
            if (until is null) { warnings.Add($"Strata {l.YearLabel}: potwierdź koniec okresu odliczenia — bez tego aktywo nie jest ujmowane."); continue; }
            if (until < asOf.AddDays(1)) continue;
            var assessment = await settings.GetAsync<LossAssessment?>(fy.CompanyId, LossAssessmentKey(l.Id), () => null, ct);
            if (assessment is null) { warnings.Add($"Strata {l.YearLabel} ({Money.Format(left)} zł): brak udokumentowanej oceny możliwości jej wykorzystania — aktywo nie zostało ujęte (art. 37 ust. 4 UoR)."); continue; }
            if (!assessment.Recoverable) continue;
            var amount = Math.Min(left, assessment.RecoverableAmount ?? left);
            if (amount <= 0) continue;
            diffs.Add(await DifferenceAsync($"Strata podatkowa {l.YearLabel}{(l.CapitalGains ? " (zyski kapitałowe)" : "")}",
                $"art. 7 ust. 5 ustawy o CIT; ocena z {assessment.AssessedOn.ToPl()}: {assessment.Justification}", 0m, amount, -amount,
                first.Start, l.CapitalGains));
        }

        var taxable = diffs.Where(d => d.Difference > 0).Sum(d => d.Difference);
        var deductible = -diffs.Where(d => d.Difference < 0).Sum(d => d.Difference);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        decimal Bal(string n) => tb.Where(t => t.Account.Number == n).Sum(t => t.Balance);
        return new DeferredTaxCalculation(asOf, rate, diffs, taxable, deductible,
            diffs.Where(d => d.Difference > 0).Sum(d => d.Tax), diffs.Where(d => d.Difference < 0).Sum(d => d.Tax), -Bal(ProvisionAccount), Bal(AssetAccount), warnings);
    }

    /// <summary>Księguje zmianę rezerwy i aktywa: Wn 872 / Ma 841 (wzrost rezerwy), Wn 842 / Ma 872 (wzrost aktywa), odwrotnie przy spadku.</summary>
    public async Task<DeferredTaxCalculation> PostAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var c = await ComputeAsync(fiscalYearId, asOf, ct);
        if (c.ProvisionChange == 0 && c.AssetChange == 0) throw new DomainException("Podatek odroczony jest już ujęty w aktualnej wysokości.");
        // istotne kwoty oparte na niepotwierdzonych założeniach (stawka, granice roku podatkowego, rok odwrócenia) — bez księgowania
        var threshold = await new Setup.SettingsService(_db, _clock).GetAsync<decimal?>(_user.CompanyId, MaterialityKey, () => null, ct) ?? 0m;
        if (c.UnconfirmedTax > threshold)
            throw new DomainException($"Podatek odroczony {Money.Format(c.UnconfirmedTax)} zł opiera się na niezatwierdzonych prognozach (próg istotności {Money.Format(threshold)} zł). " +
                                      "Zatwierdź prognozy przed księgowaniem:" + Environment.NewLine + string.Join(Environment.NewLine, c.Warnings ?? []));
        await new ValuationService(_db, _user, _clock).EnsureAccountsAsync(asOf, ct, ProvisionAccount, AssetAccount, ExpenseAccount);
        var lines = new List<LineInput>();
        if (c.ProvisionChange > 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, c.ProvisionChange, 0, Description: "Zwiększenie rezerwy z tytułu odroczonego podatku"), new(ProvisionAccount, 0, c.ProvisionChange) });
        if (c.ProvisionChange < 0) lines.AddRange(new LineInput[] { new(ProvisionAccount, -c.ProvisionChange, 0), new(ExpenseAccount, 0, -c.ProvisionChange, Description: "Zmniejszenie rezerwy z tytułu odroczonego podatku") });
        if (c.AssetChange > 0) lines.AddRange(new LineInput[] { new(AssetAccount, c.AssetChange, 0), new(ExpenseAccount, 0, c.AssetChange, Description: "Zwiększenie aktywa z tytułu odroczonego podatku") });
        if (c.AssetChange < 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, -c.AssetChange, 0, Description: "Zmniejszenie aktywa z tytułu odroczonego podatku"), new(AssetAccount, 0, -c.AssetChange) });
        var number = $"POD/{await new NumberingService(_db).NextAsync(_user.CompanyId, $"POD/{asOf.Year}", ct)}/{asOf.Year}";
        await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", asOf, asOf, asOf,
            $"Podatek odroczony na {asOf.ToPl()} (stawki wg roku odwrócenia różnic: {string.Join(", ", c.Differences.Select(d => d.RatePercent).Distinct().Select(r => $"{r:0}%"))})", "PK", number, lines), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "PODATEK_ODROCZONY", "FiscalYear", fiscalYearId.ToString(),
            $"rezerwa {Money.Xml(c.Provision)}; aktywo {Money.Xml(c.Asset)}", ct);
        await _db.SaveChangesAsync(ct);
        return c;
    }
}
