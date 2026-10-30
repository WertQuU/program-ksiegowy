using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Legal;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Legal;
using PelnaKsiegowosc.Infrastructure.Legal;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Zmiana w przepisach na liście (z otwarciem źródła i oznaczeniem jako przejrzana).</summary>
public sealed class LegalChangeItem
{
    public LegalChangeItem(StoredLegalChange c, RegulationsViewModel owner)
    {
        Change = c;
        OpenCommand = new RelayCommand(() => owner.Dialogs.OpenInSystem(c.Url));
        SeenCommand = new AsyncRelayCommand(() => owner.MarkSeenAsync(c.ChangeId));
    }

    public StoredLegalChange Change { get; }
    public string Header => $"{Change.ActName} — {Change.Kind}";
    public string Title => Change.Title;
    public string Dates => $"opublikowano: {Change.Published.ToPl()}" + (Change.InForce is { } f ? $", wchodzi w życie: {f.ToPl()}" : "");
    public bool IsNew => !Change.Seen;
    public string Color => Change.Seen ? "#78909C" : "#E08A00";
    public IRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand SeenCommand { get; }
}

/// <summary>Przepisy: reguły stosowane przez program (z podstawą prawną) i monitor zmian w ustawach.</summary>
public sealed partial class RegulationsViewModel : SectionViewModel
{
    public RegulationsViewModel(AppSession s, IDialogService d) : base(s, d, "Przepisy", "§") { }

    [ObservableProperty] private TableData _rules = TableData.Empty;
    [ObservableProperty] private List<LegalChangeItem> _changes = new();
    [ObservableProperty] private string _info = "";

    protected override async Task LoadCoreAsync()
    {
        var t = new TableData(new TableColumn("Obszar", Width: 0.8), new TableColumn("Reguła", Width: 2.4), new TableColumn("Wartość stosowana w programie", Width: 3.2),
            new TableColumn("Podstawa prawna", Width: 2.6), new TableColumn("Od", ColumnKind.Date, 0.8), new TableColumn("Do", ColumnKind.Date, 0.8), new TableColumn("Stan", Width: 1.1));
        foreach (var r in LegalRules.All)
            t.Add(r.Code, r.Area, r.Name, r.Value + (r.Note is null ? "" : $" — {r.Note}"), r.LegalBasis, r.ValidFrom, r.ValidTo,
                r.Status == RuleStatus.Zweryfikowana ? "zweryfikowana" : "do potwierdzenia");
        Rules = t;
        await using var db = Session.OpenDb();
        var store = new LegalUpdates(db, Session.Clock);
        var list = await store.ListAsync();
        Changes = list.Select(c => new LegalChangeItem(c, this)).ToList();
        var last = await store.LastCheckAsync();
        Info = $"Rejestr przepisów {LegalRules.Version}, zweryfikowany {LegalRules.VerifiedOn.ToPl()}. " +
               $"Monitorowane akty: {string.Join(", ", LegalRules.MonitoredActs.Select(a => a.Name))}. " +
               (last is { } l ? $"Ostatnie sprawdzenie zmian: {l.ToPlDateTime()}." : "Zmian jeszcze nie sprawdzano.") +
               $" Nowych zmian: {list.Count(c => !c.Seen)}.";
    }

    [RelayCommand]
    private Task CheckNowAsync() => ChangeAsync(async () =>
    {
        var added = await RegulationsCheck.RunAsync(Session);
        await Dialogs.InfoAsync("Przepisy", added == 0 ? "Nie znaleziono nowych zmian w monitorowanych ustawach." : $"Znaleziono {added} nowych zmian — przejrzyj je na liście.");
    }, "Sprawdzono zmiany w przepisach.");

    [RelayCommand]
    private Task MarkAllSeenAsync() => MarkSeenAsync(null);

    public Task MarkSeenAsync(string? id) => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new LegalUpdates(db, Session.Clock).MarkSeenAsync(id);
    }, "Oznaczono jako przejrzane.");
}

/// <summary>Sprawdzenie zmian w przepisach (ELI API Sejmu) i zapisanie wyników.</summary>
public static class RegulationsCheck
{
    public static async Task<int> RunAsync(AppSession session, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = LegalMonitor.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
        var found = await new LegalMonitor(http).CheckAsync(LegalRules.VerifiedOn, LegalRules.MonitoredActs, ct);
        await using var db = session.OpenDb();
        return await new LegalUpdates(db, session.Clock).MergeAsync(found.Select(f =>
            new StoredLegalChange(f.ActName, f.Area, f.ChangeEliId, f.Kind, f.Title, f.Published, f.InForce, f.Url, false)), ct);
    }

    /// <summary>Sprawdzenie w tle raz na dobę (błędy sieci ignorowane — zapis w logu).</summary>
    public static async Task RunDailyAsync(AppSession session)
    {
        try
        {
            await using (var db = session.OpenDb())
                if (await new LegalUpdates(db, session.Clock).LastCheckAsync() is { } last && session.Clock.UtcNow - last < TimeSpan.FromHours(24)) return;
            await RunAsync(session);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            ErrorLog.Write(ex);
        }
    }
}
