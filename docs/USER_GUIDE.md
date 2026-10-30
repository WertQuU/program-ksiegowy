# Instrukcja obsługi

## Pierwsze uruchomienie

1. **Warunki licencji** — przeczytaj i zaakceptuj (w tym § 6: odpowiedzialność za rozliczenia ponosi użytkownik).
2. **Baza** — plik na tym komputerze (domyślnie) albo serwer PostgreSQL (adres, baza, użytkownik, hasło, TLS).
   Jeżeli baza zawiera już księgi (np. drugie stanowisko), kreator tylko podłączy stanowisko.
3. **Jednostka** — forma prawna, pełna nazwa, NIP, REGON, KRS, adres, kod urzędu skarbowego, status VAT,
   zaliczki CIT. Forma prawna dopasowuje plan kont, politykę i dostępność CIT.
4. **Rok obrotowy** — zwykle 01.01–31.12; zaznacz „pierwszy rok działalności”, jeżeli dotyczy.
5. **Plan kont** — powstaje z szablonu; możesz go zatwierdzić od razu albo po zmianach (sekcja „Plan kont”).
6. **Polityka rachunkowości** — uzupełnij pola `[DO UZUPEŁNIENIA…]`; zatwierdza kierownik jednostki.
   Bez zatwierdzonej polityki i planu kont księgowanie jest zablokowane.
7. **Kierownik jednostki** — imię, nazwisko, funkcja.
8. **Kopie zapasowe** — wygeneruj klucz i **zapisz klucz odzyskiwania poza komputerem**.
9. **Utworzenie ksiąg** — program tworzy księgi, rachunek bankowy i kasę oraz wykonuje pierwszą kopię.
   Lista gotowości pokazuje, co jeszcze uzupełnić (także na Pulpicie).

## Pulpit — od czego zacząć

Pulpit pokazuje stan ksiąg, pieniądze, należności i zobowiązania, przyciski najczęstszych czynności oraz listę
**„Do zrobienia”** przygotowaną przez asystenta kontroli ksiąg. Pracuj od góry: najpierw pozycje **BŁĄD**, potem
**UWAGA**. Każda pozycja mówi, co jest nie tak i co zrobić, a przycisk **„Przejdź”** otwiera właściwe miejsce.
Obok widać **terminy** (JPK i VAT, zaliczki CIT, zaksięgowanie miesiąca, sprawozdanie) z informacją, czy zostały wykonane.

## Codzienna praca

**Wydatek (faktura zakupu):** Zakupy → „+ Dodaj wydatek” → wybierz dostawcę, wpisz numer faktury i daty → w pozycji
wybierz **rodzaj wydatku** (np. „Paliwo — samochód osobowy”, „Telefon, internet”, „Hotel”) i wpisz cenę netto →
**„Zapisz i zaksięguj”**. Program dobierze konto, odliczenie VAT i część kosztu, której nie uznaje urząd skarbowy.
Pod tabelą pojawia się wyjaśnienie zasad dla wybranego rodzaju wydatku.

**Faktura sprzedaży:** Sprzedaż → „+ Wystaw fakturę” → klient, pozycje → **„Zapisz i zaksięguj”** → „Wyślij do KSeF”.

**Nowy kontrahent:** Kontrahenci → „Nowy kontrahent” → wpisz NIP → **„Pobierz dane z białej listy”** → Zapisz.
Przed przelewem powyżej 15 000 zł kliknij **„Sprawdź rachunek na białej liście”**.

**Waluta obca:** w fakturze wpisz walutę (np. EUR) i kliknij **„Pobierz kurs NBP”**.

**Bank:** po imporcie wyciągu zaznacz operację: wpływ od klienta lub zapłatę faktury dopasuj do propozycji;
opłatę, podatek, ZUS albo przelew własny wybierz z listy **„Co to za operacja?”** → Zaksięguj.


| Zadanie | Gdzie |
|---|---|
| Wystawienie faktury | Sprzedaż → Nowa faktura → Zapisz → Zatwierdź → Zaksięguj; KSeF: „Wyślij do KSeF” |
| Korekta faktury | zaznacz zaksięgowaną fakturę → Wystaw korektę → wpisz **różnice** pozycji i przyczynę |
| Faktura zakupu | Zakupy → Zarejestruj fakturę → Potwierdź odliczenie VAT (procent, okres) → Zatwierdź → Zaksięguj |
| Import z KSeF | Zakupy → zakres dat → „Pobierz faktury z KSeF” (powstają dokumenty robocze) |
| Wyciąg bankowy | Bank i kasa → wybierz rachunek → Import MT940/CSV → dopasuj do rozrachunków lub konta → Zaksięguj |
| Kasa | Bank i kasa → kasa → operacja kasowa (+ KP, − KW) → dopasuj → Zaksięguj |
| Polecenie księgowania | Dokumenty → „Nowy dokument z dekretacją” albo Księgowość → Nowy zapis |
| Błąd w zaksięgowanym zapisie | Księgowość → zaznacz zapis → Storno → wprowadź zapis poprawny |
| Kontrahenci, rozrachunki, kompensata | Kontrahenci → zaznacz kontrahenta → zakładka „Rozrachunki” |
| Środki trwałe | Środki trwałe → Przyjęcie (OT) → co miesiąc „Nalicz i zaksięguj amortyzację” |
| Zdarzenia kapitałowe | Kapitał i właściciele → dodaj właściciela → nowe zdarzenie → potwierdź dokumenty → zaksięguj |
| Raporty | Raporty → wybierz raport i daty → Generuj → eksport PDF/XLSX/CSV |

## Koniec miesiąca — kreator „Rozlicz miesiąc”

Pulpit → **„Rozlicz miesiąc”** (albo sekcja Zamknięcie → zakładka kreatora). Wpisz miesiąc i wykonuj kroki po kolei —
każdy ma swój przycisk i pokazuje, czy jest zrobiony:

1. Dokumenty — „Zaksięguj wszystkie poprawne” (dokumenty z błędami zostaną wymienione do poprawy).
2. Bank i kasa — zaimportuj wyciąg za cały miesiąc i rozlicz operacje.
3. Amortyzacja — jeżeli firma ma środki trwałe.
4. VAT — kontrola rejestrów i zaksięgowanie rozliczenia (program pokaże kwotę do zapłaty).
5. JPK_V7M — zapis pliku; wyślij go narzędziem MF do 25. dnia (program nie wysyła JPK).
6. Zaliczka CIT — wyliczenie i zatwierdzenie; zapłać do 20. dnia.
7. Kopia zapasowa.
8. Zamknięcie miesiąca.

## Płace

1. **Płace → Osoby i umowy**: wpisz osobę, rodzaj umowy (o pracę, zlecenie, dzieło, powołanie) i kwotę brutto;
   zaznacz PIT-2, ulgę dla młodych lub status studenta, jeśli dotyczy. Ustaw stopę wypadkową z zawiadomienia ZUS.
2. **Listy płac**: „Utwórz listę płac” za miesiąc z datą wypłaty — program wylicza ZUS, zdrowotną, zaliczkę PIT
   i netto. Kwotę osoby (niepełny miesiąc, premia) zmienisz polem „Zmień brutto”. Następnie „Zaksięguj”.
3. Wypłaty netto oraz przelewy do ZUS (do 15.) i US (do 20.) księgujesz w sekcji Bank (rodzaje operacji
   „Wynagrodzenie”, „ZUS”, „PIT”).
4. **ZUS i PIT**: zestawienia do DRA/RCA (co miesiąc) oraz PIT-4R, PIT-8AR i PIT-11 (raz w roku).

## Magazyn

Dla firm handlowych i produkcyjnych: dodaj pozycję (towar, materiał, produkt) i zapisuj dokumenty PZ, WZ, RW, PW.
Wydania program wycenia ceną przeciętną ważoną i księguje (np. Wn 731 / Ma 330). Przyjęcie zakupu zaksięgowanego
fakturą na koncie 330 zapisz bez konta przeciwstawnego (tylko ilości). Na koniec roku: „Arkusz spisu z natury”,
różnice — „Niedobór” / „Nadwyżka”.

## Deklaracje

Sekcja **Deklaracje** zbiera wszystkie deklaracje z terminami. Wybierz pozycję i kliknij „Generuj / przejdź”:
CIT-8 z CIT-8/O, VAT-UE, IFT-2R/CIT-10Z, PIT-36/PIT-36L (wspólnicy lub właściciel — jeden wiersz na osobę),
uchwały roczne, inwentaryzacja (metody, potwierdzenia sald, protokół kasy). Pozostałe (JPK_V7M/V7K, JPK_CIT,
ZUS, PIT-11) program otwiera we właściwej sekcji. Przycisk „Plik XML…” zapisuje deklarację w formacie
e-Deklaracji (walidowaną oficjalnym schematem) — VAT-UE(5) za miesiąc (ponowne wygenerowanie po zmianach tworzy korektę VAT-UEK z pozycjami BYŁO/JEST) oraz PIT-11(29) (osobny plik dla każdej osoby;
w kartotece pracownika uzupełnij PESEL i kod urzędu skarbowego). Pozostałe deklaracje program wylicza i drukuje;
wysyłasz je przez e-Deklaracje / e-Urząd Skarbowy / PUE ZUS.

## Koniec roku

1. Inwentaryzacja (Deklaracje → Inwentaryzacja, potwierdzenia sald, protokół kasy; Magazyn → spis z natury).
   **Wycena na dzień bilansowy** (Zamknięcie → „Odpisy i rezerwy”): propozycje odpisów aktualizujących należności
   według dni zwłoki (progi z polityki rachunkowości, domyślnie 50% po 180 dniach, 100% po roku), rozwiązanie odpisów
   spłaconych należności, spisanie należności nieściągalnych, rezerwy na zobowiązania (urlopowa — wyliczana
   z płac, emerytalna, na koszty okresu, gwarancyjne, sądowe). Odpisy i rezerwy są kosztem niepodatkowym; ich
   wykorzystanie lub rozwiązanie program uwzględnia w uzgodnieniu CIT automatycznie.
2. Zamknij wszystkie miesiące, wykonaj kopię, „Podgląd zamknięcia roku”, następnie „Zamknij rok obrotowy”
   i wpisz frazę potwierdzenia. Program przeksięguje wynik, zablokuje rok i utworzy rok następny z bilansem otwarcia.
3. **Sprawozdanie** (Raporty): bilans, RZiS (porównawczy lub kalkulacyjny — klucz podziału kosztów ustawisz
   w raporcie kalkulacyjnym), zestawienie zmian w kapitale, rachunek przepływów, informacja dodatkowa (opisy wpisz
   w „Tekstach informacji dodatkowej”). „Sprawozdanie finansowe XML” tworzy plik e-Sprawozdania z dołączoną
   informacją dodatkową; podpisuje się go w aplikacji MF. Strukturę dobiera polityka rachunkowości: pełna SFJINZ
   (zał. nr 1), jednostka mała SFJMAZ (zał. nr 5) albo mikro SFJMIZ (zał. nr 4); pole „Kwoty w tysiącach zł” tworzy
   odpowiednio SFJINT / SFJMAT / SFJMIT.
4. Schematy XSD do walidacji: Ustawienia → Schematy XSD → „Pobierz z serwera MF” lub „Wczytaj z pliku…”.
5. CIT-8, JPK_CIT, uchwały i złożenie w KRS — sekcja Deklaracje (terminy na Pulpicie).

## Kopie zapasowe i bezpieczeństwo

- Kopie automatyczne (domyślnie co 24 h i przy zamknięciu programu) — sekcja Backupy; opcjonalnie wysyłka do
  OVHcloud Object Storage (Integracje).
- Odtworzenie: Backupy → zaznacz kopię → Odtwórz → wskaż pusty katalog → przełącz program na odtworzoną bazę.
- Na innym komputerze: zainstaluj program, w kreatorze lub w Backupach wpisz klucz odzyskiwania.
- Serwer niedostępny przy starcie: ponów, uruchom tryb awaryjny (osobna kopia robocza z ostatniej kopii — zmiany
  trzeba potem wprowadzić w bazie głównej) albo zakończ. Program nigdy nie przełącza się sam na inną bazę.

## Wiersz poleceń

```text
pk-cli info | kopia | weryfikuj PLIK | odtworz PLIK KATALOG | integralnosc | migruj --host ... | pomoc
```

Przykład kopii nocnej (Linux, cron): `0 2 * * * pk-cli kopia --powod nocna`.
