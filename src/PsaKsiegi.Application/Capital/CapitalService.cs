using Microsoft.EntityFrameworkCore;
using PsaKsiegi.Application.Abstractions;
using PsaKsiegi.Application.Audit;
using PsaKsiegi.Application.Ledger;
using PsaKsiegi.Application.Setup;
using PsaKsiegi.Application.Templates;
using PsaKsiegi.Data;
using PsaKsiegi.Domain.Capital;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Counterparties;
using PsaKsiegi.Domain.Ledger;
using PsaKsiegi.Domain.Security;
using PsaKsiegi.Domain.Settlements;

namespace PsaKsiegi.Application.Capital;

/// <summary>
/// Ewidencja księgowa zdarzeń kapitałowych PSA. Zdarzenie księguje się dopiero po potwierdzeniu dokumentów
/// korporacyjnych przez osobę uprawnioną (rola Zarząd / Główny księgowy) — dane wprowadzone przez użytkownika
/// nie są automatycznie podstawą zapisu. Moduł nie zastępuje rejestru akcjonariuszy (art. 300^31 KSH).
/// </summary>
public sealed class CapitalService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public CapitalService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    public async Task<Shareholder> AddShareholderAsync(string name, string? identifier, bool isLegalPerson, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCapital);
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Podaj nazwę/imię i nazwisko akcjonariusza.");
        var code = "A" + (await _db.Shareholders.CountAsync(s => s.CompanyId == _user.CompanyId, ct) + 1).ToString("000");
        var cp = new Counterparty
        {
            CompanyId = _user.CompanyId,
            Code = code,
            Name = name.Trim(),
            Kind = CounterpartyKind.Akcjonariusz,
            Nip = isLegalPerson ? identifier : null,
            IsRelatedParty = true,
            CreatedAtUtc = _clock.UtcNow
        };
        cp.Validate().ThrowIfInvalid();
        _db.Counterparties.Add(cp);
        var sh = new Shareholder { CompanyId = _user.CompanyId, Name = name.Trim(), Identifier = identifier, IsLegalPerson = isLegalPerson, CounterpartyId = cp.Id };
        _db.Shareholders.Add(sh);
        await _audit.AddAsync(_user, "AKCJONARIUSZ_DODANY", "Shareholder", sh.Id.ToString(), sh.Name, ct);
        await _db.SaveChangesAsync(ct);
        return sh;
    }

    public async Task<CapitalEvent> CreateEventAsync(CapitalEvent ev, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCapital);
        ev.CompanyId = _user.CompanyId;
        ev.Status = CapitalEventStatus.Roboczy;
        ev.CreatedByUserId = _user.UserId;
        ev.CreatedAtUtc = _clock.UtcNow;
        if (ev.ShareCapitalAmount < 0 || ev.SupplementaryCapitalAmount < 0 || ev.TotalAmount < 0 || ev.WithholdingTaxAmount < 0)
            throw new DomainException("Kwoty nie mogą być ujemne.");
        if (ev.Type is CapitalEventType.ObjecieAkcjiWkladPieniezny or CapitalEventType.ObjecieAkcjiWkladNiepieniezny)
        {
            if (ev.TotalAmount == 0) ev.TotalAmount = ev.ShareCapitalAmount + ev.SupplementaryCapitalAmount;
            if (ev.TotalAmount != ev.ShareCapitalAmount + ev.SupplementaryCapitalAmount)
                throw new DomainException("Wartość wkładu musi być równa sumie kwot na kapitał akcyjny i zapasowy.");
            if (ev.ContributionType == ContributionType.PracaLubUslugi)
                throw new DomainException("Wkład w postaci świadczenia pracy lub usług nie jest wnoszony na kapitał akcyjny (art. 300^3 KSH) — jego ujęcie wymaga indywidualnej oceny księgowej (funkcja niezaimplementowana).");
        }
        if (ev.WithholdingTaxAmount > ev.TotalAmount) throw new DomainException("Podatek pobrany nie może przekraczać kwoty wypłaty.");
        _db.CapitalEvents.Add(ev);
        await _audit.AddAsync(_user, "ZDARZENIE_KAPITALOWE_UTWORZONE", "CapitalEvent", ev.Id.ToString(), $"{ev.Type}: {Money.Xml(ev.TotalAmount)}", ct);
        await _db.SaveChangesAsync(ct);
        return ev;
    }

    /// <summary>Potwierdzenie dokumentów korporacyjnych (uchwała, umowa, wpis KRS, test wypłacalności) — wymagane przed księgowaniem.</summary>
    public async Task ConfirmDocumentsAsync(Guid eventId, CancellationToken ct = default)
    {
        _user.Require(Permission.ConfirmCapitalDocuments);
        var ev = await _db.CapitalEvents.FirstAsync(e => e.Id == eventId && e.CompanyId == _user.CompanyId, ct);
        if (ev.Status != CapitalEventStatus.Roboczy) throw new DomainException("Zdarzenie nie jest w stanie roboczym.");
        var missing = ev.MissingDocuments();
        if (missing.Count > 0) throw new DomainException("Brak wymaganych danych/dokumentów: " + string.Join("; ", missing));
        if (ev.CreatedByUserId == _user.UserId && !_user.Roles.Contains(Role.Zarzad) && !_user.Roles.Contains(Role.Administrator))
            throw new AccessDeniedException("Dokumenty zdarzenia kapitałowego potwierdza osoba inna niż wprowadzająca lub członek zarządu.");
        ev.Status = CapitalEventStatus.DokumentyPotwierdzone;
        ev.ConfirmedByUserId = _user.UserId;
        ev.ConfirmedAtUtc = _clock.UtcNow;
        await _audit.AddAsync(_user, "ZDARZENIE_KAPITALOWE_POTWIERDZONE", "CapitalEvent", ev.Id.ToString(),
            $"{ev.Type}; uchwała {ev.ResolutionReference}; umowa {ev.AgreementReference}; KRS {ev.KrsEntryReference}; test wypłacalności: {ev.SolvencyTestConfirmed}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<JournalEntry> PostEventAsync(Guid eventId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var ev = await _db.CapitalEvents.FirstAsync(e => e.Id == eventId && e.CompanyId == _user.CompanyId, ct);
        if (ev.Status == CapitalEventStatus.Zaksiegowany) throw new DomainException("Zdarzenie jest już zaksięgowane.");
        if (ev.Status != CapitalEventStatus.DokumentyPotwierdzone) throw new DomainException("Przed księgowaniem dokumenty korporacyjne muszą zostać potwierdzone.");
        var p = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var sh = ev.ShareholderId is { } sid ? await _db.Shareholders.FirstAsync(s => s.Id == sid, ct) : null;
        var cp = sh?.CounterpartyId;
        var lines = new List<LineInput>();

        async Task<Guid?> OldestOpenItemAsync(string account, OpenItemSide side)
        {
            if (cp is null) throw new DomainException("Zdarzenie wymaga wskazania akcjonariusza.");
            var item = await _db.OpenItems.Where(o => o.CompanyId == _user.CompanyId && o.CounterpartyId == cp && o.AccountNumber == account && o.Side == side && o.Status != OpenItemStatus.Rozliczona)
                .OrderBy(o => o.DocumentDate).FirstOrDefaultAsync(ct);
            return item?.Id ?? throw new DomainException($"Brak otwartego rozrachunku na koncie {account} dla akcjonariusza.");
        }

        switch (ev.Type)
        {
            case CapitalEventType.ObjecieAkcjiWkladPieniezny:
                lines.Add(new(p.ShareContributionsDue, ev.TotalAmount, 0, cp, "Należny wkład na akcje"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount, Description: "Kapitał akcyjny"));
                if (ev.SupplementaryCapitalAmount > 0) lines.Add(new(p.SupplementaryCapital, 0, ev.SupplementaryCapitalAmount, Description: "Nadwyżka wkładu — kapitał zapasowy"));
                break;
            case CapitalEventType.ObjecieAkcjiWkladNiepieniezny:
                if (string.IsNullOrWhiteSpace(ev.DebitAccountNumber)) throw new DomainException("Wskaż konto składnika wnoszonego aportem (np. 010-03, 020).");
                lines.Add(new(ev.DebitAccountNumber, ev.TotalAmount, 0, Description: "Wkład niepieniężny"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount));
                if (ev.SupplementaryCapitalAmount > 0) lines.Add(new(p.SupplementaryCapital, 0, ev.SupplementaryCapitalAmount));
                break;
            case CapitalEventType.WplataWkladu:
                lines.Add(new(ev.DebitAccountNumber ?? p.Bank, ev.TotalAmount, 0, Description: "Wpłata wkładu"));
                lines.Add(new(p.ShareContributionsDue, 0, ev.TotalAmount, cp, "Wpłata wkładu", await OldestOpenItemAsync(p.ShareContributionsDue, OpenItemSide.Naleznosc)));
                break;
            case CapitalEventType.PodwyzszenieKapitaluAkcyjnego:
                lines.Add(new(ev.DebitAccountNumber ?? p.SupplementaryCapital, ev.ShareCapitalAmount, 0, Description: "Podwyższenie kapitału akcyjnego ze środków własnych"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount));
                break;
            case CapitalEventType.ObnizenieKapitaluAkcyjnego:
                lines.Add(new(p.ShareCapital, ev.ShareCapitalAmount, 0, Description: "Obniżenie kapitału akcyjnego"));
                lines.Add(ev.CreditAccountNumber is { } ca && ca != p.ShareholderOther
                    ? new(ca, 0, ev.ShareCapitalAmount)
                    : new(p.ShareholderOther, 0, ev.ShareCapitalAmount, cp ?? throw new DomainException("Wypłata z obniżenia kapitału wymaga wskazania akcjonariusza.")));
                break;
            case CapitalEventType.PrzeniesienieNaKapitalZapasowy:
            case CapitalEventType.PodzialZysku:
                lines.Add(new(p.RetainedEarnings, ev.TotalAmount, 0, Description: "Podział wyniku"));
                lines.Add(new(ev.CreditAccountNumber ?? p.SupplementaryCapital, 0, ev.TotalAmount));
                break;
            case CapitalEventType.UtworzenieKapitaluRezerwowego:
                lines.Add(new(ev.DebitAccountNumber ?? p.RetainedEarnings, ev.TotalAmount, 0));
                lines.Add(new(p.ReserveCapital, 0, ev.TotalAmount, Description: "Kapitał rezerwowy"));
                break;
            case CapitalEventType.PokrycieStraty:
                lines.Add(new(ev.DebitAccountNumber ?? p.SupplementaryCapital, ev.TotalAmount, 0, Description: "Pokrycie straty"));
                lines.Add(new(p.RetainedEarnings, 0, ev.TotalAmount));
                break;
            case CapitalEventType.UchwalenieDywidendy:
                lines.Add(new(p.RetainedEarnings, ev.TotalAmount, 0, Description: "Dywidenda uchwalona"));
                lines.Add(new(p.DividendsPayable, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż akcjonariusza."), "Zobowiązanie z tytułu dywidendy"));
                break;
            case CapitalEventType.ZaliczkaNaPoczetDywidendy:
                lines.Add(new(p.DividendAdvances, ev.TotalAmount, 0, Description: "Zaliczka na poczet dywidendy"));
                lines.Add(new(p.DividendsPayable, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż akcjonariusza.")));
                break;
            case CapitalEventType.WyplataDywidendy:
                lines.Add(new(p.DividendsPayable, ev.TotalAmount, 0, cp, "Wypłata dywidendy", await OldestOpenItemAsync(p.DividendsPayable, OpenItemSide.Zobowiazanie)));
                lines.Add(new(ev.CreditAccountNumber ?? p.Bank, 0, ev.TotalAmount - ev.WithholdingTaxAmount, Description: "Wypłata netto"));
                if (ev.WithholdingTaxAmount > 0) lines.Add(new(p.WithholdingTax, 0, ev.WithholdingTaxAmount, Description: "Zryczałtowany podatek od dywidendy (płatnik)"));
                break;
            case CapitalEventType.PozyczkaOdAkcjonariusza:
                lines.Add(new(ev.DebitAccountNumber ?? p.Bank, ev.TotalAmount, 0, Description: "Wpływ pożyczki"));
                lines.Add(new(p.ShareholderLoans, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż akcjonariusza."), "Pożyczka od akcjonariusza"));
                break;
            case CapitalEventType.SplataPozyczkiAkcjonariusza:
                lines.Add(new(p.ShareholderLoans, ev.TotalAmount, 0, cp, "Spłata pożyczki", await OldestOpenItemAsync(p.ShareholderLoans, OpenItemSide.Zobowiazanie)));
                lines.Add(new(ev.CreditAccountNumber ?? p.Bank, 0, ev.TotalAmount));
                break;
            default:
                throw new DomainException("Zdarzenia typu „inne” księguje się poleceniem księgowania.");
        }

        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("KAP", ev.EventDate, ev.ResolutionDate ?? ev.EventDate, ev.EventDate,
            $"{ev.Type}: {ev.Description}".Trim(), "KAP", $"KAP/{ev.EventDate:yyyyMMdd}/{ev.Id.ToString()[..6]}", lines), ct);
        ev.JournalEntryId = entry.Id;
        ev.Status = CapitalEventStatus.Zaksiegowany;
        await _audit.AddAsync(_user, "ZDARZENIE_KAPITALOWE_ZAKSIEGOWANE", "CapitalEvent", ev.Id.ToString(), entry.JournalNumber, ct);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Zestawienie kapitałów (salda kont kapitałowych) — informacyjne, z ledger.</summary>
    public async Task<List<(string Account, string Name, decimal Balance)>> CapitalSummaryAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var p = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, asOf, asOf, ct);
        var numbers = new[] { p.ShareCapital, p.SupplementaryCapital, p.ReserveCapital, p.RetainedEarnings, p.DividendAdvances, p.ShareContributionsDue, p.DividendsPayable, p.ShareholderLoans };
        return tb.Where(t => numbers.Contains(t.Account.Number)).Select(t => (t.Account.Number, t.Account.Name, -t.Balance)).ToList();
    }
}
