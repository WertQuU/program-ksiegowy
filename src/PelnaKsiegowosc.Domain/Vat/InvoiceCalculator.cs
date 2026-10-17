using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;

namespace PelnaKsiegowosc.Domain.Vat;

/// <summary>
/// Kalkulacja faktury: wartości pozycji (netto = ilość × cena, zaokrąglone do grosza), podsumowanie wg stawek
/// (VAT liczony od sumy netto w stawce), przeliczenie na PLN po kursie (art. 31a ustawy o VAT: kurs średni NBP
/// z ostatniego dnia roboczego poprzedzającego dzień powstania obowiązku podatkowego — kurs wprowadza użytkownik).
/// </summary>
public static class InvoiceCalculator
{
    public static void Recalculate(Document doc)
    {
        if (doc.Lines.Count == 0) throw new DomainException("Faktura musi zawierać co najmniej jedną pozycję.");
        var date = doc.OperationDate == default ? doc.IssueDate : doc.OperationDate;
        var rate = doc.Currency == "PLN" ? 1m : doc.ExchangeRate;
        if (rate <= 0) throw new DomainException("Podaj kurs waluty.");

        var previousDeduction = doc.VatLines.ToDictionary(v => v.VatRateCode, v => (v.DeductionPercent, v.IsFixedAsset));
        foreach (var l in doc.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.Name)) throw new DomainException($"Pozycja {l.LineNo}: nazwa towaru/usługi jest wymagana.");
            if (l.Quantity == 0) throw new DomainException($"Pozycja {l.LineNo}: ilość nie może być zerowa.");
            if (!doc.IsCorrection && l.Quantity < 0) throw new DomainException($"Pozycja {l.LineNo}: ilość ujemna dopuszczalna jest tylko w korekcie.");
            if (!doc.IsCorrection && l.UnitNetPrice < 0) throw new DomainException($"Pozycja {l.LineNo}: cena ujemna dopuszczalna jest tylko w korekcie.");
            var vr = VatRules.Get(l.VatRateCode, date);
            l.NetAmount = Money.Round(l.Quantity * l.UnitNetPrice);
            l.VatAmount = VatRules.TaxFromNet(l.NetAmount, vr);
            l.GrossAmount = l.NetAmount + l.VatAmount;
        }

        doc.VatLines.Clear();
        foreach (var g in doc.Lines.GroupBy(l => l.VatRateCode).OrderBy(g => g.Key))
        {
            var vr = VatRules.Get(g.Key, date);
            var net = g.Sum(x => x.NetAmount);
            var vat = VatRules.TaxFromNet(net, vr);
            var netPln = Money.ToPln(net, rate);
            var vatPln = doc.Currency == "PLN" ? vat : Money.ToPln(vat, rate);
            var vl = new DocumentVatLine
            {
                DocumentId = doc.Id,
                VatRateCode = g.Key,
                NetAmount = net,
                VatAmount = vat,
                NetAmountPln = netPln,
                VatAmountPln = vatPln
            };
            if (previousDeduction.TryGetValue(g.Key, out var prev))
            {
                vl.DeductionPercent = prev.DeductionPercent;
                vl.IsFixedAsset = prev.IsFixedAsset;
            }
            vl.DeductibleVatPln = doc.IsPurchase ? Money.Round(vatPln * vl.DeductionPercent / 100m) : 0m;
            doc.VatLines.Add(vl);
        }

        doc.NetAmount = doc.VatLines.Sum(v => v.NetAmount);
        doc.VatAmount = doc.VatLines.Sum(v => v.VatAmount);
        doc.GrossAmount = doc.NetAmount + doc.VatAmount;
        doc.NetAmountPln = doc.VatLines.Sum(v => v.NetAmountPln);
        doc.VatAmountPln = doc.VatLines.Sum(v => v.VatAmountPln);
        doc.GrossAmountPln = doc.NetAmountPln + doc.VatAmountPln;
    }

    /// <summary>Ustawia procent odliczenia VAT dla stawki (zakupy) i przelicza kwotę odliczenia.</summary>
    public static void SetDeduction(Document doc, string rateCode, decimal percent, bool isFixedAsset)
    {
        if (percent is < 0 or > 100) throw new DomainException("Procent odliczenia musi mieścić się w przedziale 0–100.");
        var vl = doc.VatLines.FirstOrDefault(v => v.VatRateCode == rateCode) ?? throw new DomainException($"Brak stawki {rateCode} na dokumencie.");
        vl.DeductionPercent = percent;
        vl.IsFixedAsset = isFixedAsset;
        vl.DeductibleVatPln = Money.Round(vl.VatAmountPln * percent / 100m);
    }
}
