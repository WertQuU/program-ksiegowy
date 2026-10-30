using PelnaKsiegowosc.Application.Payroll;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>Wydruki płacowe i zestawienia do deklaracji płatnika (ZUS DRA/RCA, PIT-4R, PIT-8AR, PIT-11).</summary>
public sealed partial class ReportService
{
    private static string ContractName(ContractType t) => t switch
    {
        ContractType.UmowaOPrace => "umowa o pracę",
        ContractType.UmowaZlecenie => "umowa zlecenia",
        ContractType.UmowaODzielo => "umowa o dzieło",
        _ => "powołanie"
    };

    private async Task<List<(PayrollList List, PayrollItem Item)>> PostedItemsAsync(DateOnly start, DateOnly end, CancellationToken ct) =>
        (await (from i in _db.PayrollItems.AsNoTracking()
                join l in _db.PayrollLists.AsNoTracking() on i.PayrollListId equals l.Id
                where l.CompanyId == _user.CompanyId && l.Status == PayrollStatus.Zaksiegowana && l.PaymentDate >= start && l.PaymentDate <= end
                select new { l, i }).ToListAsync(ct)).Select(x => (x.l, x.i)).ToList();

    public async Task<ReportDocument> PayrollListAsync(Guid listId, CancellationToken ct = default)
    {
        var list = await _db.PayrollLists.AsNoTracking().Include(l => l.Items).FirstAsync(l => l.Id == listId && l.CompanyId == _user.CompanyId, ct);
        var r = await NewAsync($"Lista płac {list.Number}", $"{list.Description}; wypłata {list.PaymentDate.ToPl()}", true, ct);
        r.Filters.Add(("Status", list.Status == PayrollStatus.Zaksiegowana ? "zaksięgowana" : "robocza"));
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Osoba", ColumnKind.Text, 2), new ReportColumn("Umowa", ColumnKind.Text, 1.2), new ReportColumn("Brutto", ColumnKind.Money),
            new ReportColumn("Emeryt.", ColumnKind.Money), new ReportColumn("Rent.", ColumnKind.Money), new ReportColumn("Chorob.", ColumnKind.Money),
            new ReportColumn("Zdrowotna", ColumnKind.Money), new ReportColumn("KUP", ColumnKind.Money), new ReportColumn("Podstawa PIT", ColumnKind.Money),
            new ReportColumn("Zaliczka PIT", ColumnKind.Money), new ReportColumn("Netto", ColumnKind.Money), new ReportColumn("Koszt pracodawcy", ColumnKind.Money)
        });
        foreach (var i in list.Items.OrderBy(i => i.EmployeeName))
            r.Add(i.EmployeeName, ContractName(i.ContractType), i.Gross, i.PensionEmployee, i.DisabilityEmployee, i.SicknessEmployee, i.Health, i.CostsOfIncome, i.TaxBase, i.TaxAdvance, i.Net, i.EmployerCost);
        r.AddRow(new ReportRow("RAZEM", null, list.Items.Sum(i => i.Gross), list.Items.Sum(i => i.PensionEmployee), list.Items.Sum(i => i.DisabilityEmployee), list.Items.Sum(i => i.SicknessEmployee),
            list.Items.Sum(i => i.Health), list.Items.Sum(i => i.CostsOfIncome), list.Items.Sum(i => i.TaxBase), list.Items.Sum(i => i.TaxAdvance), list.Items.Sum(i => i.Net), list.Items.Sum(i => i.EmployerCost)) { IsTotal = true });
        foreach (var i in list.Items.Where(i => i.Notes is not null)) r.Notes.Add($"{i.EmployeeName}: {i.Notes}");
        if (list.Items.Any(i => i.PpkEmployee + i.PpkEmployer != 0))
            r.Notes.Add($"PPK: wpłaty pracowników {Money.Format(list.Items.Sum(i => i.PpkEmployee))} zł (potrącone z netto), pracodawcy {Money.Format(list.Items.Sum(i => i.PpkEmployer))} zł (przychód pracownika, koszt 405) — przelew do instytucji finansowej do 15. dnia następnego miesiąca.");
        r.Notes.Add($"Parametry: {list.RulesVersion}. Składki pracodawcy (emerytalna, rentowa, wypadkowa, FP, FGŚP) w kolumnie „Koszt pracodawcy”.");
        return r;
    }

    /// <summary>Zestawienie składek do deklaracji ZUS DRA i raportów imiennych RCA — za miesiąc wypłaty.</summary>
    public async Task<ReportDocument> ZusMonthlyAsync(DateOnly month, CancellationToken ct = default)
    {
        var m = new DateOnly(month.Year, month.Month, 1);
        var items = await PostedItemsAsync(m, m.AddMonths(1).AddDays(-1), ct);
        var r = await NewAsync("Zestawienie składek ZUS (do DRA i RCA)", $"wypłaty w {m:MM.yyyy}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Ubezpieczony", ColumnKind.Text, 2), new ReportColumn("Tytuł", ColumnKind.Text, 1.2), new ReportColumn("Podstawa społ.", ColumnKind.Money),
            new ReportColumn("Emerytalna", ColumnKind.Money), new ReportColumn("Rentowa", ColumnKind.Money), new ReportColumn("Chorobowa", ColumnKind.Money),
            new ReportColumn("Wypadkowa", ColumnKind.Money), new ReportColumn("Podstawa zdrow.", ColumnKind.Money), new ReportColumn("Zdrowotna", ColumnKind.Money),
            new ReportColumn("FP", ColumnKind.Money), new ReportColumn("FGŚP", ColumnKind.Money)
        });
        r.AddRow(new ReportRow("RCA — raporty imienne", null, null, null, null, null, null, null, null, null, null) { IsHeader = true });
        foreach (var g in items.Select(x => x.Item).GroupBy(i => (i.EmployeeId, i.EmployeeName, i.ContractType)).OrderBy(g => g.Key.EmployeeName))
            r.Add(g.Key.EmployeeName, ContractName(g.Key.ContractType), g.Sum(i => i.SocialBasis), g.Sum(i => i.PensionEmployee + i.PensionEmployer), g.Sum(i => i.DisabilityEmployee + i.DisabilityEmployer),
                g.Sum(i => i.SicknessEmployee), g.Sum(i => i.Accident), g.Sum(i => i.HealthBase), g.Sum(i => i.Health), g.Sum(i => i.LaborFund), g.Sum(i => i.Fgsp));
        var all = items.Select(x => x.Item).ToList();
        r.AddRow(new ReportRow("DRA — razem", null, all.Sum(i => i.SocialBasis), all.Sum(i => i.PensionEmployee + i.PensionEmployer), all.Sum(i => i.DisabilityEmployee + i.DisabilityEmployer),
            all.Sum(i => i.SicknessEmployee), all.Sum(i => i.Accident), all.Sum(i => i.HealthBase), all.Sum(i => i.Health), all.Sum(i => i.LaborFund), all.Sum(i => i.Fgsp)) { IsTotal = true });
        var social = all.Sum(i => i.SocialEmployee + i.PensionEmployer + i.DisabilityEmployer + i.Accident);
        r.AddRow(new ReportRow("Do zapłaty ZUS (jeden przelew na NRS)", null, null, null, null, null, null, null, social + all.Sum(i => i.Health) + all.Sum(i => i.LaborFund + i.Fgsp), null, null) { IsTotal = true });
        r.Notes.Add("Termin: do 15. dnia następnego miesiąca (jednostki z osobowością prawną) / do 20. (pozostali płatnicy) — art. 47 ust. 1 ustawy o sus. Deklarację DRA i raporty RCA wysyłasz w PUE/eZUS lub programie Płatnik; kwoty przepisz z tego zestawienia.");
        if (all.Count == 0) r.Notes.Add("Brak zaksięgowanych list płac z wypłatą w tym miesiącu.");
        return r;
    }

    /// <summary>Zestawienie do PIT-4R (zaliczki od wynagrodzeń, roczne) i PIT-8AR (podatek zryczałtowany).</summary>
    public async Task<ReportDocument> Pit4RAsync(int year, CancellationToken ct = default)
    {
        var items = await PostedItemsAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct);
        var r = await NewAsync("Zestawienie do PIT-4R i PIT-8AR", $"rok {year}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Miesiąc"), new ReportColumn("PIT-4R: zaliczki — stosunek pracy", ColumnKind.Money, 1.4),
            new ReportColumn("PIT-4R: działalność osobista (zlecenie, powołanie)", ColumnKind.Money, 1.4), new ReportColumn("PIT-4R: inne (dzieło)", ColumnKind.Money, 1.2),
            new ReportColumn("PIT-8AR: ryczałt do 200 zł", ColumnKind.Money, 1.2), new ReportColumn("Do wpłaty do US", ColumnKind.Money) });
        decimal a = 0, b = 0, c = 0, d = 0;
        for (var mo = 1; mo <= 12; mo++)
        {
            var mi = items.Where(x => x.List.PaymentDate.Month == mo).Select(x => x.Item).ToList();
            var uop = mi.Where(i => !i.FlatRateTax && i.ContractType == ContractType.UmowaOPrace).Sum(i => i.TaxAdvance);
            var pers = mi.Where(i => !i.FlatRateTax && i.ContractType is ContractType.UmowaZlecenie or ContractType.Powolanie).Sum(i => i.TaxAdvance);
            var other = mi.Where(i => !i.FlatRateTax && i.ContractType == ContractType.UmowaODzielo).Sum(i => i.TaxAdvance);
            var flat = mi.Where(i => i.FlatRateTax).Sum(i => i.TaxAdvance);
            a += uop; b += pers; c += other; d += flat;
            r.Add($"{mo:00}.{year}", uop, pers, other, flat, uop + pers + other + flat);
        }
        r.AddRow(new ReportRow("RAZEM", a, b, c, d, a + b + c + d) { IsTotal = true });
        r.Notes.Add("Zaliczki i podatek zryczałtowany wpłaca się do 20. dnia miesiąca następującego po miesiącu pobrania (art. 38 ust. 1 i art. 42 ust. 1 ustawy o PIT).");
        r.Notes.Add("PIT-4R i PIT-8AR składa się elektronicznie do końca stycznia roku następnego (e-Deklaracje / e-Urząd Skarbowy). Kwoty pól przepisz z tego zestawienia.");
        return r;
    }

    /// <summary>Dane do informacji PIT-11 — dla każdej osoby (bez umów rozliczanych ryczałtem).</summary>
    public async Task<ReportDocument> Pit11Async(int year, CancellationToken ct = default)
    {
        var items = await PostedItemsAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct);
        var employees = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId).ToDictionaryAsync(e => e.Id, ct);
        var r = await NewAsync("Dane do informacji PIT-11", $"rok {year}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Podatnik", ColumnKind.Text, 1.8), new ReportColumn("PESEL"), new ReportColumn("Źródło", ColumnKind.Text, 1.4), new ReportColumn("Przychód", ColumnKind.Money),
            new ReportColumn("Koszty", ColumnKind.Money), new ReportColumn("Dochód", ColumnKind.Money), new ReportColumn("Zaliczka", ColumnKind.Money),
            new ReportColumn("Składki społ. (odliczone)", ColumnKind.Money), new ReportColumn("Zdrowotna", ColumnKind.Money), new ReportColumn("Przychód zwolniony (ulga <26)", ColumnKind.Money)
        });
        foreach (var g in items.Select(x => x.Item).Where(i => !i.FlatRateTax).GroupBy(i => i.EmployeeId).OrderBy(g => g.First().EmployeeName))
        {
            var e = employees.GetValueOrDefault(g.Key);
            foreach (var src in g.GroupBy(i => i.ContractType switch
                     {
                         ContractType.UmowaOPrace => "stosunek pracy",
                         ContractType.UmowaODzielo => "umowa o dzieło (inne źródła)",
                         _ => "działalność wykonywana osobiście"
                     }))
            {
                var revenue = src.Sum(i => i.Gross + i.PpkEmployer - i.ExemptRevenue);
                var costs = src.Sum(i => i.CostsOfIncome);
                r.Add(g.First().EmployeeName, e?.Pesel, src.Key, revenue, costs, Math.Max(0, revenue - costs), src.Sum(i => i.TaxAdvance),
                    src.Sum(i => i.SocialEmployee), src.Sum(i => i.Health), src.Sum(i => i.ExemptRevenue));
            }
        }
        r.Notes.Add("PIT-11 przekazuje się podatnikowi i urzędowi skarbowemu do końca lutego (podatnikowi) / do końca stycznia (US, elektronicznie) roku następnego — art. 39 ust. 1 i 42 ust. 2 pkt 1 ustawy o PIT.");
        r.Notes.Add("Składki społeczne prezentowane w całości jako odliczone u płatnika; przy uldze dla młodych sprawdź podział składek między przychód zwolniony i opodatkowany.");
        return r;
    }

    /// <summary>Wiersz części E informacji PIT-11 dla jednego źródła przychodów.</summary>
    public sealed record Pit11Source(ContractType Contract, bool AuthorCosts, decimal Revenue, decimal Costs, decimal Advance, decimal AuthorRevenue, decimal AuthorCostsAmount);

    /// <summary>Dane informacji PIT-11 dla jednej osoby (rok).</summary>
    public sealed record Pit11Person(Guid EmployeeId, string FirstName, string LastName, string? Pesel, DateOnly? BirthDate, string? TaxOfficeCode,
        IReadOnlyList<Pit11Source> Sources, decimal Social, decimal Health, decimal ExemptEmployment, decimal ExemptCivil, Pit11Address? Address = null);

    /// <summary>Adres zamieszkania podatnika (PIT-11 poz. 19–27).</summary>
    public sealed record Pit11Address(string? Voivodeship, string? County, string? Commune, string? Street, string? HouseNumber, string? FlatNumber, string City, string? PostalCode);

    public async Task<List<Pit11Person>> Pit11DataAsync(int year, CancellationToken ct = default)
    {
        var items = await PostedItemsAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct);
        var employees = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId).ToDictionaryAsync(e => e.Id, ct);
        var result = new List<Pit11Person>();
        foreach (var g in items.Select(x => x.Item).Where(i => !i.FlatRateTax).GroupBy(i => i.EmployeeId))
        {
            if (!employees.TryGetValue(g.Key, out var e)) continue;
            var author = e.Costs == CostsOfIncome.Autorskie50;
            var sources = g.GroupBy(i => i.ContractType == ContractType.UmowaOPrace ? ContractType.UmowaOPrace : ContractType.UmowaZlecenie)
                .Select(s =>
                {
                    var revenue = s.Sum(i => i.Gross + i.PpkEmployer - i.ExemptRevenue);
                    var costs = s.Sum(i => i.CostsOfIncome);
                    return new Pit11Source(s.Key, author, revenue, costs, s.Sum(i => i.TaxAdvance), author ? revenue : 0m, author ? costs : 0m);
                }).ToList();
            result.Add(new Pit11Person(e.Id, e.FirstName, e.LastName, e.Pesel, e.BirthDate ?? Domain.Validation.Identifiers.BirthDateFromPesel(e.Pesel), e.TaxOfficeCode, sources,
                g.Sum(i => i.SocialEmployee), g.Sum(i => i.Health),
                g.Where(i => i.ContractType == ContractType.UmowaOPrace).Sum(i => i.ExemptRevenue), g.Where(i => i.ContractType != ContractType.UmowaOPrace).Sum(i => i.ExemptRevenue),
                string.IsNullOrWhiteSpace(e.City) ? null : new Pit11Address(e.Voivodeship, e.County, e.Commune, e.Street, e.HouseNumber, e.FlatNumber, e.City!, e.PostalCode)));
        }
        return result.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
    }

    /// <summary>Karta wynagrodzeń pracownika za rok (dokumentacja płacowa, art. 149 KP / § 10 rozporządzenia o dokumentacji pracowniczej).</summary>
    public async Task<ReportDocument> SalaryCardAsync(Guid employeeId, int year, CancellationToken ct = default)
    {
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == employeeId && x.CompanyId == _user.CompanyId, ct);
        var items = (await PostedItemsAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct)).Where(x => x.Item.EmployeeId == employeeId)
            .OrderBy(x => x.List.PaymentDate).ToList();
        var r = await NewAsync($"Karta wynagrodzeń — {e.FullName}", $"rok {year}; PESEL {e.Pesel}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Lista / za miesiąc", ColumnKind.Text, 1.4), new ReportColumn("Wypłata", ColumnKind.Date), new ReportColumn("Brutto", ColumnKind.Money),
            new ReportColumn("w tym chorobowe", ColumnKind.Money), new ReportColumn("Dni chor. / bezpł.", ColumnKind.Text, 0.7), new ReportColumn("Składki prac.", ColumnKind.Money),
            new ReportColumn("Zdrowotna", ColumnKind.Money), new ReportColumn("Zaliczka PIT", ColumnKind.Money), new ReportColumn("PPK prac.", ColumnKind.Money),
            new ReportColumn("Netto", ColumnKind.Money), new ReportColumn("Składki pracodawcy", ColumnKind.Money)
        });
        foreach (var (l, i) in items)
            r.Add($"{l.Number} ({l.Month:MM.yyyy})", l.PaymentDate, i.Gross, i.SickPay, $"{i.SickDays} / {i.UnpaidDays}", i.SocialEmployee, i.Health, i.TaxAdvance, i.PpkEmployee, i.Net, i.EmployerCost);
        r.AddRow(new ReportRow("RAZEM", null, items.Sum(x => x.Item.Gross), items.Sum(x => x.Item.SickPay), null, items.Sum(x => x.Item.SocialEmployee),
            items.Sum(x => x.Item.Health), items.Sum(x => x.Item.TaxAdvance), items.Sum(x => x.Item.PpkEmployee), items.Sum(x => x.Item.Net), items.Sum(x => x.Item.EmployerCost)) { IsTotal = true });
        r.Notes.Add("Kartę wynagrodzeń przechowuje się w dokumentacji płacowej przez 10 lat (zatrudnienie od 2019 r.).");
        return r;
    }

    /// <summary>Zestawienie urlopów wypoczynkowych (wymiar, zaległy, wykorzystany, pozostały).</summary>
    public async Task<ReportDocument> LeaveReportAsync(int year, CancellationToken ct = default)
    {
        var r = await NewAsync("Ewidencja urlopów wypoczynkowych", $"rok {year}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Pracownik", ColumnKind.Text, 2), new ReportColumn("Wymiar", ColumnKind.Money), new ReportColumn("Zaległy", ColumnKind.Money),
            new ReportColumn("Wykorzystany", ColumnKind.Money), new ReportColumn("w tym na żądanie", ColumnKind.Money), new ReportColumn("Pozostało", ColumnKind.Money) });
        foreach (var b in await new HrService(_db, _user, _clock).LeaveBalancesAsync(year, ct))
            r.Add(b.Employee, b.Entitlement, b.CarriedOver, b.Used, b.OnDemandUsed, b.Remaining);
        r.Notes.Add("Urlop zaległy należy udzielić najpóźniej do 30 września następnego roku (art. 168 KP). Wymiar w roku podjęcia pracy — proporcjonalnie (art. 155¹ KP).");
        return r;
    }
}
