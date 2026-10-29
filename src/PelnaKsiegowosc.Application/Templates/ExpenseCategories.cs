using PelnaKsiegowosc.Domain.Accounts;

namespace PelnaKsiegowosc.Application.Templates;

/// <summary>
/// Kategoria wydatku w prostym języku. Wyznacza konto kosztów (pierwsze istniejące z listy kandydatów), procent odliczenia
/// VAT, część kosztu niestanowiącą KUP i podpowiedź dla użytkownika. Reguły podatkowe odpowiadają typowym przypadkom —
/// w nietypowej sytuacji użytkownik zmienia procenty ręcznie. Podstawy prawne: ustawa o VAT (art. 86a, 88), ustawa o CIT (art. 16).
/// </summary>
public sealed record ExpenseCategory(
    string Code,
    string Name,
    string Group,
    IReadOnlyList<string> Accounts,
    decimal VatDeductionPercent,
    decimal NkupPercent,
    string Hint,
    bool FixedAsset = false,
    string DefaultVatRate = "23")
{
    public override string ToString() => $"{Group}: {Name}";
}

public static class ExpenseCategories
{
    public static readonly IReadOnlyList<ExpenseCategory> All = new List<ExpenseCategory>
    {
        new("BIURO", "Artykuły biurowe, drobne materiały", "Materiały", new[] { "401-01", "401" }, 100, 0, "Pełne odliczenie VAT i koszt podatkowy."),
        new("WYPOSAZENIE", "Drobne wyposażenie (do 10 000 zł netto za sztukę)", "Materiały", new[] { "401-01", "401" }, 100, 0,
            "Sprzęt o wartości do 10 000 zł można zaliczyć od razu w koszty. Droższy sprzęt — wybierz „Środek trwały”."),
        new("ENERGIA", "Prąd, gaz, woda, ogrzewanie", "Materiały", new[] { "401-03", "401" }, 100, 0, "Media lokalu firmowego — pełne odliczenie VAT."),
        new("PALIWO_OSOBOWY", "Paliwo — samochód osobowy (użytek mieszany)", "Samochód", new[] { "401-02", "401" }, 50, 25,
            "Samochód osobowy używany także prywatnie: odliczenie 50% VAT (art. 86a ustawy o VAT), 75% wydatku w kosztach podatkowych (art. 16 ust. 1 pkt 51 ustawy o CIT)."),
        new("PALIWO_FIRMOWY", "Paliwo — samochód wyłącznie firmowy (VAT-26) lub ciężarowy", "Samochód", new[] { "401-02", "401" }, 100, 0,
            "Pełne odliczenie tylko dla pojazdu zgłoszonego na VAT-26 z ewidencją przebiegu albo pojazdu ciężarowego/specjalnego."),
        new("AUTO_OSOBOWY", "Samochód osobowy — serwis, opony, myjnia, parking, ubezpieczenie OC/AC", "Samochód", new[] { "402-06", "402" }, 50, 25,
            "Użytek mieszany: 50% VAT, 75% kosztu w KUP. Ubezpieczenie jest zwolnione z VAT — odliczenie nie wystąpi.", DefaultVatRate: "23"),
        new("AUTO_LEASING", "Leasing samochodu osobowego (rata, użytek mieszany)", "Samochód", new[] { "402-06", "402" }, 50, 25,
            "50% VAT i 75% KUP; dodatkowo limit wartości samochodu (100 000/150 000 zł, art. 16 ust. 1 pkt 49a ustawy o CIT) — przy droższym aucie zwiększ procent NKUP po konsultacji z księgowym."),
        new("TELEFON", "Telefon, internet", "Usługi", new[] { "402-01", "402" }, 100, 0, "Pełne odliczenie VAT i koszt podatkowy."),
        new("CZYNSZ", "Czynsz, najem lokalu lub biura", "Usługi", new[] { "402-02", "402" }, 100, 0, "Najem lokalu użytkowego — 23% VAT; najem mieszkalny jest zwolniony."),
        new("KSIEGOWOSC", "Księgowość, prawnik, doradztwo", "Usługi", new[] { "402-03", "402" }, 100, 0, "Usługi profesjonalne — pełne odliczenie."),
        new("IT", "Oprogramowanie, hosting, domeny, abonamenty IT", "Usługi", new[] { "402-04", "402" }, 100, 0,
            "Usługa od firmy z zagranicy (np. abonament z UE/USA): ustaw rodzaj transakcji „Import usług 28b” — VAT nalicza się samodzielnie."),
        new("KURIER", "Kurier, transport, poczta", "Usługi", new[] { "402-05", "402" }, 100, 0, "Usługi pocztowe operatora publicznego bywają zwolnione z VAT (stawka „zw”)."),
        new("NAPRAWY", "Naprawy, remonty, serwis (poza samochodem osobowym)", "Usługi", new[] { "402-06", "402" }, 100, 0,
            "Remont przywracający stan pierwotny to koszt; ulepszenie środka trwałego (rozbudowa, modernizacja) zwiększa jego wartość."),
        new("REKLAMA", "Reklama, marketing, strona internetowa", "Usługi", new[] { "402-07", "402" }, 100, 0, "Reklama to koszt podatkowy (inaczej niż reprezentacja)."),
        new("USLUGI_INNE", "Inne usługi od firm", "Usługi", new[] { "402-99", "402" }, 100, 0, "Sprawdź, czy usługa nie jest reprezentacją lub usługą noclegową/gastronomiczną."),
        new("SZKOLENIA", "Szkolenia, kursy, konferencje", "Inne koszty", new[] { "409-05", "409-04" }, 100, 0, "Szkolenia zwykle są zwolnione z VAT (stawka „zw”)."),
        new("UBEZPIECZENIE", "Ubezpieczenia majątkowe (poza samochodem)", "Inne koszty", new[] { "409-03" }, 0, 0, "Ubezpieczenia są zwolnione z VAT.", DefaultVatRate: "zw"),
        new("PODROZ_BILET", "Podróż służbowa — bilety, przejazdy", "Podróże", new[] { "409-01" }, 100, 0, "VAT z biletów (pociąg, samolot krajowy) podlega odliczeniu."),
        new("PODROZ_HOTEL", "Podróż służbowa — hotel, nocleg", "Podróże", new[] { "409-01" }, 0, 0,
            "Usługi noclegowe nie dają prawa do odliczenia VAT (art. 88 ust. 1 pkt 4 lit. b ustawy o VAT); cała kwota brutto jest kosztem.", DefaultVatRate: "8"),
        new("PODROZ_POSILEK", "Podróż służbowa — posiłek w restauracji", "Podróże", new[] { "409-01" }, 0, 0,
            "Usługi gastronomiczne nie dają prawa do odliczenia VAT (art. 88 ust. 1 pkt 4 lit. b ustawy o VAT).", DefaultVatRate: "8"),
        new("REPREZENTACJA", "Reprezentacja — poczęstunek, restauracja z kontrahentem, prezenty", "Reprezentacja", new[] { "409-02" }, 0, 100,
            "Reprezentacja nie jest kosztem podatkowym (art. 16 ust. 1 pkt 28 ustawy o CIT); VAT z gastronomii nie podlega odliczeniu. Zwykła kawa i woda w biurze to nie reprezentacja — wybierz „Artykuły biurowe”."),
        new("PODATKI", "Podatki i opłaty (od nieruchomości, skarbowe, sądowe, KRS)", "Podatki i opłaty", new[] { "403" }, 0, 0, "Bez VAT. Podatek dochodowy (CIT) nie jest kosztem — księguje się go na koncie 870.", DefaultVatRate: "zw"),
        new("BANK", "Opłaty i prowizje bankowe", "Finanse", new[] { "751-03" }, 0, 0, "Usługi finansowe są zwolnione z VAT.", DefaultVatRate: "zw"),
        new("KARY", "Kary, mandaty, odsetki od zaległości podatkowych", "Finanse", new[] { "751-04", "761-03" }, 0, 100,
            "Kary, grzywny i odsetki od zaległości budżetowych nie są kosztem podatkowym (art. 16 ust. 1 pkt 21, 22 ustawy o CIT).", DefaultVatRate: "zw"),
        new("TOWARY", "Towary do odsprzedaży", "Towary i majątek", new[] { "330", "731" }, 100, 0, "Towary trafiają do magazynu (330); koszt powstaje przy sprzedaży."),
        new("SRODEK_TRWALY", "Środek trwały (sprzęt, maszyna, auto powyżej 10 000 zł)", "Towary i majątek", new[] { "080" }, 100, 0,
            "Zakup trafia na konto 080. Następnie przyjmij środek w sekcji „Środki trwałe” (OT) z kontem przeciwstawnym 080 — koszt powstanie przez amortyzację.", FixedAsset: true),
    };

    public static ExpenseCategory? Find(string? code) => code is null ? null : All.FirstOrDefault(c => c.Code == code);

    /// <summary>Pierwsze istniejące konto księgowalne (bez kont podrzędnych) spośród kandydatów kategorii.</summary>
    public static string? ResolveAccount(ExpenseCategory c, IReadOnlyCollection<Account> accounts)
    {
        var parents = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        foreach (var n in c.Accounts)
        {
            var a = accounts.FirstOrDefault(x => x.Number == n && x.IsActive);
            if (a is not null && !parents.Contains(a.Id)) return a.Number;
        }
        return null;
    }
}
