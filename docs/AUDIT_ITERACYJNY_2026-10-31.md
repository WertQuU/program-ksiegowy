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
| R-01 | P0 | Wynagrodzenie chorobowe | Usunięto niejawne podstawienie `gross`; księgowanie jest blokowane bez potwierdzonej podstawy. Nadal brak pełnego modelu art. 36–43 (składniki zmienne, przerwy, zmiany etatu, uzupełnienie podstawy, okresy prawa do świadczenia). Nie uznawać płac za w pełni zgodne. |
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

## Ponowny audyt ukierunkowany na bezpieczeństwo rozliczeń — 31.10.2026

**Zakres:** prześledzono model wyliczenia listy płac, przepływ zatwierdzenia i księgowania, narastanie dni wynagrodzenia chorobowego, dane wejściowe nieobecności; zweryfikowano interpretację ZUS i sprawdzono komunikat MF o JPK_ST_KR. Jest to przegląd punktowy, nie niezależny pełny audyt całego systemu.

### A-04 — brak ciągłości dni chorobowego u innych pracodawców (P0 — poprawione zabezpieczenie)

- Problem: deklaracja `SickWageAssessment.PreviousEmployerDays` była zapisywana oddzielnie per miesiąc. W marcu można było potwierdzić np. **30 dni**, a w kwietniu **0 dni**, co przy rocznym limicie 33/14 dni mogło prowadzić do ponownej wypłaty wynagrodzenia chorobowego.
- Zmiana: `RecordSickWageAssessmentAsync` kontroluje, że deklaracje w danym roku są narastające. Odrzuca także sprzeczne wpisy wsteczne. `PostAsync` sprawdza chronologię niezależnie od formularza, także dla starszych danych. Wraz z nowym rokiem licznik u innych pracodawców można zacząć od zera.
- Testy: `PayrollExternalEmployerChronologyTests` — spadek narastających dni, wsteczna niezgodność, reset na 1 stycznia, ochrona księgowania wobec wcześniejszych niespójnych wpisów.
- Granica: nie modeluje jednoczesnych pracodawców, pokrywających się dni, odcinków niezdolności, uprawnień do świadczeń ani różnic stawek w jednym miesiącu. **Issue #5 nadal P0 otwarte.**
- Podstawa: ZUS — [prawo do zasiłku i okres przysługiwania](https://www.zus.pl/swiadczenia/zasilki/zasilek-chorobowy/z-ubezpieczenia-chorobowego/prawo-do-zasilku-i-okres-przyslugiwania), art. 92 KP.

### A-05 — ujemne dni nieobecności i nieprawidłowa stawka (P1 — poprawione)

- Problem: walidacja `PayrollAbsence` była umieszczona wewnątrz gałęzi wykonywanej tylko dla dodatniej nieobecności, dlatego `SickDays=-1` i `UnpaidWorkingDays=0` omijały kontrolę.
- Zmiana: walidacja wszystkich przekazanych parametrów `PayrollAbsence` działa przed sprawdzeniem, czy należy obliczyć potrącenie. Odrzucane są dni ujemne, więcej dni niż miesiąc, nieprawidłowa liczba dni roboczych oraz stawka chorobowego inna niż 80% albo 100% w obsługiwanym modelu art. 92 KP.
- Testy: `PayrollAbsenceValidationAuditTests` — przypadki ujemne, przekroczenie dni roboczych, stawki 70%/110% oraz odmowa zapisu wadliwej pozycji roboczej.

### A-06 — 30 dni choroby w miesiącu liczącym 31 dni (P0 — blokada do ręcznej weryfikacji)

- Problem: `PayrollCalculator` ustalał pensję za pracę na 0 zł, jeśli w 31-dniowym miesiącu wpisano 30 dni choroby. Jeżeli pozostały dzień rzeczywiście przepracowano, wyzerowanie wynagrodzenia za pracę narusza art. 80 KP. GIP wskazał konieczność zapłaty za wykonaną pracę (stanowisko GPP-87-4560-64/09/PE/RP; metoda pomocnicza według § 4 ust. 2 pkt 1 rozporządzenia z 29.05.1996).
- Zmiana: lista robocza jest oznaczona **WSTĘPNA**, a `PostAsync` blokuje automatyczne księgowanie do czasu potwierdzenia grafiku i faktycznie przepracowanych godzin. Bez dodatkowych danych nie jest możliwe bezpieczne automatyczne wyliczenie; **nie jest to pełna implementacja rozliczenia**.
- Testy: `PayrollThirtyOfThirtyOneDaysTests` — oznaczenie podglądu i odmowa księgowania.
- Źródło: [stanowisko GIP z 2009 r.](https://www.rp.pl/wyjasnienia-urzedow/art15366541-stanowisko-glownego-inspekcji-pracy-w-sprawie-sposobu-ustalania-wynagrodzenia-za-jeden-dzien-wykonywania-pracy-gpp-87-4560-64-09-pe-rp); [art. 80 KP](https://eli.gov.pl/api/acts/DU/2023/1465/text.html).

### Dodatkowe ustalenie — szczególne kategorie danych (P1, wymagany przegląd RODO)

Chociaż zapis `SickWageAssessment` nie zawiera dokumentu medycznego, enum `Pregnancy100` może ujawniać informację o ciąży. Takie metadane nie powinny być traktowane jako neutralne dane techniczne. Wymagana jest ocena podstawy prawnej, kontroli uprawnień, ograniczenia dostępu, zasad retencji i audytu odczytów (RODO art. 9 i 32). Samo szyfrowanie bazy nie wyczerpuje obowiązków.

### Pozostałe ryzyka P0 / P1

| Priorytet | Zagadnienie | Stan / wymagane dalsze działania |
| --- | --- | --- |
| P0 | Art. 36–43 ustawy zasiłkowej | Brak pełnych podstaw historycznych, premii, składników za okresy dłuższe niż miesiąc, zmiany etatu oraz ponownego ustalenia podstawy po przerwie. ZUS potwierdza odmienne zasady dla tych przypadków. |
| P0 | Art. 92 KP i prawo do zasiłku | Brak ewidencji odcinków dziennych (różne powody/80%/100% w miesiącu), okresów wyczekiwania, ciągłości okresów zasiłkowych, jednoczesnych pracodawców oraz okresów z nieprzysługującym świadczeniem. |
| P0 | JPK_ST_KR | MF 30.10.2026 potwierdziło **podpisanie** rozporządzenia czasowo zwalniającego podmioty CIT i wskazane spółki za lata rozpoczynające się w 2026–2028. **Nie potwierdzono tu publikacji w Dz.U.**, która warunkuje wejście w życie; brak generatora. Nie zakładać, że zwolnienie już obowiązuje ani że dotyczy wszystkich podmiotów PIT. |
| P1 | Obowiązki deklaracyjne | Wymagana niezależna próba merytoryczna eksportów JPK, VAT, PIT, CIT i e-sprawozdań na scenariuszach referencyjnych; poprawność XSD nie jest gwarancją kwot. |
| P1 | Bazy danych i bezpieczeństwo | Próby współbieżnego księgowania, numeracji, migracji i odtwarzania zaszyfrowanych kopii do nowej instancji na SQLite oraz PostgreSQL; testy odporności na awarie. |
| P1 | Kontrola wydania | Zielone CI Windows i Linux dla **ostatniego SHA** jest konieczne, ale niewystarczające do certyfikowania zgodności prawnej. |

Źródła kontrolne: [ZUS — podstawa wymiaru zasiłku dla pracowników](https://www.zus.pl/pl/swiadczenia/zasilki/zasilek-chorobowy/podstawa-wymiaru/dla-pracownikow), [ZUS — prawo do zasiłku](https://www.zus.pl/swiadczenia/zasilki/zasilek-chorobowy/z-ubezpieczenia-chorobowego/prawo-do-zasilku-i-okres-przyslugiwania), [MF — komunikat z 30.10.2026 o JPK_ST_KR](https://www.gov.pl/web/kas/rozporzadzenie-w-sprawie-czasowego-zwolnienia-w-cit-z-obowiazkow-w-zakresie-jpk-cit-dotyczacych-ewidencji-srodkow-trwalych-i-wartosci-niematerialnych-i-prawnych).

**Status:** poprawki na gałęzi audytowej, numer aplikacji **1.0.0**, PR #2 pozostaje Draft. Nie scalać do `main` ani nie publikować jako w pełni zgodne, dopóki istnieją wskazane blokady P0 i brak końcowej weryfikacji.
