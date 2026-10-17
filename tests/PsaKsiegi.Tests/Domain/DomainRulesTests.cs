using System.Text;
using PsaKsiegi.Application.Security;
using PsaKsiegi.Domain.Cit;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Documents;
using PsaKsiegi.Domain.FixedAssets;
using PsaKsiegi.Domain.Periods;
using PsaKsiegi.Domain.Reporting;
using PsaKsiegi.Domain.Validation;
using PsaKsiegi.Domain.Vat;

namespace PsaKsiegi.Tests.DomainRules;

public class MoneyTests
{
    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1234,5", 1234.5)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-12,30", -12.30)]
    [InlineData("(100,00)", -100)]
    [InlineData("0.01", 0.01)]
    public void Parses_polish_and_international_formats(string text, double expected) =>
        Assert.Equal((decimal)expected, Money.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    public void Rejects_invalid_amounts(string text) => Assert.False(Money.TryParse(text, out _));

    [Fact]
    public void Rounds_half_away_from_zero_to_grosz()
    {
        Assert.Equal(0.13m, Money.Round(0.125m));
        Assert.Equal(-0.13m, Money.Round(-0.125m));
        Assert.Equal(0.12m, Money.Round(0.1249m));
        Assert.Equal(101m, Money.RoundToFullZloty(100.50m));
        Assert.Equal(100m, Money.RoundToFullZloty(100.49m));
    }

    [Fact]
    public void Formats_with_polish_separators()
    {
        var s = Money.Format(1234567.8m);
        Assert.Equal("1 234 567,80", s.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("1234567.80", Money.Xml(1234567.8m));
    }

    [Fact]
    public void Converts_currency_with_rounding()
    {
        Assert.Equal(425.13m, Money.ToPln(100m, 4.25125m));
        Assert.Throws<DomainException>(() => Money.ToPln(1m, 0m));
    }

    [Fact]
    public void Formats_dates_as_dd_mm_yyyy()
    {
        Assert.Equal("05.03.2026", new DateOnly(2026, 3, 5).ToPl());
        Assert.True(DateFormat.TryParsePl("31.12.2026", out var d));
        Assert.Equal(new DateOnly(2026, 12, 31), d);
        Assert.False(DateFormat.TryParsePl("31.02.2026", out _));
    }
}

public class IdentifierTests
{
    [Theory]
    [InlineData("5260250274", true)]
    [InlineData("526-025-02-74", true)]
    [InlineData("PL5260250274", true)]
    [InlineData("5260250275", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678901", false)]
    [InlineData("AB52602502", false)]
    public void Validates_nip_checksum(string nip, bool valid) => Assert.Equal(valid, Identifiers.IsValidNip(nip));

    [Theory]
    [InlineData("123456785", true)]
    [InlineData("123456786", false)]
    [InlineData("12345678512347", true)]
    [InlineData("12345678512348", false)]
    public void Validates_regon(string regon, bool valid) => Assert.Equal(valid, Identifiers.IsValidRegon(regon));

    [Theory]
    [InlineData("PL61109010140000071219812874", true)]
    [InlineData("61 1090 1014 0000 0712 1981 2874", true)]
    [InlineData("PL61109010140000071219812875", false)]
    [InlineData("DE89370400440532013000", true)]
    public void Validates_iban(string iban, bool valid) => Assert.Equal(valid, Identifiers.IsValidIban(iban));

    [Theory]
    [InlineData("44051401359", true)]
    [InlineData("44051401358", false)]
    public void Validates_pesel(string pesel, bool valid) => Assert.Equal(valid, Identifiers.IsValidPesel(pesel));

    [Fact]
    public void Validates_postal_code() { Assert.True(Identifiers.IsValidPostalCode("00-950")); Assert.False(Identifiers.IsValidPostalCode("00950")); }
}

public class VatTests
{
    private static readonly DateOnly D = new(2026, 3, 1);

    [Fact]
    public void Tax_from_net_and_gross()
    {
        var r23 = VatRules.Get("23", D);
        Assert.Equal(230m, VatRules.TaxFromNet(1000m, r23));
        Assert.Equal(0.23m, VatRules.TaxFromNet(1m, r23));
        var (net, tax) = VatRules.FromGross(123m, r23);
        Assert.Equal(100m, net);
        Assert.Equal(23m, tax);
        Assert.Equal(0m, VatRules.TaxFromNet(500m, VatRules.Get("zw", D)));
        Assert.Throws<DomainException>(() => VatRules.Get("22", D));
    }

    [Fact]
    public void Invoice_vat_is_computed_from_total_net_per_rate_not_from_lines()
    {
        // 3 pozycje po 0,10 zł netto 23%: VAT od pozycji = 0,02 × 3 = 0,06; VAT od sumy (0,30) = 0,07
        var doc = new Document { Kind = DocumentKind.FakturaSprzedazy, IssueDate = D, OperationDate = D };
        for (var i = 1; i <= 3; i++) doc.Lines.Add(new DocumentLine { LineNo = i, Name = $"Usługa {i}", Quantity = 1, UnitNetPrice = 0.10m, VatRateCode = "23" });
        doc.Lines.Add(new DocumentLine { LineNo = 4, Name = "Książka", Quantity = 2, UnitNetPrice = 10.005m, VatRateCode = "5" });
        InvoiceCalculator.Recalculate(doc);
        var v23 = doc.VatLines.Single(v => v.VatRateCode == "23");
        Assert.Equal(0.30m, v23.NetAmount);
        Assert.Equal(0.07m, v23.VatAmount);
        var v5 = doc.VatLines.Single(v => v.VatRateCode == "5");
        Assert.Equal(20.01m, v5.NetAmount);
        Assert.Equal(1.00m, v5.VatAmount);
        Assert.Equal(20.31m, doc.NetAmount);
        Assert.Equal(1.07m, doc.VatAmount);
        Assert.Equal(21.38m, doc.GrossAmount);
        Assert.Equal(doc.GrossAmount, doc.GrossAmountPln);
    }

    [Fact]
    public void Foreign_currency_invoice_converts_per_rate()
    {
        var doc = new Document { Kind = DocumentKind.FakturaZakupu, IssueDate = D, OperationDate = D, Currency = "EUR", ExchangeRate = 4.2512m };
        doc.Lines.Add(new DocumentLine { LineNo = 1, Name = "Licencja", Quantity = 1, UnitNetPrice = 1000m, VatRateCode = "23" });
        InvoiceCalculator.Recalculate(doc);
        Assert.Equal(4251.20m, doc.NetAmountPln);
        Assert.Equal(977.78m, doc.VatAmountPln); // 230 EUR × 4,2512 = 977,776
        Assert.Equal(977.78m, doc.VatLines[0].DeductibleVatPln);
        InvoiceCalculator.SetDeduction(doc, "23", 50m, false);
        Assert.Equal(488.89m, doc.VatLines[0].DeductibleVatPln);
        Assert.Throws<DomainException>(() => InvoiceCalculator.SetDeduction(doc, "23", 120m, false));
    }

    [Fact]
    public void Negative_quantity_only_in_correction()
    {
        var doc = new Document { Kind = DocumentKind.FakturaSprzedazy, IssueDate = D, OperationDate = D };
        doc.Lines.Add(new DocumentLine { LineNo = 1, Name = "X", Quantity = -1, UnitNetPrice = 10m, VatRateCode = "23" });
        Assert.Throws<DomainException>(() => InvoiceCalculator.Recalculate(doc));
        doc.Kind = DocumentKind.KorektaSprzedazy;
        InvoiceCalculator.Recalculate(doc);
        Assert.Equal(-12.30m, doc.GrossAmount);
    }

    [Fact]
    public void Jpk_mapping_for_main_rates()
    {
        Assert.Equal(new JpkFieldMapping("K_19", "K_20"), VatRules.SalesMapping("23", VatTransactionType.Krajowa));
        Assert.Equal(new JpkFieldMapping("K_17", "K_18"), VatRules.SalesMapping("8", VatTransactionType.Krajowa));
        Assert.Equal(new JpkFieldMapping("K_21", null), VatRules.SalesMapping("0WDT", VatTransactionType.WewnatrzwspolnotowaDostawaTowarow));
        Assert.Equal(new JpkFieldMapping("K_42", "K_43"), VatRules.PurchaseMapping(false));
        Assert.Equal(new JpkFieldMapping("K_29", "K_30"), VatRules.PurchaseSelfAssessedMapping(VatTransactionType.ImportUslug28b));
        Assert.NotEmpty(VatRules.DeductionWarnings("Nocleg w hotelu"));
    }
}

public class DepreciationTests
{
    private static FixedAsset Asset(decimal value, decimal rate, DepreciationMethod method = DepreciationMethod.Liniowa, string date = "2026-03-15") => new()
    {
        InventoryNumber = "ST/1",
        Name = "Laptop",
        AcquisitionDate = DateOnly.Parse(date),
        PlacedInServiceDate = DateOnly.Parse(date),
        InitialValueBook = value,
        InitialValueTax = value,
        BookMethod = method,
        TaxMethod = method,
        BookRatePercent = rate,
        TaxRatePercent = rate
    };

    [Fact]
    public void Linear_starts_month_after_service_and_ends_at_initial_value()
    {
        var a = Asset(12000m, 30m); // 300 zł / mies.
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Bilansowa);
        Assert.Equal((2026, 4), (plan[0].Year, plan[0].Month));
        Assert.Equal(300m, plan[0].Amount);
        Assert.Equal(40, plan.Count);
        Assert.Equal(12000m, plan.Sum(p => p.Amount));
        Assert.Equal(0m, plan[^1].NetValue);
    }

    [Fact]
    public void Linear_last_charge_is_adjusting()
    {
        var a = Asset(1000m, 7m); // 5,83 zł/mies.
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Podatkowa);
        Assert.Equal(5.83m, plan[0].Amount);
        Assert.Equal(1000m, plan.Sum(p => p.Amount));
        Assert.True(plan[^1].Amount <= 5.83m);
    }

    [Fact]
    public void Degressive_switches_to_linear_when_linear_is_higher()
    {
        var a = Asset(100000m, 20m, DepreciationMethod.Degresywna, "2025-12-10");
        a.BookMethod = DepreciationMethod.Liniowa;
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Podatkowa);
        // 2026: 100 000 × 40% = 40 000; 2027: 60 000 × 40% = 24 000; 2028: 36 000 × 40% = 14 400 < 20 000 → liniowa
        Assert.Equal(40000m, plan.Where(p => p.Year == 2026).Sum(p => p.Amount), 0);
        Assert.Equal(24000m, plan.Where(p => p.Year == 2027).Sum(p => p.Amount), 0);
        Assert.Equal(DepreciationMethod.Liniowa, plan.First(p => p.Year == 2028).MethodUsed);
        Assert.Equal(1666.67m, plan.First(p => p.Year == 2028).Amount);
        Assert.Equal(100000m, plan.Sum(p => p.Amount));
    }

    [Fact]
    public void One_off_low_value_asset_in_service_month()
    {
        var a = Asset(4999m, 0m, DepreciationMethod.Jednorazowa);
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Podatkowa);
        Assert.Single(plan);
        Assert.Equal((2026, 3, 4999m), (plan[0].Year, plan[0].Month, plan[0].Amount));
    }

    [Fact]
    public void Improvement_increases_base_from_next_month_and_disposal_stops()
    {
        var a = Asset(12000m, 30m);
        a.Operations.Add(new FixedAssetOperation { Type = FixedAssetOperationType.Ulepszenie, Date = new DateOnly(2026, 6, 10), BookValueChange = 4000m, TaxValueChange = 4000m });
        a.DisposalDate = new DateOnly(2026, 9, 30);
        var plan = DepreciationCalculator.Plan(a, DepreciationBasis.Bilansowa);
        Assert.Equal(300m, plan.Single(p => p.Month == 6).Amount);
        Assert.Equal(400m, plan.Single(p => p.Month == 7).Amount);
        Assert.Equal(9, plan[^1].Month);
        Assert.Equal(6, plan.Count);
    }

    [Fact]
    public void Land_is_not_depreciated_and_validation_flags_errors()
    {
        var a = Asset(50000m, 0m, DepreciationMethod.Brak);
        Assert.Empty(DepreciationCalculator.Plan(a, DepreciationBasis.Bilansowa));
        var bad = Asset(1000m, 0m);
        bad.TaxDegressiveFactor = 3m;
        bad.TaxMethod = DepreciationMethod.Degresywna;
        var v = bad.Validate(10000m);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("2,0"));
    }
}

public class CitTests
{
    [Fact]
    public void Computes_tax_with_full_zloty_rounding()
    {
        var c = CitCalculator.Compute(100_000.40m, 40_000.10m, 0m, 19m);
        Assert.Equal(60_000.30m, c.Income);
        Assert.Equal(60_000m, c.TaxBase);
        Assert.Equal(11_400m, c.Tax);
        var small = CitCalculator.Compute(100_000m, 39_999.50m, 0m, 9m);
        Assert.Equal(60_001m, small.TaxBase); // 60 000,50 → 60 001
        Assert.Equal(5_400m, small.Tax); // 5 400,09 → 5 400
    }

    [Fact]
    public void Loss_and_deductions()
    {
        var c = CitCalculator.Compute(10_000m, 15_000m, 0m, 19m);
        Assert.Equal(0m, c.Tax);
        Assert.Equal(5_000m, c.Loss);
        var d = CitCalculator.Compute(50_000m, 10_000m, 15_000m, 19m);
        Assert.Equal(25_000m, d.TaxBase);
        Assert.Equal(4_750m, d.Tax);
        Assert.Equal(0m, CitCalculator.AdvanceDue(1000m, 1500m));
        Assert.Equal(500m, CitCalculator.AdvanceDue(2000m, 1500m));
    }

    [Fact]
    public void Deadlines_shift_to_business_day()
    {
        // 20.06.2026 to sobota → 22.06.2026
        Assert.Equal(new DateOnly(2026, 6, 22), CitCalculator.AdvanceDeadline(new DateOnly(2026, 5, 31)));
        Assert.Equal(new DateOnly(2026, 2, 20), CitCalculator.AdvanceDeadline(new DateOnly(2026, 1, 31)));
        // CIT-8 za 2026: 31.03.2027 (środa)
        Assert.Equal(new DateOnly(2027, 3, 31), CitCalculator.AnnualReturnDeadline(new DateOnly(2026, 12, 31)));
        Assert.Equal(new DateOnly(2026, 4, 5), PolishHolidays.EasterSunday(2026));
        Assert.True(PolishHolidays.IsHoliday(new DateOnly(2026, 4, 6)));
        Assert.True(PolishHolidays.IsHoliday(new DateOnly(2026, 12, 24)));
        Assert.True(PolishHolidays.IsHoliday(new DateOnly(2026, 6, 4))); // Boże Ciało
    }
}

public class FiscalYearTests
{
    [Fact]
    public void Generates_twelve_monthly_periods()
    {
        var fy = new FiscalYear { StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };
        fy.GenerateMonthlyPeriods();
        Assert.Equal(12, fy.Periods.Count);
        Assert.Equal(new DateOnly(2026, 2, 28), fy.Periods[1].EndDate);
    }

    [Fact]
    public void Validates_year_length_rules()
    {
        Assert.True(FiscalYear.ValidateRange(new(2026, 1, 1), new(2026, 12, 31), false).IsValid);
        Assert.False(FiscalYear.ValidateRange(new(2026, 1, 1), new(2027, 3, 31), false).IsValid);
        Assert.True(FiscalYear.ValidateRange(new(2026, 7, 15), new(2027, 12, 31), true).IsValid);
        Assert.False(FiscalYear.ValidateRange(new(2026, 1, 1), new(2028, 6, 30), true).IsValid);
        Assert.False(FiscalYear.ValidateRange(new(2026, 1, 1), new(2026, 12, 30), true).IsValid);
    }
}

public class LayoutTests
{
    [Fact]
    public void Statement_layout_has_expected_structure()
    {
        Assert.Equal(new[] { "A", "B", "C", "D" }, FinancialStatementLayout.Assets.Select(a => a.Code));
        Assert.Equal(new[] { "P.A", "P.B" }, FinancialStatementLayout.Liabilities.Select(a => a.Code));
        Assert.Equal("R.A-R.B", FinancialStatementLayout.All["R.C"].Formula);
        Assert.True(FinancialStatementLayout.IsLeaf("B.II.3.a"));
        Assert.False(FinancialStatementLayout.IsLeaf("B.II.3"));
        Assert.False(FinancialStatementLayout.IsLeaf("R.L"));
        Assert.Equal("P.B.III.3", FinancialStatementLayout.All["P.B.III.3.d"].ParentCode);
    }
}

public class SecurityPrimitivesTests
{
    [Fact]
    public void Totp_matches_rfc6238_test_vector()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Compute(secret, 59));
        Assert.Equal("081804", Totp.Compute(secret, 1111111109));
        Assert.True(Totp.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime));
        Assert.False(Totp.Verify(secret, "287083", DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime));
        Assert.Equal(secret, Totp.FromBase32(Totp.ToBase32(secret)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(secret));
    }

    [Fact]
    public void Password_hash_verifies_and_policy_enforced()
    {
        var (h, s, it) = PasswordHasher.Hash("Bardzo-Mocne-Haslo-1", 1000);
        Assert.True(PasswordHasher.Verify("Bardzo-Mocne-Haslo-1", h, s, it));
        Assert.False(PasswordHasher.Verify("bardzo-mocne-haslo-1", h, s, it));
        Assert.False(PasswordHasher.ValidatePolicy("krotkie", "jan").IsValid);
        Assert.False(PasswordHasher.ValidatePolicy("wszystkiemalelitery", "jan").IsValid);
        Assert.False(PasswordHasher.ValidatePolicy("Jan.Kowalski.2026!", "kowalski").IsValid);
        Assert.True(PasswordHasher.ValidatePolicy("Zielony-Most-2026", "jan").IsValid);
    }

    [Fact]
    public void Data_protector_round_trip_and_tamper_detection()
    {
        var p = new DataProtector(new byte[32]);
        var enc = p.Protect(new byte[] { 1, 2, 3 }, "mfa:1");
        Assert.Equal(new byte[] { 1, 2, 3 }, p.Unprotect(enc, "mfa:1"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Unprotect(enc, "mfa:2"));
        var raw = Convert.FromBase64String(enc[3..]);
        raw[^1] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Unprotect("v1:" + Convert.ToBase64String(raw), "mfa:1"));
    }
}
