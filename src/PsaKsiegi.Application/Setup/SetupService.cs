using Microsoft.EntityFrameworkCore;
using PsaKsiegi.Application.Abstractions;
using PsaKsiegi.Application.Audit;
using PsaKsiegi.Application.Ledger;
using PsaKsiegi.Application.Templates;
using PsaKsiegi.Data;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Companies;
using PsaKsiegi.Domain.Ledger;
using PsaKsiegi.Domain.Periods;
using PsaKsiegi.Domain.Security;

namespace PsaKsiegi.Application.Setup;

/// <summary>Kroki kreatora pierwszego uruchomienia: spółka, rok obrotowy, plan kont, dzienniki, polityka rachunkowości.</summary>
public sealed class SetupService
{
    public static readonly (string Code, string Name)[] DefaultJournals =
    {
        ("SPR", "Sprzedaż"), ("ZAK", "Zakupy"), ("BANK", "Bank"), ("KASA", "Kasa"), ("PK", "Polecenia księgowania"),
        ("AM", "Amortyzacja"), ("KAP", "Zdarzenia kapitałowe"), ("VAT", "Rozliczenia VAT"), ("BO", "Bilans otwarcia"), ("ZAM", "Zamknięcie roku")
    };

    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public SetupService(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    public Task<bool> IsCompanyConfiguredAsync(CancellationToken ct = default) => _db.Companies.AnyAsync(ct);

    public async Task<Company> CreateCompanyAsync(ICurrentUser actor, Company company, CancellationToken ct = default)
    {
        if (await _db.Companies.AnyAsync(ct))
            throw new DomainException("Spółka jest już skonfigurowana. Obsługa wielu jednostek nie jest jeszcze dostępna w interfejsie.");
        company.Nip = Domain.Validation.Identifiers.NormalizeNip(company.Nip);
        company.Validate().ThrowIfInvalid();
        company.CreatedAtUtc = _clock.UtcNow;
        _db.Companies.Add(company);
        foreach (var (code, name) in DefaultJournals)
            _db.Journals.Add(new JournalDefinition { CompanyId = company.Id, Code = code, Name = name });
        await new SettingsService(_db, _clock).SetAsync(company.Id, PostingSettings.SettingKey, new PostingSettings(), ct);
        await _audit.AddAsync(actor, "SPOLKA_UTWORZONA", "Company", company.Id.ToString(), $"{company.FullName}; NIP {company.Nip}", ct);
        await _db.SaveChangesAsync(ct);
        return company;
    }

    public async Task UpdateCompanyAsync(ICurrentUser actor, Company updated, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageCompany);
        var c = await _db.Companies.FirstAsync(x => x.Id == updated.Id, ct);
        updated.Nip = Domain.Validation.Identifiers.NormalizeNip(updated.Nip);
        updated.Validate().ThrowIfInvalid();
        if (c.Nip != updated.Nip && await _db.Documents.AnyAsync(d => d.CompanyId == c.Id && d.Status == Domain.Documents.DocumentStatus.Zaksiegowany, ct))
            throw new DomainException("Zmiana NIP po zaksięgowaniu dokumentów jest niedozwolona — NIP identyfikuje podatnika w księgach i JPK.");
        var before = $"{c.FullName}|{c.Nip}|{c.AddressLine1}|{c.AddressLine2}|{c.VatStatus}";
        _db.Entry(c).CurrentValues.SetValues(updated);
        c.CreatedAtUtc = c.CreatedAtUtc == default ? _clock.UtcNow : c.CreatedAtUtc;
        await _audit.AddAsync(actor, "SPOLKA_ZMIENIONA", "Company", c.Id.ToString(), $"{before} → {c.FullName}|{c.Nip}|{c.AddressLine1}|{c.AddressLine2}|{c.VatStatus}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<FiscalYear> CreateFiscalYearAsync(ICurrentUser actor, Guid companyId, DateOnly start, DateOnly end, bool isFirstYear, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageCompany);
        FiscalYear.ValidateRange(start, end, isFirstYear).ThrowIfInvalid();
        if (await _db.FiscalYears.AnyAsync(f => f.CompanyId == companyId && f.StartDate <= end && f.EndDate >= start, ct))
            throw new DomainException("Rok obrotowy nakłada się na istniejący rok.");
        var prev = await _db.FiscalYears.Where(f => f.CompanyId == companyId && f.EndDate < start).OrderByDescending(f => f.EndDate).FirstOrDefaultAsync(ct);
        if (prev is not null && prev.EndDate.AddDays(1) != start) throw new DomainException("Lata obrotowe muszą następować bezpośrednio po sobie.");
        var fy = new FiscalYear
        {
            CompanyId = companyId,
            StartDate = start,
            EndDate = end,
            IsFirstYear = isFirstYear,
            PreviousYearId = prev?.Id,
            Name = start.Month == 1 && end.Month == 12 && start.Year == end.Year ? start.Year.ToString() : $"{start.ToPl()}–{end.ToPl()}"
        };
        fy.GenerateMonthlyPeriods();
        _db.FiscalYears.Add(fy);
        await _audit.AddAsync(actor, "ROK_OBROTOWY_UTWORZONY", "FiscalYear", fy.Id.ToString(), $"{fy.Name}: {start.ToPl()}–{end.ToPl()}", ct);
        await _db.SaveChangesAsync(ct);
        return fy;
    }

    /// <summary>Tworzy szablon polityki rachunkowości (niezatwierdzony, z polami do uzupełnienia).</summary>
    public async Task<AccountingPolicy> CreatePolicyFromTemplateAsync(ICurrentUser actor, Guid companyId, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageCompany);
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId, ct);
        var fy = await _db.FiscalYears.Where(f => f.CompanyId == companyId).OrderBy(f => f.StartDate).FirstOrDefaultAsync(ct);
        var chart = fy is null ? null : await _db.Charts.FirstOrDefaultAsync(c => c.FiscalYearId == fy.Id, ct);
        var last = await _db.AccountingPolicies.Where(p => p.CompanyId == companyId).MaxAsync(p => (int?)p.Version, ct) ?? 0;
        var fyDesc = fy is null ? "[DO UZUPEŁNIENIA]" : (fy.StartDate.Month == 1 ? "rok kalendarzowy" : $"od {fy.StartDate:dd.MM} do {fy.EndDate:dd.MM}");
        var policy = new AccountingPolicy
        {
            CompanyId = companyId,
            Version = last + 1,
            EffectiveFrom = fy?.StartDate ?? _clock.Today,
            Content = PolicyTemplate.Build(company.FullName, fyDesc, chart?.Name ?? ChartTemplate.Name, typeof(SetupService).Assembly.GetName().Version?.ToString() ?? "?")
        };
        _db.AccountingPolicies.Add(policy);
        await _audit.AddAsync(actor, "POLITYKA_SZABLON", "AccountingPolicy", policy.Id.ToString(), $"wersja {policy.Version}", ct);
        await _db.SaveChangesAsync(ct);
        return policy;
    }

    /// <summary>Zapis zmienionej treści polityki (nowa wersja, jeżeli poprzednia była zatwierdzona).</summary>
    public async Task<AccountingPolicy> SavePolicyAsync(ICurrentUser actor, Guid policyId, string content, CorrectionMethod method, decimal lowValueThreshold,
        bool isSmall, bool isMicro, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageCompany);
        var p = await _db.AccountingPolicies.FirstAsync(x => x.Id == policyId, ct);
        if (p.ApprovedByManagement)
        {
            var copy = new AccountingPolicy
            {
                CompanyId = p.CompanyId,
                Version = p.Version + 1,
                EffectiveFrom = _clock.Today,
                Title = p.Title,
                ProfitAndLossVariant = p.ProfitAndLossVariant,
                CostsByNature = p.CostsByNature,
                CostsByFunction = p.CostsByFunction,
                MaterialityThreshold = p.MaterialityThreshold,
                UseTaxDepreciationRatesForBooks = p.UseTaxDepreciationRatesForBooks
            };
            _db.AccountingPolicies.Add(copy);
            p = copy;
        }
        p.Content = content;
        p.CorrectionMethod = method;
        p.LowValueAssetThreshold = lowValueThreshold;
        p.IsSmallEntity = isSmall;
        p.IsMicroEntity = isMicro;
        await _audit.AddAsync(actor, "POLITYKA_ZAPISANA", "AccountingPolicy", p.Id.ToString(), $"wersja {p.Version}", ct);
        await _db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>Zatwierdzenie polityki przez kierownika jednostki (rola Zarząd lub Administrator).</summary>
    public async Task ApprovePolicyAsync(ICurrentUser actor, Guid policyId, string approvedBy, CancellationToken ct = default)
    {
        if (!actor.Roles.Contains(Role.Zarzad) && !actor.Roles.Contains(Role.Administrator))
            throw new AccessDeniedException("Politykę rachunkowości zatwierdza kierownik jednostki (rola „Zarząd”).");
        var p = await _db.AccountingPolicies.FirstAsync(x => x.Id == policyId, ct);
        p.ApprovedByManagement = true;
        p.ApprovedBy = approvedBy;
        p.ApprovedOn = _clock.Today;
        var v = p.Validate();
        if (!v.IsValid)
        {
            p.ApprovedByManagement = false;
            p.ApprovedBy = null;
            p.ApprovedOn = null;
            v.ThrowIfInvalid();
        }
        await _audit.AddAsync(actor, "POLITYKA_ZATWIERDZONA", "AccountingPolicy", p.Id.ToString(), $"wersja {p.Version}; zatwierdził: {approvedBy}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Zatwierdzenie planu kont (po kontroli poprawności).</summary>
    public async Task ApproveChartAsync(ICurrentUser actor, Guid chartId, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageChartOfAccounts);
        var chart = await _db.Charts.FirstAsync(c => c.Id == chartId, ct);
        var v = await new ChartService(_db, actor, _clock).ValidateChartAsync(chartId, ct);
        v.ThrowIfInvalid();
        chart.IsApproved = true;
        chart.ApprovedOn = _clock.Today;
        await _audit.AddAsync(actor, "PLAN_KONT_ZATWIERDZONY", "ChartOfAccounts", chart.Id.ToString(), chart.Name, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Lista kontrolna gotowości systemu (ostatni krok kreatora; pulpit).</summary>
    public async Task<List<(string Item, bool Ok, string Details)>> ReadinessAsync(Guid companyId, CancellationToken ct = default)
    {
        var r = new List<(string, bool, string)>();
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        r.Add(("Dane spółki", company is not null && company.Validate().IsValid, company is null ? "Brak" : string.Join("; ", company.Validate().Errors.Concat(company.Validate().Warnings))));
        var fy = await _db.FiscalYears.Where(f => f.CompanyId == companyId && f.Status == FiscalYearStatus.Otwarty).OrderBy(f => f.StartDate).FirstOrDefaultAsync(ct);
        r.Add(("Otwarty rok obrotowy", fy is not null, fy?.Name ?? "Brak"));
        var chart = fy is null ? null : await _db.Charts.Where(c => c.FiscalYearId == fy.Id).OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct);
        r.Add(("Plan kont zatwierdzony", chart?.IsApproved == true, chart is null ? "Brak planu kont" : chart.IsApproved ? chart.Name : "Niezatwierdzony"));
        var policy = await _db.AccountingPolicies.Where(p => p.CompanyId == companyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
        var pv = policy?.Validate();
        r.Add(("Polityka rachunkowości zatwierdzona", pv?.IsValid == true, policy is null ? "Brak" : pv!.IsValid ? $"wersja {policy.Version}" : string.Join("; ", pv.Errors)));
        var persons = await _db.ResponsiblePersons.CountAsync(p => p.CompanyId == companyId, ct);
        r.Add(("Osoby odpowiedzialne", persons > 0, persons > 0 ? $"{persons}" : "Nie wskazano kierownika jednostki / osoby prowadzącej księgi"));
        var backups = await _db.BackupRecords.Where(b => b.Verified).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync(ct);
        r.Add(("Zweryfikowana kopia zapasowa", backups is not null, backups is null ? "Nie wykonano" : backups.CreatedAtUtc.ToPlDateTime()));
        return r;
    }
}
