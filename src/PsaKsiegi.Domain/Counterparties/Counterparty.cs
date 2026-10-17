using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Validation;

namespace PsaKsiegi.Domain.Counterparties;

public enum CounterpartyKind
{
    Firma,
    OsobaFizyczna,
    PodmiotZagraniczny,
    UrzadPubliczny,
    Akcjonariusz,
    Pracownik,
    Bank
}

/// <summary>Kontrahent — dane osobowe osób fizycznych podlegają RODO (minimalizacja danych, cel: obowiązek prawny).</summary>
public class Counterparty : CompanyEntity
{
    /// <summary>Kod analityki kontrahenta używany w numerach kont (np. „0001”).</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public CounterpartyKind Kind { get; set; } = CounterpartyKind.Firma;
    public string? Nip { get; set; }
    /// <summary>Numer VAT UE / zagraniczny identyfikator podatkowy.</summary>
    public string? VatEuNumber { get; set; }
    public string CountryCode { get; set; } = "PL";
    public string? Regon { get; set; }
    public string? Pesel { get; set; }
    public string? Street { get; set; }
    public string? BuildingNumber { get; set; }
    public string? ApartmentNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? BankAccountIban { get; set; }
    public int DefaultPaymentDays { get; set; } = 14;
    public bool IsRelatedParty { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public string AddressLine => string.Join(", ", new[]
    {
        $"{Street} {BuildingNumber}{(string.IsNullOrWhiteSpace(ApartmentNumber) ? "" : "/" + ApartmentNumber)}".Trim(),
        $"{PostalCode} {City}".Trim()
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public ValidationResult Validate()
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(Name)) r.Error("Nazwa kontrahenta jest wymagana.");
        if (string.IsNullOrWhiteSpace(Code)) r.Error("Kod kontrahenta jest wymagany.");
        else if (Code.Length > 10 || !Code.All(char.IsLetterOrDigit)) r.Error("Kod kontrahenta: 1–10 znaków alfanumerycznych.");
        if (CountryCode == "PL" && !string.IsNullOrWhiteSpace(Nip) && !Identifiers.IsValidNip(Nip)) r.Error("NIP kontrahenta jest nieprawidłowy.");
        if (CountryCode == "PL" && Kind == CounterpartyKind.Firma && string.IsNullOrWhiteSpace(Nip))
            r.Warning("Brak NIP kontrahenta krajowego — sprawdź, czy kontrahent nie jest podatnikiem.");
        if (!string.IsNullOrWhiteSpace(Pesel) && !Identifiers.IsValidPesel(Pesel)) r.Error("PESEL jest nieprawidłowy.");
        if (!string.IsNullOrWhiteSpace(Regon) && !Identifiers.IsValidRegon(Regon)) r.Error("REGON jest nieprawidłowy.");
        if (!string.IsNullOrWhiteSpace(BankAccountIban) && !Identifiers.IsValidIban(BankAccountIban)) r.Error("Numer rachunku kontrahenta jest nieprawidłowy.");
        if (CountryCode.Length != 2) r.Error("Kod kraju musi mieć 2 litery (ISO 3166).");
        if (DefaultPaymentDays is < 0 or > 365) r.Error("Domyślny termin płatności: 0–365 dni.");
        return r;
    }
}
