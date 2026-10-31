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

    public string Glossary => """
        Słowniczek — najważniejsze pojęcia w prostych słowach

        Pełna księgowość (księgi rachunkowe) — zapis każdej operacji firmy na kontach, zawsze podwójnie: raz po stronie Wn, raz po stronie Ma.
        Wn (Winien) i Ma — dwie strony konta. Suma Wn każdego zapisu równa się sumie Ma. Program pilnuje tego sam.
        Konto — „szufladka” na jeden rodzaj operacji, np. 130 Rachunek bankowy, 402 Usługi obce, 700 Sprzedaż.
        Plan kont — lista kont firmy. Tworzy go kreator; zatwierdza kierownik jednostki.
        Dokument (dowód księgowy) — faktura, rachunek, wyciąg, polecenie księgowania. Bez dowodu nie ma zapisu.
        Zaksięgowanie — trwałe wpisanie dokumentu do ksiąg. Zaksięgowanego zapisu nie można zmienić — poprawia się go stornem.
        Storno — zapis odwracający błędny zapis (czerwone: ujemny po tej samej stronie, czarne: odwrotny po przeciwnej).
        Bufor — miejsce na zapisy robocze, które nie są jeszcze w księgach.
        Rozrachunki — kto komu ile jest winien: należności (od klientów) i zobowiązania (wobec dostawców, urzędów).
        VAT należny — VAT z faktur sprzedaży (do oddania państwu). VAT naliczony — VAT z faktur zakupu (do odliczenia).
        Odliczenie VAT — pomniejszenie VAT do zapłaty o VAT z zakupów. Nie przysługuje m.in. dla hoteli i restauracji; dla samochodu osobowego zwykle 50%.
        JPK_V7M — plik dla urzędu skarbowego z ewidencją VAT i deklaracją za miesiąc, wysyłany do 25. dnia następnego miesiąca.
        KSeF — Krajowy System e-Faktur. Faktury dla firm wystawia się w KSeF; każda dostaje numer KSeF.
        Biała lista — wykaz podatników VAT z ich rachunkami. Płać powyżej 15 000 zł tylko na rachunek z wykazu.
        KUP — koszt uzyskania przychodu (zmniejsza podatek dochodowy). NKUP — wydatek, który nie zmniejsza podatku (np. reprezentacja, mandaty).
        CIT — podatek dochodowy od osób prawnych (spółek). Zaliczki płaci się co miesiąc lub kwartał do 20. dnia.
        Środek trwały — rzecz używana dłużej niż rok i droższa niż 10 000 zł netto. Kosztem staje się stopniowo, przez amortyzację.
        Amortyzacja — comiesięczne „zużycie” środka trwałego zapisywane w kosztach.
        Bilans — zestawienie, co firma ma (aktywa) i skąd to pochodzi (pasywa). Aktywa zawsze równają się pasywom.
        Rachunek zysków i strat (RZiS) — przychody minus koszty, czyli wynik firmy (zysk lub strata).
        Zamknięcie miesiąca — zablokowanie zmian w rozliczonym miesiącu. Zamknięcie roku — przeniesienie wyniku i otwarcie nowego roku (bilans otwarcia).
        Polityka rachunkowości — dokument z zasadami prowadzenia ksiąg w firmie, przyjmowany przez kierownika jednostki.
        Kierownik jednostki — zarząd (w spółce) lub właściciel; odpowiada za prowadzenie ksiąg.
        """;

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
        var r = await new IntegrityService(db, Session.Clock).FullCheckAsync(Session.CompanyId, attachments: Session.Attachments);
        var t = new TableData(new TableColumn("Obszar", Width: 1.5), new TableColumn("Wynik", Width: 0.5), new TableColumn("Szczegóły", Width: 4));
        foreach (var (area, ok, details) in r.Results) t.Add(null, area, ok ? "✔" : "✖", details);
        Integrity = t;
        StatusMessage = r.AllOk ? "Kontrola integralności: bez zastrzeżeń." : "Kontrola integralności wykazała nieprawidłowości — sprawdź szczegóły.";
    });

    [RelayCommand] private void OpenLogs() => Dialogs.OpenInSystem(Session.Paths.LogsDirectory);
}
