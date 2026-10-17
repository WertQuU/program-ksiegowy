using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.Security;

public enum Role
{
    Administrator,
    GlownyKsiegowy,
    Ksiegowy,
    Fakturzysta,
    /// <summary>Kierownik jednostki / członek zarządu — zatwierdzenia, wgląd.</summary>
    Zarzad,
    /// <summary>Audytor / biegły rewident — tylko odczyt.</summary>
    Audytor
}

public enum Permission
{
    ViewData,
    ManageCompany,
    ManageUsers,
    ManageChartOfAccounts,
    EditDocuments,
    ApproveDocuments,
    PostEntries,
    CorrectEntries,
    ClosePeriods,
    ReopenPeriods,
    CloseYear,
    ManageVat,
    ManageCit,
    ManageFixedAssets,
    ManageCapital,
    ConfirmCapitalDocuments,
    ManageBank,
    IssueInvoices,
    RunReports,
    ExportData,
    ManageBackups,
    RestoreBackups,
    ManageIntegrations,
    ManageSettings,
    ViewAuditLog
}

public static class RolePermissions
{
    private static readonly Dictionary<Role, Permission[]> Map = new()
    {
        [Role.Administrator] = Enum.GetValues<Permission>(),
        [Role.GlownyKsiegowy] = Enum.GetValues<Permission>().Where(p => p is not Permission.ManageUsers).ToArray(),
        [Role.Ksiegowy] = new[]
        {
            Permission.ViewData, Permission.EditDocuments, Permission.ApproveDocuments, Permission.PostEntries, Permission.CorrectEntries,
            Permission.ManageVat, Permission.ManageFixedAssets, Permission.ManageBank, Permission.IssueInvoices,
            Permission.RunReports, Permission.ExportData, Permission.ManageBackups, Permission.ManageCit
        },
        [Role.Fakturzysta] = new[] { Permission.ViewData, Permission.EditDocuments, Permission.IssueInvoices, Permission.RunReports },
        [Role.Zarzad] = new[]
        {
            Permission.ViewData, Permission.ApproveDocuments, Permission.RunReports, Permission.ExportData,
            Permission.ConfirmCapitalDocuments, Permission.ManageCapital, Permission.ViewAuditLog, Permission.ManageCompany
        },
        [Role.Audytor] = new[] { Permission.ViewData, Permission.RunReports, Permission.ExportData, Permission.ViewAuditLog }
    };

    public static bool Has(IEnumerable<Role> roles, Permission p) => roles.Any(r => Map[r].Contains(p));
    public static IReadOnlyList<Permission> For(Role r) => Map[r];

    public static string Name(Role r) => r switch
    {
        Role.Administrator => "Administrator",
        Role.GlownyKsiegowy => "Główny księgowy",
        Role.Ksiegowy => "Księgowy",
        Role.Fakturzysta => "Fakturzysta",
        Role.Zarzad => "Zarząd / kierownik jednostki",
        Role.Audytor => "Audytor (tylko odczyt)",
        _ => r.ToString()
    };
}

/// <summary>Lokalne konto użytkownika. Hasło: PBKDF2-HMAC-SHA256 z solą (nigdy jawnie).</summary>
public class User : Entity
{
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; }
    public string PasswordAlgorithm { get; set; } = "PBKDF2-SHA256";
    public bool MustChangePassword { get; set; }
    public DateTime? PasswordChangedAtUtc { get; set; }

    public bool MfaEnabled { get; set; }
    /// <summary>Sekret TOTP zaszyfrowany kluczem bazy (AES-GCM); nigdy jawnie.</summary>
    public string? MfaSecretProtected { get; set; }

    public string RolesCsv { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public IReadOnlyList<Role> Roles =>
        RolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<Role>).ToList();

    public void SetRoles(IEnumerable<Role> roles) => RolesCsv = string.Join(',', roles.Distinct());

    public bool Can(Permission p) => IsActive && RolePermissions.Has(Roles, p);
}

/// <summary>Jednorazowy kod odzyskiwania (przechowywany jako skrót).</summary>
public class RecoveryCode : Entity
{
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime? UsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
