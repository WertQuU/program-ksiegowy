using System.Net;
using System.Text;
using PelnaKsiegowosc.Domain.Legal;
using PelnaKsiegowosc.Infrastructure.Legal;

namespace PelnaKsiegowosc.Tests.Integrations;

public class LegalMonitorTests
{
    private sealed class Fake : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/eli/acts/DU/2004/535" => """
                    {"ELI":"DU/2004/535","title":"Ustawa o podatku od towarów i usług","changeDate":"2026-11-20T10:00:00",
                     "references":{"Akty zmieniające":[{"id":"DU/2025/1203","date":"2025-09-01"},{"id":"DU/2026/1500","date":"2026-11-15"}],
                                   "Akty wykonawcze":[{"id":"DU/2026/999","date":"2026-11-01"}]}}
                    """,
                "/eli/acts/DU/2026/1500" => """{"title":"Ustawa z dnia 7 listopada 2026 r. o zmianie ustawy o podatku od towarów i usług","announcementDate":"2026-11-15","entryIntoForce":"2027-01-01"}""",
                "/eli/acts/DU/1994/591" => """{"title":"Ustawa o rachunkowości","changeDate":"2026-12-01T08:00:00","references":{}}""",
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Reports_only_amendments_published_after_verification_date()
    {
        using var http = new HttpClient(new Fake()) { BaseAddress = LegalMonitor.BaseUrl };
        var acts = new[] { new MonitoredAct("DU/2004/535", "Ustawa o VAT", "VAT"), new MonitoredAct("DU/1994/591", "Ustawa o rachunkowości", "Rachunkowość"),
            new MonitoredAct("DU/1992/86", "Ustawa o CIT", "CIT") };
        var changes = await new LegalMonitor(http).CheckAsync(new DateOnly(2026, 10, 30), acts);
        Assert.Equal(2, changes.Count);
        var vat = changes.Single(c => c.ActEliId == "DU/2004/535");
        Assert.Equal("akt zmieniający", vat.Kind);
        Assert.Equal(new DateOnly(2027, 1, 1), vat.InForce);
        Assert.Contains("o zmianie ustawy o podatku od towarów", vat.Title);
        Assert.Equal("https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU20260001500", vat.Url);
        Assert.Contains(changes, c => c.ActEliId == "DU/1994/591" && c.Kind == "zmiana danych aktu");
    }

    [Fact]
    public void Registry_is_consistent()
    {
        Assert.Equal(LegalRules.All.Count, LegalRules.All.Select(r => r.Code).Distinct().Count());
        Assert.All(LegalRules.All, r => Assert.False(string.IsNullOrWhiteSpace(r.LegalBasis)));
        Assert.All(LegalRules.MonitoredActs, a => Assert.Matches(@"^DU/\d{4}/\d+$", a.EliId));
    }

    [Fact]
    public async Task Changes_are_stored_once_and_can_be_marked_seen()
    {
        using var root = new PelnaKsiegowosc.Tests.Presentation.TempRoot();
        var (session, dialogs) = PelnaKsiegowosc.Presentation.Services.SelfTestScenario.CreateSession(root.Path);
        await PelnaKsiegowosc.Presentation.Services.SelfTestScenario.RunWizardAsync(session, dialogs);
        var fake = new Fake();
        Assert.True(await PelnaKsiegowosc.Presentation.ViewModels.Sections.RegulationsCheck.RunAsync(session, fake) >= 1);
        Assert.Equal(0, await PelnaKsiegowosc.Presentation.ViewModels.Sections.RegulationsCheck.RunAsync(session, fake));
        await using (var db = session.OpenDb())
        {
            var issues = await new PelnaKsiegowosc.Application.Assistant.BookkeepingAssistant(db, session.User, session.Clock).CheckAsync(session.CurrentYear.Id, session.Clock.UtcNow);
            Assert.Contains(issues, i => i.Code == "PRZEPISY_ZMIANY");
            await new PelnaKsiegowosc.Application.Legal.LegalUpdates(db, session.Clock).MarkSeenAsync(null);
        }
        await using (var db = session.OpenDb())
        {
            var issues = await new PelnaKsiegowosc.Application.Assistant.BookkeepingAssistant(db, session.User, session.Clock).CheckAsync(session.CurrentYear.Id, session.Clock.UtcNow);
            Assert.DoesNotContain(issues, i => i.Code == "PRZEPISY_ZMIANY");
        }
    }
}
