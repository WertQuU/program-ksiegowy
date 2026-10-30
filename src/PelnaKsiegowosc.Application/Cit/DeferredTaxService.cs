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

/// <summary>Różnica przejściowa: dodatnia — do opodatkowania w przyszłości (rezerwa), ujemna — do odliczenia (aktywo).</summary>
public sealed record TemporaryDifference(string Item, string Basis, decimal BookValue, decimal TaxValue, decimal Difference);

public sealed record DeferredTaxCalculation(DateOnly AsOf, decimal RatePercent, IReadOnlyList<TemporaryDifference> Differences,
    decimal TaxableDifferences, decimal DeductibleDifferences, decimal Provision, decimal Asset, decimal BookedProvision, decimal BookedAsset)
{
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

    public DeferredTaxService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    /// <param name="taxLossesToSettle">Straty podatkowe możliwe do odliczenia w przyszłości (jeśli prawdopodobne jest osiągnięcie dochodu).</param>
    public async Task<DeferredTaxCalculation> ComputeAsync(Guid fiscalYearId, DateOnly asOf, decimal taxLossesToSettle = 0m, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == fy.CompanyId, ct);
        if (!LegalForms.IsCitTaxpayer(company.LegalForm))
            throw new DomainException("Jednostka nie jest podatnikiem CIT — podatku odroczonego nie ustala się (podatek rozliczają wspólnicy / właściciel).");
        var rate = CitCalculator.RateFor(company.IsSmallCitTaxpayer || fy.IsFirstYear);
        var diffs = new List<TemporaryDifference>();

        // środki trwałe: wartość bilansowa netto vs podatkowa netto
        var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.PlacedInServiceDate <= asOf
                                                                     && (a.DisposalDate == null || a.DisposalDate > asOf)).ToListAsync(ct);
        var charges = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.CompanyId == fy.CompanyId && c.JournalEntryId != null).ToListAsync(ct))
            .Where(c => new DateOnly(c.Year, c.Month, 1) <= asOf).GroupBy(c => c.FixedAssetId)
            .ToDictionary(g => g.Key, g => (Book: g.Sum(c => c.BookAmount), Tax: g.Sum(c => c.TaxAmount)));
        foreach (var a in assets)
        {
            var c = charges.GetValueOrDefault(a.Id);
            var book = a.InitialValueBook - a.OpeningAccumulatedBook - c.Book;
            var tax = a.InitialValueTax - a.OpeningAccumulatedTax - c.Tax;
            if (book != tax) diffs.Add(new TemporaryDifference($"Środek trwały {a.InventoryNumber} {a.Name}", "wartość bilansowa ≠ podatkowa (amortyzacja)", book, tax, Money.Round(book - tax)));
        }

        // odpisy aktualizujące należności (NKUP do czasu uprawdopodobnienia nieściągalności)
        var allowance = (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.Date <= asOf).Select(a => a.Amount).ToListAsync(ct)).Sum();
        if (allowance != 0) diffs.Add(new TemporaryDifference("Odpisy aktualizujące należności", "art. 16 ust. 1 pkt 26a ustawy o CIT", -allowance, 0m, -allowance));

        // rezerwy na zobowiązania (NKUP do czasu poniesienia kosztu)
        var provisions = await _db.Provisions.AsNoTracking().Where(p => p.CompanyId == fy.CompanyId && p.Date <= asOf).ToListAsync(ct);
        foreach (var g in provisions.GroupBy(p => p.Kind))
        {
            var remaining = g.Sum(p => p.Remaining);
            if (remaining != 0) diffs.Add(new TemporaryDifference($"Rezerwy: {g.Key}", "art. 16 ust. 1 pkt 27 ustawy o CIT", -remaining, 0m, -remaining));
        }

        if (taxLossesToSettle > 0) diffs.Add(new TemporaryDifference("Straty podatkowe do rozliczenia", "art. 7 ust. 5 ustawy o CIT", 0m, taxLossesToSettle, -taxLossesToSettle));

        var taxable = diffs.Where(d => d.Difference > 0).Sum(d => d.Difference);
        var deductible = -diffs.Where(d => d.Difference < 0).Sum(d => d.Difference);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        decimal Bal(string n) => tb.Where(t => t.Account.Number == n).Sum(t => t.Balance);
        return new DeferredTaxCalculation(asOf, rate, diffs, taxable, deductible,
            Money.Round(taxable * rate / 100m), Money.Round(deductible * rate / 100m), -Bal(ProvisionAccount), Bal(AssetAccount));
    }

    /// <summary>Księguje zmianę rezerwy i aktywa: Wn 872 / Ma 841 (wzrost rezerwy), Wn 842 / Ma 872 (wzrost aktywa), odwrotnie przy spadku.</summary>
    public async Task<DeferredTaxCalculation> PostAsync(Guid fiscalYearId, DateOnly asOf, decimal taxLossesToSettle = 0m, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var c = await ComputeAsync(fiscalYearId, asOf, taxLossesToSettle, ct);
        if (c.ProvisionChange == 0 && c.AssetChange == 0) throw new DomainException("Podatek odroczony jest już ujęty w aktualnej wysokości.");
        await new ValuationService(_db, _user, _clock).EnsureAccountsAsync(asOf, ct, ProvisionAccount, AssetAccount, ExpenseAccount);
        var lines = new List<LineInput>();
        if (c.ProvisionChange > 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, c.ProvisionChange, 0, Description: "Zwiększenie rezerwy z tytułu odroczonego podatku"), new(ProvisionAccount, 0, c.ProvisionChange) });
        if (c.ProvisionChange < 0) lines.AddRange(new LineInput[] { new(ProvisionAccount, -c.ProvisionChange, 0), new(ExpenseAccount, 0, -c.ProvisionChange, Description: "Zmniejszenie rezerwy z tytułu odroczonego podatku") });
        if (c.AssetChange > 0) lines.AddRange(new LineInput[] { new(AssetAccount, c.AssetChange, 0), new(ExpenseAccount, 0, c.AssetChange, Description: "Zwiększenie aktywa z tytułu odroczonego podatku") });
        if (c.AssetChange < 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, -c.AssetChange, 0, Description: "Zmniejszenie aktywa z tytułu odroczonego podatku"), new(AssetAccount, 0, -c.AssetChange) });
        var number = $"POD/{await new NumberingService(_db).NextAsync(_user.CompanyId, $"POD/{asOf.Year}", ct)}/{asOf.Year}";
        await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", asOf, asOf, asOf,
            $"Podatek odroczony na {asOf.ToPl()} (stawka {c.RatePercent:0}%)", "PK", number, lines), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "PODATEK_ODROCZONY", "FiscalYear", fiscalYearId.ToString(),
            $"rezerwa {Money.Xml(c.Provision)}; aktywo {Money.Xml(c.Asset)}", ct);
        await _db.SaveChangesAsync(ct);
        return c;
    }
}
