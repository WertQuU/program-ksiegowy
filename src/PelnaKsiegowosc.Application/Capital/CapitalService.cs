using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Capital;

/// <summary>
/// Ewidencja księgowa zdarzeń kapitałowych (wkłady, zmiany kapitału podstawowego, podział wyniku, dywidendy i wypłaty
/// zysku, pożyczki od wspólników). Zdarzenie księguje się dopiero po potwierdzeniu dokumentów korporacyjnych przez osobę
/// uprawnioną — dane wprowadzone przez użytkownika nie są automatycznie podstawą zapisu. Moduł nie zastępuje księgi
/// udziałów, rejestru akcjonariuszy ani dokumentów korporacyjnych.
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
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Podaj nazwę albo imię i nazwisko wspólnika/akcjonariusza.");
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
        await _audit.AddAsync(_user, "WSPOLNIK_DODANY", "Shareholder", sh.Id.ToString(), sh.Name, ct);
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
                throw new DomainException("Wartość wkładu musi być równa sumie kwot na kapitał podstawowy i zapasowy.");
            if (ev.ContributionType == ContributionType.PracaLubUslugi)
                throw new DomainException("Wkład w postaci świadczenia pracy lub usług nie jest wnoszony na kapitał podstawowy (np. P.S.A. — art. 300^3 KSH) — jego ujęcie wymaga indywidualnej oceny księgowej (funkcja niezaimplementowana).");
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
        var form = await _db.Companies.Where(c => c.Id == _user.CompanyId).Select(c => c.LegalForm).FirstAsync(ct);
        var missing = ev.MissingDocuments(form);
        if (missing.Count > 0) throw new DomainException("Brak wymaganych danych/dokumentów: " + string.Join("; ", missing));
        if (ev.CreatedByUserId == _user.UserId && !_user.Roles.Contains(Role.Zarzad) && !_user.Roles.Contains(Role.Administrator))
            throw new AccessDeniedException("Dokumenty zdarzenia kapitałowego potwierdza osoba inna niż wprowadzająca lub członek zarządu.");
        ev.Status = CapitalEventStatus.DokumentyPotwierdzone;
        ev.ConfirmedByUserId = _user.UserId;
        ev.ConfirmedAtUtc = _clock.UtcNow;
        await _audit.AddAsync(_user, "ZDARZENIE_KAPITALOWE_POTWIERDZONE", "CapitalEvent", ev.Id.ToString(),
            $"{ev.Type}; uchwała {ev.ResolutionReference}; umowa {ev.AgreementReference}; KRS {ev.KrsEntryReference}; dopuszczalność wypłaty potwierdzona: {ev.SolvencyTestConfirmed}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Księgowanie zdarzenia. W prostej spółce akcyjnej kapitał akcyjny tworzą wyłącznie wniesione wkłady (art. 300^3 § 1 KSH)
    /// — objęcie akcji za wkład pieniężny nie zwiększa kapitału: zdarzenie trafia do ewidencji wkładów do wniesienia (bez zapisu
    /// w księgach, wynik null), a kapitał akcyjny (i ewentualnie zapasowy) uznaje się dopiero przy wpłacie wkładu.
    /// W spółce akcyjnej kapitał zakładowy wykazuje się w wysokości ze statutu, a niewniesione wkłady jako należne wpłaty (art. 36 ust. 2 UoR).
    /// </summary>
    public async Task<JournalEntry?> PostEventAsync(Guid eventId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var ev = await _db.CapitalEvents.FirstAsync(e => e.Id == eventId && e.CompanyId == _user.CompanyId, ct);
        if (ev.Status == CapitalEventStatus.Zaksiegowany) throw new DomainException("Zdarzenie jest już zaksięgowane.");
        if (ev.Status != CapitalEventStatus.DokumentyPotwierdzone) throw new DomainException("Przed księgowaniem dokumenty korporacyjne muszą zostać potwierdzone.");
        var p = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var sh = ev.ShareholderId is { } sid ? await _db.Shareholders.FirstAsync(s => s.Id == sid, ct) : null;
        var cp = sh?.CounterpartyId;
        var lines = new List<LineInput>();
        var form = await _db.Companies.Where(c => c.Id == _user.CompanyId).Select(c => c.LegalForm).FirstAsync(ct);
        var psa = form == LegalForm.ProstaSpolkaAkcyjna;

        if (psa && ev.Type == CapitalEventType.ObjecieAkcjiWkladPieniezny)
        {
            if (ev.ShareholderId is null) throw new DomainException("Objęcie akcji wymaga wskazania akcjonariusza.");
            ev.Status = CapitalEventStatus.Zaksiegowany;
            await _audit.AddAsync(_user, "AKCJE_OBJETE_WKLAD_DO_WNIESIENIA", "CapitalEvent", ev.Id.ToString(),
                $"P.S.A.: wkład do wniesienia {Money.Xml(ev.TotalAmount)} — kapitał akcyjny zostanie uznany przy wpłacie (art. 300^3 KSH)", ct);
            await _db.SaveChangesAsync(ct);
            return null;
        }

        async Task<Guid?> OldestOpenItemAsync(string account, OpenItemSide side)
        {
            if (cp is null) throw new DomainException("Zdarzenie wymaga wskazania wspólnika/akcjonariusza.");
            var item = await _db.OpenItems.Where(o => o.CompanyId == _user.CompanyId && o.CounterpartyId == cp && o.AccountNumber == account && o.Side == side && o.Status != OpenItemStatus.Rozliczona)
                .OrderBy(o => o.DocumentDate).FirstOrDefaultAsync(ct);
            return item?.Id ?? throw new DomainException($"Brak otwartego rozrachunku na koncie {account} dla wspólnika/akcjonariusza.");
        }

        switch (ev.Type)
        {
            case CapitalEventType.ObjecieAkcjiWkladPieniezny:
                lines.Add(new(p.ShareContributionsDue, ev.TotalAmount, 0, cp, "Należny wkład na kapitał"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount, Description: "Kapitał podstawowy"));
                if (ev.SupplementaryCapitalAmount > 0) lines.Add(new(p.SupplementaryCapital, 0, ev.SupplementaryCapitalAmount, Description: "Nadwyżka wkładu — kapitał zapasowy"));
                break;
            case CapitalEventType.ObjecieAkcjiWkladNiepieniezny:
                if (string.IsNullOrWhiteSpace(ev.DebitAccountNumber)) throw new DomainException("Wskaż konto składnika wnoszonego aportem (np. 010-03, 020).");
                lines.Add(new(ev.DebitAccountNumber, ev.TotalAmount, 0, Description: "Wkład niepieniężny"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount));
                if (ev.SupplementaryCapitalAmount > 0) lines.Add(new(p.SupplementaryCapital, 0, ev.SupplementaryCapitalAmount));
                break;
            case CapitalEventType.WplataWkladu when psa:
            {
                if (ev.ShareholderId is not { } shId) throw new DomainException("Wpłata wkładu wymaga wskazania akcjonariusza.");
                lines.Add(new(ev.DebitAccountNumber ?? p.Bank, ev.TotalAmount, 0, Description: "Wpłata wkładu na akcje"));
                // najpierw rozliczenie należnych wkładów zaksięgowanych według wcześniejszej logiki (kapitał uznany przy objęciu)
                var rest = ev.TotalAmount;
                foreach (var legacy in await LegacyOpenContributionsAsync(cp, p.ShareContributionsDue, ct))
                {
                    if (rest <= 0) break;
                    var part = Math.Min(rest, legacy.RemainingPln);
                    lines.Add(new(p.ShareContributionsDue, 0, part, cp, "Wpłata wkładu — rozliczenie należnego wkładu (zapis sprzed wersji 1.0.1)", legacy.Id));
                    rest -= part;
                }
                if (rest > 0)
                {
                    var (unpaid, capitalShare) = await UnpaidContributionAsync(shId, ev.Id, ct);
                    if (rest > unpaid)
                        throw new DomainException($"Wpłata ({Money.Format(ev.TotalAmount)}) przekracza wkłady objęte i niewniesione przez akcjonariusza ({Money.Format(unpaid + ev.TotalAmount - rest)}).");
                    var toCapital = rest == ev.TotalAmount && ev.ShareCapitalAmount + ev.SupplementaryCapitalAmount == ev.TotalAmount && ev.TotalAmount > 0
                        ? ev.ShareCapitalAmount
                        : Money.Round(rest * capitalShare);
                    lines.Add(new(p.ShareCapital, 0, toCapital, Description: "Kapitał akcyjny — wkład wniesiony"));
                    if (rest - toCapital > 0) lines.Add(new(p.SupplementaryCapital, 0, rest - toCapital, Description: "Nadwyżka wkładu — kapitał zapasowy"));
                }
                break;
            }
            case CapitalEventType.WplataWkladu:
                lines.Add(new(ev.DebitAccountNumber ?? p.Bank, ev.TotalAmount, 0, Description: "Wpłata wkładu"));
                lines.Add(new(p.ShareContributionsDue, 0, ev.TotalAmount, cp, "Wpłata wkładu", await OldestOpenItemAsync(p.ShareContributionsDue, OpenItemSide.Naleznosc)));
                break;
            case CapitalEventType.PodwyzszenieKapitaluAkcyjnego:
                lines.Add(new(ev.DebitAccountNumber ?? p.SupplementaryCapital, ev.ShareCapitalAmount, 0, Description: "Podwyższenie kapitału podstawowego ze środków własnych"));
                lines.Add(new(p.ShareCapital, 0, ev.ShareCapitalAmount));
                break;
            case CapitalEventType.ObnizenieKapitaluAkcyjnego:
                lines.Add(new(p.ShareCapital, ev.ShareCapitalAmount, 0, Description: "Obniżenie kapitału podstawowego"));
                lines.Add(ev.CreditAccountNumber is { } ca && ca != p.ShareholderOther
                    ? new(ca, 0, ev.ShareCapitalAmount)
                    : new(p.ShareholderOther, 0, ev.ShareCapitalAmount, cp ?? throw new DomainException("Wypłata z obniżenia kapitału wymaga wskazania wspólnika/akcjonariusza.")));
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
                lines.Add(new(p.DividendsPayable, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż wspólnika/akcjonariusza."), "Zobowiązanie z tytułu dywidendy"));
                break;
            case CapitalEventType.ZaliczkaNaPoczetDywidendy:
                lines.Add(new(p.DividendAdvances, ev.TotalAmount, 0, Description: "Zaliczka na poczet dywidendy"));
                lines.Add(new(p.DividendsPayable, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż wspólnika/akcjonariusza.")));
                break;
            case CapitalEventType.WyplataDywidendy:
                lines.Add(new(p.DividendsPayable, ev.TotalAmount, 0, cp, "Wypłata dywidendy", await OldestOpenItemAsync(p.DividendsPayable, OpenItemSide.Zobowiazanie)));
                lines.Add(new(ev.CreditAccountNumber ?? p.Bank, 0, ev.TotalAmount - ev.WithholdingTaxAmount, Description: "Wypłata netto"));
                if (ev.WithholdingTaxAmount > 0) lines.Add(new(p.WithholdingTax, 0, ev.WithholdingTaxAmount, Description: "Zryczałtowany podatek od dywidendy/udziału w zysku (płatnik)"));
                break;
            case CapitalEventType.PozyczkaOdAkcjonariusza:
                lines.Add(new(ev.DebitAccountNumber ?? p.Bank, ev.TotalAmount, 0, Description: "Wpływ pożyczki"));
                lines.Add(new(p.ShareholderLoans, 0, ev.TotalAmount, cp ?? throw new DomainException("Wskaż wspólnika/akcjonariusza."), "Pożyczka od wspólnika/akcjonariusza"));
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

    /// <summary>
    /// P.S.A.: wkłady pieniężne objęte, a jeszcze niewniesione przez akcjonariusza (poza bilansem — nie są kapitałem akcyjnym)
    /// oraz udział części przeznaczonej na kapitał akcyjny w objętych wkładach (do podziału wpłaty).
    /// </summary>
    public async Task<(decimal Unpaid, decimal CapitalShare)> UnpaidContributionAsync(Guid shareholderId, Guid? excludingEventId = null, CancellationToken ct = default)
    {
        var p = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var events = await _db.CapitalEvents.AsNoTracking()
            .Where(e => e.CompanyId == _user.CompanyId && e.ShareholderId == shareholderId && e.Status == CapitalEventStatus.Zaksiegowany && e.Id != excludingEventId
                        && (e.Type == CapitalEventType.ObjecieAkcjiWkladPieniezny || e.Type == CapitalEventType.WplataWkladu)).ToListAsync(ct);
        var entryIds = events.Where(e => e.JournalEntryId != null).Select(e => e.JournalEntryId!.Value).ToList();
        var entries = await _db.JournalEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).Select(e => new { e.Id, e.ReversedByEntryId }).ToDictionaryAsync(e => e.Id, ct);
        // objęcia według nowej logiki: bez zapisu albo z zapisem wcześniejszym, który wystornowano
        var subscribed = events.Where(e => e.Type == CapitalEventType.ObjecieAkcjiWkladPieniezny
                                           && (e.JournalEntryId is null || entries.GetValueOrDefault(e.JournalEntryId.Value)?.ReversedByEntryId is not null)).ToList();
        // wpłaty: bez części, która rozliczyła należne wkłady zaksięgowane według wcześniejszej logiki (konto należnych wkładów)
        var legacyPaid = await _db.JournalLines.AsNoTracking()
            .Where(l => entryIds.Contains(l.EntryId) && l.AccountNumber == p.ShareContributionsDue && l.Credit != 0)
            .Select(l => new { l.EntryId, l.Credit }).ToListAsync(ct);
        var paid = events.Where(e => e.Type == CapitalEventType.WplataWkladu)
            .Sum(e => e.TotalAmount - legacyPaid.Where(l => l.EntryId == e.JournalEntryId).Sum(l => l.Credit));
        var total = subscribed.Sum(e => e.TotalAmount);
        var share = total == 0 ? 1m : subscribed.Sum(e => e.ShareCapitalAmount) / total;
        return (total - paid, share);
    }

    private async Task<List<OpenItem>> LegacyOpenContributionsAsync(Guid? counterpartyId, string account, CancellationToken ct) =>
        counterpartyId is null ? new() : (await _db.OpenItems.Where(o => o.CompanyId == _user.CompanyId && o.CounterpartyId == counterpartyId && o.AccountNumber == account
                                                                       && o.Side == OpenItemSide.Naleznosc).OrderBy(o => o.DocumentDate).ToListAsync(ct))
            .Where(o => o.RemainingPln > 0).ToList();

    public sealed record LegacyPsaSubscription(Guid EventId, DateOnly Date, string Shareholder, decimal Amount, decimal Paid, string JournalNumber);

    /// <summary>
    /// P.S.A.: objęcia akcji za wkład pieniężny zaksięgowane według logiki sprzed wersji 1.0.1 (kapitał akcyjny uznany już przy
    /// objęciu, Wn należne wkłady / Ma kapitał), których zapis nie został wystornowany. Kontrola danych — nic nie jest zmieniane.
    /// </summary>
    public async Task<List<LegacyPsaSubscription>> LegacyPsaSubscriptionsAsync(CancellationToken ct = default)
    {
        var form = await _db.Companies.Where(c => c.Id == _user.CompanyId).Select(c => c.LegalForm).FirstAsync(ct);
        if (form != LegalForm.ProstaSpolkaAkcyjna) return new();
        var list = await (from e in _db.CapitalEvents.AsNoTracking()
                          join j in _db.JournalEntries.AsNoTracking() on e.JournalEntryId equals j.Id
                          where e.CompanyId == _user.CompanyId && e.Type == CapitalEventType.ObjecieAkcjiWkladPieniezny && j.ReversedByEntryId == null
                          select new { EventId = e.Id, e.EventDate, e.ShareholderId, e.TotalAmount, EntryId = j.Id, j.JournalNumber }).ToListAsync(ct);
        var names = await _db.Shareholders.AsNoTracking().Where(s => s.CompanyId == _user.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var result = new List<LegacyPsaSubscription>();
        foreach (var x in list)
        {
            var item = await _db.OpenItems.AsNoTracking().FirstOrDefaultAsync(o => o.JournalEntryId == x.EntryId && o.Side == OpenItemSide.Naleznosc, ct);
            result.Add(new(x.EventId, x.EventDate, x.ShareholderId is { } sid ? names.GetValueOrDefault(sid, "") : "", x.TotalAmount, item?.SettledAmountPln ?? 0m, x.JournalNumber));
        }
        return result;
    }

    /// <summary>
    /// Korekta objęcia akcji P.S.A. zaksięgowanego według wcześniejszej logiki — wyłącznie stornem (zapis pierwotny pozostaje
    /// w księgach), gdy wkład nie został jeszcze wpłacony. Po stornie objęcie jest ujęte w ewidencji wkładów do wniesienia,
    /// a kapitał zostanie uznany przy wpłacie. Przy wpłacie częściowej wymagana korekta poleceniem księgowania (procedura w podręczniku).
    /// </summary>
    public async Task<JournalEntry> CorrectLegacyPsaSubscriptionAsync(Guid eventId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.CorrectEntries);
        var legacy = (await LegacyPsaSubscriptionsAsync(ct)).FirstOrDefault(l => l.EventId == eventId)
                     ?? throw new DomainException("To zdarzenie nie jest objęciem akcji P.S.A. zaksięgowanym według wcześniejszej logiki.");
        if (legacy.Paid != 0)
            throw new DomainException($"Wkład został już częściowo wpłacony ({Money.Format(legacy.Paid)} zł) — storno całego zapisu zaburzyłoby rozliczenie wpłat. " +
                "Postępuj wg podręcznika („Kapitał P.S.A. — korekta zapisów sprzed wersji 1.0.1”): PK storno części niewpłaconej (Wn kapitał akcyjny/zapasowy, Ma należne wkłady).");
        var ev = await _db.CapitalEvents.FirstAsync(e => e.Id == eventId, ct);
        var reversal = await new LedgerService(_db, _user, _clock).ReverseAsync(ev.JournalEntryId!.Value,
            string.IsNullOrWhiteSpace(reason) ? "P.S.A.: kapitał akcyjny tworzą wniesione wkłady (art. 300^3 KSH) — objęcie przeniesione do ewidencji wkładów do wniesienia" : reason, null, ct);
        await _audit.AddAsync(_user, "KAPITAL_PSA_KOREKTA_OBJECIA", "CapitalEvent", ev.Id.ToString(), $"storno {legacy.JournalNumber} → {reversal.JournalNumber}", ct);
        await _db.SaveChangesAsync(ct);
        return reversal;
    }

    /// <summary>P.S.A.: zestawienie wkładów objętych i niewniesionych wg akcjonariuszy (ewidencja pozabilansowa).</summary>
    public async Task<List<(string Shareholder, decimal Unpaid)>> UnpaidContributionsAsync(CancellationToken ct = default)
    {
        var result = new List<(string, decimal)>();
        foreach (var sh in await _db.Shareholders.AsNoTracking().Where(s => s.CompanyId == _user.CompanyId).OrderBy(s => s.Name).ToListAsync(ct))
        {
            var (unpaid, _) = await UnpaidContributionAsync(sh.Id, null, ct);
            if (unpaid != 0) result.Add((sh.Name, unpaid));
        }
        return result;
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
