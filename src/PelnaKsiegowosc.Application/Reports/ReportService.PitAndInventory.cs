using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Reports;

public enum PitTaxForm { Skala, Liniowy }

/// <summary>Udział właściciela/wspólnika w dochodzie (art. 8 ustawy o PIT) i dane do PIT-36 / PIT-36L.</summary>
public sealed record PitPartner(string Name, decimal SharePercent, PitTaxForm Form, decimal SocialContributions, decimal HealthContributions, decimal AdvancesPaid)
{
    /// <summary>Wiersz „Jan Kowalski; 50; liniowy; 15000; 5000; 2000” (składki społeczne, zdrowotne, wpłacone zaliczki — opcjonalne).</summary>
    public static PitPartner ParseLine(string line)
    {
        var p = line.Split(';').Select(x => x.Trim()).ToArray();
        if (p.Length < 3 || string.IsNullOrWhiteSpace(p[0])) throw new DomainException($"Wiersz „{line}”: podaj co najmniej: imię i nazwisko; udział %; skala/liniowy.");
        decimal D(int i) => p.Length > i && p[i].Length > 0 ? decimal.Parse(p[i].Replace(" ", "").Replace(',', '.'), CultureInfo.InvariantCulture) : 0m;
        var form = p[2].StartsWith("lin", StringComparison.OrdinalIgnoreCase) ? PitTaxForm.Liniowy
            : p[2].StartsWith("ska", StringComparison.OrdinalIgnoreCase) ? PitTaxForm.Skala : throw new DomainException($"Wiersz „{line}”: forma opodatkowania to „skala” albo „liniowy”.");
        return new PitPartner(p[0], D(1), form, D(3), D(4), D(5));
    }
}

/// <summary>Parametry PIT dla dochodów z działalności (rok 2026).</summary>
public static class PitBusinessRules
{
    public const decimal Threshold = 120000m;
    public const decimal TaxReduction = 3600m;
    public const decimal LinearRate = 19m;
    /// <summary>Limit odliczenia składki zdrowotnej przy podatku liniowym — 2026 r. (DO POTWIERDZENIA w obwieszczeniu MF).</summary>
    public const decimal LinearHealthDeductionLimit = 14100m;

    public static decimal Tax(decimal taxBase, PitTaxForm form)
    {
        if (taxBase <= 0) return 0;
        var tax = form == PitTaxForm.Liniowy
            ? taxBase * LinearRate / 100m
            : Math.Min(taxBase, Threshold) * 0.12m + Math.Max(0, taxBase - Threshold) * 0.32m - TaxReduction;
        return Math.Max(0, Math.Round(tax, 0, MidpointRounding.AwayFromZero));
    }
}

public sealed partial class ReportService
{
    /// <summary>PIT-36 / PIT-36L: dochód z działalności (z ewidencji podatkowej ksiąg) podzielony między właściciela/wspólników.</summary>
    public async Task<ReportDocument> PitPartnersAsync(Guid fiscalYearId, IReadOnlyList<PitPartner> partners, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        if (partners.Count == 0) throw new DomainException("Podaj właściciela lub wspólników (imię i nazwisko; udział %; skala/liniowy).");
        if (partners.Sum(p => p.SharePercent) != 100m) throw new DomainException("Suma udziałów w zysku musi wynosić 100%.");
        var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fy.Periods.Count, ct);
        var r = await NewAsync("PIT-36 / PIT-36L — dochód z działalności właściciela / wspólników", $"rok {fy.Name}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Podatnik", ColumnKind.Text, 1.6), new ReportColumn("Udział %", ColumnKind.Decimal), new ReportColumn("Forma"), new ReportColumn("Przychód", ColumnKind.Money),
            new ReportColumn("Koszty", ColumnKind.Money), new ReportColumn("Dochód", ColumnKind.Money), new ReportColumn("Składki społ.", ColumnKind.Money),
            new ReportColumn("Odlicz. zdrowotnej", ColumnKind.Money), new ReportColumn("Podstawa", ColumnKind.Money), new ReportColumn("Podatek", ColumnKind.Money),
            new ReportColumn("Zaliczki", ColumnKind.Money), new ReportColumn("Do zapłaty / zwrotu", ColumnKind.Money)
        });
        foreach (var p in partners)
        {
            var share = p.SharePercent / 100m;
            var rev = Math.Round(rec.TaxRevenue * share, 2);
            var cost = Math.Round(rec.TaxCosts * share, 2);
            var income = rev - cost;
            var healthDeduction = p.Form == PitTaxForm.Liniowy ? Math.Min(p.HealthContributions, PitBusinessRules.LinearHealthDeductionLimit) : 0m;
            var baseAmount = Math.Max(0, Math.Round(income - p.SocialContributions - healthDeduction, 0, MidpointRounding.AwayFromZero));
            var tax = PitBusinessRules.Tax(baseAmount, p.Form);
            r.Add(p.Name, p.SharePercent, p.Form == PitTaxForm.Liniowy ? "PIT-36L (19%)" : "PIT-36 (skala)", rev, cost, income, p.SocialContributions, healthDeduction, baseAmount, tax, p.AdvancesPaid, tax - p.AdvancesPaid);
        }
        r.Notes.Add($"Przychody i koszty podatkowe z uzgodnienia wyniku (zespół 4/7, korekty, amortyzacja podatkowa): przychód {Money.Format(rec.TaxRevenue)} zł, koszty {Money.Format(rec.TaxCosts)} zł.");
        r.Notes.Add("Zaliczki PIT od dochodów z działalności — do 20. dnia następnego miesiąca; zeznanie PIT-36/PIT-36L do 30 kwietnia (art. 44, art. 45 ustawy o PIT).");
        r.Notes.Add($"Skala: 12% do 120 000 zł, 32% nadwyżka, kwota zmniejszająca 3600 zł; liniowy 19% z odliczeniem składki zdrowotnej do {Money.Format(PitBusinessRules.LinearHealthDeductionLimit)} zł (limit 2026 — do potwierdzenia). " +
                    "Nie uwzględniono ulg (np. na dzieci przy skali, IKZE, darowizny) ani innych źródeł dochodu podatnika.");
        r.Notes.AddRange(rec.Warnings);
        return r;
    }

    /// <summary>Inwentaryzacja (art. 26 UoR): metody dla kont bilansowych z saldami, terminy i lista potwierdzeń sald.</summary>
    public async Task<ReportDocument> InventoryPlanAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        var r = await NewAsync("Inwentaryzacja — zestawienie składników i metod (art. 26 UoR)", $"na dzień {asOf.ToPl()}", true, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Konto"), new ReportColumn("Nazwa", ColumnKind.Text, 2), new ReportColumn("Saldo Wn", ColumnKind.Money), new ReportColumn("Saldo Ma", ColumnKind.Money),
            new ReportColumn("Metoda", ColumnKind.Text, 1.6), new ReportColumn("Termin / podstawa", ColumnKind.Text, 2.2) });
        foreach (var t in tb.Where(t => t.IsLeaf && !t.Account.IsResultAccount && t.Account.Type != AccountType.Pozabilansowe && t.TotalDebit != t.TotalCredit).OrderBy(t => t.Account.Number, StringComparer.Ordinal))
        {
            var n = t.Account.Number;
            var (method, when) = n switch
            {
                _ when n.StartsWith("10") => ("spis z natury (gotówka)", "na ostatni dzień roku — art. 26 ust. 1 pkt 1"),
                _ when n.StartsWith("13") => ("potwierdzenie salda (bank)", "na ostatni dzień roku — art. 26 ust. 1 pkt 2"),
                _ when n.StartsWith("3") => ("spis z natury", "w ostatnim kwartale roku do 15. dnia następnego roku — art. 26 ust. 3 pkt 1"),
                _ when n.StartsWith("01") && !n.StartsWith("010-01") && !n.StartsWith("010-02") => ("spis z natury (raz na 4 lata) / weryfikacja", "art. 26 ust. 3 pkt 3"),
                _ when n.StartsWith("200") || n.StartsWith("201") || (t.Account.IsSettlementAccount && t.TotalDebit > t.TotalCredit && !n.StartsWith("245")) => ("potwierdzenie salda", "w ostatnim kwartale roku do 15. dnia następnego roku — art. 26 ust. 1 pkt 2, ust. 3 pkt 1"),
                _ => ("weryfikacja (porównanie z dokumentami)", "na dzień bilansowy — art. 26 ust. 1 pkt 3")
            };
            r.Add(n, t.Account.Name, Math.Max(0, t.TotalDebit - t.TotalCredit), Math.Max(0, t.TotalCredit - t.TotalDebit), method, when);
        }
        r.Notes.Add("Potwierdzeniu nie podlegają m.in. należności sporne i wątpliwe, rozrachunki z osobami fizycznymi nieprowadzącymi działalności oraz publicznoprawne — te weryfikuje się (art. 26 ust. 1 pkt 3).");
        r.Notes.Add("Różnice inwentaryzacyjne ujmij w księgach roku, na który przeprowadzono inwentaryzację (art. 27 ust. 2 UoR) — sekcja Księgowość, zapis PK z protokołem jako dowodem.");
        return r;
    }

    /// <summary>Wezwania do potwierdzenia sald należności (i opcjonalnie zobowiązań) — jeden dokument z sekcją dla każdego kontrahenta.</summary>
    public async Task<ReportDocument> BalanceConfirmationsAsync(DateOnly asOf, bool includePayables, CancellationToken ct = default)
    {
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == _user.CompanyId, ct);
        var settlements = new SettlementService(_db, _user, _clock);
        var rows = (await settlements.AgingAsync(OpenItemSide.Naleznosc, asOf, false, null, ct)).Select(x => (Side: "należność", x)).ToList();
        if (includePayables) rows.AddRange((await settlements.AgingAsync(OpenItemSide.Zobowiazanie, asOf, false, null, ct)).Select(x => (Side: "zobowiązanie", x)));
        var r = await NewAsync("Potwierdzenia sald", $"stan na {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Dokument", ColumnKind.Text, 1.6), new ReportColumn("Data", ColumnKind.Date), new ReportColumn("Termin", ColumnKind.Date), new ReportColumn("Rodzaj"), new ReportColumn("Kwota do zapłaty (zł)", ColumnKind.Money) });
        foreach (var g in rows.GroupBy(x => x.x.CounterpartyName).OrderBy(g => g.Key))
        {
            r.AddRow(new ReportRow($"Do: {g.Key}", null, null, null, null) { IsHeader = true });
            r.Add($"{company.FullName} (NIP {company.Nip}) prosi o potwierdzenie salda na dzień {asOf.ToPl()}:", null, null, null, null);
            foreach (var (side, x) in g) r.Add(x.Reference, x.DocumentDate, x.DueDate, side, x.RemainingPln);
            var receivable = g.Where(x => x.Side == "należność").Sum(x => x.x.RemainingPln);
            var payable = g.Where(x => x.Side == "zobowiązanie").Sum(x => x.x.RemainingPln);
            r.AddRow(new ReportRow("Saldo według naszych ksiąg", null, null, receivable >= payable ? "na naszą korzyść" : "na Państwa korzyść", Math.Abs(receivable - payable)) { IsTotal = true });
            r.Add("Potwierdzamy zgodność salda / Saldo niezgodne — nasze saldo: ………… zł.   Data, podpis i pieczęć: ……………………", null, null, null, null);
        }
        if (rows.Count == 0) r.Notes.Add("Brak otwartych rozrachunków na wskazany dzień.");
        r.Notes.Add("Wydrukuj i wyślij kontrahentom (lub prześlij PDF e-mailem). Potwierdzenia przechowuj w dokumentacji inwentaryzacji.");
        return r;
    }

    /// <summary>Protokół inwentaryzacji kasy: saldo księgowe vs stan policzony (różnica do zaksięgowania).</summary>
    public async Task<ReportDocument> CashCountAsync(Guid fiscalYearId, DateOnly asOf, string cashAccount, decimal counted, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        var acc = tb.FirstOrDefault(t => t.Account.Number == cashAccount) ?? throw new DomainException($"Brak konta {cashAccount} w planie kont.");
        var book = acc.TotalDebit - acc.TotalCredit;
        var r = await NewAsync("Protokół inwentaryzacji gotówki w kasie", $"na dzień {asOf.ToPl()}; konto {cashAccount}", false, ct);
        FormColumns(r);
        r.Add("1", "Stan gotówki według ksiąg", book);
        r.Add("2", "Stan gotówki stwierdzony w spisie", counted);
        var diff = counted - book;
        r.AddRow(new ReportRow("3", diff == 0 ? "Różnica" : diff > 0 ? "Nadwyżka (zaksięguj: Wn kasa / Ma 760 pozostałe przychody operacyjne)" : "Niedobór (zaksięguj: Wn 240 rozrachunki z osobą odpowiedzialną lub 761 / Ma kasa)", Math.Abs(diff)) { IsTotal = true });
        r.Add(null, "Komisja inwentaryzacyjna: ………………………   Osoba materialnie odpowiedzialna: ………………………", null);
        r.ControlsPassed = diff == 0;
        return r;
    }
}
