using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Audit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Ksef;

public sealed record KsefSendResult(Guid DocumentId, bool Accepted, string? KsefNumber, string? ReferenceNumber, string Message);

public sealed record KsefImportResult(int Found, int Imported, int AlreadyPresent, IReadOnlyList<string> Errors);

/// <summary>Orkiestracja KSeF: walidacja XSD przed wysyłką, wysyłka w sesji interaktywnej, zapis numeru KSeF, import faktur zakupowych.</summary>
public sealed class KsefService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly XsdValidator _validator;

    public KsefService(KsiegiDbContext db, ICurrentUser user, IClock clock, XsdValidator validator)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _validator = validator;
    }

    public void Log(string operation, bool success, int? http, string? message, Guid? documentId = null, string? reference = null) =>
        _db.IntegrationLogs.Add(new IntegrationLog
        {
            CompanyId = _user.CompanyId, AtUtc = _clock.UtcNow, System = "KSeF", Operation = operation, Success = success, HttpStatus = http,
            Message = message, DocumentId = documentId, Reference = reference
        });

    /// <summary>Generuje XML FA(3) dokumentu i waliduje go względem XSD (bez wysyłki).</summary>
    public async Task<(byte[] xml, XsdValidationResult validation)> BuildAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.Include(d => d.Lines).Include(d => d.VatLines).FirstAsync(d => d.Id == documentId && d.CompanyId == _user.CompanyId, ct);
        var company = await _db.Companies.FirstAsync(c => c.Id == doc.CompanyId, ct);
        var buyer = await _db.Counterparties.FirstAsync(c => c.Id == doc.CounterpartyId, ct);
        var corrected = doc.CorrectedDocumentId is { } cid ? await _db.Documents.FirstAsync(d => d.Id == cid, ct) : null;
        var x = Fa3InvoiceBuilder.Build(new Fa3InvoiceBuilder.Input(doc, company, buyer, corrected, _clock.UtcNow));
        var v = Fa3InvoiceBuilder.Validate(x, _validator);
        return (Fa3InvoiceBuilder.ToBytes(x), v);
    }

    public async Task<KsefSendResult> SendAsync(Guid documentId, KsefClient client, string ksefToken, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageIntegrations);
        var doc = await _db.Documents.FirstAsync(d => d.Id == documentId && d.CompanyId == _user.CompanyId, ct);
        if (!doc.IsSales) throw new DomainException("Do KSeF wysyła się wyłącznie faktury sprzedaży.");
        if (doc.Status is not (DocumentStatus.Zatwierdzony or DocumentStatus.Zaksiegowany)) throw new DomainException("Wysłać można fakturę zatwierdzoną lub zaksięgowaną.");
        if (!string.IsNullOrEmpty(doc.KsefNumber)) throw new DomainException($"Faktura ma już numer KSeF {doc.KsefNumber}.");
        var (xml, validation) = await BuildAsync(documentId, ct);
        if (!validation.IsValid)
        {
            Log("walidacja XSD", false, null, string.Join(" | ", validation.Errors.Take(10)), documentId);
            await _db.SaveChangesAsync(ct);
            return new KsefSendResult(documentId, false, null, null, "Faktura nie przeszła walidacji XSD FA(3): " + string.Join("; ", validation.Errors.Take(5)));
        }
        var company = await _db.Companies.FirstAsync(c => c.Id == doc.CompanyId, ct);
        try
        {
            var tokens = await client.AuthenticateWithTokenAsync(Identifiers.NormalizeNip(company.Nip), ksefToken, ct);
            using var session = await client.OpenOnlineSessionAsync(tokens.AccessToken.Token, ct);
            var sent = await client.SendInvoiceAsync(session, xml, ct);
            doc.KsefReferenceNumber = sent.ReferenceNumber;
            doc.KsefStatus = KsefStatus.Wyslana;
            await _db.SaveChangesAsync(ct);
            var st = await client.WaitForInvoiceAsync(session, sent.ReferenceNumber, ct: ct);
            await client.CloseSessionAsync(session, ct);
            if (st.Status.Code == 200 && !string.IsNullOrEmpty(st.KsefNumber))
            {
                doc.KsefNumber = st.KsefNumber;
                doc.KsefStatus = KsefStatus.Przyjeta;
                doc.JpkMarker = JpkKsefMarker.NrKSeF;
                foreach (var v in await _db.VatRegister.Where(v => v.DocumentId == doc.Id).ToListAsync(ct))
                {
                    v.KsefNumber = st.KsefNumber;
                    v.KsefMarker = JpkKsefMarker.NrKSeF;
                }
                Log("wysyłka faktury", true, 200, $"{doc.Number} → {st.KsefNumber}", doc.Id, sent.ReferenceNumber);
                _db.DocumentHistory.Add(new DocumentHistory { CompanyId = doc.CompanyId, DocumentId = doc.Id, AtUtc = _clock.UtcNow, UserId = _user.UserId, Action = "KSEF_PRZYJETA", Details = st.KsefNumber });
                await new AuditService(_db, _clock).AddAsync(_user, "KSEF_WYSLANO", "Document", doc.Id.ToString(), $"{doc.Number}: {st.KsefNumber}", ct);
                await _db.SaveChangesAsync(ct);
                return new KsefSendResult(doc.Id, true, st.KsefNumber, sent.ReferenceNumber, $"Faktura przyjęta w KSeF: {st.KsefNumber}");
            }
            doc.KsefStatus = KsefStatus.Odrzucona;
            var msg = $"KSeF odrzucił fakturę ({st.Status.Code}): {st.Status.Description} {string.Join("; ", st.Status.Details ?? new())}";
            Log("wysyłka faktury", false, null, msg, doc.Id, sent.ReferenceNumber);
            await _db.SaveChangesAsync(ct);
            return new KsefSendResult(doc.Id, false, null, sent.ReferenceNumber, msg);
        }
        catch (KsefException ex)
        {
            Log("wysyłka faktury", false, ex.HttpStatus, ex.Message, doc.Id);
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Import faktur zakupowych z KSeF jako dokumentów roboczych (z numerem KSeF i oryginalnym XML w załączniku).
    /// Duplikaty (ten sam numer KSeF) są pomijane. Dekretacja i decyzja o odliczeniu VAT pozostają po stronie użytkownika.
    /// </summary>
    public async Task<KsefImportResult> ImportPurchasesAsync(KsefClient client, string ksefToken, DateTimeOffset from, DateTimeOffset to, IFileStore attachments, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageIntegrations);
        var company = await _db.Companies.FirstAsync(c => c.Id == _user.CompanyId, ct);
        var tokens = await client.AuthenticateWithTokenAsync(Identifiers.NormalizeNip(company.Nip), ksefToken, ct);
        var list = await client.QueryPurchaseInvoicesAsync(tokens.AccessToken.Token, from, to, ct);
        var errors = new List<string>();
        int imported = 0, present = 0;
        var docs = new DocumentService(_db, _user, _clock);
        foreach (var m in list)
        {
            if (await _db.Documents.AnyAsync(d => d.CompanyId == company.Id && d.KsefNumber == m.KsefNumber, ct)) { present++; continue; }
            try
            {
                var xml = await client.DownloadInvoiceAsync(tokens.AccessToken.Token, m.KsefNumber, ct);
                var parsed = Fa3Parser.Parse(XDocument.Parse(xml));
                var seller = await FindOrCreateSupplierAsync(parsed.SellerNip, parsed.SellerName, ct);
                var input = new InvoiceInput(DocumentKind.FakturaZakupu, seller.Id, parsed.IssueDate, parsed.OperationDate ?? parsed.IssueDate,
                    parsed.Lines.Select(l => new DocumentLineInput(l.Name, l.Quantity, l.UnitNetPrice, l.RateCode, l.Unit)).ToList(),
                    $"Import z KSeF {m.KsefNumber}", parsed.DueDate, parsed.Currency, 1m, ExternalNumber: parsed.Number, ReceiptDate: DateOnly.FromDateTime(m.InvoicingDate.UtcDateTime));
                if (parsed.Currency != "PLN") { errors.Add($"{m.KsefNumber}: faktura w walucie {parsed.Currency} — wprowadź kurs ręcznie."); input = input with { ExchangeRate = 1m }; }
                var doc = await docs.CreateInvoiceAsync(input, ct);
                doc.KsefNumber = m.KsefNumber;
                doc.KsefStatus = KsefStatus.Pobrana;
                doc.JpkMarker = JpkKsefMarker.NrKSeF;
                await _db.SaveChangesAsync(ct);
                if (Math.Abs(doc.GrossAmount - parsed.Gross) > 0.01m)
                    errors.Add($"{m.KsefNumber}: suma brutto po przeliczeniu ({Money.Format(doc.GrossAmount)}) różni się od faktury ({Money.Format(parsed.Gross)}) — sprawdź pozycje.");
                await docs.AddAttachmentAsync(doc.Id, attachments, $"{m.KsefNumber}.xml", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)), "Faktura FA(3) z KSeF", ct);
                imported++;
            }
            catch (Exception ex) when (ex is DomainException or FormatException or System.Xml.XmlException)
            {
                errors.Add($"{m.KsefNumber}: {ex.Message}");
            }
        }
        Log("import faktur zakupowych", errors.Count == 0, null, $"znaleziono {list.Count}, zaimportowano {imported}, istniejące {present}");
        await _db.SaveChangesAsync(ct);
        return new KsefImportResult(list.Count, imported, present, errors);
    }

    private async Task<Counterparty> FindOrCreateSupplierAsync(string nip, string name, CancellationToken ct)
    {
        var existing = await _db.Counterparties.FirstOrDefaultAsync(c => c.CompanyId == _user.CompanyId && c.Nip == nip, ct);
        if (existing is not null) return existing;
        var n = await _db.Counterparties.CountAsync(c => c.CompanyId == _user.CompanyId, ct) + 1;
        var cp = new Counterparty { CompanyId = _user.CompanyId, Code = "KSEF" + n.ToString("00000"), Name = name, Nip = nip, CreatedAtUtc = _clock.UtcNow };
        _db.Counterparties.Add(cp);
        await _db.SaveChangesAsync(ct);
        return cp;
    }
}

/// <summary>Odczyt podstawowych danych faktury FA(3) (sprzedawca, numer, daty, pozycje, sumy) do rejestracji zakupu.</summary>
public static class Fa3Parser
{
    public sealed record Line(string Name, string Unit, decimal Quantity, decimal UnitNetPrice, string RateCode);
    public sealed record Result(string SellerNip, string SellerName, string Number, DateOnly IssueDate, DateOnly? OperationDate, DateOnly? DueDate, string Currency, decimal Gross, List<Line> Lines);

    public static Result Parse(XDocument x)
    {
        var ns = x.Root!.Name.Namespace;
        string? V(XElement? e, string n) => e?.Element(ns + n)?.Value;
        var p1 = x.Root.Element(ns + "Podmiot1")?.Element(ns + "DaneIdentyfikacyjne");
        var fa = x.Root.Element(ns + "Fa") ?? throw new FormatException("Brak elementu Fa.");
        decimal Dec(string? s) => decimal.Parse(s ?? "0", CultureInfo.InvariantCulture);
        DateOnly? Date(string? s) => s is null ? null : DateOnly.Parse(s, CultureInfo.InvariantCulture);
        var lines = fa.Elements(ns + "FaWiersz").Select(w =>
        {
            var p12 = V(w, "P_12") ?? "23";
            var code = p12 switch { "0 KR" => "0", "0 WDT" => "0WDT", "0 EX" => "0EX", "np I" => "npI", "np II" => "npII", _ => p12 };
            var qty = Dec(V(w, "P_8B") ?? "1");
            var net = V(w, "P_11") is { } n ? Dec(n) : Dec(V(w, "P_9A")) * qty;
            var price = V(w, "P_9A") is { } pa ? Dec(pa) : qty == 0 ? net : net / qty;
            return new Line(V(w, "P_7") ?? "pozycja", V(w, "P_8A") ?? "szt.", qty == 0 ? 1 : qty, price, code);
        }).ToList();
        if (lines.Count == 0) throw new FormatException("Faktura nie zawiera pozycji (FaWiersz) — wymaga ręcznej rejestracji.");
        var due = fa.Element(ns + "Platnosc")?.Element(ns + "TerminPlatnosci")?.Element(ns + "Termin")?.Value;
        return new Result(V(p1, "NIP") ?? throw new FormatException("Brak NIP sprzedawcy."), V(p1, "Nazwa") ?? "", V(fa, "P_2") ?? "", Date(V(fa, "P_1"))!.Value,
            Date(V(fa, "P_6")), Date(due), V(fa, "KodWaluty") ?? "PLN", Dec(V(fa, "P_15")), lines);
    }
}
