using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Jpk;

/// <summary>
/// JPK_CIT — księgi rachunkowe ze znacznikami (JPK_KR_PD(1), wersja schematu 1-1, art. 9 ust. 1e ustawy o CIT), zgodnie
/// z oficjalnym schematem MF (schemas/MF): nagłówek, podmiot, kontrahenci, zestawienie obrotów i sald jednostek pozostałych
/// (ZOiS7) ze znacznikiem pozycji sprawozdania (S_12_1) i znacznikiem podatkowym (S_12_3), dziennik z zapisami na kontach,
/// sumy kontrolne i rozliczenie podatku dochodowego (RPD).
/// </summary>
public static class JpkKrPdBuilder
{
    public const string SchemaFile = "Schemat_JPK_KR_PD(1)_v1-1.xsd";
    public static readonly XNamespace Ns = "http://jpk.mf.gov.pl/wzor/2024/09/04/09041/";
    public static readonly XNamespace Etd = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Znacznik podatkowy konta (słownik TMapKontaPD) wg kwalifikacji CIT; null — konto bez znacznika podatkowego.</summary>
    public static string? TaxMarker(CitClassification c, string? profitLossLine = null) => c switch
    {
        CitClassification.KosztNiepodatkowy => "PD4",
        CitClassification.PrzychodNiepodatkowy when profitLossLine != "R.A.II" => "PD1",
        _ => null
    };

    /// <summary>Znacznik konta (słownik TMapKontaPOZ) wyznaczony z pozycji bilansu / RZiS przypisanej do konta.</summary>
    public static string StatementMarker(Account a, decimal closingBalance)
    {
        string? line = a.Type switch
        {
            AccountType.WynikFinansowy => "P.A.VI",
            AccountType.WynikoweKosztowe or AccountType.WynikowePrzychodowe => a.ProfitLossLine,
            AccountType.AktywnoPasywne => closingBalance < 0 ? a.BalanceCreditLine ?? a.BalanceDebitLine : a.BalanceDebitLine ?? a.BalanceCreditLine,
            _ => a.BalanceDebitLine ?? a.BalanceCreditLine
        };
        if (line is null) return "INNE";
        string prefix;
        string[] prefer;
        if (line.StartsWith("R."))
        {
            prefix = "RP" + line[2..].Replace(".", "");
            prefer = new[] { "", "_POZ" };
        }
        else if (line.StartsWith("P."))
        {
            prefix = "BP" + line[2..].Replace(".", "");
            prefer = new[] { "", "_D12", "_INN", "_K" };
        }
        else
        {
            prefix = "BA" + line.Replace(".", "");
            prefer = a.Number.StartsWith("07") ? new[] { "_U", "", "_W" } : new[] { "", "_W", "_D12", "1_SPKR" };
        }
        var rx = new Regex("^" + Regex.Escape(prefix) + "([a-z]|[0-9]_)?(_|$)");
        var candidates = JpkKrPdMarkers.Poz.Where(v => v == prefix || rx.IsMatch(v) || v.StartsWith(prefix + "1_")).ToList();
        foreach (var p in prefer)
        {
            var hit = p switch
            {
                "" => candidates.FirstOrDefault(c => c == prefix),
                "_POZ" => candidates.Where(c => c.Contains("_POZ")).OrderBy(c => c.Length).FirstOrDefault(),
                _ => candidates.FirstOrDefault(c => c == prefix + p) ?? candidates.FirstOrDefault(c => c.EndsWith(p) && c.StartsWith(prefix))
            };
            if (hit is not null) return hit;
        }
        return candidates.OrderBy(c => c.Length).FirstOrDefault() ?? "INNE";
    }

    private static string Text(string? s, int max = 256)
    {
        var t = string.IsNullOrWhiteSpace(s) ? "-" : Regex.Replace(s.Trim(), @"\s+", " ");
        return t.Length > max ? t[..max] : t;
    }

    public static async Task<JpkResult> BuildAsync(KsiegiDbContext db, Company company, FiscalYear fy, bool correction, DateTime generatedUtc, XsdValidator validator, CancellationToken ct = default)
    {
        var ns = Ns;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki (wymagany w nagłówku JPK).");
        if (string.IsNullOrWhiteSpace(company.Voivodeship) || string.IsNullOrWhiteSpace(company.County) || string.IsNullOrWhiteSpace(company.Commune))
            throw new DomainException("Uzupełnij województwo, powiat i gminę siedziby w danych jednostki — są wymagane w adresie JPK_CIT.");
        XElement E(string n, object? v) => new(ns + n, v);
        XElement D(string n, object? v) => new(Etd + n, v);
        string Dt(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);
        string M(decimal v) => v.ToString("0.00", Inv);

        var tb = await new LedgerQueries(db).TrialBalanceAsync(fy.Id, fy.StartDate, fy.EndDate, excludeClosing: false, ct);
        var entries = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.FiscalYearId == fy.Id && e.Status == EntryStatus.Zaksiegowany).OrderBy(e => e.SequenceNumber).ToListAsync(ct);
        var accounts = tb.ToDictionary(t => t.Account.Id, t => t.Account);
        var cpIds = entries.SelectMany(e => e.Lines).Where(l => l.CounterpartyId != null).Select(l => l.CounterpartyId!.Value).Distinct().ToList();
        var counterparties = await db.Counterparties.AsNoTracking().Where(c => cpIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var docIds = entries.Where(e => e.SourceDocumentId != null).Select(e => e.SourceDocumentId!.Value).Distinct().ToList();
        var ksef = await db.Documents.AsNoTracking().Where(d => docIds.Contains(d.Id) && d.KsefNumber != null).ToDictionaryAsync(d => d.Id, d => d.KsefNumber!, ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Login : u.DisplayName, ct);
        if (fy.Status != FiscalYearStatus.Zamkniety) warnings.Add($"Rok {fy.Name} nie jest zamknięty — JPK_CIT składa się za zamknięty rok podatkowy.");

        var naglowek = new XElement(ns + "Naglowek",
            new XElement(ns + "KodFormularza", new XAttribute("kodSystemowy", "JPK_KR_PD (1)"), new XAttribute("wersjaSchemy", "1-1"), "JPK_KR_PD"),
            E("WariantFormularza", "1"),
            E("CelZlozenia", correction ? "2" : "1"),
            E("DataWytworzeniaJPK", generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("DataOd", Dt(fy.StartDate)),
            E("DataDo", Dt(fy.EndDate)),
            E("RokDataOd", Dt(fy.StartDate)),
            E("RokDataDo", Dt(fy.EndDate)),
            E("DomyslnyKodWaluty", "PLN"),
            E("KodUrzedu", company.TaxOfficeCode));
        var podmiot = new XElement(ns + "Podmiot1",
            new XElement(ns + "IdentyfikatorPodmiotu", D("NIP", Identifiers.NormalizeNip(company.Nip)), D("PelnaNazwa", Text(company.FullName, 240)),
                string.IsNullOrWhiteSpace(company.Regon) ? null : D("REGON", company.Regon!.Trim())),
            new XElement(ns + "Adres", new XElement(ns + "AdresPol",
                D("KodKraju", "PL"), D("Wojewodztwo", company.Voivodeship!.Trim()), D("Powiat", company.County!.Trim()), D("Gmina", company.Commune!.Trim()),
                string.IsNullOrWhiteSpace(company.Street) ? null : D("Ulica", company.Street.Trim()),
                D("NrDomu", company.BuildingNumber.Trim()),
                string.IsNullOrWhiteSpace(company.ApartmentNumber) ? null : D("NrLokalu", company.ApartmentNumber!.Trim()),
                D("Miejscowosc", company.City.Trim()), D("KodPocztowy", company.PostalCode.Trim()))));

        var kontrahenci = counterparties.Values.OrderBy(c => c.Code).Select(c => new XElement(ns + "Kontrahent",
            E("T_1", Text(c.Code)),
            string.IsNullOrWhiteSpace(c.Nip) ? null : E("T_2", string.IsNullOrWhiteSpace(c.CountryCode) ? "PL" : c.CountryCode.ToUpperInvariant()),
            string.IsNullOrWhiteSpace(c.Nip) ? null : E("T_3", c.CountryCode is null or "PL" ? Identifiers.NormalizeNip(c.Nip) : c.Nip!.Trim()))).ToList();

        // Zestawienie obrotów i sald — konta analityczne (najniższy poziom), wariant „jednostki pozostałe” (ZOiS7)
        var zois = new XElement(ns + "ZOiS");
        var leafs = tb.Where(t => t.IsLeaf && (t.TotalDebit != 0 || t.TotalCredit != 0)).OrderBy(t => t.Account.Number, StringComparer.Ordinal).ToList();
        var parentNumbers = tb.ToDictionary(t => t.Account.Id, t => t.Account.Number);
        foreach (var t in leafs)
        {
            var a = t.Account;
            var closing = t.TotalDebit - t.TotalCredit;
            var marker = StatementMarker(a, closing);
            if (marker == "INNE" && a.Type != AccountType.Pozabilansowe)
                warnings.Add($"Konto {a.Number} {a.Name}: brak przypisania do pozycji sprawozdania — znacznik „INNE”. Uzupełnij przypisanie w planie kont.");
            var pd = TaxMarker(a.CitClassification, a.ProfitLossLine);
            if (a.IsResultAccount && a.CitClassification == CitClassification.DoAnalizy && (t.CumulativeDebit != 0 || t.CumulativeCredit != 0))
                warnings.Add($"Konto {a.Number} {a.Name}: kwalifikacja podatkowa „do analizy” — ustal ją w planie kont (znacznik PD).");
            zois.Add(new XElement(ns + "ZOiS7",
                E("S_1", a.Number), E("S_2", Text(a.Name)),
                E("S_3", a.ParentId is { } pid && parentNumbers.TryGetValue(pid, out var pn) ? pn : a.Number),
                E("S_4", M(t.OpeningDebit)), E("S_5", M(t.OpeningCredit)),
                E("S_6", M(t.CumulativeDebit)), E("S_7", M(t.CumulativeCredit)),
                E("S_8", M(t.CumulativeDebit)), E("S_9", M(t.CumulativeCredit)),
                E("S_10", M(Math.Max(0, closing))), E("S_11", M(Math.Max(0, -closing))),
                E("S_12_1", marker),
                pd is null ? null : E("S_12_3", pd)));
        }
        if (leafs.Count == 0) throw new DomainException("Brak zapisów w roku — JPK_CIT nie może być pusty.");

        // Dziennik z zapisami na kontach
        var dzienniki = new List<XElement>();
        decimal dzSum = 0, sumDt = 0, sumCt = 0;
        int lines = 0;
        foreach (var e in entries)
        {
            var amount = e.TotalDebit;
            dzSum += amount;
            var cpLine = e.Lines.FirstOrDefault(l => l.CounterpartyId is { } id && counterparties.ContainsKey(id));
            var d = new XElement(ns + "Dziennik",
                E("D_1", e.JournalNumber ?? e.SequenceNumber?.ToString(Inv) ?? e.DocumentNumber),
                E("D_2", Text(e.JournalCode)),
                cpLine is null ? null : E("D_3", Text(counterparties[cpLine.CounterpartyId!.Value].Code)),
                E("D_4", Text(e.DocumentNumber)),
                E("D_5", Text(string.IsNullOrWhiteSpace(e.DocumentType) ? e.Kind.ToString() : e.DocumentType)),
                E("D_6", Dt(e.OperationDate)), E("D_7", Dt(e.DocumentDate)), E("D_8", Dt(e.PostingDate)),
                E("D_9", Text(e.PostedByUserId is { } uid && users.TryGetValue(uid, out var un) ? un : "operator")),
                E("D_10", Text(e.Description, 512)),
                E("D_11", M(amount)),
                e.SourceDocumentId is { } sd && ksef.TryGetValue(sd, out var kn) ? E("D_12", kn) : null);
            foreach (var l in e.Lines.OrderBy(l => l.LineNo))
            {
                if (!accounts.TryGetValue(l.AccountId, out var a)) continue;
                lines++;
                sumDt += l.Debit;
                sumCt += l.Credit;
                d.Add(new XElement(ns + "KontoZapis",
                    E("Z_1", lines.ToString(Inv)),
                    E("Z_2", Text(l.Description ?? e.Description, 512)),
                    E("Z_3", a.Number),
                    l.Debit != 0 ? E("Z_4", M(l.Debit)) : E("Z_7", M(l.Credit))));
            }
            dzienniki.Add(d);
        }
        if (dzienniki.Count == 0) throw new DomainException("Brak zaksięgowanych zapisów w roku.");
        var ctrl = new XElement(ns + "Ctrl", E("C_1", dzienniki.Count), E("C_2", M(dzSum)), E("C_3", lines), E("C_4", M(sumDt)), E("C_5", M(sumCt)));
        if (sumDt != sumCt) warnings.Add($"Suma zapisów Wn ({Money.Format(sumDt)}) ≠ Ma ({Money.Format(sumCt)}).");

        // Rozliczenie podatku dochodowego — różnice z kwalifikacji kont/pozycji oraz korekt wprowadzonych w sekcji CIT
        decimal Effective(Func<CitClassification, bool> pred, bool revenue) => entries.Where(e => e.Kind != EntryKind.Zamkniecie && e.Kind != EntryKind.BilansOtwarcia)
            .SelectMany(e => e.Lines).Where(l => accounts.TryGetValue(l.AccountId, out var a) && a.IsResultAccount && a.ProfitLossLine != "R.A.II" && pred(l.CitClassification ?? a.CitClassification))
            .Sum(l => revenue ? l.Credit - l.Debit : l.Debit - l.Credit);
        var adjustments = await db.CitAdjustments.AsNoTracking().Where(a => a.FiscalYearId == fy.Id).ToListAsync(ct);
        decimal Adj(CitAdjustmentType t) => adjustments.Where(a => a.Type == t).Sum(a => a.Amount);
        var rpd = new XElement(ns + "RPD",
            E("K_1", M(Effective(c => c == CitClassification.PrzychodNiepodatkowy, true))),
            E("K_2", M(Adj(CitAdjustmentType.ZmniejszeniePrzychodow))),
            E("K_3", M(0)),
            E("K_4", M(Effective(c => c == CitClassification.KosztNiepodatkowy, false))),
            E("K_5", M(Adj(CitAdjustmentType.KosztNKUP))),
            E("K_6", M(0)),
            E("K_7", M(Adj(CitAdjustmentType.ZwiekszeniePrzychodow))),
            E("K_8", M(Adj(CitAdjustmentType.DodatkowyKUP))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "JPK", new XAttribute(XNamespace.Xmlns + "tns", ns.NamespaceName), new XAttribute(XNamespace.Xmlns + "etd", Etd.NamespaceName),
                naglowek, podmiot, kontrahenci, zois, dzienniki, ctrl, rpd));
        var validation = validator.Validate(doc, SchemaFile, "JPK_KR_PD(1)");
        if (!validation.IsValid) warnings.Add("Plik JPK_CIT nie przeszedł walidacji oficjalnym schematem XSD — popraw wskazane dane przed wysłaniem.");
        warnings.Add("Znaczniki pozycji (S_12_1) wynikają z przypisania kont do pozycji bilansu i RZiS, znaczniki podatkowe (S_12_3) — z kwalifikacji CIT kont. Przy wątpliwościach zmień przypisanie konta w planie kont.");
        return new JpkResult(doc, validation, warnings);
    }
}
