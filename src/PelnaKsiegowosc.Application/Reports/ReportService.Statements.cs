using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Reporting;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>Opisowa część informacji dodatkowej, której nie da się wyliczyć z ksiąg — wpisuje ją użytkownik.</summary>
public sealed record NotesSection(string Key, string Title, string DefaultText);

/// <summary>Teksty informacji dodatkowej (i wprowadzenia do sprawozdania) zapisywane per rok obrotowy.</summary>
public sealed class FinancialStatementNotes
{
    public static readonly IReadOnlyList<NotesSection> Sections = new NotesSection[]
    {
        new("kontynuacja", "Założenie kontynuacji działalności (wprowadzenie, art. 5 ust. 2 UoR)",
            "Sprawozdanie finansowe sporządzono przy założeniu kontynuowania działalności gospodarczej przez jednostkę w dającej się przewidzieć przyszłości. Nie są znane okoliczności wskazujące na zagrożenie kontynuowania działalności."),
        new("zasady", "Omówienie przyjętych zasad rachunkowości (wprowadzenie)",
            "Zasady wyceny aktywów i pasywów, pomiaru wyniku finansowego i sporządzania sprawozdania opisano w zatwierdzonej polityce rachunkowości. Środki trwałe i WNiP wyceniono według cen nabycia pomniejszonych o odpisy amortyzacyjne; należności i zobowiązania — w kwocie wymaganej zapłaty; środki pieniężne — według wartości nominalnej."),
        new("zmiany-zasad", "Zmiany zasad rachunkowości i korekty błędów lat ubiegłych", "W roku obrotowym nie zmieniano zasad rachunkowości i nie korygowano błędów lat ubiegłych."),
        new("zatrudnienie", "Przeciętne zatrudnienie z podziałem na grupy zawodowe", "[DO UZUPEŁNIENIA — np. 0 etatów; zarząd pełni funkcję bez wynagrodzenia]"),
        new("organy", "Wynagrodzenia, zaliczki, pożyczki i gwarancje dla członków organów", "Nie wypłacano wynagrodzeń ani nie udzielano zaliczek, pożyczek i gwarancji członkom organów jednostki."),
        new("warunkowe", "Zobowiązania warunkowe, gwarancje, poręczenia, zabezpieczenia na majątku", "Jednostka nie posiada zobowiązań warunkowych ani zobowiązań zabezpieczonych na jej majątku."),
        new("powiazane", "Transakcje z podmiotami powiązanymi zawarte na warunkach innych niż rynkowe", "Nie wystąpiły."),
        new("po-dniu-bilansowym", "Istotne zdarzenia po dniu bilansowym", "Po dniu bilansowym nie wystąpiły zdarzenia, które wymagałyby ujęcia w sprawozdaniu finansowym."),
        new("podzial-wyniku", "Propozycja podziału zysku lub pokrycia straty", "[DO UZUPEŁNIENIA — np. zysk netto przeznaczyć na kapitał zapasowy / stratę pokryć z zysków lat przyszłych]"),
        new("inne", "Inne informacje mogące istotnie wpłynąć na ocenę sytuacji jednostki", "Brak."),
    };

    private readonly SettingsService _settings;
    private readonly Guid _companyId;

    public FinancialStatementNotes(SettingsService settings, Guid companyId)
    {
        _settings = settings;
        _companyId = companyId;
    }

    private static string Key(string yearName) => $"informacja-dodatkowa.{yearName}";

    public async Task<Dictionary<string, string>> GetAsync(string yearName, CancellationToken ct = default)
    {
        var stored = await _settings.GetAsync(_companyId, Key(yearName), () => new Dictionary<string, string>(), ct);
        return Sections.ToDictionary(s => s.Key, s => stored.TryGetValue(s.Key, out var t) && !string.IsNullOrWhiteSpace(t) ? t : s.DefaultText);
    }

    public Task SaveAsync(string yearName, Dictionary<string, string> texts, CancellationToken ct = default) =>
        _settings.SetAsync(_companyId, Key(yearName), texts.Where(t => Sections.Any(s => s.Key == t.Key)).ToDictionary(t => t.Key, t => t.Value.Trim()), ct);
}

public sealed partial class ReportService
{
    public Task<CostAllocationKey> CostAllocationKeyAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, CostAllocationKey.SettingsKey, () => CostAllocationKey.Default, ct);

    public async Task SaveCostAllocationKeyAsync(CostAllocationKey key, CancellationToken ct = default)
    {
        key.Validate();
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, CostAllocationKey.SettingsKey, key, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<ProfitAndLossVariant> VariantAsync(CancellationToken ct) =>
        (await _db.AccountingPolicies.AsNoTracking().Where(p => p.CompanyId == _user.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct))?.ProfitAndLossVariant
        ?? ProfitAndLossVariant.Porownawczy;

    /// <summary>RZiS w wariancie przyjętym w polityce rachunkowości.</summary>
    public async Task<ReportDocument> ProfitAndLossByPolicyAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default) =>
        await VariantAsync(ct) == ProfitAndLossVariant.Kalkulacyjny
            ? await ProfitAndLossCalculationAsync(fiscalYearId, asOf, ct)
            : await ProfitAndLossAsync(fiscalYearId, asOf, ct);

    public async Task<ReportDocument> ProfitAndLossCalculationAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var key = await CostAllocationKeyAsync(ct);
        var sb = new StatementBuilder(_db);
        var cur = await sb.ProfitAndLossCalculationAsync(fiscalYearId, asOf, key, ct);
        var prevYear = await sb.PreviousYearAsync(fiscalYearId, ct);
        var prev = prevYear is null ? null : await sb.ProfitAndLossCalculationAsync(prevYear.Id, prevYear.EndDate, key, ct);
        var r = await NewAsync("Rachunek zysków i strat (wariant kalkulacyjny)", $"{fy.StartDate.ToPl()} – {asOf.ToPl()}", false, ct);
        r.Filters.Add(("Klucz podziału kosztów", key.ToString()));
        r.Columns.AddRange(new[] { new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn("Rok bieżący", ColumnKind.Money, 1.2), new ReportColumn("Rok poprzedni", ColumnKind.Money, 1.2) });
        foreach (var l in FinancialStatementLayout.Flatten(FinancialStatementLayout.ProfitLossCalculation))
            r.AddRow(new ReportRow(Label(l.Code), l.Label, cur.Get(l.Code), prev?.Get(l.Code) ?? 0m) { Level = l.Depth, IsHeader = l.Children.Count > 0, IsTotal = l.Formula is not null });
        r.Notes.AddRange(cur.Warnings);
        var approval = await new FinancialStatementApprovals(_db, _user, _clock).CostKeyApprovalAsync(key, ct);
        if (approval is null)
            r.Notes.Add($"UWAGA: klucz podziału kosztów ({key}) NIE jest zatwierdzony z opisem metodologii — pozycje kosztów (koszt wytworzenia, sprzedaży, zarządu) mają charakter szacunkowy i mogą być istotnie zniekształcone; sprawozdanie nie zostanie oznaczone jako gotowe do podpisu.");
        else
            r.Notes.Add($"Klucz zatwierdzony {approval.ApprovedOn.ToPl()} ({approval.ApprovedBy}); metodologia: {approval.Methodology}. Klucz weryfikuje się okresowo.");
        r.Notes.Add($"Koszty rodzajowe (zespół 4, bez wartości sprzedanych towarów) przypisano do kosztu wytworzenia, kosztów sprzedaży i ogólnego zarządu kluczem: {key}. " +
                    "Jeżeli jednostka prowadzi konta zespołu 5, rzeczywiste przeznaczenie kosztów wynika z tych kont.");
        r.ControlsPassed = cur.Warnings.Count == 0 && approval is not null;
        return r;
    }

    public async Task<ReportDocument> EquityChangesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var sb = new StatementBuilder(_db);
        var cur = await sb.EquityChangesAsync(fiscalYearId, asOf, ct);
        var prevYear = await sb.PreviousYearAsync(fiscalYearId, ct);
        var prev = prevYear is null ? null : await sb.EquityChangesAsync(prevYear.Id, prevYear.EndDate, ct);
        var r = await NewAsync("Zestawienie zmian w kapitale (funduszu) własnym", $"{fy.StartDate.ToPl()} – {asOf.ToPl()}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 3), new ReportColumn("Na początek", ColumnKind.Money), new ReportColumn("Zwiększenia", ColumnKind.Money),
            new ReportColumn("Zmniejszenia", ColumnKind.Money), new ReportColumn("Na koniec", ColumnKind.Money), new ReportColumn("Na koniec — rok poprz.", ColumnKind.Money)
        });
        r.AddRow(new ReportRow("I.", "Kapitał (fundusz) własny na początek okresu (BO)", cur.Opening, null, null, null, prev?.Opening) { IsTotal = true });
        var i = 1;
        foreach (var row in cur.Rows)
            r.Add($"{i++}.", row.Label, row.Opening, row.Increase, row.Decrease, row.Closing, prev?.Rows.FirstOrDefault(p => p.Code == row.Code)?.Closing);
        r.AddRow(new ReportRow("II.", "Kapitał (fundusz) własny na koniec okresu (BZ)", null, null, null, cur.Closing, prev?.Closing) { IsTotal = true });
        r.AddRow(new ReportRow("III.", "Kapitał (fundusz) własny po uwzględnieniu proponowanego podziału zysku (pokrycia straty)", null, null, null, cur.Closing, prev?.Closing) { IsTotal = true });
        r.Notes.AddRange(cur.Warnings);
        r.Notes.Add("Stan początkowy — z zapisów bilansu otwarcia; zwiększenia/zmniejszenia — obroty Ma/Wn kont kapitałowych w roku. Szczegółowe zmiany (np. emisja udziałów, podział zysku) opisz w informacji dodatkowej.");
        r.ControlsPassed = cur.Warnings.Count == 0;
        return r;
    }

    public async Task<ReportDocument> CashFlowAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var sb = new StatementBuilder(_db);
        var cur = await sb.CashFlowAsync(fiscalYearId, asOf, ct);
        var prevYear = await sb.PreviousYearAsync(fiscalYearId, ct);
        var prev = prevYear is null ? null : await sb.CashFlowAsync(prevYear.Id, prevYear.EndDate, ct);
        var r = await NewAsync("Rachunek przepływów pieniężnych (metoda pośrednia)", $"{fy.StartDate.ToPl()} – {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn("Rok bieżący", ColumnKind.Money, 1.2), new ReportColumn("Rok poprzedni", ColumnKind.Money, 1.2) });
        foreach (var l in cur.Lines)
        {
            var header = l.Level == 0 && !l.IsTotal && l.Code.Length == 1 && l.Code[0] <= 'C';
            r.AddRow(new ReportRow(l.Code, l.Label, header ? null : l.Amount, header ? null : prev?.Get(l.Code) ?? 0m) { Level = l.Level, IsHeader = header, IsTotal = l.IsTotal });
        }
        r.Notes.AddRange(cur.Warnings);
        r.Notes.Add("Metoda pośrednia: wynik netto skorygowany o amortyzację i zmiany pozycji bilansu (BO → stan na dzień). Pozycje inwestycyjne i finansowe prezentowane per saldo — " +
                    "jeżeli jednostka podlega badaniu (art. 64 UoR), rozdziel wpływy i wydatki zgodnie z załącznikiem nr 1 do UoR. Jednostki małe/mikro mogą nie sporządzać RPP (art. 50 ust. 3, art. 48b UoR).");
        r.ControlsPassed = cur.Warnings.Count == 0;
        return r;
    }

    /// <summary>
    /// Informacja dodatkowa (dodatkowe informacje i objaśnienia) — tabele wyliczane z ksiąg i ewidencji (środki trwałe,
    /// należności i zobowiązania, przychody, podatek dochodowy) oraz opisy wprowadzone przez użytkownika.
    /// </summary>
    public async Task<ReportDocument> NotesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == _user.CompanyId, ct);
        var policy = await _db.AccountingPolicies.AsNoTracking().Where(p => p.CompanyId == _user.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
        var texts = await new FinancialStatementNotes(new SettingsService(_db, _clock), _user.CompanyId).GetAsync(fy.Name, ct);
        var sb = new StatementBuilder(_db);
        var bs = await sb.BalanceSheetAsync(fiscalYearId, asOf, ct);
        var pl = await sb.ProfitAndLossAsync(fiscalYearId, asOf, ct);
        var r = await NewAsync("Informacja dodatkowa (dodatkowe informacje i objaśnienia)", $"{fy.StartDate.ToPl()} – {asOf.ToPl()}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn("Kol. 1", ColumnKind.Money), new ReportColumn("Kol. 2", ColumnKind.Money),
            new ReportColumn("Kol. 3", ColumnKind.Money), new ReportColumn("Kol. 4", ColumnKind.Money)
        });
        void Text(string no, string title, string body)
        {
            r.AddRow(new ReportRow(no, title, null, null, null, null) { IsHeader = true });
            foreach (var para in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                r.Add(null, para, null, null, null, null);
        }
        void Header(string no, string title, params string[] cols) =>
            r.AddRow(new ReportRow(no, $"{title} [{string.Join(" | ", cols)}]", null, null, null, null) { IsHeader = true });

        // Wprowadzenie
        Text("W.1", "Dane jednostki", $"{company.FullName}, NIP {company.Nip}{(string.IsNullOrEmpty(company.Krs) ? "" : $", KRS {company.Krs}")}, {company.PostalCode} {company.City}, {company.Street} {company.BuildingNumber}.\n" +
                                       $"Okres objęty sprawozdaniem: {fy.StartDate.ToPl()} – {fy.EndDate.ToPl()}.");
        Text("W.2", FinancialStatementNotes.Sections[0].Title, texts["kontynuacja"]);
        var policyLine = policy is null ? "" : $"\nWariant RZiS: {(policy.ProfitAndLossVariant == ProfitAndLossVariant.Kalkulacyjny ? "kalkulacyjny" : "porównawczy")}; " +
                                                $"próg jednorazowej amortyzacji: {Money.Format(policy.LowValueAssetThreshold)} zł; " +
                                                $"{(policy.UseTaxDepreciationRatesForBooks ? "stawki amortyzacji bilansowej równe podatkowym (art. 32 ust. 6 UoR)" : "stawki amortyzacji bilansowej ustalone indywidualnie")}" +
                                                (policy.IsMicroEntity ? "; jednostka stosuje uproszczenia dla jednostek mikro (art. 28a, 48b UoR)" : policy.IsSmallEntity ? "; jednostka stosuje uproszczenia dla jednostek małych (art. 3 ust. 1c UoR)" : "") + ".";
        Text("W.3", FinancialStatementNotes.Sections[1].Title, texts["zasady"] + policyLine);

        // 1. Środki trwałe i WNiP
        Header("1.", "Zmiany wartości środków trwałych i WNiP", "wartość pocz. BO", "zwiększenia", "zmniejszenia", "wartość pocz. BZ");
        var svc = new FixedAssetService(_db, _user, _clock);
        var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.PlacedInServiceDate <= asOf).ToListAsync(ct);
        var before = fy.StartDate.AddDays(-1);
        var moves = new Dictionary<FixedAssetCategory, (decimal g0, decimal gi, decimal gd, decimal g1, decimal a0, decimal a1)>();
        foreach (var a in assets)
        {
            bool Active(DateOnly d) => a.PlacedInServiceDate <= d && !(a.Status is FixedAssetStatus.Zlikwidowany or FixedAssetStatus.Sprzedany && a.DisposalDate <= d);
            var (g0, acc0, _, _) = Active(before) ? await svc.ValuesAsOfAsync(a.Id, before, ct) : default;
            var (g1, acc1, _, _) = Active(asOf) ? await svc.ValuesAsOfAsync(a.Id, asOf, ct) : default;
            var m = moves.GetValueOrDefault(a.Category);
            moves[a.Category] = (m.g0 + g0, m.gi + Math.Max(g1 - g0, 0), m.gd + Math.Max(g0 - g1, 0), m.g1 + g1, m.a0 + acc0, m.a1 + acc1);
        }
        foreach (var (cat, m) in moves.OrderBy(x => x.Key))
        {
            var name = cat switch { FixedAssetCategory.WartoscNiematerialnaIPrawna => "Wartości niematerialne i prawne", FixedAssetCategory.SrodekTrwalyWBudowie => "Środki trwałe w budowie", _ => "Środki trwałe" };
            r.Add(null, name, m.g0, m.gi, m.gd, m.g1);
            r.AddRow(new ReportRow(null, $"   umorzenie {name.ToLowerInvariant()} (BO → BZ), wartość netto BZ", m.a0, m.a1 - m.a0, null, m.g1 - m.a1) { Level = 1 });
        }
        if (moves.Count == 0) r.Add(null, "Jednostka nie posiada środków trwałych ani WNiP.", null, null, null, null);

        // 2. Należności i zobowiązania wg terminu wymagalności
        Header("2.", "Należności i zobowiązania według pozostałego okresu spłaty", "do 1 roku", "powyżej 1 roku", "razem", "");
        r.Add(null, "Należności", bs.Get("B.II"), bs.Get("A.III"), bs.Get("B.II") + bs.Get("A.III"), null);
        r.Add(null, "Zobowiązania (bez rezerw i rozliczeń międzyokresowych)", bs.Get("P.B.III"), bs.Get("P.B.II"), bs.Get("P.B.III") + bs.Get("P.B.II"), null);
        r.Add(null, "w tym kredyty i pożyczki", bs.Get("P.B.III.3.a"), bs.Get("P.B.II.3.a"), bs.Get("P.B.III.3.a") + bs.Get("P.B.II.3.a"), null);

        // 3. Struktura przychodów
        Header("3.", "Struktura przychodów", "kwota", "udział %", "", "");
        var revenues = new[] { ("Sprzedaż produktów i usług", pl.Get("R.A.I")), ("Sprzedaż towarów i materiałów", pl.Get("R.A.IV")), ("Pozostałe przychody operacyjne", pl.Get("R.D")), ("Przychody finansowe", pl.Get("R.G")) };
        var revTotal = revenues.Sum(x => x.Item2);
        foreach (var (n, v) in revenues) r.Add(null, n, v, revTotal == 0 ? 0m : Math.Round(v / revTotal * 100m, 2), null, null);

        // 4. Podatek dochodowy
        Header("4.", "Rozliczenie wyniku brutto z podstawą opodatkowania", "kwota", "", "", "");
        try
        {
            var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fy.Periods.LastOrDefault(p => p.StartDate <= asOf)?.Number ?? fy.Periods.Count, ct);
            r.Add(null, "Wynik finansowy brutto", rec.BookResultBeforeTax, null, null, null);
            foreach (var a in rec.Adjustments) r.AddRow(new ReportRow(null, $"{a.Item} ({a.Basis})", a.Amount, null, null, null) { Level = 1 });
            r.Add(null, "Dochód (strata) do opodatkowania", rec.Computation.Income - rec.Computation.Loss, null, null, null);
            r.Add(null, $"Podatek dochodowy wyliczony ({rec.Computation.RatePercent:0}%)", rec.Computation.Tax, null, null, null);
            r.Add(null, "Podatek dochodowy ujęty w RZiS", pl.Get("R.J"), null, null, null);
        }
        catch (DomainException e)
        {
            r.Add(null, $"Nie wyliczono: {e.Message}", null, null, null, null);
        }

        // 5–12. Opisy
        var no = 5;
        foreach (var s in FinancialStatementNotes.Sections.Skip(2)) Text($"{no++}.", s.Title, texts[s.Key]);

        if (texts.Values.Any(t => t.Contains("[DO UZUPEŁNIENIA", StringComparison.OrdinalIgnoreCase)))
        {
            r.Notes.Add("Informacja dodatkowa zawiera pola [DO UZUPEŁNIENIA] — uzupełnij je w Raporty → „Teksty informacji dodatkowej”.");
            r.ControlsPassed = false;
        }
        r.Notes.Add("Zakres informacji dodatkowej dostosuj do wielkości jednostki (załącznik nr 1, 4 lub 5 do UoR). Tabele wyliczono z ksiąg i ewidencji programu; opisy wprowadza użytkownik i odpowiada za ich treść.");
        return r;
    }
}
