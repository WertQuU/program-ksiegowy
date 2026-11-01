using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;

namespace PelnaKsiegowosc.Tests.Presentation;

public class PayrollPrivacyScreenTests
{
    [Fact]
    public async Task Accounting_role_cannot_open_payroll_export_keys_restore_or_full_backup()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        await SelfTestScenario.RunWizardAsync(session, dialogs);
        await using (var db = session.OpenDb())
        {
            var access = new OperatorAccessService(db, session.Clock);
            var otherAdmin = await access.RegisterAsync(session.User, "db:other_admin", new[] { Role.Administrator });
            await access.SetAsync(new CurrentUser(otherAdmin.Id, otherAdmin.Login, session.CompanyId, otherAdmin.Roles), session.User.UserId, new[] { Role.Ksiegowy }, active: true);
        }
        await session.RefreshAsync();
        Assert.False(session.User.Can(Permission.ViewPayrollData));
        var payroll = new PayrollViewModel(session, dialogs);
        await payroll.LoadAsync();
        Assert.False(payroll.CanViewPayrollData);
        Assert.Empty(payroll.Employees.Rows);
        Assert.Contains(dialogs.Errors, e => e.Contains("Brak uprawnień"));
        dialogs.Errors.Clear();
        await new BackupsViewModel(session, dialogs).RestoreCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("Brak uprawnień"));
        dialogs.Errors.Clear();
        await new SettingsViewModel(session, dialogs).ShowDatabaseKeyCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("Brak uprawnień"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => session.BackupAsync("odmowa", upload: false));
    }
}
