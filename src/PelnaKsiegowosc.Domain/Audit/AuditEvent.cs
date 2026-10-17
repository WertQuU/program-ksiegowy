using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Audit;

/// <summary>
/// Zdarzenie dziennika audytowego. Wpisy tworzą łańcuch skrótów SHA-256 — usunięcie lub zmiana wpisu
/// jest wykrywana przez kontrolę integralności. Wpis nie zawiera sekretów ani haseł.
/// </summary>
public class AuditEvent : Entity
{
    public long Sequence { get; set; }
    public Guid? CompanyId { get; set; }
    public DateTime AtUtc { get; set; }
    public Guid? UserId { get; set; }
    public string UserLogin { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string MachineName { get; set; } = "";
    public string? PreviousHash { get; set; }
    public string Hash { get; set; } = "";

    public string ComputeHash()
    {
        var s = string.Join("|", Sequence.ToString(CultureInfo.InvariantCulture), Id, CompanyId, AtUtc.ToString("O", CultureInfo.InvariantCulture),
            UserId, UserLogin, Action, EntityType, EntityId, Details, MachineName, PreviousHash ?? "GENESIS");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}

/// <summary>Ustawienie aplikacji przechowywane w bazie (bez sekretów).</summary>
public class AppSetting : Entity
{
    public Guid? CompanyId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Rejestr wykonanych kopii zapasowych (metadane; same archiwa poza bazą).</summary>
public class BackupRecord : Entity
{
    public Guid ArchiveId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Encrypted { get; set; }
    public bool Verified { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public string? RemoteKey { get; set; }
}

/// <summary>Log operacji integracyjnych (KSeF, Object Storage) — bez tokenów i sekretów.</summary>
public class IntegrationLog : Entity
{
    public Guid? CompanyId { get; set; }
    public DateTime AtUtc { get; set; }
    public string System { get; set; } = "";
    public string Operation { get; set; } = "";
    public bool Success { get; set; }
    public string? Reference { get; set; }
    public string? Message { get; set; }
    public int? HttpStatus { get; set; }
    public Guid? DocumentId { get; set; }
}
