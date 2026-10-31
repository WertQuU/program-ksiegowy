namespace PelnaKsiegowosc.Application.Payroll;

/// <summary>Art. 92 Kodeksu pracy: przyczyna decydująca o stawce 80% albo 100%.
/// Nie stosować tego modelu do zasiłku ZUS ani zasiłku wypadkowego.</summary>
public enum SickWageCause
{
    Ordinary80,
    Pregnancy100,
    CommuteAccident100,
    OrganOrTissueDonation100
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
    SickWageCause Cause,
    string EvidenceReference,
    DateTime ConfirmedAtUtc,
    Guid ConfirmedByUserId)
{
    public decimal RatePercent => Cause == SickWageCause.Ordinary80 ? 80m : 100m;
}
