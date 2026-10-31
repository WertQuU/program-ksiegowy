# Audyt iteracyjny programu Pełna Księgowość — 31.10.2026 (wersja 1.0.0)

## Zasady projektu audytowego

- Gałąź: `audit/february-sick-pay-20261031`; integracja zmian wyłącznie po akceptacji właściciela.
- Nie zmieniać numeru wydania **1.0.0** i nie tworzyć tagu wydania automatycznie.
- Priorytet: zgodność z polskim prawem; dodatkowo integralność danych, bezpieczeństwo, niezawodność, przywracanie kopii, wydajność i jakość testów.
- Wynik CI i poprawność składni XML/XSD nie oznaczają samodzielnie zgodności materialnej z prawem.
- Testy regresyjne wykonuje GitHub Actions (Linux i Windows). Ocena końcowa wymaga testów na realnych scenariuszach i niezależnej kontroli merytorycznej.

## Usterki poprawione na gałęzi

### A-01 — całomiesięczna choroba w lutym

**Obszar:** prawo pracy / płace / prawidłowość kwot.

Poprawka: kalkulator rozpoznaje chorobę obejmującą cały miesiąc kalendarzowy, również luty 28- i 29-dniowy, i nie pozostawia wynagrodzenia za pracę, której pracownik nie wykonywał. `PayrollService` przekazuje liczbę dni miesiąca do `PayrollCalculator`.

Test: `Whole_february_sickness_has_no_salary_for_work_in_28_or_29_day_month`.

Podstawa: § 11 rozporządzenia MPiPS z 29 maja 1996 r. w sprawie sposobu ustalania wynagrodzenia w okresie niewykonywania pracy; art. 92 Kodeksu pracy. Test obejmuje wyłącznie przypadek stałego wynagrodzenia miesięcznego.

### A-02 — obejście zatwierdzenia SF przy ostatecznym zamknięciu

**Obszar:** wymóg ustawowy / nieodwracalność ksiąg / kontrola dostępu do operacji.

Poprawka: standardowe zamknięcie roku kontynuującej działalność jednostki nie może nastąpić bez zarejestrowanego zatwierdzenia sprawozdania finansowego. Sam fakt braku zatwierdzenia w terminie **nie** odblokowuje zamknięcia. Rejestr szczególnych okoliczności pozostaje wyłącznie informacyjny. Zdarzenia z art. 12 ust. 2 UoR wymagają osobnego workflow z właściwym dniem zamknięcia; nie należy używać procedury automatycznie otwierającej kolejny zwykły rok.

Test: `Full_year_produces_balanced_statements_and_correct_opening_balance` obejmuje teraz próbę obejścia blokady mimo zapisania dokumentacji niezatwierdzenia.

Podstawa: art. 12 ust. 2, ust. 4 i ust. 5 ustawy z 29.09.1994 r. o rachunkowości; tekst jednolity Dz.U. 2026 poz. 522.

### A-03 — regresja blokady MFA (test dodany na gałęzi)

**Obszar:** bezpieczeństwo kont.

Test: `Five_invalid_mfa_codes_lock_account_even_when_password_is_correct` weryfikuje limit pięciu błędnych kodów MFA i zablokowanie logowania prawidłowym kodem w czasie blokady. Na gałęzi znajduje się nowy test; bez osobnej kontroli runtime nie oznacza to audytu wszystkich dróg logowania.

## Ryzyka pozostałe — wymagają osobnych iteracji

| ID | Waga | Obszar | Co sprawdzić / warunek akceptacji |
| --- | --- | --- | --- |
| R-01 | P0 | Wynagrodzenie chorobowe | Aktualnie podstawa chorobowego bez podanej `SickBasis` pochodzi z bieżącego `gross`. Wymagana analiza średniej z właściwego okresu, uzupełnień oraz wyjątków. Nie wolno bez dodatkowej walidacji zakładać, że bieżące brutto to podstawa zasiłkowa. |
| R-02 | P0 | Zamykanie i otwieranie ksiąg | Przetestować rzeczywiste zdarzenia z art. 12 ust. 2 UoR (likwidacja, upadłość, połączenie) w osobnym procesie; standardowy proces nie może ich zastępować. |
| R-03 | P1 | ZUS / PIT / PPK | Testy kilku równoczesnych tytułów i list, zmiany oświadczeń, świadczeń w kolejnych miesiącach, oraz różnic rocznych po korekcie list płac. |
| R-04 | P1 | Integralność ksiąg | Dwa równoległe procesy księgujące, ten sam numer dziennika, rollback przy błędzie, nieprzerwana sekwencja skrótów i ochrona triggerami SQLite/PostgreSQL. |
| R-05 | P1 | Odtwarzanie kopii | Pełny drill: backup → weryfikacja → odtworzenie do nowego katalogu/instancji → porównanie bilansu, dziennika i załączników; przerwanie, uszkodzenie i błędny klucz. |
| R-06 | P1 | Dane osobowe | Kontrola uprawnień per jednostka przy imporcie/eksporcie, logach, załącznikach i odtwarzaniu; uzasadnienie retencji RODO, ograniczenie danych w logach i kopiach. |
| R-07 | P1 | E-deklaracje i JPK | Porównać kwoty i pola struktur z obowiązującymi wzorami MF, nie tylko XSD; testy okresów przejściowych i korekt. |
| R-08 | P2 | Wydajność | Duże dzienniki i wieloletnie księgi, raporty i JPK; test pamięci, limit czasu i koszt zapytań do baz danych. |
| R-09 | P2 | Łańcuch dostaw | Regularny audyt NuGet, transytywnych zależności, GH Actions i konfiguracji publikacji; nie publikować wydania bez świadomego uruchomienia procesu release. |

## Zweryfikowane mechanizmy w kodzie (nie jest to certyfikacja)

- Zaksięgowane wpisy i pozycje podlegają strażnikom niezmienności na poziomie bazy danych (SQLite i PostgreSQL), obok kontroli aplikacyjnych.
- Backup ma szyfrowanie i kontrolę integralności, prywatne katalogi staging oraz ochronę rozpakowania przed wyjściem poza katalog i nadmierną ekspansją archiwum.
- Logowanie ma PBKDF2 i MFA; do dalszej weryfikacji pozostają szczegóły ataków na sesje, retencję, recovery codes oraz blokady w sytuacjach równoległych.

## Minimalny standard zakończenia pojedynczej poprawki

1. Reprodukcja błędu konkretnym testem.
2. Zweryfikowana podstawa prawna, gdy dotyczy; oddzielnie wymagania techniczne.
3. Poprawa kodu na gałęzi, bez zmiany wersji 1.0.0.
4. Test jednostkowy i integracyjny, Linux/Windows, a przy zmianie schematu także SQLite/PostgreSQL.
5. Ponowny audyt pokrewnych ścieżek i negatywnych scenariuszy.
6. Wynik CI, odnotowanie pozostałych ograniczeń i dopiero wtedy wniosek o zgodę na scalenie.

## Źródła

- Ustawa o rachunkowości (t.j. Dz.U. 2026 poz. 522): https://eli.gov.pl/api/acts/DU/2026/522/text.html
- Rozporządzenie o ustalaniu wynagrodzenia w okresie niewykonywania pracy (29.05.1996): https://eli.gov.pl/
- Kodeks pracy, art. 92: https://eli.gov.pl/
