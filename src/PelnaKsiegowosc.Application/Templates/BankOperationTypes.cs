namespace PelnaKsiegowosc.Application.Templates;

/// <summary>Rodzaj operacji bankowej/kasowej w prostym języku — wyznacza konto przeciwstawne (gdy operacja nie rozlicza faktury).</summary>
public sealed record BankOperationType(string Code, string Name, string AccountNumber, string Hint)
{
    public override string ToString() => Name;
}

public static class BankOperationTypes
{
    public static readonly IReadOnlyList<BankOperationType> All = new List<BankOperationType>
    {
        new("OPLATA", "Opłata lub prowizja bankowa", "751-03", "Koszt finansowy, bez VAT."),
        new("ODSETKI_PLUS", "Odsetki otrzymane (lokata, rachunek)", "750-01", "Przychód finansowy."),
        new("VAT", "Zapłata VAT do urzędu skarbowego", "221-04", "Rozlicza zobowiązanie z deklaracji/JPK."),
        new("CIT", "Zapłata zaliczki lub podatku CIT", "220-01", "Rozlicza zobowiązanie z tytułu CIT."),
        new("PIT", "Zapłata PIT (płatnik — wynagrodzenia, umowy)", "220-02", "Zaliczki pobrane od wynagrodzeń."),
        new("ZUS", "Zapłata składek ZUS", "220-03", "Składki ZUS."),
        new("WYNAGRODZENIE", "Wypłata wynagrodzenia", "230", "Rozlicza listę płac."),
        new("WLASNY", "Przelew między własnymi rachunkami / wpłata do kasy", "139", "Środki w drodze — druga strona przelewu trafi na to samo konto i wyzeruje je."),
        new("POZYCZKA_WSPOLNIK", "Pożyczka od wspólnika/akcjonariusza (wpływ) lub jej spłata", "245-03", "Rozrachunki z właścicielami; wymaga umowy pożyczki (PCC — sprawdź zwolnienie)."),
        new("KREDYT", "Kredyt bankowy — wpływ lub spłata kapitału", "134", "Odsetki od kredytu zaksięguj osobno jako koszt finansowy."),
        new("KARA", "Kara, mandat, grzywna", "761-03", "Nie jest kosztem podatkowym — po zaksięgowaniu dodaj korektę NKUP w sekcji CIT."),
        new("ODSETKI_BUDZ", "Odsetki od zaległości podatkowych lub ZUS", "751-04", "Nie są kosztem podatkowym."),
        new("INNE_PRZYCHODY", "Inny przychód (np. odszkodowanie, zwrot)", "760-03", "Pozostały przychód operacyjny — sprawdź, czy nie powinien być fakturą."),
        new("INNE_KOSZTY", "Inny koszt bez faktury", "761-03", "Pozostały koszt operacyjny — zachowaj dowód (np. potwierdzenie)."),
    };
}
