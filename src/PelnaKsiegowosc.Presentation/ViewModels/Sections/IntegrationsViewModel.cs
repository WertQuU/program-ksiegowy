using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Ksef;
using PelnaKsiegowosc.Infrastructure.Storage;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Integracje: OVHcloud Object Storage (S3) na kopie i archiwa, KSeF; dziennik operacji integracyjnych.</summary>
public sealed partial class IntegrationsViewModel : SectionViewModel
{
    public IntegrationsViewModel(AppSession s, IDialogService d) : base(s, d, "Integracje", "🔌") { }

    [ObservableProperty] private bool _s3Enabled;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _bucket = "";
    [ObservableProperty] private string _prefix = "";
    [ObservableProperty] private bool _pathStyle = true;
    [ObservableProperty] private bool _sse;
    [ObservableProperty] private string _retainRemote = "";
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _s3Info = "";
    public List<string> EndpointHints { get; } = S3BackupTarget.OvhEndpointHints.Select(h => h.Endpoint).ToList();

    [ObservableProperty] private bool _ksefEnabled;
    [ObservableProperty] private Choice<KsefEnvironment> _ksefEnvironment = Choices.Of<KsefEnvironment>()[0];
    [ObservableProperty] private string _ksefToken = "";
    [ObservableProperty] private string _ksefInfo = "";
    public List<Choice<KsefEnvironment>> KsefEnvironments { get; } = Choices.Of<KsefEnvironment>();

    [ObservableProperty] private TableData _log = TableData.Empty;

    protected override async Task LoadCoreAsync()
    {
        var o = Session.Config.ObjectStorage;
        S3Enabled = o.Enabled; Endpoint = o.Endpoint ?? ""; Region = o.Region ?? ""; Bucket = o.Bucket ?? ""; Prefix = o.Prefix; PathStyle = o.ForcePathStyle;
        Sse = o.ServerSideEncryption; RetainRemote = o.RetainRemoteCount.ToString();
        AccessKey = SecretKey = "";
        S3Info = (Session.Secrets.Get(o.AccessKeySecretName) is null ? "Klucze dostępowe nie są zapisane." : $"Klucze zapisane w: {Session.Secrets.ProviderName}.") +
                 (o.LastSuccessUtc is { } t ? $" Ostatnia udana wysyłka: {t.ToPlDateTime()}." : "") +
                 " Endpoint regionu sprawdź w aktualnej dokumentacji OVHcloud.";
        var k = Session.Config.Ksef;
        KsefEnabled = k.Enabled;
        KsefEnvironment = KsefEnvironments.First(e => e.Value == k.Environment);
        KsefToken = "";
        UpdateKsefInfo();
        await using var db = Session.OpenDb();
        var t2 = new TableData(new TableColumn("Czas", Width: 1.1), new TableColumn("System", Width: 0.7), new TableColumn("Operacja", Width: 1.3), new TableColumn("Wynik", Width: 0.5),
            new TableColumn("HTTP", ColumnKind.Integer, 0.4), new TableColumn("Referencja", Width: 1.3), new TableColumn("Komunikat", Width: 3));
        foreach (var l in await db.IntegrationLogs.AsNoTracking().OrderByDescending(l => l.AtUtc).Take(1000).ToListAsync())
            t2.Add(l.Id, l.AtUtc.ToPlDateTime(), l.System, l.Operation, l.Success ? "OK" : "błąd", l.HttpStatus, l.Reference, l.Message);
        Log = t2;
    }

    partial void OnKsefEnvironmentChanged(Choice<KsefEnvironment> value) => UpdateKsefInfo();

    private void UpdateKsefInfo()
    {
        var name = $"PelnaKsiegowosc/ksef/{KsefEnvironment.Value}/token";
        KsefInfo = $"Adres API: {KsefClient.BaseUrl(KsefEnvironment.Value)}. " + (Session.Secrets.Get(name) is null ? "Token nie jest zapisany." : "Token zapisany w magazynie sekretów.") +
                   (KsefEnvironment.Value == Infrastructure.Configuration.KsefEnvironment.Produkcja ? " UWAGA: środowisko produkcyjne — faktury mają skutki prawne." : "");
    }

    private void ApplyS3()
    {
        var o = Session.Config.ObjectStorage;
        o.Enabled = S3Enabled; o.Endpoint = Parse.Trim(Endpoint); o.Region = Parse.Trim(Region); o.Bucket = Parse.Trim(Bucket);
        o.Prefix = string.IsNullOrWhiteSpace(Prefix) ? "" : Prefix.Trim().TrimStart('/').TrimEnd('/') + "/";
        o.ForcePathStyle = PathStyle; o.ServerSideEncryption = Sse; o.RetainRemoteCount = Math.Max(0, Parse.Int(RetainRemote, "Liczba kopii zdalnych"));
        if (!string.IsNullOrWhiteSpace(AccessKey)) Session.Secrets.Set(o.AccessKeySecretName, AccessKey.Trim());
        if (!string.IsNullOrWhiteSpace(SecretKey)) Session.Secrets.Set(o.SecretKeySecretName, SecretKey.Trim());
    }

    [RelayCommand]
    private Task SaveS3Async() => ChangeAsync(async () =>
    {
        ApplyS3();
        Session.SaveConfig();
        await Session.AuditAsync("OBJECT_STORAGE_USTAWIENIA", "Settings", null, $"{Session.Config.ObjectStorage.Endpoint}/{Session.Config.ObjectStorage.Bucket}; włączone: {Session.Config.ObjectStorage.Enabled}");
    }, "Zapisano ustawienia Object Storage.");

    [RelayCommand]
    private Task TestS3Async() => RunAsync(async () =>
    {
        ApplyS3();
        Session.SaveConfig();
        using var target = S3BackupTarget.FromSecrets(Session.Config.ObjectStorage, Session.Secrets);
        var r = await target.TestAsync();
        await Dialogs.InfoAsync("Test Object Storage", $"{r.Message}{Environment.NewLine}Połączenie: {(r.Connected ? "tak" : "nie")}, zapis: {(r.WriteOk ? "tak" : "nie")}, odczyt: {(r.ReadOk ? "tak" : "nie")}, usuwanie: {(r.DeleteOk ? "tak" : "nie")}");
    });

    [RelayCommand]
    private Task SaveKsefAsync() => ChangeAsync(async () =>
    {
        var k = Session.Config.Ksef;
        k.Enabled = KsefEnabled;
        k.Environment = KsefEnvironment.Value;
        if (!string.IsNullOrWhiteSpace(KsefToken)) Session.Secrets.Set(k.TokenSecretName, KsefToken.Trim());
        Session.SaveConfig();
        await Session.AuditAsync("KSEF_USTAWIENIA", "Settings", null, $"środowisko {k.Environment}; włączone: {k.Enabled}");
        KsefToken = "";
    }, "Zapisano ustawienia KSeF.");

    [RelayCommand]
    private Task TestKsefAsync() => RunAsync(async () =>
    {
        var k = Session.Config.Ksef;
        var token = (string.IsNullOrWhiteSpace(KsefToken) ? null : KsefToken.Trim()) ?? Session.Secrets.Get($"PelnaKsiegowosc/ksef/{KsefEnvironment.Value}/token")
                    ?? throw new DomainException("Wpisz lub zapisz token KSeF.");
        using var http = new HttpClient { BaseAddress = KsefClient.BaseUrl(KsefEnvironment.Value), Timeout = TimeSpan.FromSeconds(60) };
        await new KsefClient(http).AuthenticateWithTokenAsync(Session.Company!.Nip, token);
        await Dialogs.InfoAsync("Test KSeF", $"Uwierzytelnienie tokenem w środowisku {KsefEnvironment.Label} zakończone powodzeniem.");
    });
}
