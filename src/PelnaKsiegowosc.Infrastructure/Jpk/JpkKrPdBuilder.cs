using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Jpk;

/// <summary>
/// JPK_CIT — księgi rachunkowe ze znacznikami podatkowymi (struktura JPK_KR_PD), art. 9 ust. 1e ustawy o CIT.
/// Zawiera: nagłówek, podmiot, zestawienie obrotów i sald ze znacznikami kont (pozycja bilansu / RZiS / kwalifikacja
/// podatkowa CIT), dziennik z zapisami na kontach oraz sumy kontrolne. Nazwy elementów i słowniki znaczników odtworzono
/// według opisu struktury — WYMAGAJĄ walidacji oficjalnym schematem XSD (przestrzeń nazw pobierana z wczytanego schematu).
/// </summary>
public static class JpkKrPdBuilder
{
    public const string SchemaFile = "Schemat_JPK_KR_PD(1)_v1-0E.xsd";
    public const string SystemCode = "JPK_KR_PD (1)";
    public const string SchemaVersion = "1-0E";
    public const string NamespacePlaceholder = "urn:pelna-ksiegowosc:jpk_kr_pd-1:wczytaj-oficjalny-schemat";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Znacznik podatkowy konta wg kwalifikacji CIT przypisanej w planie kont (słownik programu — do mapowania na słownik MF).</summary>
    public static string TaxMarker(CitClassification c) => c switch
    {
        CitClassification.PrzychodPodatkowy => "PD_P",
        CitClassification.PrzychodNiepodatkowy => "PD_PN",
        CitClassification.KosztPodatkowy => "PD_K",
        CitClassification.KosztNiepodatkowy => "PD_KN",
        CitClassification.DoAnalizy => "PD_A",
        _ => "PD_ND"
    };

    public static async Task<JpkResult> BuildAsync(KsiegiDbContext db, Company company, FiscalYear fy, bool correction, DateTime generatedUtc, XsdValidator validator, CancellationToken ct = default)
    {
        XNamespace ns = validator.TargetNamespace(SchemaFile) ?? NamespacePlaceholder;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki (wymagany w nagłówku JPK).");
        XElement E(string n, object? v) => new(ns + n, v);
        string D(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

        var tb = await new LedgerQueries(db).TrialBalanceAsync(fy.Id, fy.StartDate, fy.EndDate, excludeClosing: false, ct);
        var entries = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.FiscalYearId == fy.Id && e.Status == EntryStatus.Zaksiegowany).OrderBy(e => e.SequenceNumber).ToListAsync(ct);
        var accounts = tb.ToDictionary(t => t.Account.Id, t => t.Account);
        var cpIds = entries.SelectMany(e => e.Lines).Where(l => l.CounterpartyId != null).Select(l => l.CounterpartyId!.Value).Distinct().ToList();
        var counterparties = await db.Counterparties.AsNoTracking().Where(c => cpIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        if (fy.Status != FiscalYearStatus.Zamkniety) warnings.Add($"Rok {fy.Name} nie jest zamknięty — JPK_CIT składa się za zamknięty rok podatkowy.");

        var naglowek = new XElement(ns + "Naglowek",
            new XElement(ns + "KodFormularza", new XAttribute("kodSystemowy", SystemCode), new XAttribute("wersjaSchemy", SchemaVersion), "JPK_KR_PD"),
            E("WariantFormularza", "1"),
            E("CelZlozenia", correction ? "2" : "1"),
            E("DataWytworzeniaJPK", generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("DataOd", D(fy.StartDate)),
            E("DataDo", D(fy.EndDate)),
            E("KodUrzedu", company.TaxOfficeCode));
        var podmiot = new XElement(ns + "Podmiot1",
            new XElement(ns + "IdentyfikatorPodmiotu", E("NIP", Identifiers.NormalizeNip(company.Nip)), E("PelnaNazwa", company.FullName),
                string.IsNullOrWhiteSpace(company.Krs) ? null : E("KRS", company.Krs)),
            new XElement(ns + "AdresPodmiotu", E("KodKraju", "PL"), E("Miejscowosc", company.City), E("Ulica", company.Street),
                E("NrDomu", company.BuildingNumber), E("KodPocztowy", company.PostalCode)));

        // Zestawienie obrotów i sald ze znacznikami (konta analityczne — najniższy poziom)
        var zois = new XElement(ns + "ZOiS");
        decimal zoisDt = 0, zoisCt = 0;
        var leafs = tb.Where(t => t.IsLeaf).OrderBy(t => t.Account.Number, StringComparer.Ordinal).ToList();
        foreach (var t in leafs)
        {
            var a = t.Account;
            var closingDt = Math.Max(0, t.TotalDebit - t.TotalCredit);
            var closingCt = Math.Max(0, t.TotalCredit - t.TotalDebit);
            zoisDt += t.TotalDebit;
            zoisCt += t.TotalCredit;
            var row = new XElement(ns + "ZOiSWiersz",
                E("KodKonta", a.Number), E("OpisKonta", a.Name), E("TypKonta", a.Type.ToString()),
                E("KodZespolu", a.Number[..1]),
                E("BilansOtwarciaWinien", Money.Xml(t.OpeningDebit)), E("BilansOtwarciaMa", Money.Xml(t.OpeningCredit)),
                E("ObrotyWinien", Money.Xml(t.CumulativeDebit)), E("ObrotyMa", Money.Xml(t.CumulativeCredit)),
                E("SaldoWinien", Money.Xml(closingDt)), E("SaldoMa", Money.Xml(closingCt)),
                E("ZnacznikBilans", a.BalanceDebitLine ?? a.BalanceCreditLine ?? ""),
                E("ZnacznikRZiS", a.ProfitLossLine ?? ""),
                E("ZnacznikPD", TaxMarker(a.CitClassification)));
            zois.Add(row);
            if (a.IsResultAccount && a.CitClassification == CitClassification.NieDotyczy && (t.CumulativeDebit != 0 || t.CumulativeCredit != 0))
                warnings.Add($"Konto {a.Number} {a.Name}: brak kwalifikacji podatkowej (znacznik PD) — uzupełnij w planie kont.");
            if (a.IsResultAccount && a.CitClassification == CitClassification.DoAnalizy && (t.CumulativeDebit != 0 || t.CumulativeCredit != 0))
                warnings.Add($"Konto {a.Number} {a.Name}: kwalifikacja „do analizy” — przed wysłaniem ustal znacznik podatkowy.");
        }

        // Dziennik i zapisy na kontach
        var dziennik = new XElement(ns + "Dziennik");
        var zapisy = new XElement(ns + "KontoZapis");
        decimal dzSum = 0, zapDt = 0, zapCt = 0;
        var lp = 0;
        foreach (var e in entries)
        {
            var amount = e.TotalDebit;
            dzSum += amount;
            dziennik.Add(new XElement(ns + "DziennikWiersz",
                E("LpZapisuDziennika", e.SequenceNumber ?? ++lp),
                E("NrZapisuDziennika", e.JournalNumber ?? e.DocumentNumber),
                E("OpisDziennika", e.JournalCode),
                E("NrDowoduKsiegowego", e.DocumentNumber),
                E("RodzajDowodu", string.IsNullOrWhiteSpace(e.DocumentType) ? e.Kind.ToString() : e.DocumentType),
                E("DataOperacji", D(e.OperationDate)),
                E("DataDowodu", D(e.DocumentDate)),
                E("DataKsiegowania", D(e.PostingDate)),
                E("KodOperatora", e.PostedByUserId?.ToString("N")[..8] ?? ""),
                E("OpisOperacji", e.Description),
                E("DziennikKwotaOperacji", Money.Xml(amount))));
            foreach (var l in e.Lines.OrderBy(l => l.LineNo))
            {
                if (!accounts.TryGetValue(l.AccountId, out var a)) continue;
                zapDt += l.Debit;
                zapCt += l.Credit;
                var cp = l.CounterpartyId is { } id && counterparties.TryGetValue(id, out var c) ? c : null;
                zapisy.Add(new XElement(ns + "KontoZapisWiersz",
                    E("LpZapisu", e.SequenceNumber ?? lp),
                    E("NrZapisu", e.JournalNumber ?? e.DocumentNumber),
                    E("KodKontaWinien", l.Debit != 0 ? a.Number : "null"),
                    E("KwotaWinien", Money.Xml(l.Debit)),
                    E("KodKontaMa", l.Credit != 0 ? a.Number : "null"),
                    E("KwotaMa", Money.Xml(l.Credit)),
                    cp is null ? null : E("NrKontrahenta", string.IsNullOrWhiteSpace(cp.Nip) ? cp.Code : cp.Nip),
                    E("ZnacznikPD", TaxMarker(l.CitClassification ?? a.CitClassification))));
            }
        }
        var ctrl = new XElement(ns + "Ctrl",
            E("LiczbaWierszyZOiS", leafs.Count), E("SumaWinienZOiS", Money.Xml(zoisDt)), E("SumaMaZOiS", Money.Xml(zoisCt)),
            E("LiczbaWierszyDziennika", entries.Count), E("SumaKwotOperacji", Money.Xml(dzSum)),
            E("LiczbaWierszyKontoZapisj", zapisy.Elements().Count()), E("SumaWinien", Money.Xml(zapDt)), E("SumaMa", Money.Xml(zapCt)));
        if (zapDt != zapCt) warnings.Add($"Suma zapisów Wn ({Money.Format(zapDt)}) ≠ Ma ({Money.Format(zapCt)}).");

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "JPK", new XAttribute(XNamespace.Xmlns + "tns", ns.NamespaceName), naglowek, podmiot, zois, dziennik, zapisy, ctrl));
        var validation = validator.Validate(doc, SchemaFile, "JPK_KR_PD(1)");
        if (!validation.IsValid) warnings.Add("Plik JPK_CIT nie został zweryfikowany oficjalnym schematem XSD — nie wysyłaj go bez walidacji.");
        warnings.Add("Znaczniki podatkowe wynikają z kwalifikacji CIT kont i pozycji (kategorie NKUP) — przed wysłaniem porównaj je ze słownikiem znaczników MF.");
        return new JpkResult(doc, validation, warnings);
    }
}
