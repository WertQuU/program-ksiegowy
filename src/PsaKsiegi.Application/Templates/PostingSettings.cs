namespace PsaKsiegi.Application.Templates;

/// <summary>
/// Parametry księgowania — numery kont stosowane w schematach automatycznej dekretacji.
/// Przechowywane w bazie (AppSetting „posting.accounts”), edytowalne w ustawieniach.
/// </summary>
public sealed class PostingSettings
{
    public const string SettingKey = "posting.accounts";

    public string Receivables { get; set; } = "200";
    public string Payables { get; set; } = "210";
    public string SalesRevenueDefault { get; set; } = "700-01";
    public string SalesRevenueForeign { get; set; } = "700-02";
    public string VatOutput { get; set; } = "221-01";
    public string VatInput { get; set; } = "221-02";
    public string VatInputDeferred { get; set; } = "221-03";
    public string VatSettlement { get; set; } = "221-04";
    public string PurchaseCostDefault { get; set; } = "402";
    public string NonDeductibleVatCost { get; set; } = "409-04";
    public string Bank { get; set; } = "130-01";
    public string Cash { get; set; } = "100";
    public string BankFees { get; set; } = "751-03";
    public string FxGains { get; set; } = "750-02";
    public string FxLosses { get; set; } = "751-02";
    public string InterestIncome { get; set; } = "750-01";
    public string FixedAssetsSuspense { get; set; } = "080";
    public string OtherOperatingIncome { get; set; } = "760-03";
    public string OtherOperatingCosts { get; set; } = "761-03";
    public string DisposalGain { get; set; } = "760-01";
    public string DisposalLoss { get; set; } = "761-01";
    public string ShareCapital { get; set; } = "801";
    public string SupplementaryCapital { get; set; } = "802";
    public string ReserveCapital { get; set; } = "804";
    public string ShareContributionsDue { get; set; } = "245-01";
    public string DividendsPayable { get; set; } = "245-02";
    public string ShareholderLoans { get; set; } = "245-03";
    public string ShareholderOther { get; set; } = "245-04";
    public string WithholdingTax { get; set; } = "220-04";
    public string RetainedEarnings { get; set; } = "820";
    public string DividendAdvances { get; set; } = "821";
    public string FinancialResult { get; set; } = "860";
    public string IncomeTaxExpense { get; set; } = "870";
    public string CitPayable { get; set; } = "220-01";

    public IEnumerable<(string Name, string Number)> All() => GetType().GetProperties()
        .Where(p => p.PropertyType == typeof(string))
        .Select(p => (p.Name, (string)p.GetValue(this)!));
}
