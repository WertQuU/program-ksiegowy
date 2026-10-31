using PelnaKsiegowosc.Data;

namespace PelnaKsiegowosc.Tests.DomainRules;

public sealed class CompanyFieldCodecTests
{
    private static readonly Guid Firma = Guid.Parse("6a174606-5c11-41d7-85a8-7dba5a77b05a");
    private static readonly Guid Wiersz = Guid.Parse("eaba60d6-834c-4198-a014-bfa7dbe1769d");

    [Fact]
    public void Same_company_and_record_produce_the_same_value_on_every_workstation()
    {
        const string pesel = "44051401359";
        var first = CompanyFieldCodec.Protect(pesel, Firma, Wiersz, "Pesel");
        var second = CompanyFieldCodec.Protect(pesel, Firma, Wiersz, "Pesel");
        Assert.Equal(first, second);
        Assert.StartsWith(CompanyFieldCodec.Prefix, first);
        Assert.DoesNotContain(pesel, first);
        Assert.Equal(pesel, CompanyFieldCodec.Unprotect(second, Firma, Wiersz, "Pesel"));
    }

    [Fact]
    public void Company_record_and_field_are_bound_to_the_ciphertext()
    {
        var encoded = CompanyFieldCodec.Protect("PL61109010140000071219812874", Firma, Wiersz, "BankAccountIban")!;
        Assert.NotEqual(encoded, CompanyFieldCodec.Protect("PL61109010140000071219812874",
            Guid.NewGuid(), Wiersz, "BankAccountIban"));
        Assert.Throws<InvalidDataException>(() => CompanyFieldCodec.Unprotect(encoded,
            Firma, Guid.NewGuid(), "BankAccountIban"));
        Assert.Throws<InvalidDataException>(() => CompanyFieldCodec.Unprotect(encoded,
            Firma, Wiersz, "Pesel"));
    }

    [Fact]
    public void Editing_ciphertext_in_sql_makes_decryption_fail()
    {
        var encoded = CompanyFieldCodec.Protect("44051401359", Firma, Wiersz, "Pesel")!;
        var bytes = Convert.FromBase64String(encoded[CompanyFieldCodec.Prefix.Length..]);
        bytes[^1] ^= 0x01;
        var edited = CompanyFieldCodec.Prefix + Convert.ToBase64String(bytes);
        Assert.Throws<InvalidDataException>(() => CompanyFieldCodec.Unprotect(edited, Firma, Wiersz, "Pesel"));
    }

    [Fact]
    public void Legacy_plaintext_remains_readable_until_explicit_maintenance_migration()
    {
        Assert.Equal("44051401359", CompanyFieldCodec.Unprotect("44051401359", Firma, Wiersz, "Pesel"));
        Assert.Null(CompanyFieldCodec.Protect(null, Firma, Wiersz, "Pesel"));
        Assert.Throws<InvalidDataException>(() =>
            CompanyFieldCodec.Protect("PK1$already-ciphertext", Firma, Wiersz, "Pesel"));
    }
}
