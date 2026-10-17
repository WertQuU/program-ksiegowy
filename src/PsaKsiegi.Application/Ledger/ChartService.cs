using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PsaKsiegi.Application.Abstractions;
using PsaKsiegi.Application.Audit;
using PsaKsiegi.Application.Templates;
using PsaKsiegi.Data;
using PsaKsiegi.Domain.Accounts;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Ledger;
using PsaKsiegi.Domain.Reporting;
using PsaKsiegi.Domain.Security;

namespace PsaKsiegi.Application.Ledger;

/// <summary>Plan kont: tworzenie z szablonu, konta syntetyczne/analityczne, walidacja, import/eksport CSV, wersjonowanie per rok.</summary>
public sealed class ChartService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public ChartService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    public async Task<ChartOfAccounts> GetChartForYearAsync(Guid fiscalYearId, CancellationToken ct = default) =>
        await _db.Charts.Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct)
        ?? throw new DomainException("Brak planu kont dla roku obrotowego. Utwórz plan kont (Plan kont → Utwórz z szablonu).");

    public Task<List<Account>> GetAccountsAsync(Guid chartId, CancellationToken ct = default) =>
        _db.Accounts.Where(a => a.ChartId == chartId).OrderBy(a => a.Number).ToListAsync(ct);

    public async Task<ChartOfAccounts> CreateFromTemplateAsync(Guid fiscalYearId, IEnumerable<AccountTemplate>? template = null, string? name = null, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageChartOfAccounts);
        if (await _db.Charts.AnyAsync(c => c.FiscalYearId == fiscalYearId, ct))
            throw new DomainException("Plan kont dla tego roku już istnieje.");
        var chart = new ChartOfAccounts
        {
            CompanyId = _user.CompanyId,
            FiscalYearId = fiscalYearId,
            Name = name ?? ChartTemplate.Name,
            CreatedAtUtc = _clock.UtcNow
        };
        _db.Charts.Add(chart);
        var byNumber = new Dictionary<string, Account>();
        foreach (var t in (template ?? ChartTemplate.Accounts).OrderBy(t => t.Number))
        {
            Account.ValidateNumber(t.Number).ThrowIfInvalid();
            var parentNumber = t.Number.Contains('-') ? t.Number[..t.Number.LastIndexOf('-')] : null;
            var acc = new Account
            {
                CompanyId = _user.CompanyId,
                ChartId = chart.Id,
                Number = t.Number,
                Name = t.Name,
                Type = t.Type,
                Group = Account.GroupFromNumber(t.Number),
                BalanceDebitLine = t.BalanceDebitLine,
                BalanceCreditLine = t.BalanceCreditLine,
                ProfitLossLine = t.ProfitLossLine,
                IsSettlementAccount = t.Settlement,
                RequiresCounterparty = t.Settlement,
                CitClassification = t.Cit,
                CreatedAtUtc = _clock.UtcNow
            };
            if (parentNumber is not null)
            {
                if (!byNumber.TryGetValue(parentNumber, out var parent)) throw new DomainException($"Brak konta nadrzędnego {parentNumber} dla {t.Number}.");
                acc.ParentId = parent.Id;
                acc.Level = parent.Level + 1;
            }
            byNumber[t.Number] = acc;
            _db.Accounts.Add(acc);
        }
        await _audit.AddAsync(_user, "PLAN_KONT_UTWORZONY", "ChartOfAccounts", chart.Id.ToString(), $"{chart.Name}; kont: {byNumber.Count}", ct);
        await _db.SaveChangesAsync(ct);
        return chart;
    }

    /// <summary>Dodaje konto (syntetyczne lub analityczne). Konto analityczne do konta z zapisami — niedozwolone.</summary>
    public async Task<Account> AddAccountAsync(Guid chartId, string number, string name, AccountType type, string? balanceDebitLine = null,
        string? balanceCreditLine = null, string? profitLossLine = null, bool settlement = false, CitClassification cit = CitClassification.NieDotyczy,
        CancellationToken ct = default)
    {
        _user.Require(Permission.ManageChartOfAccounts);
        number = number.Trim();
        Account.ValidateNumber(number).ThrowIfInvalid();
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nazwa konta jest wymagana.");
        if (await _db.Accounts.AnyAsync(a => a.ChartId == chartId && a.Number == number, ct)) throw new DomainException($"Konto {number} już istnieje.");
        ValidateLines(balanceDebitLine, balanceCreditLine, profitLossLine);
        var chart = await _db.Charts.FirstAsync(c => c.Id == chartId, ct);
        Account? parent = null;
        if (number.Contains('-'))
        {
            var pn = number[..number.LastIndexOf('-')];
            parent = await _db.Accounts.FirstOrDefaultAsync(a => a.ChartId == chartId && a.Number == pn, ct)
                     ?? throw new DomainException($"Brak konta nadrzędnego {pn}.");
            if (await _db.JournalLines.AnyAsync(l => l.AccountId == parent.Id, ct))
                throw new DomainException($"Konto {pn} posiada zapisy — nie można dodać do niego analityki. Przeksięguj saldo na nowe konto analityczne poleceniem księgowania.");
        }
        var acc = new Account
        {
            CompanyId = chart.CompanyId,
            ChartId = chartId,
            Number = number,
            Name = name.Trim(),
            Type = type,
            Group = Account.GroupFromNumber(number),
            ParentId = parent?.Id,
            Level = parent is null ? 0 : parent.Level + 1,
            BalanceDebitLine = balanceDebitLine,
            BalanceCreditLine = balanceCreditLine,
            ProfitLossLine = profitLossLine,
            IsSettlementAccount = settlement,
            RequiresCounterparty = settlement,
            CitClassification = cit,
            CreatedAtUtc = _clock.UtcNow
        };
        _db.Accounts.Add(acc);
        await _audit.AddAsync(_user, "KONTO_DODANE", "Account", acc.Id.ToString(), $"{number} {name}", ct);
        await _db.SaveChangesAsync(ct);
        return acc;
    }

    public async Task UpdateAccountAsync(Guid accountId, string name, string? balanceDebitLine, string? balanceCreditLine, string? profitLossLine,
        CitClassification cit, bool isActive, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageChartOfAccounts);
        var acc = await _db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        ValidateLines(balanceDebitLine, balanceCreditLine, profitLossLine);
        var before = $"{acc.Name}|{acc.BalanceDebitLine}|{acc.BalanceCreditLine}|{acc.ProfitLossLine}|{acc.CitClassification}|{acc.IsActive}";
        acc.Name = name.Trim();
        acc.BalanceDebitLine = balanceDebitLine;
        acc.BalanceCreditLine = balanceCreditLine;
        acc.ProfitLossLine = profitLossLine;
        acc.CitClassification = cit;
        acc.IsActive = isActive;
        await _audit.AddAsync(_user, "KONTO_ZMIENIONE", "Account", acc.Id.ToString(), $"{acc.Number}: {before} → {acc.Name}|{balanceDebitLine}|{balanceCreditLine}|{profitLossLine}|{cit}|{isActive}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Usunięcie konta — tylko bez zapisów (również w buforze), bez analityk i bez odwołań w dokumentach.</summary>
    public async Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageChartOfAccounts);
        var acc = await _db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        if (await _db.JournalLines.AnyAsync(l => l.AccountId == accountId, ct))
            throw new DomainException($"Konto {acc.Number} ma zapisy księgowe — nie może zostać usunięte. Możesz je oznaczyć jako nieaktywne.");
        if (await _db.Accounts.AnyAsync(a => a.ParentId == accountId, ct))
            throw new DomainException($"Konto {acc.Number} ma konta analityczne — usuń je najpierw.");
        if (await _db.DocumentPostingLines.AnyAsync(l => l.AccountId == accountId, ct) || await _db.DocumentLines.AnyAsync(l => l.AccountId == accountId, ct))
            throw new DomainException($"Konto {acc.Number} jest użyte w dekretacji dokumentów.");
        _db.Accounts.Remove(acc);
        await _audit.AddAsync(_user, "KONTO_USUNIETE", "Account", acc.Id.ToString(), acc.Number, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Kontrola poprawności planu kont (mapowanie na sprawozdanie, typy, kompletność).</summary>
    public async Task<ValidationResult> ValidateChartAsync(Guid chartId, CancellationToken ct = default)
    {
        var accounts = await GetAccountsAsync(chartId, ct);
        var parents = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        var r = new ValidationResult();
        if (accounts.Count == 0) return r.Error("Plan kont jest pusty.");
        foreach (var a in accounts.Where(a => !parents.Contains(a.Id) && a.IsActive))
        {
            if (a.IsBalanceAccount)
            {
                if (a.Type == AccountType.AktywnoPasywne && (a.BalanceDebitLine is null || a.BalanceCreditLine is null))
                    r.Error($"Konto {a.Number}: konto aktywno-pasywne wymaga pozycji bilansu dla salda Wn i Ma.");
                if (a.Type != AccountType.AktywnoPasywne && a.BalanceDebitLine is null && a.BalanceCreditLine is null)
                    r.Error($"Konto {a.Number}: brak przypisania do pozycji bilansu.");
            }
            if (a.IsResultAccount && a.ProfitLossLine is null) r.Error($"Konto {a.Number}: brak przypisania do pozycji rachunku zysków i strat.");
            foreach (var code in new[] { a.BalanceDebitLine, a.BalanceCreditLine, a.ProfitLossLine }.Where(c => c != null))
                if (!FinancialStatementLayout.IsLeaf(code!)) r.Error($"Konto {a.Number}: pozycja „{code}” nie istnieje lub nie jest pozycją końcową sprawozdania.");
            if (a.IsResultAccount && a.CitClassification == CitClassification.NieDotyczy)
                r.Warning($"Konto {a.Number}: brak klasyfikacji podatkowej (CIT).");
        }
        foreach (var required in new[] { "860", "820" })
            if (!accounts.Any(a => a.Number == required)) r.Warning($"Brak konta {required} wymaganego do zamknięcia roku.");
        return r;
    }

    private static void ValidateLines(params string?[] codes)
    {
        foreach (var c in codes.Where(c => !string.IsNullOrWhiteSpace(c)))
            if (!FinancialStatementLayout.IsLeaf(c!)) throw new DomainException($"Pozycja sprawozdania „{c}” nie istnieje lub nie jest pozycją końcową.");
    }

    /// <summary>Eksport planu kont do CSV (UTF-8, separator „;”).</summary>
    public async Task<string> ExportCsvAsync(Guid chartId, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Numer;Nazwa;Typ;PozycjaBilansuWn;PozycjaBilansuMa;PozycjaRZiS;Rozrachunkowe;CIT;Aktywne");
        foreach (var a in await GetAccountsAsync(chartId, ct))
            sb.AppendLine(string.Join(';', Esc(a.Number), Esc(a.Name), a.Type, a.BalanceDebitLine, a.BalanceCreditLine, a.ProfitLossLine,
                a.IsSettlementAccount ? "1" : "0", a.CitClassification, a.IsActive ? "1" : "0"));
        return sb.ToString();
    }

    /// <summary>Import planu kont z CSV (format eksportu) do nowego, pustego planu dla roku.</summary>
    public static List<AccountTemplate> ParseCsv(string csv)
    {
        var list = new List<AccountTemplate>();
        var lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < lines.Length; i++)
        {
            var f = SplitCsv(lines[i]);
            if (f.Count < 8) throw new DomainException($"Wiersz {i + 1}: oczekiwano co najmniej 8 kolumn.");
            if (!Enum.TryParse<AccountType>(f[2], out var type)) throw new DomainException($"Wiersz {i + 1}: nieznany typ konta „{f[2]}”.");
            if (!Enum.TryParse<CitClassification>(f[7], out var cit)) cit = CitClassification.NieDotyczy;
            list.Add(new AccountTemplate(f[0].Trim(), f[1].Trim(), type, Null(f[3]), Null(f[4]), Null(f[5]), f[6] == "1", cit));
        }
        return list;
        static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Nowa wersja planu kont dla kolejnego roku — kopia struktury (bez sald).</summary>
    public async Task<ChartOfAccounts> CopyChartToYearAsync(Guid sourceChartId, Guid targetFiscalYearId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageChartOfAccounts);
        var src = await _db.Charts.FirstAsync(c => c.Id == sourceChartId, ct);
        var accounts = await GetAccountsAsync(sourceChartId, ct);
        var template = accounts.Where(a => a.IsActive).Select(a => new AccountTemplate(a.Number, a.Name, a.Type, a.BalanceDebitLine, a.BalanceCreditLine,
            a.ProfitLossLine, a.IsSettlementAccount, a.CitClassification)).ToList();
        var chart = await CreateFromTemplateAsync(targetFiscalYearId, template, src.Name, ct);
        chart.CopiedFromId = src.Id;
        chart.Version = src.Version + 1;
        await _db.SaveChangesAsync(ct);
        return chart;
    }

    private static string Esc(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    internal static List<string> SplitCsv(string line, char sep = ';')
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == sep) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
