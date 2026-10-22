using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Pomoc i diagnostyka: wersja, środowisko, kontrola integralności, licencja.</summary>
public sealed partial class HelpViewModel : SectionViewModel
{
    public HelpViewModel(AppSession s, IDialogService d) : base(s, d, "Pomoc i diagnostyka", "?") { }

    [ObservableProperty] private string _about = "";
    [ObservableProperty] private TableData _integrity = TableData.Empty;
    public string LicenseText => Terms.Text;

    public static string Version => typeof(HelpViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    protected override Task LoadCoreAsync()
    {
        About = $"Pełna Księgowość {Version}{Environment.NewLine}" +
                $"System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}{Environment.NewLine}" +
                $"Operator: {Session.User.Login}{Environment.NewLine}Baza: {Session.DatabaseDescription}{Environment.NewLine}Katalog danych: {Session.Paths.Root}{Environment.NewLine}{Environment.NewLine}" +
                "Program wspiera prowadzenie ksiąg rachunkowych, ale nie stanowi porady prawnej ani podatkowej. Wyniki (deklaracje, JPK, sprawozdania, faktury KSeF) " +
                "wymagają weryfikacji przez użytkownika, który ponosi odpowiedzialność za rozliczenia. Dokumentacja: pliki README.md i docs/ w repozytorium programu.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RunIntegrityCheckAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var r = await new IntegrityService(db, Session.Clock).FullCheckAsync(Session.CompanyId);
        var t = new TableData(new TableColumn("Obszar", Width: 1.5), new TableColumn("Wynik", Width: 0.5), new TableColumn("Szczegóły", Width: 4));
        foreach (var (area, ok, details) in r.Results) t.Add(null, area, ok ? "✔" : "✖", details);
        Integrity = t;
        StatusMessage = r.AllOk ? "Kontrola integralności: bez zastrzeżeń." : "Kontrola integralności wykazała nieprawidłowości — sprawdź szczegóły.";
    });

    [RelayCommand] private void OpenLogs() => Dialogs.OpenInSystem(Session.Paths.LogsDirectory);
}
