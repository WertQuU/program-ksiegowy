using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Companies;

namespace PelnaKsiegowosc.Application.Templates;

public sealed record AccountTemplate(
    string Number,
    string Name,
    AccountType Type,
    string? BalanceDebitLine = null,
    string? BalanceCreditLine = null,
    string? ProfitLossLine = null,
    bool Settlement = false,
    CitClassification Cit = CitClassification.NieDotyczy);

/// <summary>
/// Przykładowy plan kont jednostki prowadzącej pełną księgowość (układ zbliżony do wzorcowego planu kont, koszty w układzie rodzajowym,
/// RZiS w wariancie porównawczym). Jest to PUNKT WYJŚCIA — plan kont jest elementem polityki rachunkowości
/// (art. 10 ust. 1 pkt 3 UoR) i musi zostać dostosowany oraz zatwierdzony przez kierownika jednostki.
/// Mapowanie na pozycje sprawozdania wymaga weryfikacji przez księgowego.
/// </summary>
public static class ChartTemplate
{
    public const string Name = "Przykładowy plan kont (wariant porównawczy) v2";

    /// <summary>Szablon dostosowany do formy prawnej: nazwa kapitału podstawowego (801) i konta podatku dochodowego (870).</summary>
    public static IReadOnlyList<AccountTemplate> For(LegalForm form) => Accounts.Select(a => a.Number switch
    {
        "801" => a with { Name = LegalForms.ShareCapitalName(form) },
        "870" when !LegalForms.IsCitTaxpayer(form) => a with { Name = "Podatek dochodowy i inne obowiązkowe obciążenia wyniku (jednostka niebędąca podatnikiem CIT — zwykle nieużywane)" },
        _ => a
    }).ToList();

    /// <summary>
    /// Plan kont z kreatora: szablon dopasowany do formy prawnej i odpowiedzi użytkownika. Szczegółowe konta kosztów
    /// pozwalają automatycznie przypisywać wydatki według kategorii (paliwo, telefon, najem…).
    /// </summary>
    public static IReadOnlyList<AccountTemplate> Build(LegalForm form, ChartOptions o)
    {
        var list = For(form).ToList();
        if (o.DetailedCosts)
        {
            list.AddRange(new AccountTemplate[]
            {
                new("401-01", "Materiały biurowe i drobne wyposażenie", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.II", Cit: CitClassification.KosztPodatkowy),
                new("401-02", "Paliwo", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.II", Cit: CitClassification.KosztPodatkowy),
                new("401-03", "Energia, gaz, woda", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.II", Cit: CitClassification.KosztPodatkowy),
                new("401-99", "Pozostałe materiały", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.II", Cit: CitClassification.KosztPodatkowy),
                new("402-01", "Telefon i internet", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-02", "Najem i czynsz", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-03", "Usługi księgowe, prawne i doradcze", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-04", "Oprogramowanie i usługi IT", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-05", "Transport, kurier, poczta", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-06", "Naprawy, serwis, eksploatacja pojazdów", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-07", "Reklama i marketing", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("402-99", "Pozostałe usługi obce", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
                new("409-05", "Szkolenia i konferencje", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VII", Cit: CitClassification.KosztPodatkowy),
            });
            // konta syntetyczne z analityką nie mają już własnej pozycji RZiS (sumują analityki)
            for (var i = 0; i < list.Count; i++)
                if (list[i].Number is "401" or "402") list[i] = list[i] with { ProfitLossLine = null, Cit = CitClassification.NieDotyczy };
        }
        if (o.ForeignCurrencyUsd)
            list.Add(new("130-04", "Rachunek walutowy USD", AccountType.Aktywne, "B.III.1.c"));
        if (!o.ForeignCurrencyEur)
            list.RemoveAll(a => a.Number == "130-03");
        if (o.BankLoans)
        {
            list.Add(new("134", "Kredyty bankowe", AccountType.Pasywne, BalanceCreditLine: "P.B.III.3.a"));
            list.Add(new("751-05", "Odsetki od kredytów i pożyczek", AccountType.WynikoweKosztowe, ProfitLossLine: "R.H.I", Cit: CitClassification.DoAnalizy));
        }
        if (o.Leasing)
            list.Add(new("249", "Rozrachunki z tytułu leasingu", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.i", Settlement: true));
        if (!o.Employees)
            list.RemoveAll(a => a.Number is "230" or "404" or "405" or "220-02" or "220-03");
        if (!o.TradingGoods)
            list.RemoveAll(a => a.Number is "330" or "730" or "731");
        return list.OrderBy(a => a.Number, StringComparer.Ordinal).ToList();
    }

    public static readonly IReadOnlyList<AccountTemplate> Accounts = new List<AccountTemplate>
    {
        // Zespół 0 — aktywa trwałe
        new("010", "Środki trwałe", AccountType.Aktywne),
        new("010-01", "Grunty", AccountType.Aktywne, "A.II.1.a"),
        new("010-02", "Budynki, lokale i obiekty inżynierii", AccountType.Aktywne, "A.II.1.b"),
        new("010-03", "Urządzenia techniczne i maszyny", AccountType.Aktywne, "A.II.1.c"),
        new("010-04", "Środki transportu", AccountType.Aktywne, "A.II.1.d"),
        new("010-05", "Inne środki trwałe", AccountType.Aktywne, "A.II.1.e"),
        new("020", "Wartości niematerialne i prawne", AccountType.Aktywne, "A.I.3"),
        new("030", "Długoterminowe aktywa finansowe", AccountType.Aktywne, "A.IV.3.c"),
        new("070", "Odpisy umorzeniowe środków trwałych", AccountType.Pasywne),
        new("070-01", "Umorzenie — grunty", AccountType.Pasywne, "A.II.1.a"),
        new("070-02", "Umorzenie — budynki i lokale", AccountType.Pasywne, "A.II.1.b"),
        new("070-03", "Umorzenie — urządzenia techniczne i maszyny", AccountType.Pasywne, "A.II.1.c"),
        new("070-04", "Umorzenie — środki transportu", AccountType.Pasywne, "A.II.1.d"),
        new("070-05", "Umorzenie — inne środki trwałe", AccountType.Pasywne, "A.II.1.e"),
        new("075", "Odpisy umorzeniowe wartości niematerialnych i prawnych", AccountType.Pasywne, "A.I.3"),
        new("080", "Środki trwałe w budowie", AccountType.Aktywne, "A.II.2"),

        // Zespół 1 — środki pieniężne
        new("100", "Kasa krajowych środków pieniężnych", AccountType.Aktywne, "B.III.1.c"),
        new("130", "Rachunki bankowe", AccountType.Aktywne),
        new("130-01", "Rachunek bieżący PLN", AccountType.Aktywne, "B.III.1.c"),
        new("130-02", "Rachunek VAT (podzielona płatność)", AccountType.Aktywne, "B.III.1.c"),
        new("130-03", "Rachunek walutowy EUR", AccountType.Aktywne, "B.III.1.c"),
        new("139", "Środki pieniężne w drodze", AccountType.Aktywne, "B.III.1.c"),
        new("140", "Krótkoterminowe aktywa finansowe", AccountType.Aktywne, "B.III.1.b"),

        // Zespół 2 — rozrachunki
        new("200", "Rozrachunki z odbiorcami", AccountType.AktywnoPasywne, "B.II.3.a", "P.B.III.3.e", Settlement: true),
        new("210", "Rozrachunki z dostawcami", AccountType.AktywnoPasywne, "B.I.5", "P.B.III.3.d", Settlement: true),
        new("220", "Rozrachunki publicznoprawne", AccountType.AktywnoPasywne),
        new("220-01", "Rozrachunki z US — CIT", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("220-02", "Rozrachunki z US — PIT (płatnik)", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("220-03", "Rozrachunki z ZUS", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("220-04", "Rozrachunki z US — podatek u źródła / od dywidend", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("221", "Rozliczenie VAT", AccountType.AktywnoPasywne),
        new("221-01", "VAT należny", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("221-02", "VAT naliczony do odliczenia", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("221-03", "VAT naliczony do odliczenia w następnych okresach", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("221-04", "Rozrachunek z US — VAT (do zapłaty / do zwrotu)", AccountType.AktywnoPasywne, "B.II.3.b", "P.B.III.3.g"),
        new("230", "Rozrachunki z tytułu wynagrodzeń", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.h", Settlement: true),
        new("240", "Pozostałe rozrachunki", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.i", Settlement: true),
        new("245", "Rozrachunki z akcjonariuszami", AccountType.AktywnoPasywne),
        new("245-01", "Należne wkłady na kapitał akcyjny", AccountType.AktywnoPasywne, "C", "P.B.III.3.i", Settlement: true),
        new("245-02", "Rozrachunki z tytułu dywidend", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.i", Settlement: true),
        new("245-03", "Pożyczki od akcjonariuszy", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.a", Settlement: true),
        new("245-04", "Inne rozrachunki z akcjonariuszami", AccountType.AktywnoPasywne, "B.II.3.c", "P.B.III.3.i", Settlement: true),
        new("290", "Odpisy aktualizujące należności", AccountType.Pasywne, "B.II.3.a"),

        // Zespół 3 — materiały i towary
        new("300", "Rozliczenie zakupu", AccountType.AktywnoPasywne, "B.I.1", "P.B.III.3.i"),
        new("310", "Materiały", AccountType.Aktywne, "B.I.1"),
        new("330", "Towary", AccountType.Aktywne, "B.I.4"),

        // Zespół 4 — koszty według rodzajów
        new("400", "Amortyzacja", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.I", Cit: CitClassification.DoAnalizy),
        new("401", "Zużycie materiałów i energii", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.II", Cit: CitClassification.KosztPodatkowy),
        new("402", "Usługi obce", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.III", Cit: CitClassification.KosztPodatkowy),
        new("403", "Podatki i opłaty", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.IV", Cit: CitClassification.KosztPodatkowy),
        new("404", "Wynagrodzenia", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.V", Cit: CitClassification.DoAnalizy),
        new("405", "Ubezpieczenia społeczne i inne świadczenia", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VI", Cit: CitClassification.DoAnalizy),
        new("409", "Pozostałe koszty rodzajowe", AccountType.WynikoweKosztowe),
        new("409-01", "Podróże służbowe", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VII", Cit: CitClassification.KosztPodatkowy),
        new("409-02", "Reprezentacja (NKUP)", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VII", Cit: CitClassification.KosztNiepodatkowy),
        new("409-03", "Ubezpieczenia majątkowe", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VII", Cit: CitClassification.KosztPodatkowy),
        new("409-04", "Inne koszty rodzajowe", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VII", Cit: CitClassification.KosztPodatkowy),

        // Zespół 6 — rozliczenia międzyokresowe
        new("640", "Czynne rozliczenia międzyokresowe kosztów", AccountType.Aktywne, "B.IV"),

        // Zespół 7 — przychody i koszty ich osiągnięcia
        new("700", "Sprzedaż produktów i usług", AccountType.WynikowePrzychodowe),
        new("700-01", "Sprzedaż usług — kraj", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.A.I", Cit: CitClassification.PrzychodPodatkowy),
        new("700-02", "Sprzedaż usług — UE i eksport", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.A.I", Cit: CitClassification.PrzychodPodatkowy),
        new("730", "Sprzedaż towarów i materiałów", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.A.IV", Cit: CitClassification.PrzychodPodatkowy),
        new("731", "Wartość sprzedanych towarów i materiałów", AccountType.WynikoweKosztowe, ProfitLossLine: "R.B.VIII", Cit: CitClassification.KosztPodatkowy),
        new("750", "Przychody finansowe", AccountType.WynikowePrzychodowe),
        new("750-01", "Odsetki uzyskane", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.G.II", Cit: CitClassification.DoAnalizy),
        new("750-02", "Dodatnie różnice kursowe", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.G.V", Cit: CitClassification.DoAnalizy),
        new("750-03", "Inne przychody finansowe", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.G.V", Cit: CitClassification.PrzychodPodatkowy),
        new("751", "Koszty finansowe", AccountType.WynikoweKosztowe),
        new("751-01", "Odsetki zapłacone i naliczone", AccountType.WynikoweKosztowe, ProfitLossLine: "R.H.I", Cit: CitClassification.DoAnalizy),
        new("751-02", "Ujemne różnice kursowe", AccountType.WynikoweKosztowe, ProfitLossLine: "R.H.IV", Cit: CitClassification.DoAnalizy),
        new("751-03", "Prowizje i opłaty bankowe", AccountType.WynikoweKosztowe, ProfitLossLine: "R.H.IV", Cit: CitClassification.KosztPodatkowy),
        new("751-04", "Odsetki budżetowe (NKUP)", AccountType.WynikoweKosztowe, ProfitLossLine: "R.H.I", Cit: CitClassification.KosztNiepodatkowy),
        new("760", "Pozostałe przychody operacyjne", AccountType.WynikowePrzychodowe),
        new("760-01", "Zysk ze zbycia niefinansowych aktywów trwałych", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.D.I", Cit: CitClassification.DoAnalizy),
        new("760-02", "Dotacje", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.D.II", Cit: CitClassification.DoAnalizy),
        new("760-03", "Inne przychody operacyjne", AccountType.WynikowePrzychodowe, ProfitLossLine: "R.D.IV", Cit: CitClassification.PrzychodPodatkowy),
        new("761", "Pozostałe koszty operacyjne", AccountType.WynikoweKosztowe),
        new("761-01", "Strata ze zbycia / likwidacji niefinansowych aktywów trwałych", AccountType.WynikoweKosztowe, ProfitLossLine: "R.E.I", Cit: CitClassification.DoAnalizy),
        new("761-02", "Aktualizacja wartości aktywów niefinansowych", AccountType.WynikoweKosztowe, ProfitLossLine: "R.E.II", Cit: CitClassification.KosztNiepodatkowy),
        new("761-03", "Inne koszty operacyjne", AccountType.WynikoweKosztowe, ProfitLossLine: "R.E.III", Cit: CitClassification.DoAnalizy),

        // Zespół 8 — kapitały, fundusze, rezerwy, wynik
        new("801", "Kapitał (fundusz) podstawowy", AccountType.Pasywne, BalanceCreditLine: "P.A.I"),
        new("802", "Kapitał zapasowy", AccountType.Pasywne, BalanceCreditLine: "P.A.II"),
        new("803", "Kapitał z aktualizacji wyceny", AccountType.Pasywne, BalanceCreditLine: "P.A.III"),
        new("804", "Pozostałe kapitały rezerwowe", AccountType.Pasywne, BalanceCreditLine: "P.A.IV"),
        new("820", "Rozliczenie wyniku finansowego (wynik z lat ubiegłych)", AccountType.Pasywne, BalanceCreditLine: "P.A.V"),
        new("821", "Odpisy z zysku netto w ciągu roku (zaliczki na dywidendę)", AccountType.Pasywne, BalanceCreditLine: "P.A.VII"),
        new("840", "Rezerwy na zobowiązania", AccountType.Pasywne, BalanceCreditLine: "P.B.I.3"),
        new("841", "Rezerwa z tytułu odroczonego podatku dochodowego", AccountType.Pasywne, BalanceCreditLine: "P.B.I.1"),
        new("842", "Aktywa z tytułu odroczonego podatku dochodowego", AccountType.Aktywne, "A.V.1"),
        new("845", "Rozliczenia międzyokresowe przychodów", AccountType.Pasywne, BalanceCreditLine: "P.B.IV.2"),
        new("860", "Wynik finansowy", AccountType.WynikFinansowy, BalanceCreditLine: "P.A.VI"),
        new("870", "Podatek dochodowy od osób prawnych", AccountType.WynikoweKosztowe, ProfitLossLine: "R.J", Cit: CitClassification.KosztNiepodatkowy),

        // Zespół 9 — pozabilansowe
        new("990", "Konta pozabilansowe — zabezpieczenia, zobowiązania warunkowe", AccountType.Pozabilansowe),
    };
}

/// <summary>Odpowiedzi kreatora planu kont.</summary>
public sealed record ChartOptions(
    bool DetailedCosts = true,
    bool TradingGoods = true,
    bool Employees = true,
    bool ForeignCurrencyEur = true,
    bool ForeignCurrencyUsd = false,
    bool BankLoans = false,
    bool Leasing = false);
