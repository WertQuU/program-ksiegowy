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

/// <summary>
/// Tytuły do ubezpieczeń społecznych osoby POZA tym płatnikiem — do ustalenia, czy umowa zlecenia podlega obowiązkowo
/// ubezpieczeniom emerytalnemu i rentowym (art. 9 ust. 1c, 2, 2a–2b ustawy o systemie ubezpieczeń społecznych).
/// </summary>
public enum ExternalInsuranceTitle
{
    /// <summary>Nie ustalono (brak oświadczenia zleceniobiorcy) — listy z taką umową nie można zaksięgować.</summary>
    Nieustalone,
    /// <summary>Brak innych tytułów poza tym płatnikiem (także emeryt/rencista bez innych umów) — zlecenie obowiązkowo.</summary>
    Brak,
    /// <summary>Umowa o pracę u innego pracodawcy — obowiązkowo ze zlecenia tylko, gdy podstawa z etatu &lt; minimalne wynagrodzenie.</summary>
    EtatUInnegoPracodawcy,
    /// <summary>Wcześniej zawarta umowa zlecenia u innego zleceniodawcy — obowiązkowo, gdy podstawa z niej &lt; minimalne wynagrodzenie.</summary>
    ZlecenieUInnegoZleceniodawcy,
    /// <summary>Inny zbieg (np. działalność gospodarcza) ustalony przez płatnika: zlecenie podlega obowiązkowo.</summary>
    ObowiazkoweWgUstalenia,
    /// <summary>Inny zbieg (np. działalność gospodarcza) ustalony przez płatnika: ze zlecenia emerytalne i rentowe nie są obowiązkowe.</summary>
    DobrowolneWgUstalenia
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
    /// <summary>
    /// Umowa zlecenia / o dzieło: łączna kwota należności określona w umowie (art. 30 ust. 1 pkt 5a ustawy o PIT — ryczałt tylko,
    /// gdy nie przekracza 200 zł; decyduje kwota z umowy, nie pojedyncza wypłata). Brak — przyjmowana jest kwota wypłaty z ostrzeżeniem.
    /// </summary>
    public decimal? ContractTotalAmount { get; set; }
    /// <summary>Umowa zlecenia / o dzieło zawarta z osobą trzecią, ale wykonywana na rzecz własnego pracodawcy (art. 8 ust. 2a ustawy o sus) — składki jak od stosunku pracy.</summary>
    public bool WorkForOwnEmployer { get; set; }
    /// <summary>Koszty 50%: udział wynagrodzenia za korzystanie z praw autorskich / rozporządzanie nimi (%, wg umowy i ewidencji utworów).</summary>
    public decimal AuthorSharePercent { get; set; } = 100m;
    /// <summary>Koszty 50%: podstawa (umowa, ewidencja utworów, przeniesienie praw) — wymagana przy stosowaniu kosztów autorskich.</summary>
    public string? AuthorCostsBasis { get; set; }
    public decimal WorkTimeFraction { get; set; } = 1m;
    public CostsOfIncome Costs { get; set; } = CostsOfIncome.Standardowe;
    /// <summary>
    /// PIT-2 (art. 31a / art. 32 ust. 3 ustawy o PIT): miesięczna kwota zmniejszająca zaliczkę stosowana przez tego płatnika —
    /// 0 (brak oświadczenia), 100 zł (1/36), 150 zł (1/24) albo 300 zł (1/12 z 3600 zł). Stosowana raz w miesiącu na osobę, łącznie
    /// dla wszystkich jej umów i list płac z wypłatą w tym miesiącu.
    /// </summary>
    public decimal Pit2Reduction { get; set; }
    /// <summary>Zgodność wsteczna: PIT-2 złożony (true ustawia 1/12 = 300 zł, jeśli nie wybrano innej kwoty).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool Pit2
    {
        get => Pit2Reduction > 0;
        set => Pit2Reduction = value ? (Pit2Reduction > 0 ? Pit2Reduction : 300m) : 0m;
    }
    /// <summary>Umowa zlecenia: tytuły do ubezpieczeń poza tym płatnikiem (zbieg tytułów).</summary>
    public ExternalInsuranceTitle ExternalTitles { get; set; }
    /// <summary>Miesięczna podstawa składek z tytułu poza płatnikiem (etat u innego pracodawcy / wcześniejsze zlecenie).</summary>
    public decimal? ExternalTitlesBasis { get; set; }
    /// <summary>Zlecenie niepodlegające obowiązkowo — wniosek o dobrowolne ubezpieczenia emerytalne i rentowe (wtedy także wypadkowe).</summary>
    public bool VoluntaryPension { get; set; }
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
    /// <summary>Roczny wymiar urlopu wypoczynkowego (20 lub 26 dni — art. 154 KP), dla pełnego etatu.</summary>
    public int AnnualLeaveDays { get; set; } = 26;
    /// <summary>Urlop zaległy przeniesiony z okresu przed rozpoczęciem ewidencji w programie (dni).</summary>
    public decimal LeaveCarriedOver { get; set; }
    /// <summary>Ważność badań lekarskich okresowych i szkolenia BHP.</summary>
    public DateOnly? MedicalExamUntil { get; set; }
    public DateOnly? BhpTrainingUntil { get; set; }
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
    /// <summary>Data przekazania wpłat PPK z tej listy do instytucji finansowej — wpłata pracodawcy staje się wtedy przychodem pracownika.</summary>
    public DateOnly? PpkTransferredOn { get; set; }
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
    /// <summary>Dni choroby opłacone wynagrodzeniem chorobowym pracodawcy (do limitu art. 92 KP).</summary>
    public int SickDaysEmployer { get; set; }
    public int UnpaidDays { get; set; }
    /// <summary>Wynagrodzenie chorobowe (bez składek społecznych; zdrowotna i PIT — tak).</summary>
    public decimal SickPay { get; set; }
    /// <summary>Wynagrodzenie zasadnicze za pełny miesiąc przyjęte do wyliczenia (do ponownego przeliczenia pozycji); null — kwota z ewidencji osoby, 0 — świadomie zero.</summary>
    public decimal? BaseGross { get; set; }
    /// <summary>Koszty 50% (prawa autorskie) w tej pozycji — do limitu rocznego.</summary>
    public decimal AuthorCosts { get; set; }
    public decimal PpkEmployee { get; set; }
    /// <summary>Wpłata PPK pracodawcy naliczona na tej liście — koszt pracodawcy; przychodem pracownika staje się w dniu przekazania do instytucji finansowej.</summary>
    public decimal PpkEmployer { get; set; }
    /// <summary>Wpłaty PPK pracodawcy z wcześniejszych list, przekazane do instytucji finansowej — opodatkowane w tej pozycji (przychód pracownika).</summary>
    public decimal PpkEmployerTaxed { get; set; }
    /// <summary>Lista płac, w której opodatkowano wpłatę PPK pracodawcy z tej pozycji (po zaksięgowaniu tamtej listy).</summary>
    public Guid? PpkTaxedInListId { get; set; }
    /// <summary>Kwota zmniejszająca zaliczkę (PIT-2) zastosowana w tej pozycji.</summary>
    public decimal TaxReduction { get; set; }
    /// <summary>Umowa zlecenia z nieustalonym zbiegiem tytułów do ubezpieczeń — listy nie można zaksięgować.</summary>
    public bool InsuranceUndetermined { get; set; }
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

/// <summary>Rodzaj nieobecności w ewidencji czasu pracy.</summary>
public enum AbsenceKind
{
    UrlopWypoczynkowy,
    UrlopNaZadanie,
    Choroba,
    UrlopBezplatny,
    Opieka,
    UrlopOkolicznosciowy,
    NieobecnoscNieusprawiedliwiona
}

/// <summary>Nieobecność pracownika (od–do, włącznie). Choroba i urlop bezpłatny trafiają automatycznie na listę płac.</summary>
public class Absence : CompanyEntity
{
    public Guid EmployeeId { get; set; }
    public AbsenceKind Kind { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public bool IsLeave => Kind is AbsenceKind.UrlopWypoczynkowy or AbsenceKind.UrlopNaZadanie;
}
