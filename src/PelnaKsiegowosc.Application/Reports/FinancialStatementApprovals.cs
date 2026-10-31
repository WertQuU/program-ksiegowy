using System.Security.Cryptography;
using System.Text;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>
/// Udokumentowane decyzje kierownika jednostki dotyczące sprawozdania finansowego za rok: potwierdzenie treści informacji
/// dodatkowej (każda domyślna deklaracja — np. brak transakcji z podmiotami powiązanymi — musi zostać zweryfikowana),
/// metodologia podziału kosztów w RZiS kalkulacyjnym, weryfikacja rachunku przepływów pieniężnych oraz data zatwierdzenia
/// sprawozdania przez organ zatwierdzający (art. 53 UoR; termin ostatecznego zamknięcia ksiąg — art. 12 ust. 4 UoR).
/// </summary>
public sealed class FinancialStatementApprovals
{
    public sealed record NotesConfirmation(string TextsHash, DateOnly ConfirmedOn, string ConfirmedBy);
    public sealed record CostKeyApproval(string Key, string Methodology, DateOnly ApprovedOn, string ApprovedBy);
    public sealed record CashFlowVerification(string Justification, DateOnly VerifiedOn, string VerifiedBy, string Hash = "");
    public sealed record StatementApproval(DateOnly ApprovedOn, string Body, string Reference, DateOnly RecordedOn, string RecordedBy);

    public static string NotesKey(string year) => $"sf.informacja.potwierdzenie.{year}";
    public const string CostKeyKey = "rzis.klucz-podzialu.zatwierdzenie";
    public static string CashFlowKey(string year) => $"sf.rpp.weryfikacje.{year}";
    public static string ApprovalKey(string year) => $"sf.zatwierdzenie.{year}";

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly SettingsService _settings;

    public FinancialStatementApprovals(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _settings = new SettingsService(db, clock);
    }

    /// <summary>Skrót treści informacji dodatkowej — zmiana dowolnej sekcji unieważnia potwierdzenie.</summary>
    public static string Hash(IReadOnlyDictionary<string, string> texts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001e", texts.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "\u001f" + t.Value)))));

    public async Task ConfirmNotesAsync(string year, IReadOnlyDictionary<string, string> texts, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        if (texts.Values.Any(t => t.Contains("do uzupełnienia", StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Informacja dodatkowa zawiera pola „DO UZUPEŁNIENIA” — uzupełnij je przed potwierdzeniem.");
        await _settings.SetAsync<NotesConfirmation?>(_user.CompanyId, NotesKey(year), new NotesConfirmation(Hash(texts), _clock.Today, _user.Login), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "SF_INFORMACJA_POTWIERDZONA", "AppSetting", NotesKey(year), $"rok {year}: treść informacji dodatkowej zweryfikowana ({Hash(texts)[..16]}…)", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> NotesConfirmedAsync(string year, IReadOnlyDictionary<string, string> texts, CancellationToken ct = default) =>
        await _settings.GetAsync<NotesConfirmation?>(_user.CompanyId, NotesKey(year), () => null, ct) is { } c && c.TextsHash == Hash(texts);

    public async Task ApproveCostKeyAsync(CostAllocationKey key, string methodology, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        key.Validate();
        if (string.IsNullOrWhiteSpace(methodology))
            throw new DomainException("Opisz metodologię podziału kosztów (np. ewidencja czasu pracy, powierzchnia, przychody, analiza dokumentów) — sam klucz procentowy nie dokumentuje przeznaczenia kosztów.");
        await _settings.SetAsync(_user.CompanyId, CostAllocationKey.SettingsKey, key, ct);
        await _settings.SetAsync<CostKeyApproval?>(_user.CompanyId, CostKeyKey, new CostKeyApproval(key.ToString(), methodology.Trim(), _clock.Today, _user.Login), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "KLUCZ_PODZIALU_KOSZTOW_ZATWIERDZONY", "AppSetting", CostKeyKey, $"{key}; metodologia: {methodology.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Klucz zatwierdzony przez kierownika jednostki (z metodologią), zgodny z kluczem używanym w RZiS; null — brak.</summary>
    public async Task<CostKeyApproval?> CostKeyApprovalAsync(CostAllocationKey current, CancellationToken ct = default) =>
        await _settings.GetAsync<CostKeyApproval?>(_user.CompanyId, CostKeyKey, () => null, ct) is { } a && a.Key == current.ToString() ? a : null;

    /// <summary>Skrót wyliczonego rachunku przepływów (kody i kwoty wszystkich pozycji, w tym korekt) — identyfikuje zweryfikowaną wersję.</summary>
    public static string CashFlowHash(CashFlowStatement cf) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001e", cf.Lines.Select(l => $"{l.Code}\u001f{l.Label}\u001f{l.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}")))));

    /// <summary>Historia weryfikacji RPP (każda wersja ze skrótem wyliczenia).</summary>
    public Task<List<CashFlowVerification>> CashFlowVerificationsAsync(string year, CancellationToken ct = default) =>
        _settings.GetAsync(_user.CompanyId, CashFlowKey(year), () => new List<CashFlowVerification>(), ct);

    /// <summary>Weryfikacja konkretnej wersji RPP — zmiana zapisów, kategorii kont lub korekt zmienia skrót i unieważnia potwierdzenie.</summary>
    public async Task VerifyCashFlowAsync(string year, string justification, CashFlowStatement cashFlow, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        if (string.IsNullOrWhiteSpace(justification))
            throw new DomainException("Opisz weryfikację: rozdzielenie wpływów i wydatków inwestycyjnych i finansowych, różnice kursowe, operacje niepieniężne (zał. nr 1 do UoR).");
        if (cashFlow.Unreconciled != 0) throw new DomainException("Rachunek przepływów nie uzgadnia się ze zmianą środków pieniężnych — nie można potwierdzić weryfikacji.");
        var hash = CashFlowHash(cashFlow);
        var history = await CashFlowVerificationsAsync(year, ct);
        history.Add(new CashFlowVerification(justification.Trim(), _clock.Today, _user.Login, hash));
        await _settings.SetAsync(_user.CompanyId, CashFlowKey(year), history, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "SF_RPP_ZWERYFIKOWANY", "AppSetting", CashFlowKey(year), $"{justification.Trim()} (wersja {hash[..16]}…)", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Czy bieżąca wersja RPP została zweryfikowana (skrót zgodny z ostatnim potwierdzeniem).</summary>
    public async Task<bool> CashFlowVerifiedAsync(string year, CashFlowStatement current, CancellationToken ct = default) =>
        (await CashFlowVerificationsAsync(year, ct)).LastOrDefault() is { } v && v.Hash == CashFlowHash(current);

    public sealed record CashFlowAdjustment(string Description, decimal Amount, string Basis, DateOnly RecordedOn, string RecordedBy);
    public static string CashFlowAdjustmentsKey(string year) => $"sf.rpp.korekty.{year}";

    /// <summary>Udokumentowane korekty działalności operacyjnej RPP (A.II.10) — np. różnice kursowe od środków pieniężnych.</summary>
    public Task<List<CashFlowAdjustment>> CashFlowAdjustmentsAsync(string year, CancellationToken ct = default) =>
        _settings.GetAsync(_user.CompanyId, CashFlowAdjustmentsKey(year), () => new List<CashFlowAdjustment>(), ct);

    public async Task AddCashFlowAdjustmentAsync(string year, string description, decimal amount, string basis, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(basis))
            throw new DomainException("Opisz korektę i wskaż dokument / wyliczenie, z którego wynika (np. wycena środków pieniężnych w walucie na dzień bilansowy).");
        if (amount == 0) throw new DomainException("Kwota korekty nie może być zerowa.");
        var list = await CashFlowAdjustmentsAsync(year, ct);
        list.Add(new CashFlowAdjustment(description.Trim(), Money.Round(amount), basis.Trim(), _clock.Today, _user.Login));
        await _settings.SetAsync(_user.CompanyId, CashFlowAdjustmentsKey(year), list, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "SF_RPP_KOREKTA", "AppSetting", CashFlowAdjustmentsKey(year), $"{description.Trim()}: {Money.Xml(amount)}; podstawa: {basis.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(string Description, decimal Amount)>> CashFlowAdjustmentPairsAsync(string year, CancellationToken ct = default) =>
        (await CashFlowAdjustmentsAsync(year, ct)).Select(a => (a.Description, a.Amount)).ToList();

    public Task<CashFlowVerification?> CashFlowVerifiedAsync(string year, CancellationToken ct = default) =>
        _settings.GetAsync<CashFlowVerification?>(_user.CompanyId, CashFlowKey(year), () => null, ct);

    public async Task RecordApprovalAsync(string year, DateOnly approvedOn, string body, string reference, CancellationToken ct = default)
    {
        _user.Require(Permission.CloseYear);
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(reference))
            throw new DomainException("Podaj organ zatwierdzający (np. zwyczajne walne zgromadzenie) i numer uchwały.");
        if (approvedOn > _clock.Today) throw new DomainException("Data zatwierdzenia nie może być późniejsza niż dzisiejsza.");
        await _settings.SetAsync<StatementApproval?>(_user.CompanyId, ApprovalKey(year), new StatementApproval(approvedOn, body.Trim(), reference.Trim(), _clock.Today, _user.Login), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "SF_ZATWIERDZONE", "AppSetting", ApprovalKey(year), $"rok {year}: {approvedOn.ToPl()}, {body.Trim()}, {reference.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task<StatementApproval?> ApprovalAsync(string year, CancellationToken ct = default) =>
        _settings.GetAsync<StatementApproval?>(_user.CompanyId, ApprovalKey(year), () => null, ct);
}
