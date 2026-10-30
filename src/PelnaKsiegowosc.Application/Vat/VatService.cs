using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Application.Vat;

/// <summary>Pozycje części deklaracyjnej (VAT-7) wyliczone z ewidencji. Kwoty deklaracji w pełnych złotych.</summary>
public sealed class VatDeclaration
{
    public DateOnly Period { get; init; }
    public SortedDictionary<string, decimal> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Sumy ewidencji (K_xx) w groszach.</summary>
    public SortedDictionary<string, decimal> RegisterTotals { get; } = new(StringComparer.Ordinal);
    public decimal OutputTax { get; set; }
    public decimal InputTax { get; set; }
    public decimal CarriedForwardFromPrevious { get; set; }
    public decimal Payable { get; set; }
    public decimal Surplus { get; set; }
    public List<string> Warnings { get; } = new();
    public int SalesRows { get; set; }
    public int PurchaseRows { get; set; }
    public decimal SalesTaxControl { get; set; }
    public decimal PurchaseTaxControl { get; set; }
    /// <summary>Deklaracja kwartalna (JPK_V7K) — obejmuje trzy miesiące kończące się w <see cref="Period"/>.</summary>
    public bool Quarterly { get; init; }
    public int Quarter => (Period.Month - 1) / 3 + 1;
}

/// <summary>Rejestry VAT, wyliczenie pozycji deklaracji, rozliczenie VAT na kontach, kontrole poprawności.</summary>
public sealed class VatService
{
    private static readonly string[] NetFieldsInTotal = { "K_10", "K_11", "K_13", "K_15", "K_17", "K_19", "K_21", "K_22", "K_23", "K_25", "K_27", "K_29", "K_31" };
    private static readonly string[] TaxFieldsInTotal = { "K_16", "K_18", "K_20", "K_24", "K_26", "K_28", "K_30", "K_32", "K_33", "K_34", "K_35", "K_36" };

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public VatService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public Task<List<VatRegisterEntry>> RegisterAsync(VatRegisterType register, DateOnly period, CancellationToken ct = default)
    {
        var p = new DateOnly(period.Year, period.Month, 1);
        return _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == _user.CompanyId && v.Register == register && v.Period == p)
            .OrderBy(v => v.OrdinalNumber).ToListAsync(ct);
    }

    private async Task<bool> QuarterlyAsync(CancellationToken ct) =>
        await _db.Companies.AsNoTracking().Where(c => c.Id == _user.CompanyId).Select(c => c.VatPeriod).FirstOrDefaultAsync(ct) == VatSettlementPeriod.Kwartalny;

    /// <summary>Czy w tym miesiącu składa się deklarację (miesięcznie — zawsze; kwartalnie — w ostatnim miesiącu kwartału).</summary>
    public async Task<bool> IsDeclarationMonthAsync(DateOnly period, CancellationToken ct = default) => !await QuarterlyAsync(ct) || period.Month % 3 == 0;

    /// <summary>
    /// Deklaracja VAT za okres. Podatnik kwartalny: w ostatnim miesiącu kwartału — za cały kwartał (JPK_V7K);
    /// <paramref name="monthOnly"/> wymusza zestawienie wyłącznie z jednego miesiąca (sumy kontrolne ewidencji).
    /// </summary>
    public async Task<VatDeclaration> ComputeDeclarationAsync(DateOnly period, decimal? carriedForward = null, CancellationToken ct = default) =>
        await ComputeDeclarationAsync(period, carriedForward, monthOnly: false, ct);

    public async Task<VatDeclaration> ComputeDeclarationAsync(DateOnly period, decimal? carriedForward, bool monthOnly, CancellationToken ct = default)
    {
        var p = new DateOnly(period.Year, period.Month, 1);
        var quarterly = !monthOnly && p.Month % 3 == 0 && await QuarterlyAsync(ct);
        var months = quarterly ? new[] { p.AddMonths(-2), p.AddMonths(-1), p } : new[] { p };
        var sales = new List<VatRegisterEntry>();
        var purchases = new List<VatRegisterEntry>();
        foreach (var m in months)
        {
            sales.AddRange(await RegisterAsync(VatRegisterType.Sprzedaz, m, ct));
            purchases.AddRange(await RegisterAsync(VatRegisterType.Zakup, m, ct));
        }
        var d = new VatDeclaration { Period = p, Quarterly = quarterly, SalesRows = sales.Select(s => s.DocumentId).Distinct().Count(), PurchaseRows = purchases.Select(s => s.DocumentId).Distinct().Count() };

        foreach (var e in sales)
        {
            foreach (var nf in VatRules.NetFields(e.JpkNetField)) d.RegisterTotals[nf] = d.RegisterTotals.GetValueOrDefault(nf) + e.NetPln;
            if (e.JpkTaxField is { } tf) d.RegisterTotals[tf] = d.RegisterTotals.GetValueOrDefault(tf) + e.VatPln;
        }
        foreach (var e in purchases)
        {
            if (e.JpkNetField is { } nf) d.RegisterTotals[nf] = d.RegisterTotals.GetValueOrDefault(nf) + e.NetPln;
            if (e.JpkTaxField is { } tf) d.RegisterTotals[tf] = d.RegisterTotals.GetValueOrDefault(tf) + e.DeductibleVatPln;
        }
        d.SalesTaxControl = TaxFieldsInTotal.Sum(f => d.RegisterTotals.GetValueOrDefault(f));
        d.PurchaseTaxControl = new[] { "K_41", "K_43", "K_44", "K_45", "K_46", "K_47" }.Sum(f => d.RegisterTotals.GetValueOrDefault(f));

        foreach (var (k, v) in d.RegisterTotals) d.Fields["P_" + k[2..]] = Money.RoundToFullZloty(v);
        d.Fields["P_37"] = NetFieldsInTotal.Sum(f => d.Fields.GetValueOrDefault("P_" + f[2..]));
        d.Fields["P_38"] = TaxFieldsInTotal.Sum(f => d.Fields.GetValueOrDefault("P_" + f[2..]));
        d.CarriedForwardFromPrevious = carriedForward ?? await PreviousSurplusAsync(p, quarterly, ct);
        d.Fields["P_39"] = Money.RoundToFullZloty(d.CarriedForwardFromPrevious);
        d.Fields["P_48"] = d.Fields["P_39"] + d.Fields.GetValueOrDefault("P_41") + d.Fields.GetValueOrDefault("P_43") + d.Fields.GetValueOrDefault("P_44") +
                           d.Fields.GetValueOrDefault("P_45") + d.Fields.GetValueOrDefault("P_46") + d.Fields.GetValueOrDefault("P_47");
        var diff = d.Fields["P_38"] - d.Fields["P_48"];
        d.Fields["P_51"] = Math.Max(0, diff);
        d.Fields["P_53"] = Math.Max(0, -diff);
        d.OutputTax = d.SalesTaxControl;
        d.InputTax = d.PurchaseTaxControl;
        d.Payable = d.Fields["P_51"];
        d.Surplus = d.Fields["P_53"];

        foreach (var e in sales.Concat(purchases))
        {
            if (e.CounterpartyCountry == "PL" && string.IsNullOrWhiteSpace(e.CounterpartyNip) && e.Register == VatRegisterType.Zakup)
                d.Warnings.Add($"Zakup {e.DocumentNumber}: brak NIP dostawcy.");
            if (e.KsefMarker is null) d.Warnings.Add($"{e.DocumentNumber}: brak oznaczenia NrKSeF/OFF/BFK/DI (wymagane w JPK_V7(3)).");
            if (e.KsefMarker == JpkKsefMarker.NrKSeF && string.IsNullOrWhiteSpace(e.KsefNumber)) d.Warnings.Add($"{e.DocumentNumber}: oznaczenie NrKSeF bez numeru KSeF.");
        }
        if (sales.Any(s => s.RulesVersion != VatRules.CurrentVersion) || purchases.Any(s => s.RulesVersion != VatRules.CurrentVersion))
            d.Warnings.Add("Część wpisów utworzono według wcześniejszej wersji reguł VAT — zweryfikuj klasyfikację.");
        d.Warnings.Add("Pole P_39 (nadwyżka z poprzedniej deklaracji) przyjęto z zapisanego rozliczenia poprzedniego okresu — zweryfikuj z faktycznie złożoną deklaracją.");
        return d;
    }

    private async Task<decimal> PreviousSurplusAsync(DateOnly period, bool quarterly, CancellationToken ct)
    {
        // poprzednia deklaracja: miesiąc wcześniej, a przy kwartale — ostatni miesiąc poprzedniego kwartału
        var snap = await new SettingsService(_db, _clock).GetAsync<VatSettlementSnapshot?>(_user.CompanyId, $"vat.settlement.{period.AddMonths(quarterly ? -3 : -1):yyyy-MM}", () => null, ct);
        return snap is null ? 0 : snap.CarriedForward;
    }

    public sealed record VatSettlementSnapshot(DateOnly Period, decimal Payable, decimal Surplus, decimal CarriedForward, decimal RefundRequested, Guid? JournalEntryId);

    /// <summary>
    /// Rozliczenie VAT za okres na kontach: przeksięgowanie VAT należnego i naliczonego na rozrachunek z US (221-04),
    /// różnica z zaokrągleń do pełnych złotych na pozostałe przychody/koszty operacyjne.
    /// </summary>
    public async Task<JournalEntry> PostSettlementAsync(DateOnly period, decimal refundRequested = 0, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageVat);
        var p = new DateOnly(period.Year, period.Month, 1);
        var key = $"vat.settlement.{p:yyyy-MM}";
        var settings = new SettingsService(_db, _clock);
        if (await settings.GetAsync<VatSettlementSnapshot?>(_user.CompanyId, key, () => null, ct) is { JournalEntryId: not null })
            throw new DomainException($"VAT za {p:MM.yyyy} został już rozliczony.");
        if (!await IsDeclarationMonthAsync(p, ct))
            throw new DomainException($"Jednostka rozlicza VAT kwartalnie — rozliczenie wykonuje się w ostatnim miesiącu kwartału ({p.AddMonths(3 - (p.Month - 1) % 3 - 1):MM.yyyy}) za cały kwartał.");
        var d = await ComputeDeclarationAsync(p, null, ct);
        if (refundRequested < 0 || refundRequested > d.Surplus) throw new DomainException("Kwota zwrotu nie może przekraczać nadwyżki podatku naliczonego.");
        var posting = await settings.GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);

        var lines = new List<LineInput>();
        if (d.OutputTax != 0) lines.Add(d.OutputTax > 0 ? new LineInput(posting.VatOutput, d.OutputTax, 0, Description: "Przeksięgowanie VAT należnego") : new LineInput(posting.VatOutput, 0, -d.OutputTax));
        if (d.InputTax != 0) lines.Add(d.InputTax > 0 ? new LineInput(posting.VatInput, 0, d.InputTax, Description: "Przeksięgowanie VAT naliczonego") : new LineInput(posting.VatInput, -d.InputTax, 0));
        // wynik okresu w pełnych złotych (bez nadwyżki z poprzedniego okresu, która już jest na koncie rozrachunku z US)
        var exact = d.OutputTax - d.InputTax;
        var periodResult = d.Fields["P_38"] - (d.Fields["P_48"] - d.Fields["P_39"]);
        if (periodResult > 0) lines.Add(new LineInput(posting.VatSettlement, 0, periodResult, Description: "VAT należny do zapłaty / zmniejszenie nadwyżki"));
        if (periodResult < 0) lines.Add(new LineInput(posting.VatSettlement, -periodResult, 0, Description: "Nadwyżka VAT naliczonego"));
        var rounding = exact - periodResult;
        if (rounding > 0) lines.Add(new LineInput(posting.OtherOperatingIncome, 0, rounding, Description: "Zaokrąglenie VAT do pełnych złotych"));
        if (rounding < 0) lines.Add(new LineInput(posting.OtherOperatingCosts, -rounding, 0, Description: "Zaokrąglenie VAT do pełnych złotych"));
        if (lines.Count == 0) throw new DomainException("Brak kwot do rozliczenia.");
        var lastDay = p.AddMonths(1).AddDays(-1);
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("VAT", lastDay, lastDay, lastDay,
            $"Rozliczenie VAT za {(d.Quarterly ? $"{d.Quarter} kwartał {p.Year}" : p.ToString("MM.yyyy"))}: do zapłaty {Money.Format(d.Payable)}, nadwyżka {Money.Format(d.Surplus)}", "PK", $"PK/VAT/{p:yyyy-MM}", lines), ct);
        await settings.SetAsync(_user.CompanyId, key, new VatSettlementSnapshot(p, d.Payable, d.Surplus, d.Surplus - refundRequested, refundRequested, entry.Id), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "VAT_ROZLICZONY", "VatPeriod", p.ToString("yyyy-MM"), $"P_51={d.Payable}; P_53={d.Surplus}", ct);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Kontrola rejestrów: duplikaty numerów, brak NIP, faktury z datą późniejszą niż okres, niezgodność z kontami.</summary>
    public async Task<List<string>> ValidateRegistersAsync(DateOnly period, CancellationToken ct = default)
    {
        var p = new DateOnly(period.Year, period.Month, 1);
        var r = new List<string>();
        var all = await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == _user.CompanyId && v.Period == p).ToListAsync(ct);
        foreach (var g in all.Where(x => x.Register == VatRegisterType.Zakup).GroupBy(x => (x.CounterpartyNip, x.DocumentNumber)).Where(g => g.Select(x => x.DocumentId).Distinct().Count() > 1))
            r.Add($"Zdublowana faktura zakupu {g.Key.DocumentNumber} (NIP {g.Key.CounterpartyNip}).");
        foreach (var e in all.Where(x => x.Register == VatRegisterType.Sprzedaz && x.IssueDate > p.AddMonths(1).AddDays(-1) && x.OperationDate > p.AddMonths(1).AddDays(-1)))
            r.Add($"Sprzedaż {e.DocumentNumber}: data wystawienia i sprzedaży po okresie rozliczenia.");
        foreach (var e in all.Where(x => x.Register == VatRegisterType.Zakup && x.DeductibleVatPln > x.VatPln))
            r.Add($"Zakup {e.DocumentNumber}: VAT do odliczenia większy niż VAT z faktury.");
        return r;
    }
}
