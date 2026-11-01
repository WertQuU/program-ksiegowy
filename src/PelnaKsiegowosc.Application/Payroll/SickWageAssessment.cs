namespace PelnaKsiegowosc.Application.Payroll;

/// <summary>Historyczne wartości wejścia API art. 92 KP; do bazy zapisujemy wyłącznie stawkę.
/// Nie stosować tego modelu do zasiłku ZUS ani zasiłku wypadkowego.</summary>
public enum SickWageCause
{
    Ordinary80,
    Pregnancy100,
    CommuteAccident100,
    OrganOrTissueDonation100,
    Statutory100
}

/// <summary>
/// Udokumentowane ustalenie dla jednego pracownika i miesiąca choroby.
/// PreviousEmployerDays to wcześniej wykorzystane dni wynagrodzenia chorobowego
/// w tym samym roku, u INNYCH pracodawców, według świadectw/oświadczeń.
/// Jeśli w miesiącu są różne przyczyny lub okresy o innych stawkach,
/// należy rozdzielić rozliczenie; to ustalenie dotyczy całego miesiąca.
/// Przechowujemy jedynie odnośnik do dowodu, nie dane medyczne z dokumentu.
/// </summary>
public sealed record SickWageAssessment(
    int PreviousEmployerDays,
    decimal RatePercent,
    string EvidenceReference,
    DateTime ConfirmedAtUtc,
    Guid ConfirmedByUserId);


/// <summary>
/// Potwierdzenie kompletności składników podstawy chorobowego (art. 36–43 ustawy
/// zasiłkowej), odrębne od ustalenia dni i stawki z art. 92 KP.
/// Odcisk wiąże je z danymi konkretnej roboczej listy i historią wypłat.
/// Zapisujemy odnośnik do dokumentacji, bez danych o stanie zdrowia.
/// </summary>
public sealed record SickBasisCompletenessConfirmation(
    bool NoOtherQualifyingComponents,
    string EvidenceReference,
    string SourceFingerprint,
    DateTime ConfirmedAtUtc,
    Guid ConfirmedByUserId);
