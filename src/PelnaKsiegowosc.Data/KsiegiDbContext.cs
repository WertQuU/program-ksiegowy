using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Audit;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Domain.Warehouse;

namespace PelnaKsiegowosc.Data;

/// <summary>
/// Kontekst bazy danych — wspólny model dla SQLite i PostgreSQL. Zapytania wyłącznie przez LINQ/parametry
/// (brak sklejania SQL z danych użytkownika — ochrona przed SQL injection).
/// </summary>
public class KsiegiDbContext : DbContext
{
    public KsiegiDbContext(DbContextOptions<KsiegiDbContext> options) : base(options) { }

    /// <summary>
    /// Jednostka sesji operatora. Gdy ustawiona: zapytania o dane firmowe (<see cref="Domain.Common.CompanyEntity"/>) zwracają wyłącznie
    /// rekordy tej jednostki (globalny filtr), a zapis, zmiana lub usunięcie rekordu innej jednostki jest odrzucane. Bez ustawienia
    /// (operacje techniczne: migracja, kopia, odtworzenie, kontrola integralności całej bazy) — brak ograniczenia.
    /// </summary>
    public Guid? CompanyScope { get; set; }

    internal bool CompanyScopeSet => CompanyScope.HasValue;
    internal Guid CompanyScopeValue => CompanyScope ?? Guid.Empty;

    /// <summary>
    /// Zmiana NIP lub kraju kontrahenta (dowolną ścieżką) — zapis poprzednich danych identyfikacyjnych do historii z datą obowiązywania
    /// do dnia poprzedzającego <see cref="Counterparty.IdentityValidFrom"/> (albo dzień zmiany).
    /// </summary>
    private void TrackCounterpartyIdentity()
    {
        foreach (var e in ChangeTracker.Entries<Counterparty>().Where(x => x.State == EntityState.Modified).ToList())
        {
            var oldNip = e.OriginalValues.GetValue<string?>(nameof(Counterparty.Nip));
            var oldCountry = e.OriginalValues.GetValue<string>(nameof(Counterparty.CountryCode));
            static string N(string? v) => (v ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
            if (N(oldNip) == N(e.Entity.Nip) && N(oldCountry) == N(e.Entity.CountryCode)) continue;
            // uzupełnienie brakującego NIP — brak poprzedniego zestawu; usunięcie NIP (np. anonimizacja RODO) — bez kopiowania danych do historii
            if (string.IsNullOrWhiteSpace(oldNip) || string.IsNullOrWhiteSpace(e.Entity.Nip)) continue;
            // data obowiązywania nowych danych — tylko podana przy tej zmianie (pozostawiona data z poprzedniej zmiany nie jest brana)
            var given = e.Entity.IdentityValidFrom;
            if (given == e.OriginalValues.GetValue<DateOnly?>(nameof(Counterparty.IdentityValidFrom))) given = null;
            var from = given ?? DateOnly.FromDateTime(DateTime.Now);
            e.Entity.IdentityValidFrom = from;
            CounterpartyIdentityChanges.Add(new CounterpartyIdentityChange
            {
                CompanyId = e.Entity.CompanyId, CounterpartyId = e.Entity.Id, CountryCode = oldCountry, Nip = oldNip,
                ValidUntil = from.AddDays(-1), ChangedAtUtc = DateTime.UtcNow
            });
        }
    }

    private void EnforceCompanyScope()
    {
        TrackCounterpartyIdentity();
        if (CompanyScope is not { } scope) return;
        foreach (var e in ChangeTracker.Entries<Domain.Common.CompanyEntity>())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && e.Entity.CompanyId != scope)
                throw new InvalidOperationException("Odmowa: operacja dotyczy danych innej jednostki niż jednostka sesji operatora.");
    }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<ResponsiblePerson> ResponsiblePersons => Set<ResponsiblePerson>();
    public DbSet<AccountingPolicy> AccountingPolicies => Set<AccountingPolicy>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<AccountingPeriod> Periods => Set<AccountingPeriod>();
    public DbSet<PeriodStatusChange> PeriodStatusChanges => Set<PeriodStatusChange>();
    public DbSet<ChartOfAccounts> Charts => Set<ChartOfAccounts>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalDefinition> Journals => Set<JournalDefinition>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<CounterpartyIdentityChange> CounterpartyIdentityChanges => Set<CounterpartyIdentityChange>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentLine> DocumentLines => Set<DocumentLine>();
    public DbSet<DocumentVatLine> DocumentVatLines => Set<DocumentVatLine>();
    public DbSet<DocumentPostingLine> DocumentPostingLines => Set<DocumentPostingLine>();
    public DbSet<DocumentHistory> DocumentHistory => Set<DocumentHistory>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<OpenItem> OpenItems => Set<OpenItem>();
    public DbSet<SettlementAllocation> SettlementAllocations => Set<SettlementAllocation>();
    public DbSet<MoneyAccount> MoneyAccounts => Set<MoneyAccount>();
    public DbSet<MoneyStatement> MoneyStatements => Set<MoneyStatement>();
    public DbSet<MoneyTransaction> MoneyTransactions => Set<MoneyTransaction>();
    public DbSet<VatRegisterEntry> VatRegister => Set<VatRegisterEntry>();
    public DbSet<CitAdjustment> CitAdjustments => Set<CitAdjustment>();
    public DbSet<CitAdvance> CitAdvances => Set<CitAdvance>();
    public DbSet<CitLiabilityAdjustment> CitLiabilityAdjustments => Set<CitLiabilityAdjustment>();
    public DbSet<TaxLoss> TaxLosses => Set<TaxLoss>();
    public DbSet<TaxLossDeduction> TaxLossDeductions => Set<TaxLossDeduction>();
    public DbSet<DebtFinancingCarryforward> DebtFinancingCarryforwards => Set<DebtFinancingCarryforward>();
    public DbSet<DebtFinancingCarryforwardUse> DebtFinancingCarryforwardUses => Set<DebtFinancingCarryforwardUse>();
    public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<ReceivableAllowance> ReceivableAllowances => Set<ReceivableAllowance>();
    public DbSet<Provision> Provisions => Set<Provision>();
    public DbSet<Absence> Absences => Set<Absence>();
    public DbSet<DocumentInstallment> DocumentInstallments => Set<DocumentInstallment>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<PayrollList> PayrollLists => Set<PayrollList>();
    public DbSet<PayrollItem> PayrollItems => Set<PayrollItem>();
    public DbSet<FixedAssetOperation> FixedAssetOperations => Set<FixedAssetOperation>();
    public DbSet<DepreciationCharge> DepreciationCharges => Set<DepreciationCharge>();
    public DbSet<DepreciationRun> DepreciationRuns => Set<DepreciationRun>();
    public DbSet<Shareholder> Shareholders => Set<Shareholder>();
    public DbSet<CapitalEvent> CapitalEvents => Set<CapitalEvent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();
    public DbSet<IntegrationLog> IntegrationLogs => Set<IntegrationLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(18, 2);
        b.Properties<decimal?>().HavePrecision(18, 2);
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(64);
        b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        b.Properties<string>().HaveMaxLength(1000);
    }

    /// <summary>
    /// Zapis księgowany bez bufora (storno, korekta, księgowanie z modułów) jest wstawiany jako bufor razem z pozycjami,
    /// a status „zaksięgowany” ustawiany w tej samej transakcji po wstawieniu pozycji — wyzwalacze bazy bezwzględnie
    /// zabraniają dopisywania pozycji do zapisu już zaksięgowanego.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceCompanyScope();
        var direct = DirectlyPosted();
        if (direct.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await using var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        int n;
        foreach (var e in direct) e.Status = EntryStatus.Bufor;
        try { n = await base.SaveChangesAsync(true, cancellationToken); }
        finally { foreach (var e in direct) e.Status = EntryStatus.Zaksiegowany; }
        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (own is not null) await own.CommitAsync(cancellationToken);
        return n;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceCompanyScope();
        var direct = DirectlyPosted();
        if (direct.Count == 0) return base.SaveChanges(acceptAllChangesOnSuccess);
        using var own = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        int n;
        foreach (var e in direct) e.Status = EntryStatus.Bufor;
        try { n = base.SaveChanges(true); }
        finally { foreach (var e in direct) e.Status = EntryStatus.Zaksiegowany; }
        base.SaveChanges(acceptAllChangesOnSuccess);
        own?.Commit();
        return n;
    }

    private List<JournalEntry> DirectlyPosted() => ChangeTracker.Entries<JournalEntry>()
        .Where(e => e.State == EntityState.Added && e.Entity.Status == EntryStatus.Zaksiegowany).Select(e => e.Entity).ToList();

    protected override void OnModelCreating(ModelBuilder m)
    {
        // Identyfikatory GUID (v7) nadaje aplikacja — nowe obiekty podrzędne dodane do kolekcji są wstawiane (INSERT),
        // a identyfikatory pozostają identyczne po migracji między silnikami baz danych.
        foreach (var et in m.Model.GetEntityTypes().Where(t => typeof(Domain.Common.Entity).IsAssignableFrom(t.ClrType)))
            m.Entity(et.ClrType).Property(nameof(Domain.Common.Entity.Id)).ValueGeneratedNever();

        // izolacja jednostek: rekordy firmowe widoczne tylko w sesji ich jednostki (gdy CompanyScope ustawione)
        foreach (var et in m.Model.GetEntityTypes().Where(t => t.BaseType is null && typeof(Domain.Common.CompanyEntity).IsAssignableFrom(t.ClrType)).ToList())
        {
            var p = System.Linq.Expressions.Expression.Parameter(et.ClrType, "e");
            var ctx = System.Linq.Expressions.Expression.Constant(this);
            // porównanie z kolumną typu Guid (parametr otrzymuje mapowanie typu kolumny — ten sam zapis identyfikatora w obu silnikach)
            var body = System.Linq.Expressions.Expression.OrElse(
                System.Linq.Expressions.Expression.Not(System.Linq.Expressions.Expression.Property(ctx, nameof(CompanyScopeSet))),
                System.Linq.Expressions.Expression.Equal(
                    System.Linq.Expressions.Expression.Property(p, nameof(Domain.Common.CompanyEntity.CompanyId)),
                    System.Linq.Expressions.Expression.Property(ctx, nameof(CompanyScopeValue))));
            m.Entity(et.ClrType).HasQueryFilter(System.Linq.Expressions.Expression.Lambda(body, p));
        }

        m.Entity<Company>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(512);
            e.Property(x => x.InvoiceFooter).HasMaxLength(4000);
        });
        m.Entity<AccountingPolicy>(e =>
        {
            e.Property(x => x.Content).HasMaxLength(int.MaxValue);
            e.HasIndex(x => new { x.CompanyId, x.Version }).IsUnique();
        });

        m.Entity<FiscalYear>(e =>
        {
            e.HasMany(x => x.Periods).WithOne().HasForeignKey(p => p.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CompanyId, x.StartDate }).IsUnique();
        });
        m.Entity<AccountingPeriod>(e => e.HasIndex(x => new { x.FiscalYearId, x.Number }).IsUnique());
        m.Entity<PeriodStatusChange>(e => e.Property(x => x.ChecklistJson).HasMaxLength(int.MaxValue));

        m.Entity<ChartOfAccounts>(e => e.HasIndex(x => x.FiscalYearId));
        m.Entity<Account>(e =>
        {
            e.HasIndex(x => new { x.ChartId, x.Number }).IsUnique();
            e.Property(x => x.Number).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256);
        });
        m.Entity<JournalDefinition>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());

        m.Entity<JournalEntry>(e =>
        {
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FiscalYearId, x.SequenceNumber }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.JournalNumber }).IsUnique();
            e.HasIndex(x => x.PeriodId);
            e.HasIndex(x => x.SourceDocumentId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.ExchangeRate).HasPrecision(18, 6);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PreviousHash).HasMaxLength(64);
        });
        m.Entity<JournalLine>(e =>
        {
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => x.CounterpartyId);
            e.HasIndex(x => x.OpenItemId);
        });

        m.Entity<Counterparty>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => x.Nip);
            e.Property(x => x.Name).HasMaxLength(512);
        });

        m.Entity<Document>(e =>
        {
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.VatLines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.PostingLines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CompanyId, x.Kind, x.Number }).IsUnique();
            e.HasIndex(x => x.CounterpartyId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.IssueDate);
            e.HasIndex(x => x.KsefNumber);
            e.Property(x => x.ExchangeRate).HasPrecision(18, 6);
            e.Property(x => x.Description).HasMaxLength(4000);
        });
        m.Entity<DocumentLine>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitNetPrice).HasPrecision(18, 4);
            e.Property(x => x.Name).HasMaxLength(512);
        });
        m.Entity<DocumentVatLine>(e => e.Property(x => x.DeductionPercent).HasPrecision(7, 4));
        m.Entity<DocumentHistory>(e => { e.HasIndex(x => x.DocumentId); e.Property(x => x.Details).HasMaxLength(int.MaxValue); });
        m.Entity<Attachment>(e => { e.HasIndex(x => x.DocumentId); e.HasIndex(x => x.Sha256); });
        m.Entity<NumberSequence>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Key }).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        m.Entity<OpenItem>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.CounterpartyId });
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.Status);
        });
        m.Entity<SettlementAllocation>(e => { e.HasIndex(x => x.OpenItemId); e.HasIndex(x => x.MoneyTransactionId); });

        m.Entity<MoneyStatement>(e => e.HasIndex(x => x.MoneyAccountId));
        m.Entity<MoneyTransaction>(e =>
        {
            e.HasIndex(x => new { x.MoneyAccountId, x.DedupHash }).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Title).HasMaxLength(2000);
        });

        m.Entity<VatRegisterEntry>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Register, x.Period });
            e.HasIndex(x => x.DocumentId);
        });
        m.Entity<CitAdvance>(e => { e.HasIndex(x => new { x.FiscalYearId, x.PeriodNumber }).IsUnique(); e.Property(x => x.RatePercent).HasPrecision(7, 4); e.Property(x => x.WarningsText).HasMaxLength(int.MaxValue); });
        m.Entity<CitAdjustment>(e =>
        {
            e.HasIndex(x => x.FiscalYearId);
            e.HasOne<OpenItem>().WithMany().HasForeignKey(x => x.OpenItemId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<CitLiabilityAdjustment>(e => { e.HasIndex(x => x.FiscalYearId); e.HasIndex(x => x.OpenItemId); });
        m.Entity<TaxLoss>(e => e.HasIndex(x => x.CompanyId));
        m.Entity<TaxLossDeduction>(e => { e.HasIndex(x => x.TaxLossId); e.HasIndex(x => new { x.TaxLossId, x.FiscalYearId }).IsUnique(); });
        m.Entity<DebtFinancingCarryforward>(e => { e.HasIndex(x => x.CompanyId); e.HasIndex(x => new { x.CompanyId, x.YearEnd, x.CapitalGains }).IsUnique(); });
        m.Entity<DebtFinancingCarryforwardUse>(e => { e.HasIndex(x => x.CarryforwardId); e.HasIndex(x => new { x.CarryforwardId, x.FiscalYearId }).IsUnique(); });

        m.Entity<ReceivableAllowance>(e => e.HasIndex(x => new { x.CompanyId, x.OpenItemId }));
        m.Entity<Provision>(e => e.HasIndex(x => new { x.CompanyId, x.Date }));
        m.Entity<Absence>(e => e.HasIndex(x => new { x.CompanyId, x.EmployeeId, x.From }));
        m.Entity<DocumentInstallment>(e => e.HasIndex(x => new { x.DocumentId, x.No }).IsUnique());

        m.Entity<StockItem>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.Property(x => x.Quantity).HasPrecision(18, 4);
        });

        m.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.StockItemId, x.Date });
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.QuantityAfter).HasPrecision(18, 4);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
        });

        m.Entity<Employee>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.LastName, x.FirstName });
            e.Property(x => x.WorkTimeFraction).HasPrecision(7, 4);
        });

        m.Entity<PayrollList>(e =>
        {
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.PayrollListId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        });

        m.Entity<FixedAsset>(e =>
        {
            e.HasMany(x => x.Operations).WithOne().HasForeignKey(o => o.FixedAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CompanyId, x.InventoryNumber }).IsUnique();
            e.Property(x => x.BookRatePercent).HasPrecision(7, 4);
            e.Property(x => x.TaxRatePercent).HasPrecision(7, 4);
            e.Property(x => x.BookDegressiveFactor).HasPrecision(7, 4);
            e.Property(x => x.TaxDegressiveFactor).HasPrecision(7, 4);
            e.Property(x => x.TaxNonDeductiblePercent).HasPrecision(7, 4);
        });
        m.Entity<DepreciationCharge>(e => e.HasIndex(x => new { x.FixedAssetId, x.Year, x.Month }).IsUnique());
        m.Entity<DepreciationRun>(e => e.HasIndex(x => new { x.CompanyId, x.Year, x.Month }));

        m.Entity<CapitalEvent>(e => e.HasIndex(x => x.CompanyId));

        m.Entity<User>(e =>
        {
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.MfaSecretProtected).HasMaxLength(2000);
        });
        m.Entity<RecoveryCode>(e => e.HasIndex(x => x.UserId));
        m.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => x.AtUtc);
            e.Property(x => x.Details).HasMaxLength(int.MaxValue);
        });
        // wartość ustawienia jako token współbieżności: zapis zmian dokonanych równolegle przez innego operatora (np. rejestr
        // zarachowań wpłat CIT) kończy się błędem zamiast cichego nadpisania (utraty zapisu)
        m.Entity<AppSetting>(e => { e.HasIndex(x => new { x.CompanyId, x.Key }).IsUnique(); e.Property(x => x.Value).HasMaxLength(int.MaxValue).IsConcurrencyToken(); });
        m.Entity<CounterpartyIdentityChange>(e => e.HasIndex(x => new { x.CounterpartyId, x.ValidUntil }));
        m.Entity<BackupRecord>(e => e.HasIndex(x => x.CreatedAtUtc));
        m.Entity<IntegrationLog>(e => { e.HasIndex(x => x.AtUtc); e.Property(x => x.Message).HasMaxLength(int.MaxValue); });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value.ToUniversalTime(), DateTimeKind.Utc)) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
