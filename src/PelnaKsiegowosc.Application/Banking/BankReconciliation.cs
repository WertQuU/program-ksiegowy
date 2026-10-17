using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;

namespace PelnaKsiegowosc.Application.Banking;

public sealed record BankReconciliationRow(Guid MoneyAccountId, string AccountName, string LedgerAccount, decimal LedgerBalance,
    decimal? StatementBalance, DateOnly? StatementDate, decimal? Difference, int UnpostedTransactions, decimal UnpostedAmount);

/// <summary>Uzgodnienie salda rachunku z wyciągu z saldem konta księgowego na dzień.</summary>
public sealed class BankReconciliation
{
    private readonly KsiegiDbContext _db;

    public BankReconciliation(KsiegiDbContext db) => _db = db;

    public async Task<List<BankReconciliationRow>> ReconcileAsync(Guid companyId, Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var accounts = await _db.MoneyAccounts.AsNoTracking().Where(m => m.CompanyId == companyId && m.IsActive).ToListAsync(ct);
        if (accounts.Count == 0) return new();
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, asOf, asOf, ct);
        var rows = new List<BankReconciliationRow>();
        foreach (var m in accounts)
        {
            var ledger = tb.FirstOrDefault(t => t.Account.Number == m.LedgerAccountNumber)?.Balance ?? 0m;
            var statement = await _db.MoneyStatements.AsNoTracking()
                .Where(s => s.MoneyAccountId == m.Id && s.DateTo <= asOf && s.ClosingBalance != null)
                .OrderByDescending(s => s.DateTo).FirstOrDefaultAsync(ct);
            var unposted = await _db.MoneyTransactions.AsNoTracking()
                .Where(t => t.MoneyAccountId == m.Id && t.BookingDate <= asOf && t.Status != Domain.Banking.TransactionStatus.Zaksiegowana && t.Status != Domain.Banking.TransactionStatus.Pominieta)
                .Select(t => t.Amount).ToListAsync(ct);
            decimal? diff = statement is null || statement.DateTo != asOf ? null : statement.ClosingBalance - ledger;
            rows.Add(new(m.Id, m.Name, m.LedgerAccountNumber, ledger, statement?.ClosingBalance, statement?.DateTo, diff, unposted.Count, unposted.Sum()));
        }
        return rows;
    }
}
