using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Payroll;

public enum ContractType
{
    UmowaOPrace,
    UmowaZlecenie,
    UmowaODzielo,
    /// <summary>Wynagrodzenie z tytułu powołania (np. członek zarządu) — art. 13 pkt 7 ustawy o PIT.</summary>
    Powolanie
}

public enum CostsOfIncome
{
    /// <summary>Umowa o pracę: 250 zł / mies.; zlecenie/dzieło: 20%.</summary>
    Standardowe,
    /// <summary>Umowa o pracę: 300 zł / mies. (dojazd z innej miejscowości).</summary>
    Podwyzszone,
    /// <summary>Prawa autorskie: 50% (limit roczny 120 000 zł).</summary>
    Autorskie50,
    Brak
}

/// <summary>Osoba zatrudniona (pracownik, zleceniobiorca, wykonawca dzieła, członek zarządu). Rozrachunki — przez powiązanego kontrahenta.</summary>
public class Employee : CompanyEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Pesel { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string? Address { get; set; }
    /// <summary>Adres zamieszkania (PIT-11 poz. 19–27).</summary>
    public string? Street { get; set; }
    public string? HouseNumber { get; set; }
    public string? FlatNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Voivodeship { get; set; }
    public string? County { get; set; }
    public string? Commune { get; set; }
    public string? BankAccountIban { get; set; }
    /// <summary>Kod urzędu skarbowego właściwego dla podatnika (PIT-11).</summary>
    public string? TaxOfficeCode { get; set; }
    public ContractType ContractType { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>Wynagrodzenie miesięczne brutto (umowa o pracę, powołanie) albo domyślna kwota umowy cywilnoprawnej.</summary>
    public decimal Gross { get; set; }
    public decimal WorkTimeFraction { get; set; } = 1m;
    public CostsOfIncome Costs { get; set; } = CostsOfIncome.Standardowe;
    /// <summary>Złożony PIT-2 — płatnik stosuje kwotę zmniejszającą podatek (1/12 z 3600 zł).</summary>
    public bool Pit2 { get; set; }
    /// <summary>Ulga dla młodych (do 26 lat, art. 21 ust. 1 pkt 148 ustawy o PIT) — przychody zwolnione do 85 528 zł rocznie.</summary>
    public bool YouthRelief { get; set; }
    /// <summary>Uczeń / student do 26 lat — umowa zlecenia bez składek ZUS (art. 6 ust. 4 ustawy o sus).</summary>
    public bool StudentUnder26 { get; set; }
    /// <summary>Zlecenie: dobrowolne ubezpieczenie chorobowe.</summary>
    public bool VoluntarySickness { get; set; }
    /// <summary>Zwolnienie z FP/FGŚP (np. 60/55+, powrót z urlopu macierzyńskiego) — decyzja użytkownika.</summary>
    public bool ExemptFromFp { get; set; }
    /// <summary>Uczestnik PPK (pracownik i zleceniobiorca objęty ZUS).</summary>
    public bool Ppk { get; set; }
    public decimal PpkEmployeeRate { get; set; } = 2m;
    public decimal PpkEmployerRate { get; set; } = 1.5m;
    public Guid CounterpartyId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public enum PayrollStatus
{
    Robocza,
    Zaksiegowana
}

/// <summary>Lista płac za miesiąc (wypłata w <see cref="PaymentDate"/> — miesiąc wypłaty decyduje o PIT i ZUS).</summary>
public class PayrollList : CompanyEntity
{
    public string Number { get; set; } = "";
    public DateOnly Month { get; set; }
    public DateOnly PaymentDate { get; set; }
    public string Description { get; set; } = "";
    public PayrollStatus Status { get; set; } = PayrollStatus.Robocza;
    public Guid? JournalEntryId { get; set; }
    public string RulesVersion { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public List<PayrollItem> Items { get; set; } = new();
}

public class PayrollItem : Entity
{
    public Guid PayrollListId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public ContractType ContractType { get; set; }
    public decimal Gross { get; set; }
    /// <summary>Podstawa składek społecznych (przed limitem 30-krotności) — do sumowania narastająco.</summary>
    public decimal SocialBasis { get; set; }
    /// <summary>Dni choroby (wynagrodzenie chorobowe 80%, art. 92 KP) i nieobecności nieodpłatnej (dni robocze).</summary>
    public int SickDays { get; set; }
    public int UnpaidDays { get; set; }
    /// <summary>Wynagrodzenie chorobowe (bez składek społecznych; zdrowotna i PIT — tak).</summary>
    public decimal SickPay { get; set; }
    public decimal PpkEmployee { get; set; }
    /// <summary>Wpłata PPK pracodawcy — przychód pracownika (PIT), koszt pracodawcy.</summary>
    public decimal PpkEmployer { get; set; }
    /// <summary>Przychód zwolniony (ulga dla młodych).</summary>
    public decimal ExemptRevenue { get; set; }
    public decimal PensionEmployee { get; set; }
    public decimal DisabilityEmployee { get; set; }
    public decimal SicknessEmployee { get; set; }
    public decimal HealthBase { get; set; }
    public decimal Health { get; set; }
    public decimal CostsOfIncome { get; set; }
    public decimal TaxBase { get; set; }
    public decimal TaxAdvance { get; set; }
    /// <summary>Podatek zryczałtowany (umowy do 200 zł bez PIT-2 / oświadczenia) — PIT-8AR.</summary>
    public bool FlatRateTax { get; set; }
    public decimal Net { get; set; }
    public decimal PensionEmployer { get; set; }
    public decimal DisabilityEmployer { get; set; }
    public decimal Accident { get; set; }
    public decimal LaborFund { get; set; }
    public decimal Fgsp { get; set; }
    public string? Notes { get; set; }

    public decimal SocialEmployee => PensionEmployee + DisabilityEmployee + SicknessEmployee;
    public decimal EmployerCost => PensionEmployer + DisabilityEmployer + Accident + LaborFund + Fgsp;
}
