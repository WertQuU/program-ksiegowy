using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.FixedAssets;

/// <summary>Ewidencja środków trwałych: przyjęcie (OT), amortyzacja miesięczna (bilansowa i podatkowa osobno), ulepszenia, likwidacja (LT), sprzedaż.</summary>
public sealed class FixedAssetService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public FixedAssetService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    /// <summary>Przyjęcie do ewidencji. Jeżeli wskazano konto przeciwstawne (np. 080, 210), księgowany jest dokument OT: Wn konto ŚT / Ma konto przeciwstawne.</summary>
    public async Task<FixedAsset> RegisterAsync(FixedAsset asset, string? offsetAccountNumber, Guid? supplierCounterpartyId = null, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageFixedAssets);
        var policy = await _db.AccountingPolicies.Where(p => p.CompanyId == _user.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
        asset.CompanyId = _user.CompanyId;
        asset.CreatedAtUtc = _clock.UtcNow;
        asset.Validate(policy?.LowValueAssetThreshold ?? 10000m).ThrowIfInvalid();
        if (await _db.FixedAssets.AnyAsync(a => a.CompanyId == _user.CompanyId && a.InventoryNumber == asset.InventoryNumber, ct))
            throw new DomainException($"Numer inwentarzowy {asset.InventoryNumber} jest już zajęty.");
        var ledger = new LedgerService(_db, _user, _clock);
        var (fy, _) = await ledger.PeriodForDateAsync(asset.PlacedInServiceDate, ct);
        var accounts = await ledger.AccountsByNumberAsync(fy.Id, ct);
        foreach (var n in new[] { asset.AssetAccountNumber, asset.AccumulatedDepreciationAccountNumber, asset.ExpenseAccountNumber })
            if (!accounts.ContainsKey(n)) throw new DomainException($"Konto {n} nie istnieje w planie kont.");
        _db.FixedAssets.Add(asset);
        asset.Operations.Add(new FixedAssetOperation
        {
            CompanyId = asset.CompanyId,
            FixedAssetId = asset.Id,
            Type = FixedAssetOperationType.Przyjecie,
            Date = asset.PlacedInServiceDate,
            BookValueChange = asset.InitialValueBook,
            TaxValueChange = asset.InitialValueTax,
            Description = "Przyjęcie do używania",
            UserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        });
        if (!string.IsNullOrWhiteSpace(offsetAccountNumber))
        {
            var lines = new List<LineInput>
            {
                new(asset.AssetAccountNumber, asset.InitialValueBook, 0, Description: $"OT {asset.InventoryNumber} {asset.Name}"),
                new(offsetAccountNumber, 0, asset.InitialValueBook, accounts[offsetAccountNumber].IsSettlementAccount ? supplierCounterpartyId : null, $"OT {asset.InventoryNumber}")
            };
            var entry = await ledger.CreateAndPostAsync(new EntryInput("PK", asset.PlacedInServiceDate, asset.PlacedInServiceDate, asset.PlacedInServiceDate,
                $"OT — przyjęcie środka trwałego {asset.InventoryNumber} {asset.Name}", "OT", $"OT/{asset.InventoryNumber}", lines), ct);
            asset.Operations[0].JournalEntryId = entry.Id;
        }
        await _audit.AddAsync(_user, "SRODEK_TRWALY_PRZYJETY", "FixedAsset", asset.Id.ToString(), $"{asset.InventoryNumber} {asset.Name}: {Money.Xml(asset.InitialValueBook)}", ct);
        await _db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>Naliczenie amortyzacji za miesiąc: zapis Wn koszty amortyzacji / Ma umorzenie (bilansowo); odpisy podatkowe zapisywane do CIT.</summary>
    public async Task<DepreciationRun> RunDepreciationAsync(int year, int month, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageFixedAssets);
        if (await _db.DepreciationRuns.AnyAsync(r => r.CompanyId == _user.CompanyId && r.Year == year && r.Month == month && !r.IsReversed, ct))
            throw new DomainException($"Amortyzacja za {month:00}.{year} została już naliczona.");
        var assets = await _db.FixedAssets.Include(a => a.Operations).Where(a => a.CompanyId == _user.CompanyId && a.Status == FixedAssetStatus.WEwidencji).ToListAsync(ct);
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var run = new DepreciationRun { CompanyId = _user.CompanyId, Year = year, Month = month, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow };
        var lines = new List<LineInput>();
        foreach (var a in assets.OrderBy(a => a.InventoryNumber))
        {
            if (await _db.DepreciationCharges.AnyAsync(c => c.FixedAssetId == a.Id && c.Year == year && c.Month == month, ct)) continue;
            var book = DepreciationCalculator.AmountFor(a, DepreciationBasis.Bilansowa, year, month);
            var tax = DepreciationCalculator.AmountFor(a, DepreciationBasis.Podatkowa, year, month);
            if (book == 0 && tax == 0) continue;
            var nonDed = Money.Round(tax * a.TaxNonDeductiblePercent / 100m);
            _db.DepreciationCharges.Add(new DepreciationCharge
            {
                CompanyId = a.CompanyId, FixedAssetId = a.Id, Year = year, Month = month, BookAmount = book, TaxAmount = tax,
                TaxNonDeductibleAmount = nonDed, RunId = run.Id
            });
            run.TotalBook += book;
            run.TotalTax += tax;
            if (book > 0)
            {
                lines.Add(new LineInput(a.ExpenseAccountNumber, book, 0, Description: $"Amortyzacja {a.InventoryNumber}"));
                lines.Add(new LineInput(a.AccumulatedDepreciationAccountNumber, 0, book, Description: $"Umorzenie {a.InventoryNumber}"));
            }
            var planBook = DepreciationCalculator.Plan(a, DepreciationBasis.Bilansowa);
            if (planBook.Count > 0 && planBook[^1].Year == year && planBook[^1].Month == month && planBook[^1].NetValue == 0) a.Status = FixedAssetStatus.CalkowicieZamortyzowany;
        }
        _db.DepreciationRuns.Add(run);
        if (lines.Count > 0)
        {
            var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("AM", lastDay, lastDay, lastDay,
                $"Amortyzacja środków trwałych i WNiP za {month:00}.{year}", "AM", $"AM/{year}/{month:00}", lines), ct);
            run.JournalEntryId = entry.Id;
            foreach (var c in _db.DepreciationCharges.Local.Where(c => c.RunId == run.Id)) c.JournalEntryId = entry.Id;
        }
        await _audit.AddAsync(_user, "AMORTYZACJA_NALICZONA", "DepreciationRun", run.Id.ToString(), $"{month:00}.{year}: bilansowa {Money.Xml(run.TotalBook)}, podatkowa {Money.Xml(run.TotalTax)}", ct);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task AddImprovementAsync(Guid assetId, DateOnly date, decimal amount, string offsetAccountNumber, string description, Guid? counterpartyId = null, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageFixedAssets);
        if (amount <= 0) throw new DomainException("Wartość ulepszenia musi być dodatnia.");
        var a = await _db.FixedAssets.Include(x => x.Operations).FirstAsync(x => x.Id == assetId && x.CompanyId == _user.CompanyId, ct);
        if (a.Status is FixedAssetStatus.Zlikwidowany or FixedAssetStatus.Sprzedany) throw new DomainException("Środek trwały został wycofany z ewidencji.");
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date, $"Ulepszenie {a.InventoryNumber}: {description}", "OT", $"OT-U/{a.InventoryNumber}/{date:yyyyMMdd}",
            new List<LineInput> { new(a.AssetAccountNumber, amount, 0), new(offsetAccountNumber, 0, amount, counterpartyId) }), ct);
        a.Operations.Add(new FixedAssetOperation
        {
            CompanyId = a.CompanyId, FixedAssetId = a.Id, Type = FixedAssetOperationType.Ulepszenie, Date = date, BookValueChange = amount, TaxValueChange = amount,
            Description = description, JournalEntryId = entry.Id, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        });
        if (a.Status == FixedAssetStatus.CalkowicieZamortyzowany) a.Status = FixedAssetStatus.WEwidencji;
        await _audit.AddAsync(_user, "SRODEK_TRWALY_ULEPSZENIE", "FixedAsset", a.Id.ToString(), $"{Money.Xml(amount)}: {description}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Likwidacja lub sprzedaż (wyksięgowanie): Wn umorzenie, Wn pozostałe koszty operacyjne (wartość netto) / Ma konto ŚT.
    /// Amortyzacja za miesiąc rozchodu musi zostać naliczona przed wyksięgowaniem. Przychód ze sprzedaży — fakturą sprzedaży (konto 760-01).
    /// </summary>
    public async Task DisposeAsync(Guid assetId, DateOnly date, bool isSale, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageFixedAssets);
        var a = await _db.FixedAssets.Include(x => x.Operations).FirstAsync(x => x.Id == assetId && x.CompanyId == _user.CompanyId, ct);
        if (a.Status is FixedAssetStatus.Zlikwidowany or FixedAssetStatus.Sprzedany) throw new DomainException("Środek trwały został już wycofany z ewidencji.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj przyczynę / podstawę rozchodu (protokół LT, umowa sprzedaży).");
        if (date < a.PlacedInServiceDate)
            throw new DomainException("Data rozchodu środka trwałego nie może poprzedzać przyjęcia go do używania.");
        // Nie ustawiamy DisposalDate przed kontrolą amortyzacji i księgowaniem.
        // W razie wyjątku EF Core pozostawiłby zmianę śledzoną w pamięci,
        // a następny SaveChanges mógłby ją utrwalić mimo odmowy rozchodu.
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Bilansowa);
        var expectedThroughMonth = plan.Where(p => new DateOnly(p.Year, p.Month, 1) <= new DateOnly(date.Year, date.Month, 1)).Sum(p => p.Amount);
        var charged = (await _db.DepreciationCharges.Where(c => c.FixedAssetId == a.Id).Select(c => c.BookAmount).ToListAsync(ct)).Sum();
        if (charged < expectedThroughMonth)
            throw new DomainException($"Najpierw nalicz amortyzację do miesiąca rozchodu włącznie (naliczono {Money.Format(charged)}, wymagane {Money.Format(expectedThroughMonth)}).");
        var p = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var gross = a.InitialValueBook + a.Operations.Where(o => o.Type is FixedAssetOperationType.Ulepszenie or FixedAssetOperationType.CzesciowaLikwidacja or FixedAssetOperationType.AktualizacjaWyceny).Sum(o => o.BookValueChange);
        var accumulated = a.OpeningAccumulatedBook + charged;
        var net = gross - accumulated;
        var lines = new List<LineInput>();
        if (accumulated > 0) lines.Add(new LineInput(a.AccumulatedDepreciationAccountNumber, accumulated, 0, Description: "Wyksięgowanie umorzenia"));
        if (net > 0) lines.Add(new LineInput(p.DisposalLoss, net, 0, Description: "Wartość netto rozchodowanego środka trwałego"));
        lines.Add(new LineInput(a.AssetAccountNumber, 0, gross, Description: "Wyksięgowanie wartości początkowej"));
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
            $"{(isSale ? "Sprzedaż" : "Likwidacja")} {a.InventoryNumber} {a.Name}: {reason}", "LT", $"LT/{a.InventoryNumber}", lines), ct);
        a.DisposalDate = date;
        a.Status = isSale ? FixedAssetStatus.Sprzedany : FixedAssetStatus.Zlikwidowany;
        a.Operations.Add(new FixedAssetOperation
        {
            CompanyId = a.CompanyId, FixedAssetId = a.Id, Type = isSale ? FixedAssetOperationType.Sprzedaz : FixedAssetOperationType.Likwidacja, Date = date,
            BookValueChange = -gross, TaxValueChange = -a.InitialValueTax, Description = reason, JournalEntryId = entry.Id, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        });
        await _audit.AddAsync(_user, isSale ? "SRODEK_TRWALY_SPRZEDANY" : "SRODEK_TRWALY_ZLIKWIDOWANY", "FixedAsset", a.Id.ToString(), $"{a.InventoryNumber}; netto {Money.Xml(net)}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(decimal gross, decimal accumulatedBook, decimal accumulatedTax, decimal netBook)> ValuesAsOfAsync(Guid assetId, DateOnly asOf, CancellationToken ct = default)
    {
        var a = await _db.FixedAssets.AsNoTracking().Include(x => x.Operations).FirstAsync(x => x.Id == assetId && x.CompanyId == _user.CompanyId, ct);
        var charges = await _db.DepreciationCharges.AsNoTracking().Where(c => c.FixedAssetId == assetId).ToListAsync(ct);
        var upto = charges.Where(c => new DateOnly(c.Year, c.Month, 1) <= asOf).ToList();
        var gross = a.InitialValueBook + a.Operations.Where(o => o.Date <= asOf && o.Type is FixedAssetOperationType.Ulepszenie or FixedAssetOperationType.AktualizacjaWyceny or FixedAssetOperationType.CzesciowaLikwidacja).Sum(o => o.BookValueChange);
        var accB = a.OpeningAccumulatedBook + upto.Sum(c => c.BookAmount);
        var accT = a.OpeningAccumulatedTax + upto.Sum(c => c.TaxAmount);
        return (gross, accB, accT, gross - accB);
    }
}
