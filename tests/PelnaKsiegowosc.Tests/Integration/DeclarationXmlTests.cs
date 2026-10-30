using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Declarations;
using PelnaKsiegowosc.Infrastructure.Xml;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public class DeclarationXmlTests
{
    private static readonly XsdValidator Validator = new(Path.Combine(AppContext.BaseDirectory, "schemas"));

    private static Company Company() => new()
    {
        FullName = "Przykładowa Spółka z o.o.", ShortName = "Przykładowa", Nip = TestBooks.ValidNip, LegalForm = LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia,
        TaxOfficeCode = "1471", City = "Warszawa", PostalCode = "00-001", BuildingNumber = "1"
    };

    [Fact]
    public void Vat_ue_is_valid_against_official_schema()
    {
        var lines = new List<ReportService.VatUeLine>
        {
            new("C", "DE", "123456789", "Kunde GmbH", 10500m),
            new("D", "CZ", "12345678", "Dodavatel s.r.o.", 2300m),
            new("E", "GR", "123456789", "Pelatis AE", 800m),
        };
        var r = VatUeXmlBuilder.Build(Company(), new DateOnly(2026, 3, 1), lines, Validator);
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors));
        var ns = VatUeXmlBuilder.Ns;
        Assert.Equal("EL", r.Document.Root!.Descendants(ns + "P_Ua").Single().Value);
        Assert.Equal("10500", r.Document.Root!.Descendants(ns + "P_Dc").Single().Value);

        var person = VatUeXmlBuilder.Build(Company(), new DateOnly(2026, 3, 1), lines, Validator, new TaxpayerPerson("Jan", "Nowak", new DateOnly(1980, 5, 1)));
        Assert.True(person.Validation.IsValid, string.Join(Environment.NewLine, person.Validation.Errors));
        Assert.Throws<DomainException>(() => VatUeXmlBuilder.Build(Company(), new DateOnly(2026, 3, 1), new[] { new ReportService.VatUeLine("C", "DE", "", "Bez numeru", 1m) }, Validator));
    }

    [Fact]
    public void Pit11_is_valid_against_official_schema()
    {
        var person = new ReportService.Pit11Person(Guid.NewGuid(), "Jan", "Nowak", "44051401359", new DateOnly(1944, 5, 14), "1402",
            new[]
            {
                new ReportService.Pit11Source(Domain.Payroll.ContractType.UmowaOPrace, false, 72000m, 3000m, 3492m, 0m, 0m),
                new ReportService.Pit11Source(Domain.Payroll.ContractType.UmowaZlecenie, false, 5000m, 887.40m, 494m, 0m, 0m),
            }, 10504.40m, 5591.64m, 0m, 0m);
        var r = Pit11XmlBuilder.Build(Company(), 2026, person, Validator);
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors));
        var ns = Pit11XmlBuilder.Ns;
        Assert.Equal("69000.00", r.Document.Root!.Descendants(ns + "P_31").Single().Value);
        Assert.Equal("494", r.Document.Root!.Descendants(ns + "P_61").Single().Value);

        var young = person with { Sources = new[] { new ReportService.Pit11Source(Domain.Payroll.ContractType.UmowaZlecenie, true, 4000m, 2000m, 240m, 4000m, 2000m) }, ExemptCivil = 3000m };
        var y = Pit11XmlBuilder.Build(Company(), 2026, young, Validator);
        Assert.True(y.Validation.IsValid, string.Join(Environment.NewLine, y.Validation.Errors));
        Assert.Throws<DomainException>(() => Pit11XmlBuilder.Build(Company(), 2026, person with { TaxOfficeCode = null }, Validator));
    }
}
