using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>
/// Zestawienia do deklaracji i dokumenty roczne: CIT-8 z CIT-8/O, VAT-UE, IFT-2R/CIT-10Z, uchwały zgromadzenia.
/// Program wylicza kwoty z ksiąg; numery pól przenosi się według aktualnego wzoru formularza (wzory zmieniają się co roku).
/// Deklaracje wysyła się przez e-Deklaracje / e-Urząd Skarbowy — z podpisem podatnika.
/// </summary>
public sealed partial class ReportService
{
    public static readonly IReadOnlySet<string> EuCountries = new HashSet<string>
    {
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "EL", "GR", "ES", "FI", "FR", "HR", "HU", "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PT", "RO", "SE", "SI", "SK", "XI"
    };

    private void FormColumns(ReportDocument r) =>
        r.Columns.AddRange(new[] { new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn("Kwota", ColumnKind.Money, 1.3) });

    /// <summary>CIT-8 (zeznanie roczne) z załącznikiem CIT-8/O (odliczenia) — kwoty z uzgodnienia wyniku i zaliczek.</summary>
    public async Task<ReportDocument> Cit8Async(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == _user.CompanyId, ct);
        var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fy.Periods.Count, ct);
        var advances = await _db.CitAdvances.AsNoTracking().Where(a => a.FiscalYearId == fiscalYearId && a.IsApproved).ToListAsync(ct);
        var paid = advances.Sum(a => a.AdvanceDue);
        var deductions = await _db.CitAdjustments.AsNoTracking().Where(a => a.FiscalYearId == fiscalYearId && a.Type == CitAdjustmentType.OdliczenieOdDochodu).ToListAsync(ct);
        var r = await NewAsync("CIT-8 — zestawienie do zeznania rocznego (z CIT-8/O)", $"rok podatkowy {fy.StartDate.ToPl()} – {fy.EndDate.ToPl()}", false, ct);
        FormColumns(r);
        r.AddRow(new ReportRow("A", $"Podatnik: {company.FullName}, NIP {company.Nip}; urząd skarbowy: {company.TaxOfficeCode}", null) { IsHeader = true });
        r.AddRow(new ReportRow("D", "Dochód / strata ze źródła przychodów innych niż z zysków kapitałowych", null) { IsHeader = true });
        r.Add("D.1", "Przychody", rec.TaxRevenue);
        r.Add("D.2", "Koszty uzyskania przychodów", rec.TaxCosts);
        r.Add("D.3", "Dochód (przychody − koszty)", rec.Computation.Income);
        r.Add("D.4", "Strata (koszty − przychody)", rec.Computation.Loss);
        r.AddRow(new ReportRow("E", "Odliczenia od dochodu (szczegóły w CIT-8/O)", rec.Deductions) { IsTotal = true });
        r.AddRow(new ReportRow("F", "Podstawa opodatkowania (po zaokrągleniu do pełnych złotych)", rec.Computation.TaxBase) { IsTotal = true });
        r.Add("G.1", $"Stawka podatku: {rec.Computation.RatePercent:0}% (art. 19 ustawy o CIT)", null);
        r.Add("G.2", "Podatek należny", rec.Computation.Tax);
        r.Add("G.3", "Zaliczki należne za rok (zatwierdzone w programie)", paid);
        var diff = rec.Computation.Tax - paid;
        r.AddRow(new ReportRow("G.4", diff >= 0 ? "Różnica do zapłaty" : "Nadpłata", Math.Abs(diff)) { IsTotal = true });
        r.AddRow(new ReportRow("CIT-8/O", "Odliczenia od dochodu i podstawy opodatkowania", null) { IsHeader = true });
        foreach (var d in deductions) r.Add(null, $"{d.Description}{(d.LegalBasis is null ? "" : $" ({d.LegalBasis})")}", d.Amount);
        if (deductions.Count == 0) r.Add(null, "Brak odliczeń", 0m);
        r.AddRow(new ReportRow("Informacje dodatkowe", "Wynik bilansowy i korekty (do części informacyjnej zeznania)", null) { IsHeader = true });
        r.Add(null, "Przychody bilansowe", rec.BookRevenue);
        r.Add(null, "Koszty bilansowe", rec.BookCosts);
        r.Add(null, "Wynik finansowy brutto", rec.BookResultBeforeTax);
        foreach (var a in rec.Adjustments) r.AddRow(new ReportRow(null, $"{a.Item} ({a.Basis})", a.Amount) { Level = 1 });
        r.Notes.AddRange(rec.Warnings);
        foreach (var (acc, name, amt) in rec.ToAnalyze) r.Notes.Add($"Do analizy przed złożeniem: {acc} {name} — {Money.Format(amt)}");
        r.Notes.Add("Termin: do końca 3. miesiąca roku następnego (art. 27 ust. 1 ustawy o CIT); zapłata różnicy w tym samym terminie. Do zeznania dołącza się sprawozdanie finansowe (podmioty spoza KRS — do KAS).");
        r.Notes.Add("Numery pól nie są podawane — przenieś kwoty do odpowiednich pól aktualnego wzoru CIT-8 i CIT-8/O. Sprawdź ulgi (B+R, IP Box, PSA/CIT-ST, darowizny) i informację CIT/TP, jeśli dotyczą.");
        r.ControlsPassed = rec.ToAnalyze.Count == 0 && rec.Warnings.Count == 0;
        return r;
    }

    /// <summary>Pozycja VAT-UE: część (C — dostawy, D — nabycia, E — usługi), kraj, numer VAT bez prefiksu, kontrahent, wartość w pełnych złotych.</summary>
    public sealed record VatUeLine(string Part, string Country, string VatNumber, string Counterparty, decimal Value);

    public async Task<List<VatUeLine>> VatUeLinesAsync(DateOnly periodFrom, DateOnly periodTo, CancellationToken ct = default)
    {
        var p0 = new DateOnly(periodFrom.Year, periodFrom.Month, 1);
        var p1 = new DateOnly(periodTo.Year, periodTo.Month, 1);
        var entries = await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == _user.CompanyId && v.Period >= p0 && v.Period <= p1).ToListAsync(ct);
        IEnumerable<VatUeLine> Part(string code, IEnumerable<VatRegisterEntry> list) =>
            list.GroupBy(e => (e.CounterpartyCountry, Nip: (e.CounterpartyNip ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant())).OrderBy(g => g.Key.CounterpartyCountry).ThenBy(g => g.Key.Nip)
                .Select(g => new VatUeLine(code, g.Key.CounterpartyCountry,
                    g.Key.Nip.StartsWith(g.Key.CounterpartyCountry, StringComparison.OrdinalIgnoreCase) ? g.Key.Nip[g.Key.CounterpartyCountry.Length..] : g.Key.Nip,
                    g.First().CounterpartyName, Math.Round(g.Sum(e => e.NetPln), 0, MidpointRounding.AwayFromZero)));
        return Part("C", entries.Where(e => e.Register == VatRegisterType.Sprzedaz && e.TransactionType == VatTransactionType.WewnatrzwspolnotowaDostawaTowarow))
            .Concat(Part("D", entries.Where(e => e.Register == VatRegisterType.Zakup && e.TransactionType == VatTransactionType.WewnatrzwspolnotoweNabycieTowarow)))
            .Concat(Part("E", entries.Where(e => e.Register == VatRegisterType.Sprzedaz && e.JpkNetField == "K_12" && EuCountries.Contains(e.CounterpartyCountry))))
            .ToList();
    }

    /// <summary>VAT-UE: wewnątrzwspólnotowe dostawy (C), nabycia (D) i świadczenie usług (E) za miesiąc lub kwartał.</summary>
    public async Task<ReportDocument> VatUeAsync(DateOnly periodFrom, DateOnly periodTo, CancellationToken ct = default)
    {
        var p0 = new DateOnly(periodFrom.Year, periodFrom.Month, 1);
        var p1 = new DateOnly(periodTo.Year, periodTo.Month, 1);
        var lines = await VatUeLinesAsync(p0, p1, ct);
        var r = await NewAsync("VAT-UE — informacja podsumowująca", p0 == p1 ? $"{p0:MM.yyyy}" : $"{p0:MM.yyyy} – {p1:MM.yyyy}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Część"), new ReportColumn("Kod kraju"), new ReportColumn("Numer VAT kontrahenta", ColumnKind.Text, 1.5), new ReportColumn("Kontrahent", ColumnKind.Text, 2), new ReportColumn("Wartość (zł)", ColumnKind.Money) });
        foreach (var (code, title) in new[] { ("C", "Wewnątrzwspólnotowe dostawy towarów"), ("D", "Wewnątrzwspólnotowe nabycia towarów"), ("E", "Wewnątrzwspólnotowe świadczenie usług (art. 100 ust. 1 pkt 4)") })
        {
            r.AddRow(new ReportRow(code, title, null, null, null) { IsHeader = true });
            var part = lines.Where(l => l.Part == code).ToList();
            foreach (var l in part)
            {
                r.Add(code, l.Country, string.IsNullOrEmpty(l.VatNumber) ? "BRAK — uzupełnij" : l.VatNumber, l.Counterparty, l.Value);
                if (string.IsNullOrEmpty(l.VatNumber)) r.ControlsPassed = false;
            }
            if (part.Count == 0) r.Add(code, null, null, "brak transakcji", null);
        }
        r.Notes.Add("Termin: do 25. dnia miesiąca następującego po miesiącu (art. 100 ust. 3 ustawy o VAT). Kwoty w pełnych złotych, numery VAT bez prefiksu kraju — sprawdź je w VIES (Kontrahenci → Sprawdź).");
        r.Notes.Add("Część F (procedura magazynu typu call-off) — jeśli dotyczy, uzupełnij ręcznie.");
        return r;
    }

    /// <summary>Wypłaty na rzecz nierezydentów (do IFT-2R / CIT-10Z i oceny podatku u źródła).</summary>
    public async Task<ReportDocument> ForeignPaymentsAsync(int year, CancellationToken ct = default)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 1);
        var entries = await _db.VatRegister.AsNoTracking().Where(v => v.CompanyId == _user.CompanyId && v.Register == VatRegisterType.Zakup && v.Period >= from && v.Period <= to && v.CounterpartyCountry != "PL").ToListAsync(ct);
        var r = await NewAsync("Wypłaty na rzecz podmiotów zagranicznych (IFT-2R / CIT-10Z)", $"rok {year}", true, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Kraj"), new ReportColumn("Kontrahent", ColumnKind.Text, 2), new ReportColumn("Numer identyfikacyjny", ColumnKind.Text, 1.4), new ReportColumn("Rodzaj"),
            new ReportColumn("Liczba dokumentów", ColumnKind.Integer), new ReportColumn("Wartość netto (zł)", ColumnKind.Money),
            new ReportColumn("WHT 20% bez certyfikatu", ColumnKind.Money), new ReportColumn("WHT przy umowie (zwykle 0%)", ColumnKind.Money) });
        decimal services = 0, wht = 0;
        foreach (var g in entries.GroupBy(e => (e.CounterpartyCountry, e.CounterpartyName, e.CounterpartyNip, e.TransactionType)).OrderBy(g => g.Key.CounterpartyCountry))
        {
            var kind = g.Key.TransactionType switch
            {
                VatTransactionType.ImportUslug or VatTransactionType.ImportUslug28b => "usługi",
                VatTransactionType.WewnatrzwspolnotoweNabycieTowarow or VatTransactionType.ImportTowarow => "towary",
                _ => "inne"
            };
            var net = g.Sum(e => e.NetPln);
            // art. 21 ust. 1 pkt 2a ustawy o CIT: usługi niematerialne 20%; podatek wyliczany od kwoty brutto należności, w pełnych złotych
            var tax = kind == "usługi" ? Math.Round(net * 0.20m, 0, MidpointRounding.AwayFromZero) : 0m;
            if (kind == "usługi") { services += net; wht += tax; }
            r.Add(g.Key.CounterpartyCountry, g.Key.CounterpartyName, g.Key.CounterpartyNip, kind, g.Select(e => e.DocumentId).Distinct().Count(), net, tax, 0m);
        }
        r.AddRow(new ReportRow("RAZEM usługi", null, null, null, null, services, wht, 0m) { IsTotal = true });
        r.Notes.Add("Zestawienie pomocnicze: IFT-2R składa się dla należności z art. 21 ust. 1 i art. 22 ust. 1 ustawy o CIT (m.in. usługi doradcze, księgowe, reklamowe, przetwarzania danych, licencje, odsetki, dywidendy) — do końca 3. miesiąca roku następnego; CIT-10Z, gdy pobrano podatek u źródła.");
        r.Notes.Add("Kolumna „WHT 20%” — podatek do pobrania, gdy brak certyfikatu rezydencji kontrahenta (art. 21 ust. 1 pkt 2a i art. 26 ustawy o CIT): wpłata do 7. dnia miesiąca następującego po wypłacie, CIT-10Z do końca 3. miesiąca roku następnego. Przy certyfikacie i umowie o unikaniu podwójnego opodatkowania zwykle 0% — wówczas tylko IFT-2R. Dla wypłat ponad 2 mln zł rocznie do jednego podmiotu sprawdź mechanizm „pay and refund”.");
        r.Notes.Add("Zakupy towarów zwykle nie podlegają podatkowi u źródła. Ocenę, czy należność podlega WHT (certyfikat rezydencji, umowa o unikaniu podwójnego opodatkowania), wykonuje użytkownik.");
        return r;
    }

    /// <summary>Projekty uchwał zwyczajnego zgromadzenia: zatwierdzenie sprawozdania, podział zysku / pokrycie straty, absolutorium.</summary>
    public async Task<ReportDocument> AnnualResolutionsAsync(Guid fiscalYearId, string? profitDistribution = null, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == _user.CompanyId, ct);
        var sb = new StatementBuilder(_db);
        var bs = await sb.BalanceSheetAsync(fiscalYearId, fy.EndDate, ct);
        var pl = await sb.ProfitAndLossAsync(fiscalYearId, fy.EndDate, ct);
        var persons = (await _db.ResponsiblePersons.AsNoTracking().Where(p => p.CompanyId == _user.CompanyId).ToListAsync(ct))
            .Where(p => p.Role is ResponsibleRole.CzlonekZarzadu or ResponsibleRole.PrzewodniczacyRadyDyrektorow or ResponsibleRole.DyrektorWykonawczy)
            .Where(p => (p.From is null || p.From <= fy.EndDate) && (p.To is null || p.To >= fy.StartDate)).ToList();
        var (body, bodyGen, organ) = company.LegalForm switch
        {
            LegalForm.SpolkaAkcyjna => ("Zwyczajne Walne Zgromadzenie", "Zwyczajnego Walnego Zgromadzenia", "Zarządu i Rady Nadzorczej"),
            LegalForm.ProstaSpolkaAkcyjna => ("Zwyczajne Walne Zgromadzenie", "Zwyczajnego Walnego Zgromadzenia", "Zarządu (Rady Dyrektorów)"),
            _ => ("Zwyczajne Zgromadzenie Wspólników", "Zwyczajnego Zgromadzenia Wspólników", "Zarządu")
        };
        var net = pl.Get("R.L");
        var r = await NewAsync($"Projekty uchwał {bodyGen}", $"rok obrotowy {fy.StartDate.ToPl()} – {fy.EndDate.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Nr"), new ReportColumn("Treść", ColumnKind.Text, 6) });
        void U(string no, string title, params string[] paragraphs)
        {
            r.AddRow(new ReportRow(no, title) { IsHeader = true });
            foreach (var p in paragraphs) r.Add(null, p);
        }
        var deadline = fy.EndDate.AddMonths(6);
        U("1/" + fy.Name, $"Uchwała {bodyGen} {company.FullName} w sprawie zatwierdzenia sprawozdania finansowego za rok obrotowy {fy.Name}",
            $"§1. Na podstawie art. 53 ust. 1 ustawy o rachunkowości {body} zatwierdza sprawozdanie finansowe za rok obrotowy od {fy.StartDate.ToPl()} do {fy.EndDate.ToPl()}, obejmujące: " +
            $"bilans sporządzony na dzień {fy.EndDate.ToPl()}, który po stronie aktywów i pasywów wykazuje sumę {Money.Format(bs.Get("AKTYWA"))} zł; " +
            $"rachunek zysków i strat wykazujący {(net >= 0 ? "zysk" : "stratę")} netto w wysokości {Money.Format(Math.Abs(net))} zł; informację dodatkową" +
            "[; zestawienie zmian w kapitale własnym; rachunek przepływów pieniężnych — jeśli sporządzane].",
            "§2. Uchwała wchodzi w życie z dniem podjęcia.");
        U("2/" + fy.Name, net >= 0 ? $"Uchwała w sprawie podziału zysku za rok obrotowy {fy.Name}" : $"Uchwała w sprawie pokrycia straty za rok obrotowy {fy.Name}",
            net >= 0
                ? $"§1. {body} postanawia zysk netto w kwocie {Money.Format(net)} zł przeznaczyć {profitDistribution ?? "[DO UZUPEŁNIENIA: na kapitał zapasowy / na wypłatę dywidendy w kwocie … zł; dzień dywidendy …; termin wypłaty …]"}."
                : $"§1. {body} postanawia stratę netto w kwocie {Money.Format(-net)} zł pokryć {profitDistribution ?? "[DO UZUPEŁNIENIA: z zysków lat przyszłych / z kapitału zapasowego]"}.",
            "§2. Uchwała wchodzi w życie z dniem podjęcia.");
        var no = 3;
        foreach (var p in persons)
            U($"{no++}/{fy.Name}", $"Uchwała w sprawie udzielenia absolutorium — {p.FullName}",
                $"§1. {body} udziela {p.FullName} ({p.Role switch { ResponsibleRole.CzlonekZarzadu => "członek zarządu", ResponsibleRole.PrzewodniczacyRadyDyrektorow => "przewodniczący rady dyrektorów", _ => "dyrektor wykonawczy" }}) absolutorium z wykonania obowiązków w okresie od {fy.StartDate.ToPl()} do {fy.EndDate.ToPl()}.",
                "§2. Uchwała wchodzi w życie z dniem podjęcia.");
        if (persons.Count == 0) U($"{no}/{fy.Name}", $"Uchwała w sprawie udzielenia absolutorium członkom {organ}", "[DO UZUPEŁNIENIA — imiona i nazwiska, funkcje, okres pełnienia funkcji]");
        r.Notes.Add($"Zatwierdzenie — najpóźniej do {deadline.ToPl()} (6 miesięcy od dnia bilansowego, art. 53 ust. 1 UoR; art. 231 KSH / art. 395 KSH / art. 300^86 KSH).");
        r.Notes.Add("W ciągu 15 dni od zatwierdzenia: złożenie w e-KRS sprawozdania, uchwały zatwierdzającej i uchwały o podziale zysku / pokryciu straty (art. 69 ust. 1 UoR); sprawozdanie z badania — jeśli dotyczy.");
        r.Notes.Add("Projekty — dostosuj do umowy/statutu spółki (kworum, sposób głosowania, protokół notarialny w S.A./P.S.A.).");
        r.ControlsPassed = profitDistribution is not null && persons.Count > 0;
        return r;
    }
}
