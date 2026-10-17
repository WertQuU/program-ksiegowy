using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.FixedAssets;

public enum DepreciationBasis
{
    Bilansowa,
    Podatkowa
}

public sealed record DepreciationPlanRow(int Year, int Month, decimal BaseValue, decimal Amount, decimal Accumulated, decimal NetValue, DepreciationMethod MethodUsed);

/// <summary>
/// Kalkulator planu amortyzacji. Zasady:
/// <list type="bullet">
/// <item>Odpisy od miesiąca następującego po miesiącu przyjęcia do używania (art. 16h ust. 1 pkt 1 ustawy o CIT; bilansowo — art. 32 ust. 1 UoR, przyjęte w polityce).</item>
/// <item>Do miesiąca zrównania odpisów z wartością początkową lub miesiąca likwidacji/sprzedaży włącznie.</item>
/// <item>Liniowa: wartość początkowa × stawka / 12 (zaokrąglenie do grosza, ostatni odpis wyrównujący).</item>
/// <item>Degresywna: wartość netto na początek roku × stawka × współczynnik; przejście na liniową, gdy odpis liniowy ≥ degresywny (art. 16k ust. 1).</item>
/// <item>Jednorazowa: całość w miesiącu przyjęcia do używania.</item>
/// <item>Ulepszenie zwiększa wartość początkową od miesiąca następującego po miesiącu ulepszenia (art. 16g ust. 13).</item>
/// </list>
/// </summary>
public static class DepreciationCalculator
{
    public static IReadOnlyList<DepreciationPlanRow> Plan(FixedAsset a, DepreciationBasis basis, int maxMonths = 12 * 120)
    {
        var method = basis == DepreciationBasis.Bilansowa ? a.BookMethod : a.TaxMethod;
        var rate = basis == DepreciationBasis.Bilansowa ? a.BookRatePercent : a.TaxRatePercent;
        var factor = basis == DepreciationBasis.Bilansowa ? a.BookDegressiveFactor : a.TaxDegressiveFactor;
        var value = basis == DepreciationBasis.Bilansowa ? a.InitialValueBook : a.InitialValueTax;
        var accumulated = basis == DepreciationBasis.Bilansowa ? a.OpeningAccumulatedBook : a.OpeningAccumulatedTax;
        var rows = new List<DepreciationPlanRow>();
        if (method == DepreciationMethod.Brak || value <= 0) return rows;

        var serviceMonth = new DateOnly(a.PlacedInServiceDate.Year, a.PlacedInServiceDate.Month, 1);
        if (method == DepreciationMethod.Jednorazowa)
        {
            var amt = value - accumulated;
            if (amt > 0) rows.Add(new(serviceMonth.Year, serviceMonth.Month, value, amt, value, 0m, method));
            return rows;
        }

        var start = serviceMonth.AddMonths(1);
        if (a.OpeningAccumulatedAsOf is { } asOf)
        {
            var m = new DateOnly(asOf.Year, asOf.Month, 1).AddMonths(1);
            if (m > start) start = m;
        }

        DateOnly? endMonth = a.DisposalDate is { } dd ? new DateOnly(dd.Year, dd.Month, 1) : null;
        var improvements = a.Operations
            .Where(o => o.Type is FixedAssetOperationType.Ulepszenie or FixedAssetOperationType.CzesciowaLikwidacja or FixedAssetOperationType.AktualizacjaWyceny)
            .Select(o => (Effective: new DateOnly(o.Date.Year, o.Date.Month, 1).AddMonths(1),
                          Change: basis == DepreciationBasis.Bilansowa ? o.BookValueChange : o.TaxValueChange))
            .OrderBy(x => x.Effective).ToList();

        // wartości obowiązujące przed startem
        foreach (var imp in improvements.Where(i => i.Effective <= start)) value += imp.Change;

        var cursor = start;
        var useLinear = method == DepreciationMethod.Liniowa;
        decimal annualDegressive = 0, annualLinear = 0;
        var currentYear = -1;

        for (var i = 0; i < maxMonths; i++)
        {
            if (endMonth is { } e && cursor > e) break;
            foreach (var imp in improvements.Where(x => x.Effective == cursor && cursor != start)) value += imp.Change;
            var remaining = value - accumulated;
            if (remaining <= 0) break;

            decimal monthly;
            if (method == DepreciationMethod.Degresywna)
            {
                if (cursor.Year != currentYear)
                {
                    currentYear = cursor.Year;
                    annualLinear = Money.Round(value * rate / 100m);
                    annualDegressive = Money.Round((value - accumulated) * rate * factor / 100m);
                    if (!useLinear && annualLinear >= annualDegressive) useLinear = true;
                }
                monthly = useLinear ? Money.Round(annualLinear / 12m) : Money.Round(annualDegressive / 12m);
            }
            else
            {
                monthly = Money.Round(value * rate / 100m / 12m);
            }
            if (monthly <= 0) break;
            if (monthly > remaining) monthly = remaining;
            accumulated += monthly;
            rows.Add(new(cursor.Year, cursor.Month, value, monthly, accumulated, value - accumulated,
                method == DepreciationMethod.Degresywna && !useLinear ? DepreciationMethod.Degresywna : DepreciationMethod.Liniowa));
            cursor = cursor.AddMonths(1);
        }
        return rows;
    }

    public static decimal AmountFor(FixedAsset a, DepreciationBasis basis, int year, int month) =>
        Plan(a, basis).FirstOrDefault(r => r.Year == year && r.Month == month)?.Amount ?? 0m;
}
