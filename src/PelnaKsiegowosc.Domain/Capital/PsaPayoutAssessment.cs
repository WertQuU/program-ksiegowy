using System.Text.Json;

namespace PelnaKsiegowosc.Domain.Capital;

/// <summary>
/// Ustrukturyzowana kontrola części liczbowej art. 300¹⁵ i 300¹⁹ KSH.
/// Wejście stanowi przeniesienie kwot z zatwierdzonego sprawozdania i uchwał;
/// sama walidacja nie ustala autentyczności dokumentów ani prawidłowości
/// scenariusza płynności. Przechowywana w istniejącym polu SolvencyTestNotes
/// jako JSON, bez zmiany schematu bazy w wersji 1.0.0.
/// </summary>
public sealed class PsaPayoutAssessment
{
    public string? ApprovedFinancialStatementReference { get; set; }
    public string? SixMonthForecastReference { get; set; }
    public decimal LastYearProfit { get; set; }
    public decimal UndistributedProfits { get; set; }
    public decimal DistributableReserves { get; set; }
    public decimal ShareCapitalApprovedForDistribution { get; set; }
    public decimal UncoveredLosses { get; set; }
    public decimal TreasurySharesValue { get; set; }
    public decimal NonDistributableReserveAllocation { get; set; }
    public decimal ShareCapitalBefore { get; set; }
    public decimal LiabilitiesInApprovedStatement { get; set; }
    /// <summary>Najniższe przewidywane saldo środków dostępnych po płatnościach przez kolejne 6 miesięcy, w PLN.</summary>
    public decimal MinimumProjectedLiquidityAfterPayout { get; set; }
    public bool SixMonthSolvencyConfirmed { get; set; }
    public bool CreditorProtectionRequirementsConfirmed { get; set; }
    public bool CapitalReductionRegisteredInKrs { get; set; }

    public decimal MandatoryCapitalAllocation =>
        ShareCapitalBefore < LiabilitiesInApprovedStatement * 0.05m && LastYearProfit > 0
            ? Math.Round(LastYearProfit * 0.08m, 2, MidpointRounding.AwayFromZero)
            : 0m;

    public decimal DistributableLimit =>
        LastYearProfit + UndistributedProfits + DistributableReserves + ShareCapitalApprovedForDistribution -
        UncoveredLosses - TreasurySharesValue - NonDistributableReserveAllocation;

    public static bool TryParse(string? notes, out PsaPayoutAssessment? assessment)
    {
        assessment = null;
        if (string.IsNullOrWhiteSpace(notes)) return false;
        try
        {
            assessment = JsonSerializer.Deserialize<PsaPayoutAssessment>(notes);
            return assessment is not null;
        }
        catch (JsonException) { return false; }
    }

    public IReadOnlyList<string> Problems(CapitalEvent ev)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(ApprovedFinancialStatementReference))
            issues.Add("wymagany identyfikator zatwierdzonego sprawozdania finansowego");
        if (string.IsNullOrWhiteSpace(SixMonthForecastReference))
            issues.Add("wymagany identyfikator prognozy płynności na 6 miesięcy");
        if (!SixMonthSolvencyConfirmed || MinimumProjectedLiquidityAfterPayout < 0)
            issues.Add("brak potwierdzenia zdolności regulowania zobowiązań w okresie 6 miesięcy po wypłacie");
        if (LastYearProfit < 0 || UndistributedProfits < 0 || DistributableReserves < 0 ||
            ShareCapitalApprovedForDistribution < 0 || UncoveredLosses < 0 || TreasurySharesValue < 0 ||
            NonDistributableReserveAllocation < 0 || ShareCapitalBefore < 1 || LiabilitiesInApprovedStatement < 0)
            issues.Add("nieprawidłowe (ujemne lub brakujące) kwoty elementów kalkulacji P.S.A.");
        if (NonDistributableReserveAllocation < MandatoryCapitalAllocation)
            issues.Add($"art. 300¹⁹ KSH: należy przeznaczyć co najmniej {MandatoryCapitalAllocation:0.00} zł na obowiązkowe zasilenie kapitału akcyjnego");
        // Obniżenie kapitału jest księgowane z ShareCapitalAmount, nawet jeżeli
        // stary formularz pozostawił TotalAmount równe zero. Nie wolno wtedy
        // uznać wypłaty za zerową przy teście z art. 300¹⁵ § 2 KSH.
        var proposedAmount = ev.Type == CapitalEventType.ObnizenieKapitaluAkcyjnego
            ? Math.Max(ev.TotalAmount, ev.ShareCapitalAmount)
            : ev.TotalAmount;
        if (DistributableLimit < proposedAmount)
            issues.Add($"art. 300¹⁵ § 2 KSH: limit podziału {DistributableLimit:0.00} zł jest niższy od kwoty zdarzenia {proposedAmount:0.00} zł");
        if (ev.Type == CapitalEventType.ObnizenieKapitaluAkcyjnego &&
            ShareCapitalApprovedForDistribution < ev.ShareCapitalAmount)
            issues.Add("art. 300¹⁵ § 2 KSH: suma środków z kapitału akcyjnego zatwierdzonych do wypłaty nie pokrywa całego obniżenia");
        if (ev.Type == CapitalEventType.ZaliczkaNaPoczetDywidendy && ShareCapitalApprovedForDistribution != 0)
            issues.Add("art. 300¹⁷ KSH: zaliczki nie można wypłacać z kapitału akcyjnego");
        if (ShareCapitalApprovedForDistribution > 0 || ev.Type == CapitalEventType.ObnizenieKapitaluAkcyjnego)
        {
            if (ShareCapitalBefore - Math.Max(ShareCapitalApprovedForDistribution, ev.ShareCapitalAmount) < 1m)
                issues.Add("art. 300¹⁵ § 4 KSH: kapitał akcyjny po wypłacie musi wynosić co najmniej 1 zł");
            if (!CapitalReductionRegisteredInKrs || string.IsNullOrWhiteSpace(ev.KrsEntryReference))
                issues.Add("art. 300¹⁵ § 6 KSH: wymagana uprzednia rejestracja obniżenia kapitału akcyjnego w KRS");
            if (!CreditorProtectionRequirementsConfirmed)
                issues.Add("wymagane zweryfikowanie ewentualnej procedury ochrony wierzycieli przy wypłacie z kapitału akcyjnego");
        }
        return issues;
    }
}
