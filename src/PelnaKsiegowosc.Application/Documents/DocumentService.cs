using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Application.Documents;

public sealed record DocumentLineInput(string Name, decimal Quantity, decimal UnitNetPrice, string VatRateCode, string Unit = "szt.",
    string? AccountNumber = null, string? GtuCode = null);

public sealed record InvoiceInput(
    DocumentKind Kind,
    Guid CounterpartyId,
    DateOnly IssueDate,
    DateOnly OperationDate,
    IReadOnlyList<DocumentLineInput> Lines,
    string Description = "",
    DateOnly? DueDate = null,
    string Currency = "PLN",
    decimal ExchangeRate = 1m,
    string? ExchangeRateTable = null,
    DateOnly? ExchangeRateDate = null,
    string? ExternalNumber = null,
    DateOnly? ReceiptDate = null,
    PaymentMethod PaymentMethod = PaymentMethod.Przelew,
    VatTransactionType VatTransactionType = VatTransactionType.Krajowa,
    bool SplitPayment = false,
    string? JpkProcedures = null,
    string? JpkDocumentType = null);

/// <summary>
/// Dokumenty księgowe: wystawianie i rejestracja faktur, korekty, zatwierdzanie, dekretacja (propozycja + edycja),
/// księgowanie (zapis w dzienniku + rejestr VAT + rozrachunki), załączniki, historia.
/// Moduł wystawiania faktur jest oddzielony od silnika księgowego — księgowanie odbywa się przez <see cref="LedgerService"/>.
/// </summary>
public sealed class DocumentService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly NumberingService _numbering;

    public DocumentService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
        _numbering = new NumberingService(db);
    }

    private Task<PostingSettings> PostingAsync(CancellationToken ct) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);

    public Task<Document> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Documents.Include(d => d.Lines).Include(d => d.VatLines).Include(d => d.PostingLines).FirstAsync(d => d.Id == id && d.CompanyId == _user.CompanyId, ct);

    private async Task<string> NextNumberAsync(DocumentKind kind, DateOnly date, CancellationToken ct)
    {
        var code = Document.KindCode(kind);
        var n = await _numbering.NextAsync(_user.CompanyId, $"DOC/{code}/{date.Year}", ct);
        return kind is DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy ? $"{code}/{n}/{date:MM}/{date.Year}" : $"{code}/{n:00000}/{date.Year}";
    }

    /// <summary>Tworzy fakturę sprzedaży lub rejestruje fakturę zakupu (status roboczy).</summary>
    public async Task<Document> CreateInvoiceAsync(InvoiceInput input, CancellationToken ct = default)
    {
        _user.Require(input.Kind is DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy ? Permission.IssueInvoices : Permission.EditDocuments);
        if (input.Kind is not (DocumentKind.FakturaSprzedazy or DocumentKind.FakturaZakupu or DocumentKind.Rachunek))
            throw new DomainException("Do korekt użyj funkcji „Wystaw/zarejestruj korektę”.");
        var company = await _db.Companies.FirstAsync(c => c.Id == _user.CompanyId, ct);
        var cp = await _db.Counterparties.FirstOrDefaultAsync(c => c.Id == input.CounterpartyId && c.CompanyId == _user.CompanyId, ct)
                 ?? throw new DomainException("Wybierz kontrahenta.");
        var doc = new Document
        {
            CompanyId = _user.CompanyId,
            Kind = input.Kind,
            CreatedByUserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        };
        await FillAsync(doc, input, cp, company, ct);
        doc.Number = await NextNumberAsync(input.Kind, input.IssueDate, ct);
        _db.Documents.Add(doc);
        AddHistory(doc, "UTWORZONY", $"{Document.KindName(doc.Kind)} {doc.Number}");
        await _audit.AddAsync(_user, "DOKUMENT_UTWORZONY", "Document", doc.Id.ToString(), $"{doc.Number}; brutto {Money.Xml(doc.GrossAmount)} {doc.Currency}", ct);
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    /// <summary>Edycja dokumentu roboczego.</summary>
    public async Task<Document> UpdateInvoiceAsync(Guid documentId, InvoiceInput input, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var doc = await GetAsync(documentId, ct);
        EnsureEditable(doc);
        var company = await _db.Companies.FirstAsync(c => c.Id == _user.CompanyId, ct);
        var cp = await _db.Counterparties.FirstAsync(c => c.Id == input.CounterpartyId && c.CompanyId == _user.CompanyId, ct);
        _db.DocumentLines.RemoveRange(doc.Lines);
        doc.Lines.Clear();
        _db.DocumentPostingLines.RemoveRange(doc.PostingLines);
        doc.PostingLines.Clear();
        await FillAsync(doc, input, cp, company, ct);
        foreach (var l in doc.Lines) _db.Entry(l).State = EntityState.Added;
        foreach (var l in doc.VatLines.Where(v => _db.Entry(v).State == EntityState.Detached)) _db.Entry(l).State = EntityState.Added;
        doc.ModifiedAtUtc = _clock.UtcNow;
        AddHistory(doc, "ZMIENIONY", null);
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    private async Task FillAsync(Document doc, InvoiceInput input, Counterparty cp, Company company, CancellationToken ct)
    {
        cp.Validate().ThrowIfInvalid();
        if (input.Lines.Count == 0) throw new DomainException("Dokument musi zawierać co najmniej jedną pozycję.");
        if (doc.IsSales && input.OperationDate > input.IssueDate.AddMonths(1))
            throw new DomainException("Data sprzedaży nie może przypadać później niż miesiąc po dacie wystawienia.");
        if (input.Currency != "PLN" && input.ExchangeRate <= 0) throw new DomainException("Podaj kurs waluty (np. średni kurs NBP z dnia poprzedzającego).");
        if (input.Currency.Length != 3) throw new DomainException("Kod waluty musi mieć 3 litery (ISO 4217).");
        if (doc.IsPurchase && string.IsNullOrWhiteSpace(input.ExternalNumber)) throw new DomainException("Podaj numer dokumentu dostawcy.");
        if (doc.IsSales && company.VatStatus == VatStatus.ZwolnionyPodmiotowo && input.Lines.Any(l => l.VatRateCode != "zw"))
            throw new DomainException("Jednostka zwolniona podmiotowo z VAT — dopuszczalna wyłącznie stawka „zw” (z podaniem podstawy zwolnienia).");

        doc.CounterpartyId = cp.Id;
        doc.CounterpartyName = cp.Name;
        doc.CounterpartyNip = cp.Nip;
        doc.CounterpartyAddress = cp.AddressLine;
        doc.CounterpartyCountry = cp.CountryCode;
        doc.IssueDate = input.IssueDate;
        doc.OperationDate = input.OperationDate;
        doc.ReceiptDate = input.ReceiptDate;
        doc.DueDate = input.DueDate ?? input.IssueDate.AddDays(cp.DefaultPaymentDays);
        doc.Currency = input.Currency.ToUpperInvariant();
        doc.ExchangeRate = doc.Currency == "PLN" ? 1m : input.ExchangeRate;
        doc.ExchangeRateTable = input.ExchangeRateTable;
        doc.ExchangeRateDate = input.ExchangeRateDate;
        doc.ExternalNumber = input.ExternalNumber?.Trim();
        doc.Description = input.Description;
        doc.PaymentMethod = input.PaymentMethod;
        doc.VatTransactionType = input.VatTransactionType;
        doc.SplitPayment = input.SplitPayment;
        doc.JpkProcedures = input.JpkProcedures;
        doc.JpkDocumentType = input.JpkDocumentType;
        doc.BankAccountIban = doc.IsSales ? company.DefaultBankAccountIban : cp.BankAccountIban;
        doc.PaymentStatus = PaymentStatus.Nieoplacony;
        if (doc.IsSales && doc.KsefStatus == KsefStatus.NieDotyczy && cp.CountryCode == "PL" && !string.IsNullOrWhiteSpace(cp.Nip))
            doc.KsefStatus = KsefStatus.DoWyslania;

        Dictionary<string, Account>? accounts = null;
        if (input.Lines.Any(l => !string.IsNullOrWhiteSpace(l.AccountNumber)))
        {
            var ledger = new LedgerService(_db, _user, _clock);
            var (fy, _) = await ledger.PeriodForDateAsync(doc.IsPurchase ? new[] { input.OperationDate, input.IssueDate }.Max() : input.OperationDate, ct);
            accounts = await ledger.AccountsByNumberAsync(fy.Id, ct);
        }

        var n = 1;
        foreach (var l in input.Lines)
        {
            Guid? accountId = null;
            if (!string.IsNullOrWhiteSpace(l.AccountNumber))
            {
                if (accounts is null || !accounts.TryGetValue(l.AccountNumber, out var a)) throw new DomainException($"Pozycja {n}: konto {l.AccountNumber} nie istnieje.");
                accountId = a.Id;
            }
            if (l.GtuCode is { } g && (g.Length != 2 || !int.TryParse(g, out var gi) || gi is < 1 or > 13))
                throw new DomainException($"Pozycja {n}: kod GTU musi mieć postać 01–13.");
            doc.Lines.Add(new DocumentLine
            {
                DocumentId = doc.Id,
                LineNo = n++,
                Name = l.Name.Trim(),
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitNetPrice = l.UnitNetPrice,
                VatRateCode = l.VatRateCode,
                AccountId = accountId,
                GtuCode = l.GtuCode
            });
        }
        InvoiceCalculator.Recalculate(doc);
        if (doc.IsPurchase && IsSelfAssessed(doc.VatTransactionType))
        {
            // WNT / import usług / odwrotne obciążenie: podatek rozlicza nabywca — nie jest należny dostawcy
            doc.GrossAmount = doc.NetAmount;
            doc.GrossAmountPln = doc.NetAmountPln;
            doc.ReverseCharge = true;
        }
        doc.VatPeriod = DefaultVatPeriod(doc);
        if (doc.IsPurchase) doc.VatDeductionConfirmed = false;
        if (company.VatStatus != VatStatus.CzynnyPodatnikVat && doc.IsPurchase)
        {
            foreach (var v in doc.VatLines) { v.DeductionPercent = 0; v.DeductibleVatPln = 0; }
        }
    }

    public static bool IsSelfAssessed(VatTransactionType t) => VatRules.PurchaseSelfAssessedMapping(t).NetField is not null;

    /// <summary>
    /// Domyślny okres VAT: sprzedaż — miesiąc powstania obowiązku podatkowego (co do zasady data dostawy/wykonania usługi, art. 19a ust. 1);
    /// zakup — nie wcześniej niż miesiąc otrzymania faktury (art. 86 ust. 10b pkt 1).
    /// </summary>
    public static DateOnly DefaultVatPeriod(Document doc)
    {
        DateOnly d;
        if (doc.IsSales) d = doc.OperationDate == default ? doc.IssueDate : (doc.OperationDate < doc.IssueDate ? doc.OperationDate : doc.IssueDate);
        else d = new[] { doc.OperationDate, doc.ReceiptDate ?? doc.IssueDate, doc.IssueDate }.Max();
        return new DateOnly(d.Year, d.Month, 1);
    }

    /// <summary>Ustawienia odliczenia VAT dla faktury zakupu — świadoma decyzja użytkownika (wymagana przed zatwierdzeniem).</summary>
    public async Task SetVatDeductionAsync(Guid documentId, IReadOnlyDictionary<string, (decimal percent, bool fixedAsset)> perRate, DateOnly vatPeriod, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageVat);
        var doc = await GetAsync(documentId, ct);
        EnsureEditable(doc);
        if (!doc.IsPurchase) throw new DomainException("Odliczenie VAT dotyczy wyłącznie dokumentów zakupu.");
        var earliest = DefaultVatPeriod(doc);
        var period = new DateOnly(vatPeriod.Year, vatPeriod.Month, 1);
        if (period < earliest) throw new DomainException($"Okres odliczenia nie może być wcześniejszy niż {earliest:MM.yyyy} (art. 86 ust. 10b ustawy o VAT).");
        if (period > earliest.AddMonths(3)) throw new DomainException($"Odliczenie możliwe najpóźniej w jednym z trzech kolejnych okresów (art. 86 ust. 11) — po tym terminie wymagana korekta deklaracji (art. 86 ust. 13).");
        foreach (var (rate, (percent, fixedAsset)) in perRate) InvoiceCalculator.SetDeduction(doc, rate, percent, fixedAsset);
        doc.VatPeriod = period;
        doc.VatDeductionConfirmed = true;
        _db.DocumentPostingLines.RemoveRange(doc.PostingLines);
        doc.PostingLines.Clear();
        AddHistory(doc, "ODLICZENIE_VAT", string.Join(", ", doc.VatLines.Select(v => $"{v.VatRateCode}: {v.DeductionPercent}%")) + $"; okres {period:MM.yyyy}");
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Zatwierdzenie dokumentu (kontrola formalna i merytoryczna).</summary>
    public async Task ApproveAsync(Guid documentId, CancellationToken ct = default)
    {
        _user.Require(Permission.ApproveDocuments);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status != DocumentStatus.Roboczy) throw new DomainException("Zatwierdzić można wyłącznie dokument roboczy.");
        var v = await ValidateAsync(doc, ct);
        v.ThrowIfInvalid();
        doc.Status = DocumentStatus.Zatwierdzony;
        doc.ApprovedByUserId = _user.UserId;
        doc.ApprovedAtUtc = _clock.UtcNow;
        AddHistory(doc, "ZATWIERDZONY", string.Join("; ", v.Warnings));
        await _audit.AddAsync(_user, "DOKUMENT_ZATWIERDZONY", "Document", doc.Id.ToString(), doc.Number, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Cofnięcie zatwierdzenia (dokument niezaksięgowany).</summary>
    public async Task UnapproveAsync(Guid documentId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ApproveDocuments);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status != DocumentStatus.Zatwierdzony) throw new DomainException("Dokument nie jest w stanie „zatwierdzony”.");
        doc.Status = DocumentStatus.Roboczy;
        AddHistory(doc, "COFNIETO_ZATWIERDZENIE", reason);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ValidationResult> ValidateAsync(Document doc, CancellationToken ct = default)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(doc.Number)) r.Error("Brak numeru dokumentu.");
        if (doc.IsInvoice || doc.Kind == DocumentKind.Rachunek)
        {
            if (doc.CounterpartyId is null) r.Error("Brak kontrahenta.");
            if (doc.Lines.Count == 0) r.Error("Brak pozycji.");
            if (doc.IsPurchase && doc.VatAmountPln != 0 && !doc.VatDeductionConfirmed && !doc.ExcludedFromVatRegister)
                r.Error("Nie potwierdzono prawa do odliczenia VAT (zakres i okres) — nie każda faktura kosztowa daje prawo do odliczenia.");
            if (doc.IsPurchase) foreach (var w in VatRules.DeductionWarnings(doc.Description + " " + string.Join(" ", doc.Lines.Select(l => l.Name)))) r.Warning(w);
            if (doc.IsSales && doc.CounterpartyCountry == "PL" && doc.KsefStatus is KsefStatus.DoWyslania && string.IsNullOrWhiteSpace(doc.KsefNumber))
                r.Warning("Faktura krajowa B2B nie ma jeszcze numeru KSeF — od 2026 r. faktury wystawia się w KSeF (z wyjątkami); bez numeru KSeF w JPK zostanie oznaczona jako BFK/OFF.");
            if (doc.IsPurchase && doc.ReceiptDate is null) r.Warning("Brak daty wpływu faktury — przyjęto datę wystawienia.");
            if (doc.Currency != "PLN" && string.IsNullOrWhiteSpace(doc.ExchangeRateTable)) r.Warning("Nie podano numeru tabeli kursów NBP.");
            if (doc.DueDate < doc.IssueDate) r.Error("Termin płatności poprzedza datę wystawienia.");
        }
        if (doc.IsCorrection && string.IsNullOrWhiteSpace(doc.CorrectionReason)) r.Error("Korekta wymaga podania przyczyny.");
        var dup = doc.IsPurchase && doc.CounterpartyId is not null && doc.ExternalNumber is not null &&
                  await _db.Documents.AnyAsync(d => d.Id != doc.Id && d.CompanyId == doc.CompanyId && d.CounterpartyId == doc.CounterpartyId &&
                                                    d.ExternalNumber == doc.ExternalNumber && d.Kind == doc.Kind && d.Status != DocumentStatus.Anulowany, ct);
        if (dup) r.Error($"Dokument {doc.ExternalNumber} od tego kontrahenta jest już zarejestrowany (duplikat).");
        return r;
    }

    /// <summary>Propozycja dekretacji według schematu dla rodzaju dokumentu. Użytkownik może ją zmienić przed księgowaniem.</summary>
    public async Task<List<DocumentPostingLine>> ProposePostingAsync(Guid documentId, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status == DocumentStatus.Zaksiegowany) throw new DomainException("Dokument jest już zaksięgowany.");
        var p = await PostingAsync(ct);
        var (fy, _) = await new LedgerService(_db, _user, _clock).PeriodForDateAsync(PostingDate(doc), ct);
        var accounts = await new LedgerService(_db, _user, _clock).AccountsByNumberAsync(fy.Id, ct);
        var byId = accounts.Values.ToDictionary(a => a.Id);
        Guid Acc(string number) => accounts.TryGetValue(number, out var a) ? a.Id : throw new DomainException($"Brak konta {number} (parametry księgowania).");

        var lines = new List<DocumentPostingLine>();
        void Add(Guid account, decimal debit, decimal credit, string? desc, bool counterparty = false, decimal? cur = null, bool openItem = false)
        {
            if (debit == 0 && credit == 0) return;
            // kwoty ujemne (np. korekta in minus) przenoszone na stronę przeciwną
            if (debit < 0) { credit = -debit; debit = 0; }
            if (credit < 0) { debit = -credit; credit = 0; }
            lines.Add(new DocumentPostingLine
            {
                DocumentId = doc.Id,
                LineNo = lines.Count + 1,
                AccountId = account,
                Debit = debit,
                Credit = credit,
                CounterpartyId = counterparty ? doc.CounterpartyId : null,
                Description = desc,
                CurrencyAmount = cur is { } c ? Math.Abs(c) : null,
                CreatesOpenItem = openItem
            });
        }

        var foreign = doc.Currency != "PLN";
        switch (doc.Kind)
        {
            case DocumentKind.FakturaSprzedazy:
            case DocumentKind.KorektaSprzedazy:
            {
                Add(Acc(p.Receivables), doc.GrossAmountPln, 0, $"Należność {doc.Number}", true, foreign ? doc.GrossAmount : null, true);
                var revenueDefault = doc.CounterpartyCountry == "PL" ? p.SalesRevenueDefault : p.SalesRevenueForeign;
                foreach (var g in doc.Lines.GroupBy(l => l.AccountId ?? Acc(revenueDefault)))
                {
                    var net = g.Sum(x => x.NetAmount);
                    Add(g.Key, 0, foreign ? Money.ToPln(net, doc.ExchangeRate) : net, $"Przychód {doc.Number}");
                }
                if (doc.VatAmountPln != 0 && !doc.ReverseCharge) Add(Acc(p.VatOutput), 0, doc.VatAmountPln, "VAT należny");
                FixRounding(lines);
                break;
            }
            case DocumentKind.FakturaZakupu:
            case DocumentKind.KorektaZakupu:
            case DocumentKind.Rachunek:
            {
                var deductible = doc.VatLines.Sum(v => v.DeductibleVatPln);
                var nonDeductible = doc.VatAmountPln - deductible;
                foreach (var g in doc.Lines.GroupBy(l => l.AccountId ?? Acc(p.PurchaseCostDefault)))
                {
                    var net = g.Sum(x => x.NetAmount);
                    Add(g.Key, foreign ? Money.ToPln(net, doc.ExchangeRate) : net, 0, $"Koszt {doc.ExternalNumber ?? doc.Number}");
                }
                // VAT nieodliczalny powiększa koszt (art. 28 ust. 2 UoR — cena nabycia z podatkiem niepodlegającym odliczeniu)
                if (nonDeductible != 0)
                {
                    var target = lines.Where(l => l.Debit > 0).OrderByDescending(l => l.Debit).FirstOrDefault()?.AccountId ?? Acc(p.NonDeductibleVatCost);
                    Add(target, nonDeductible, 0, "VAT nieodliczalny");
                }
                if (deductible != 0) Add(Acc(p.VatInput), deductible, 0, "VAT naliczony do odliczenia");
                if (IsSelfAssessed(doc.VatTransactionType) && doc.VatAmountPln != 0)
                    Add(Acc(p.VatOutput), 0, doc.VatAmountPln, "VAT należny (rozliczany przez nabywcę)");
                Add(Acc(p.Payables), 0, doc.GrossAmountPln, $"Zobowiązanie {doc.ExternalNumber ?? doc.Number}", true, foreign ? doc.GrossAmount : null, true);
                FixRounding(lines);
                break;
            }
            default:
                throw new DomainException("Dla tego rodzaju dokumentu dekretację wprowadza się ręcznie (polecenie księgowania).");
        }

        _db.DocumentPostingLines.RemoveRange(doc.PostingLines);
        doc.PostingLines.Clear();
        foreach (var l in lines) doc.PostingLines.Add(l);
        AddHistory(doc, "DEKRETACJA_ZAPROPONOWANA", string.Join("; ", lines.Select(l => $"{byId[l.AccountId].Number} Wn {Money.Format(l.Debit)} Ma {Money.Format(l.Credit)}")));
        await _db.SaveChangesAsync(ct);
        return lines;
    }

    /// <summary>Wyrównanie różnic groszowych z przeliczenia walut na największej pozycji przychodu/kosztu (nie na rozrachunku ani VAT).</summary>
    private static void FixRounding(List<DocumentPostingLine> lines)
    {
        var diff = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        if (diff == 0) return;
        if (Math.Abs(diff) > 0.05m) throw new DomainException($"Dekretacja nie bilansuje się (różnica {Money.Format(diff)}) — sprawdź dane dokumentu.");
        var target = lines.Where(l => !l.CreatesOpenItem && l.Description is { } d && (d.StartsWith("Przychód") || d.StartsWith("Koszt")))
            .OrderByDescending(l => l.Debit + l.Credit).First();
        if (target.Credit > 0) target.Credit += diff; else target.Debit -= diff;
    }

    /// <summary>Ręczna dekretacja dokumentu (np. PK, nota, dokument wewnętrzny) — zastępuje propozycję.</summary>
    public async Task SetPostingLinesAsync(Guid documentId, IReadOnlyList<LineInput> lines, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status == DocumentStatus.Zaksiegowany) throw new DomainException("Dokument jest zaksięgowany — dekretacji nie można zmienić.");
        var (fy, _) = await new LedgerService(_db, _user, _clock).PeriodForDateAsync(PostingDate(doc), ct);
        var accounts = await new LedgerService(_db, _user, _clock).AccountsByNumberAsync(fy.Id, ct);
        _db.DocumentPostingLines.RemoveRange(doc.PostingLines);
        doc.PostingLines.Clear();
        var n = 1;
        foreach (var l in lines)
        {
            if (!accounts.TryGetValue(l.AccountNumber, out var a)) throw new DomainException($"Konto {l.AccountNumber} nie istnieje.");
            doc.PostingLines.Add(new DocumentPostingLine
            {
                DocumentId = doc.Id,
                LineNo = n++,
                AccountId = a.Id,
                Debit = l.Debit,
                Credit = l.Credit,
                CounterpartyId = l.CounterpartyId,
                Description = l.Description,
                CurrencyAmount = l.CurrencyAmount,
                CreatesOpenItem = a.IsSettlementAccount && l.OpenItemId is null
            });
        }
        AddHistory(doc, "DEKRETACJA_RECZNA", $"{lines.Count} pozycji");
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Tworzy dokument wewnętrzny / PK / notę z ręczną dekretacją.</summary>
    public async Task<Document> CreateGeneralDocumentAsync(DocumentKind kind, DateOnly date, string description, IReadOnlyList<LineInput> lines,
        Guid? counterpartyId = null, string? externalNumber = null, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        if (kind is DocumentKind.FakturaSprzedazy or DocumentKind.FakturaZakupu or DocumentKind.KorektaSprzedazy or DocumentKind.KorektaZakupu)
            throw new DomainException("Faktury wprowadza się funkcją faktur.");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Opis operacji jest wymagany.");
        var doc = new Document
        {
            CompanyId = _user.CompanyId,
            Kind = kind,
            IssueDate = date,
            OperationDate = date,
            Description = description,
            CounterpartyId = counterpartyId,
            ExternalNumber = externalNumber,
            CreatedByUserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow,
            NetAmount = lines.Sum(l => l.Debit),
            GrossAmount = lines.Sum(l => l.Debit),
            NetAmountPln = lines.Sum(l => l.Debit),
            GrossAmountPln = lines.Sum(l => l.Debit),
            ExcludedFromVatRegister = true
        };
        if (counterpartyId is { } cid)
        {
            var cp = await _db.Counterparties.FirstAsync(c => c.Id == cid, ct);
            doc.CounterpartyName = cp.Name;
            doc.CounterpartyNip = cp.Nip;
        }
        doc.Number = await NextNumberAsync(kind, date, ct);
        _db.Documents.Add(doc);
        AddHistory(doc, "UTWORZONY", Document.KindName(kind));
        await _db.SaveChangesAsync(ct);
        await SetPostingLinesAsync(doc.Id, lines, ct);
        return doc;
    }

    private static DateOnly PostingDate(Document doc) =>
        doc.IsPurchase ? new[] { doc.OperationDate, doc.IssueDate }.Max() : doc.OperationDate == default ? doc.IssueDate : doc.OperationDate;

    /// <summary>
    /// Księgowanie dokumentu: zapis w dzienniku (wg dekretacji), wpisy rejestru VAT, rozrachunki z terminem płatności.
    /// Dla faktur zakupu data księgowania = później z: daty operacji i daty wystawienia (koszt w okresie, którego dotyczy).
    /// </summary>
    public async Task<JournalEntry> PostAsync(Guid documentId, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status == DocumentStatus.Zaksiegowany) throw new DomainException("Dokument jest już zaksięgowany.");
        if (doc.Status != DocumentStatus.Zatwierdzony) throw new DomainException("Przed zaksięgowaniem dokument musi zostać zatwierdzony.");
        if (doc.PostingLines.Count == 0)
        {
            if (doc.IsInvoice || doc.Kind == DocumentKind.Rachunek) { await ProposePostingAsync(documentId, ct); doc = await GetAsync(documentId, ct); }
            else throw new DomainException("Dokument nie ma dekretacji.");
        }

        var date = postingDate ?? PostingDate(doc);
        var (fy, _) = await new LedgerService(_db, _user, _clock).PeriodForDateAsync(date, ct);
        var chartId = await _db.Charts.Where(c => c.FiscalYearId == fy.Id).OrderByDescending(c => c.Version).Select(c => c.Id).FirstAsync(ct);
        var accounts = await _db.Accounts.Where(a => a.ChartId == chartId).ToDictionaryAsync(a => a.Id, ct);

        var linkedOpenItem = doc.IsCorrection && doc.CorrectedDocumentId is { } orig
            ? await _db.OpenItems.FirstOrDefaultAsync(o => o.DocumentId == orig && o.Status != OpenItemStatus.Rozliczona, ct)
            : null;

        var input = new EntryInput(
            JournalFor(doc.Kind),
            doc.OperationDate == default ? doc.IssueDate : doc.OperationDate,
            doc.IssueDate,
            date,
            Truncate($"{Document.KindName(doc.Kind)} {doc.ExternalNumber ?? doc.Number} — {doc.CounterpartyName} {doc.Description}".Trim(), 900),
            Document.KindCode(doc.Kind),
            doc.ExternalNumber is { Length: > 0 } ext && doc.IsPurchase ? $"{doc.Number} ({ext})" : doc.Number,
            doc.PostingLines.OrderBy(l => l.LineNo).Select(l =>
            {
                if (!accounts.TryGetValue(l.AccountId, out var acc))
                    throw new DomainException("Dekretacja odwołuje się do konta spoza planu kont roku księgowania — wygeneruj dekretację ponownie.");
                Guid? openItemId = null;
                // korekta zmniejszająca: rozliczenie z pozycją faktury pierwotnej, jeżeli pozostała kwota wystarcza
                if (linkedOpenItem is not null && acc.IsSettlementAccount && acc.Number == linkedOpenItem.AccountNumber)
                {
                    var reducing = linkedOpenItem.Side == OpenItemSide.Naleznosc ? l.Credit > 0 : l.Debit > 0;
                    var amount = l.Debit + l.Credit;
                    if (!reducing || linkedOpenItem.RemainingPln >= amount) openItemId = linkedOpenItem.Id;
                }
                return new LineInput(acc.Number, l.Debit, l.Credit, l.CounterpartyId, l.Description, openItemId, l.CurrencyAmount);
            }).ToList(),
            doc.Id,
            doc.Currency,
            doc.Currency == "PLN" ? null : doc.ExchangeRate);

        var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var ledger = new LedgerService(_db, _user, _clock);
            var entry = await ledger.CreateAndPostAsync(input, ct);
            foreach (var oi in _db.OpenItems.Local.Where(o => o.JournalEntryId == entry.Id))
            {
                oi.DueDate = doc.DueDate;
                oi.DocumentId = doc.Id;
            }
            if (doc.IsInvoice && !doc.ExcludedFromVatRegister) await AddVatRegisterAsync(doc, ct);
            doc.Status = DocumentStatus.Zaksiegowany;
            doc.JournalEntryId = entry.Id;
            doc.PostedAtUtc = _clock.UtcNow;
            doc.PostedByUserId = _user.UserId;
            AddHistory(doc, "ZAKSIEGOWANY", $"{entry.JournalNumber} (poz. {entry.SequenceNumber})");
            await _audit.AddAsync(_user, "DOKUMENT_ZAKSIEGOWANY", "Document", doc.Id.ToString(), $"{doc.Number} → {entry.JournalNumber}", ct);
            await _db.SaveChangesAsync(ct);
            await RefreshPaymentStatusAsync(doc.Id, ct);
            if (doc.IsCorrection && doc.CorrectedDocumentId is { } od) await RefreshPaymentStatusAsync(od, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return entry;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private static string JournalFor(DocumentKind k) => k switch
    {
        DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy => "SPR",
        DocumentKind.FakturaZakupu or DocumentKind.KorektaZakupu or DocumentKind.Rachunek => "ZAK",
        DocumentKind.WyciagBankowy => "BANK",
        DocumentKind.KasaPrzyjmie or DocumentKind.KasaWyda or DocumentKind.RaportKasowy => "KASA",
        DocumentKind.AmortyzacjaSrodkowTrwalych => "AM",
        DocumentKind.DokumentKapitalowy or DocumentKind.RozliczenieZAkcjonariuszem => "KAP",
        _ => "PK"
    };

    private async Task AddVatRegisterAsync(Document doc, CancellationToken ct)
    {
        var company = await _db.Companies.FirstAsync(c => c.Id == doc.CompanyId, ct);
        if (company.VatStatus != VatStatus.CzynnyPodatnikVat && doc.IsPurchase) return;
        var period = doc.VatPeriod ?? DefaultVatPeriod(doc);
        var register = doc.IsSales ? VatRegisterType.Sprzedaz : VatRegisterType.Zakup;
        var ord = await _db.VatRegister.CountAsync(v => v.CompanyId == doc.CompanyId && v.Register == register && v.Period == period, ct)
                  + _db.VatRegister.Local.Count(v => v.CompanyId == doc.CompanyId && v.Register == register && v.Period == period && _db.Entry(v).State == EntityState.Added);
        var marker = doc.KsefNumber is { Length: > 0 } ? JpkKsefMarker.NrKSeF : doc.JpkMarker ?? JpkKsefMarker.BFK;
        var gtu = string.Join(",", doc.Lines.Where(l => l.GtuCode != null).Select(l => "GTU_" + l.GtuCode).Distinct().OrderBy(x => x));
        foreach (var v in doc.VatLines)
        {
            JpkFieldMapping map;
            if (doc.IsSales) map = VatRules.SalesMapping(v.VatRateCode, doc.VatTransactionType);
            else
            {
                var self = VatRules.PurchaseSelfAssessedMapping(doc.VatTransactionType);
                if (self.NetField is not null)
                {
                    // nabywca rozlicza podatek należny (WNT, import usług, odwrotne obciążenie) — wpis do rejestru sprzedaży
                    _db.VatRegister.Add(NewEntry(doc, VatRegisterType.Sprzedaz, period, ++ord, v, self, marker, gtu, v.VatAmountPln));
                }
                map = VatRules.PurchaseMapping(v.IsFixedAsset);
            }
            if (map.NetField is null && doc.IsSales)
                throw new DomainException($"Stawka „{v.VatRateCode}” przy rodzaju transakcji {doc.VatTransactionType} nie ma potwierdzonego mapowania do JPK_V7 — wymaga klasyfikacji ręcznej (funkcja niezaimplementowana).");
            _db.VatRegister.Add(NewEntry(doc, register, period, ++ord, v, map, marker, gtu, doc.IsPurchase ? v.DeductibleVatPln : v.VatAmountPln));
        }
    }

    private static VatRegisterEntry NewEntry(Document doc, VatRegisterType reg, DateOnly period, int ord, DocumentVatLine v, JpkFieldMapping map,
        JpkKsefMarker marker, string gtu, decimal vatForRegister) => new()
    {
        CompanyId = doc.CompanyId,
        DocumentId = doc.Id,
        Register = reg,
        Period = period,
        OrdinalNumber = ord,
        DocumentNumber = doc.IsPurchase ? doc.ExternalNumber ?? doc.Number : doc.Number,
        IssueDate = doc.IssueDate,
        OperationDate = doc.OperationDate,
        ReceiptDate = doc.ReceiptDate,
        CounterpartyName = doc.CounterpartyName ?? "",
        CounterpartyNip = doc.CounterpartyNip,
        CounterpartyCountry = doc.CounterpartyCountry ?? "PL",
        VatRateCode = v.VatRateCode,
        TransactionType = doc.VatTransactionType,
        NetPln = v.NetAmountPln,
        VatPln = reg == VatRegisterType.Sprzedaz ? (doc.IsPurchase ? v.VatAmountPln : vatForRegister) : v.VatAmountPln,
        DeductibleVatPln = reg == VatRegisterType.Zakup ? vatForRegister : 0,
        JpkNetField = map.NetField,
        JpkTaxField = map.TaxField,
        GtuCodes = string.IsNullOrEmpty(gtu) ? null : gtu,
        Procedures = doc.JpkProcedures ?? (doc.SplitPayment ? "MPP" : null),
        JpkDocumentType = doc.JpkDocumentType,
        KsefNumber = doc.KsefNumber,
        KsefMarker = marker,
        IsFixedAsset = v.IsFixedAsset,
        IsCorrection = doc.IsCorrection
    };

    /// <summary>
    /// Faktura korygująca: pozycje korekty to różnice (ilość/cena „po” minus „przed”), np. ilość −1 dla zwrotu.
    /// </summary>
    public async Task<Document> CreateCorrectionAsync(Guid originalId, string reason, DateOnly issueDate, IReadOnlyList<DocumentLineInput> differenceLines, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var orig = await GetAsync(originalId, ct);
        if (orig.Status != DocumentStatus.Zaksiegowany) throw new DomainException("Korygować można wyłącznie dokument zaksięgowany — dokument roboczy po prostu edytuj.");
        if (!orig.IsInvoice || orig.IsCorrection) throw new DomainException("Korekta dotyczy faktury pierwotnej.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj przyczynę korekty.");
        if (issueDate < orig.IssueDate) throw new DomainException("Data korekty nie może poprzedzać daty faktury korygowanej.");
        var kind = orig.IsSales ? DocumentKind.KorektaSprzedazy : DocumentKind.KorektaZakupu;
        var cp = await _db.Counterparties.FirstAsync(c => c.Id == orig.CounterpartyId, ct);
        var company = await _db.Companies.FirstAsync(c => c.Id == _user.CompanyId, ct);
        var doc = new Document
        {
            CompanyId = _user.CompanyId,
            Kind = kind,
            CorrectedDocumentId = orig.Id,
            CorrectionReason = reason.Trim(),
            CreatedByUserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        };
        await FillAsync(doc, new InvoiceInput(kind, cp.Id, issueDate, issueDate, differenceLines, $"Korekta do {orig.Number}: {reason}",
            issueDate.AddDays(cp.DefaultPaymentDays), orig.Currency, orig.ExchangeRate, orig.ExchangeRateTable, orig.ExchangeRateDate,
            orig.IsPurchase ? $"KOR/{orig.ExternalNumber}" : null, orig.IsPurchase ? issueDate : null, orig.PaymentMethod, orig.VatTransactionType,
            orig.SplitPayment, orig.JpkProcedures, orig.JpkDocumentType), cp, company, ct);
        if (orig.IsPurchase)
            foreach (var v in doc.VatLines)
            {
                var ov = orig.VatLines.FirstOrDefault(x => x.VatRateCode == v.VatRateCode);
                if (ov is not null) InvoiceCalculator.SetDeduction(doc, v.VatRateCode, ov.DeductionPercent, ov.IsFixedAsset);
            }
        doc.Number = await NextNumberAsync(kind, issueDate, ct);
        _db.Documents.Add(doc);
        AddHistory(doc, "KOREKTA_UTWORZONA", $"do {orig.Number}: {reason}");
        AddHistory(orig, "SKORYGOWANY", $"korekta {doc.Number}");
        await _audit.AddAsync(_user, "KOREKTA_UTWORZONA", "Document", doc.Id.ToString(), $"{doc.Number} do {orig.Number}", ct);
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    /// <summary>Anulowanie dokumentu, który nigdy nie był zaksięgowany. Numer pozostaje zajęty (ciągłość numeracji).</summary>
    public async Task CancelAsync(Guid documentId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var doc = await GetAsync(documentId, ct);
        if (doc.Status == DocumentStatus.Zaksiegowany) throw new DomainException("Dokumentu zaksięgowanego nie można anulować — wystaw korektę.");
        if (doc.KsefNumber is not null) throw new DomainException("Faktura ma numer KSeF — nie może zostać anulowana, wymagana faktura korygująca.");
        doc.Status = DocumentStatus.Anulowany;
        AddHistory(doc, "ANULOWANY", reason);
        await _audit.AddAsync(_user, "DOKUMENT_ANULOWANY", "Document", doc.Id.ToString(), $"{doc.Number}: {reason}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RefreshPaymentStatusAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.FirstAsync(d => d.Id == documentId, ct);
        var items = await _db.OpenItems.Where(o => o.DocumentId == documentId).ToListAsync(ct);
        var corrections = await _db.Documents.Where(d => d.CorrectedDocumentId == documentId).Select(d => d.Id).ToListAsync(ct);
        var corrItems = await _db.OpenItems.Where(o => o.DocumentId != null && corrections.Contains(o.DocumentId.Value)).ToListAsync(ct);
        var all = items.Concat(corrItems).ToList();
        if (all.Count == 0) { doc.PaymentStatus = PaymentStatus.NieDotyczy; await _db.SaveChangesAsync(ct); return; }
        decimal Signed(OpenItem o, decimal v) => o.Side == OpenItemSide.Naleznosc ? v : -v;
        var due = all.Sum(o => Signed(o, o.AmountPln));
        var paid = all.Sum(o => Signed(o, o.SettledAmountPln));
        var remaining = due - paid;
        doc.PaymentStatus = Math.Abs(due) == 0 && paid == 0 ? PaymentStatus.Oplacony
            : remaining == 0 ? PaymentStatus.Oplacony
            : paid == 0 ? PaymentStatus.Nieoplacony
            : Math.Sign(remaining) != Math.Sign(due) ? PaymentStatus.Nadplacony
            : PaymentStatus.CzesciowoOplacony;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Dodanie załącznika (PDF, XML, obraz…) do dokumentu — walidacja typu, sygnatury i rozmiaru, SHA-256.</summary>
    public async Task<Attachment> AddAttachmentAsync(Guid documentId, IFileStore store, string fileName, Stream content, string? description = null, CancellationToken ct = default)
    {
        _user.Require(Permission.EditDocuments);
        var doc = await _db.Documents.FirstAsync(d => d.Id == documentId && d.CompanyId == _user.CompanyId, ct);
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var header = ms.GetBuffer().AsSpan(0, (int)Math.Min(16, ms.Length));
        var contentType = AttachmentPolicy.Validate(fileName, header, ms.Length);
        ms.Position = 0;
        var (path, sha, size) = await store.SaveAsync(ms, fileName, ct);
        var att = new Attachment
        {
            CompanyId = doc.CompanyId,
            DocumentId = doc.Id,
            FileName = Path.GetFileName(fileName),
            ContentType = contentType,
            SizeBytes = size,
            Sha256 = sha,
            StoragePath = path,
            CreatedAtUtc = _clock.UtcNow,
            CreatedByUserId = _user.UserId,
            Description = description
        };
        _db.Attachments.Add(att);
        AddHistory(doc, "ZALACZNIK_DODANY", $"{att.FileName} ({size} B, SHA-256 {sha[..16]}…)");
        await _audit.AddAsync(_user, "ZALACZNIK_DODANY", "Attachment", att.Id.ToString(), $"{doc.Number}: {att.FileName}; {sha}", ct);
        await _db.SaveChangesAsync(ct);
        return att;
    }

    /// <summary>Odczyt załącznika z weryfikacją sumy kontrolnej.</summary>
    public async Task<byte[]> ReadAttachmentAsync(Guid attachmentId, IFileStore store, CancellationToken ct = default)
    {
        _user.Require(Permission.ViewData);
        var att = await _db.Attachments.FirstAsync(a => a.Id == attachmentId && a.CompanyId == _user.CompanyId, ct);
        await using var s = await store.OpenAsync(att.StoragePath, ct);
        var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
        if (sha != att.Sha256) throw new DomainException($"Załącznik {att.FileName} jest uszkodzony lub zmieniony (niezgodna suma SHA-256).");
        return ms.ToArray();
    }

    private void EnsureEditable(Document doc)
    {
        if (!doc.IsEditable) throw new DomainException(doc.Status == DocumentStatus.Zaksiegowany
            ? "Dokument jest zaksięgowany — zmiany wyłącznie przez dokument korygujący."
            : "Dokument nie jest w stanie roboczym (cofnij zatwierdzenie, aby edytować).");
    }

    private void AddHistory(Document doc, string action, string? details) =>
        _db.DocumentHistory.Add(new DocumentHistory { CompanyId = doc.CompanyId, DocumentId = doc.Id, AtUtc = _clock.UtcNow, UserId = _user.UserId, Action = action, Details = details });

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
