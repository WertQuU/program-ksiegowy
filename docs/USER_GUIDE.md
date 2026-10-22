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

## Codzienna praca

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

## Koniec miesiąca

1. Zaksięguj dokumenty i wyciągi, nalicz amortyzację.
2. VAT → okres → Kontrola rejestrów → Zaksięguj rozliczenie VAT → Generuj JPK_V7M (zweryfikuj i wyślij
   narzędziem MF — program nie wysyła JPK).
3. CIT → okres → Przelicz → Zapisz wyliczenie zaliczki → po weryfikacji pozycji „do analizy” Zatwierdź.
4. Zamknięcie okresu i roku → zaznacz miesiąc → Lista kontrolna → Zamknij okres.

## Koniec roku

Zamknij wszystkie miesiące, wykonaj kopię, „Podgląd zamknięcia roku”, następnie „Zamknij rok obrotowy”
i wpisz frazę potwierdzenia. Program przeksięguje wynik, zablokuje rok i utworzy rok następny z bilansem otwarcia.
Bilans i RZiS: Raporty; plik XML sprawozdania: Raporty → „Sprawozdanie finansowe XML” (uzupełnij informację
dodatkową i podpisy w narzędziu MF).

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
