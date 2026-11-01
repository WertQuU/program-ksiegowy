using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public class PayrollPrivacyBackupTests
{
    [Fact]
    public async Task Minimized_assessment_survives_encrypted_backup_and_restore_without_medical_cause()
    {
        await using var books = await TestBooks.CreateAsync();
        var legacy = new { PreviousEmployerDays = 0, Cause = 1, EvidenceReference = "DOK-123", ConfirmedAtUtc = books.Clock.UtcNow, ConfirmedByUserId = books.User.UserId };
        var key = "place.chorobowe.weryfikacja." + Guid.NewGuid().ToString("N") + ".202603";
        await new SettingsService(books.Db, books.Clock).SetAsync(books.Company.Id, key, legacy);
        await books.Db.SaveChangesAsync();
        await new PayrollPrivacyService(books.Db, books.User, books.Clock).MinimizeLegacySettingsAsync();
        var backup = new BackupService(books.Clock, Path.Combine(books.Directory, "tmp"));
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = books.DbPath };
        var archiveKey = new BackupKey.Passphrase("Testowe-Haslo-Kopii-RODO-2026");
        var result = await backup.CreateAsync(books.Db, profile, new LocalFileStore(Path.Combine(books.Directory, "files")), null,
            new BackupOptions(archiveKey, Path.Combine(books.Directory, "backups")));
        var sqliteKey = SqliteEncryption.NewKey();
        var directory = Path.Combine(books.Directory, "restored");
        var restored = await backup.RestoreToSqliteAsync(result.FilePath, archiveKey, directory, sqliteKey, new byte[32]);
        Assert.True(restored.Ok, string.Join("; ", restored.Problems));
        var restoredProfile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = restored.DatabasePath };
        await using var db = DbContextFactory.Create(DatabaseEngine.Sqlite, restoredProfile.BuildConnectionString(null, encrypted: true), sqliteKey);
        var row = await db.Settings.SingleAsync(s => s.Key == key);
        Assert.DoesNotContain("Cause", row.Value);
        Assert.Equal(100m, JsonSerializer.Deserialize<SickWageAssessment>(row.Value)!.RatePercent);
    }
}
