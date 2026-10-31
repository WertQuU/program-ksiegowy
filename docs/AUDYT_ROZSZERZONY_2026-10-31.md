# Audyt rozszerzony — 31.10.2026 (wersja 1.0.0)

Repozytorium: WertQuU/program-ksiegowy
Gałąź: audit/february-sick-pay-20261031
PR: https://github.com/WertQuU/program-ksiegowy/pull/2

## Zakres

Priorytet 1: zgodność z polskim prawem rachunkowym, podatkowym, ubezpieczeniowym i ochroną danych.
Pozostałe obszary: bezpieczeństwo, kopie zapasowe, spójność i trwałość zapisów, integralność danych, migracje, współbieżność, wydajność i testy.
Numer wersji: **1.0.0**. Zgoda właściciela na scalenie została udzielona 31.10.2026, ale nie zwalnia to z weryfikacji CI ani usunięcia znanych blokujących ryzyk prawnych.
Audyt statyczny i CI nie gwarantują 100% zgodności we wszystkich stanach faktycznych.

## Poprawki w tej gałęzi (wymagają pozytywnego CI dla ostatecznego SHA)

| Id | Problem | Poprawka |
|---|---|---|
| A-01 | Cały luty choroby (28/29 dni) mógł pozostawiać wynagrodzenie za pracę | Wykrywanie całego miesiąca kalendarzowego, walidacja i testy |
| A-02 | RPP G zawierało dodatkowo E.1 | G = F ± D według załącznika nr 1 do UoR i KSR 1; test |
| A-03 | Nieudane kody TOTP nie blokowały konta | Blokada po pięciu błędnych MFA; test |
| A-04 | Sprawdzanie ścieżek kopii akceptowało podobny prefiks katalogu | Kontrola prawdziwej granicy katalogu, ochrona przed traversal; test |
| A-05 | Niezweryfikowany nagłówek kopii mógł narzucać koszt PBKDF2 i rozmiar bufora | Istniejąca walidacja stałych parametrów `ArchiveCrypto.ValidateHeader` i test `Malformed_archive_header_cannot_control_kdf_workload_or_chunk_allocation` — potwierdzone w kodzie, oczekuje na zielone CI |
| A-06 | Brak wiarygodnego wieku przy ulgach PIT/ZUS lub ukończenie 26 lat w miesiącu zlecenia | `PayrollService.PostAsync` blokuje księgowanie bez daty urodzenia/PESEL i przy granicy 26 lat w miesiącu zlecenia; testy regresyjne. Podział okresów nadal wymaga ustalenia przez użytkownika |
| A-07 | Błędy CS0246/CS0103 w testach pełnej kopii | Poprawiono przestrzeń nazw `PelnaKsiegowosc.Data` w `BackupCompletenessAuditTests.cs`; weryfikacja przez CI w toku |
| A-08 | Test wspólnego limitu ulgi PIT używał PESEL osoby 36-letniej | Zmieniono dane testowe na poprawny PESEL osoby urodzonej 01.01.2001 (25 lat w dniu wypłaty 10.03.2026); nie zmieniano zasad naliczania ulgi | 
| A-09 | Logowanie nieznanego użytkownika zużywało tylko 10 000 iteracji PBKDF2 wobec 600 000 domyślnie | Wyrównano koszt fikcyjnej weryfikacji hasła do konfiguracji `SecurityService` (`_iterations`); dalszy audyt ograniczania prób i starszych hashy pozostaje otwarty |
| A-10 | Chorobowe wyliczane domyślnie od bieżącej pensji bez weryfikacji historii | Dla prostego przypadku (pełne miesiące, jedno wynagrodzenie zasadnicze, niezmieniona pensja) podstawa z zaksięgowanych miesięcy poprzedzających chorobę (do 12); brak/zmiana historii, dodatkowe umowy i wcześniejsze nieobecności blokują księgowanie. Trzy testy regresyjne. **Częściowe zabezpieczenie, nie pełna implementacja art. 36–43.** |
| A-11 | Limit 33/14 dni chorobowego błędnie sumowany według roku wypłaty | Oddzielono rok wypłaty (PIT/ZUS) od roku niezdolności (art. 92 KP). `YearToDateAsync` sumuje chorobowe wg `PayrollList.Month`, a `PayrollAbsence.SickYear` wskazuje rok dla limitu 33/14 dni. Testy granicy grudzień/styczeń, wypłaty z opóźnieniem i 50. urodzin. |
| A-12 | Brak wieku przy chorobowym mógł domyślnie zastosować limit 33 dni | `PostAsync` blokuje zaksięgowanie chorobowego bez zweryfikowanej daty urodzenia/PESEL; test regresyjny. |
| A-13 | Data urodzenia w kartotece mogła być sprzeczna z poprawnym PESEL | `PayrollService.Validate` odrzuca sprzeczne daty i PESEL z nieprawidłową zakodowaną datą; test regresyjny. |
| A-14 | JPK_KR_PD wybierał dowolnego pierwszego kontrahenta dla zapisu z kilkoma NIP-ami | `JpkKrPdBuilder` blokuje tworzenie nierzetelnego `D_3` w niejednoznacznym zapisie i wskazuje potrzebę rozdzielenia dekretów lub udokumentowanej obsługi zbiorczej; test regresyjny. |
| A-15 | Kalkulator chorobowego po cichu używał bieżącej pensji jako podstawy, gdy jej nie znano | Zlikwidowano fallback `SickBasis ?? gross`. Kalkulator wymaga jawnej podstawy; lista robocza bez historii pokazuje 0 zł, jednoznaczne ostrzeżenie i blokuje księgowanie. Testy weryfikują oba przypadki. |
| A-16 | Raport Deep Research błędnie uznał historię NIP i wyrównanie kosztu PBKDF2 za niezaimplementowane | Potwierdzono w **gałęzi PR #2**: `CounterpartyIdentityChange`, migracje, zapis NIP na dokumentach i trzy testy JPK historycznych identyfikatorów; w `SecurityService` brak starego `Math.Min(_iterations, 10_000)`. Ustalenia raportu odnosiły się do innej/starej rewizji albo były nieaktualne. |

## Dalsze poprawki i wymagania weryfikacyjne (v1.0.0)

| Id | Priorytet | Zmiana lub ryzyko | Dowód / test |
| --- | --- | --- | --- |
| R-10 | ZUS P0 | **Częściowo zabezpieczone**: blokada księgowania przy nieznanym wieku i w miesiącu ukończenia 26 lat. Nadal brak rejestru okresów statusu ucznia/studenta i automatycznego rozdzielenia wynagrodzenia przed/od urodzin; nie utożsamiać daty wypłaty z okresem ubezpieczenia | Testy `PayrollAgeAuditTests`, dokumenty potwierdzające status, test rozdzielenia okresów |
| R-11 | JPK P0 (warunkowo) | `JPK_ST_KR` poza programem. MF 30.10.2026 ogłosiło podpisanie rozporządzenia o zwolnieniu wybranych podmiotów CIT i spółek niebędących osobami prawnymi za lata rozpoczynające się w 2026–2028; wejście w życie dopiero po ogłoszeniu w Dz.U. **Nie potwierdzono jeszcze publikacji ani zakresu wobec PIT**. Nie uznawać zwolnienia za obowiązujące bez weryfikacji | https://github.com/WertQuU/program-ksiegowy/issues/4 ; weryfikacja Dz.U., zakresu podmiotowego i generator dla niezwolnionych |
| R-12 | ochrona danych P1 | Należy potwierdzić systematyczne wykonywanie i testowe odtwarzanie kopii oraz okresy retencji odpowiednie do rodzaju dokumentów | Art. 10, 13, 71–74 UoR i procedury organizacyjne |
| R-13 | ZUS P0 | Wg ZUS limit 33/14 dni sumuje choroby w danym roku również u **innych pracodawców**. Program nie ma potwierdzenia wcześniejszego wykorzystania limitu ani kwalifikacji stawki 100% (ciąża, wypadek w drodze do pracy, dawstwo); nie wolno uznawać automatu za kompletny | Ewidencja wcześniejszych okresów i przyczyn, udokumentowane oświadczenia, testy |

Oficjalne źródła: ulga dla młodych https://podatki.gov.pl/ulgi-i-odliczenia/ulga-dla-mlodych-pit ; ZUS (umowy cywilnoprawne) https://www.zus.pl/en/-/umowy-cywilnoprawne-w-ubezpieczeniach-spolecznych ; JPK_PD https://podatki.gov.pl/podatki-firmowe/jednolity-plik-kontrolny/jpk_pd/jpk_pd ; UoR Dz.U. 2026 poz. 522 https://eli.gov.pl/eli/DU/2026/522/ogl .

**Uwaga:** W razie nieznanej daty urodzenia samo zaznaczenie ulgi w kartotece pozostaje niewystarczającym potwierdzeniem prawa do zwolnienia. Konieczna dalsza walidacja danych osoby przed ostatecznym zatwierdzeniem listy.

## Otwarte zagadnienia zgodności

### L-01 — Podstawa świadczenia chorobowego (P0, potwierdzone ryzyko)

**Częściowo naprawiono:** `PayrollCalculator.Compute` nie dopuszcza już niejawnego zastąpienia podstawy chorobowego bieżącą płacą. Robocza lista z niezweryfikowaną podstawą ma kwotę świadczenia 0 zł i ostrzeżenie, nie może zostać zaksięgowana. `PayrollService.VerifiedSickBasisAsync` odczytuje maksymalnie 12 pełnych miesięcy zaksięgowanych list poprzedzających chorobę i przekazuje historyczną kwotę brutto jako `PayrollAbsence.SickBasis`, ale wyłącznie dla stabilnego wynagrodzenia zasadniczego bez nieobecności, innych umów i zmian płacy. Brak jednoznacznej historii lub przypadek szczególny blokuje `PostAsync`; wyświetlana na liście kwota wstępna NIE jest kwotą zatwierdzoną. Nie ma jeszcze ręcznego, audytowalnego zatwierdzania podstawy dla przypadków złożonych.
**Nadal P0:** składniki zmienne i kwartalne/roczne, niepełne miesiące, zmiana etatu, przerwy między niezdolnościami, szczególne stawki i podstawy (art. 36–43), wcześniejsze choroby u innych pracodawców (art. 92 KP), stawka 100% przy określonych przyczynach, historia sprzed uruchomienia programu oraz kompletna dokumentacja wyliczenia. Potrzebna dalsza implementacja i niezależny przegląd.

### L-02 — Rozliczenia płac, PIT i ZUS (P1, do weryfikacji)

Testować PIT-2, zbiegi tytułów, 50% koszty autorskie, ulgę dla młodych, PPK i moment przychodu, kilka wypłat jednego miesiąca, korekty, deklaracje PIT i ZUS na oficjalnych wzorach.

### L-03 — JPK i sprawozdawczość (P1, do weryfikacji)

Weryfikować aktualne XSD i zgodność merytoryczną JPK_V7, JPK_KR_PD oraz e-sprawozdań. JPK_ST_KR: zwolnienie zapowiedziane przez MF nie jest równoważne z potwierdzonym wejściem w życie ani automatycznym objęciem wszystkich podmiotów. RPP: różnice kursowe, leasing, inwestycje, operacje niepieniężne, przelewy wewnętrzne, porównanie danych z bilansem i dokumentacją.

### L-04 — Księgi rachunkowe (P1, do weryfikacji)

Testować kompletność dowodów i trwałość zapisów, numerację, historię korekt, granice miesięcy i lat, zamknięcie/otwarcie zgodnie z art. 12, 20–25 UoR. Tryb wyjątkowy nie może zastępować podstawy prawnej.

## Otwarte zagadnienia techniczne i bezpieczeństwa

### T-01 — Różnica czasu sprawdzenia loginu (P2, potwierdzone ryzyko)

**Częściowo naprawiono**: nieznany login używa tej samej skonfigurowanej liczby iteracji PBKDF2 co nowe konta (domyślnie 600 000). Potwierdzono odczytem bieżącego pliku z PR #2: wskazany przez Deep Research stary fallback 10 tys. iteracji **nie występuje**. Wciąż wymagają oceny starsze hashe z inną liczbą iteracji, ograniczanie prób na poziomie źródłowego adresu/urządzenia i wpływ obciążenia KDF na DoS.

### T-02 — Kopie zapasowe (P1, do weryfikacji)

Przetestować odtwarzanie po awarii, brak miejsca, uszkodzenie archiwum, klucze i rotację, pełną zgodność bazy/załączników po odtworzeniu oraz kompatybilność SQLite/PostgreSQL. Test odzyskania jest konieczny, by uznać kopię za użyteczną.

### T-03 — Współbieżność (P1, do weryfikacji)

Testować równoległą numerację dzienników, łańcuch hash, operacje na rozrachunkach, korekty, dwie próby zarachowania tej samej wpłaty CIT i kilka równoczesnych list płac. Sprawdzać atomiczność transakcji i zachowanie na PostgreSQL.

### T-04 — Ochrona danych i logi (P1, do weryfikacji)

Zweryfikować retencję, żądania osób, uprawnienia, szyfrowanie i magazyn kluczy, kopie ze starszymi danymi, logowanie bezpieczeństwa oraz procedury organizacyjne RODO.

### T-05 — Wersjonowanie i migracje (P1, do weryfikacji)

Nie zwiększać numeru wersji ani nie publikować wydania; zgoda właściciela na scalenie jest, ale wymagana jest pozytywna bramka akceptacji. Migracje sprawdzić na danych testowych z obu silników i kopii starych wersji.

## Bramka akceptacji

1. CI dla ostatniego commita zakończone sukcesem (Windows i Linux), smoke oraz testy integracyjne.
2. Testy SQLite/PostgreSQL, backupu i odtwarzania, konfliktów współbieżności.
3. Test regresyjny dla każdej naprawionej niezgodności i przypadków granicznych.
4. Porównanie wyliczeń podatkowych, kadrowych, JPK i sprawozdań z przykładami urzędowymi.
5. Brak znanych blokujących rozbieżności księgowych/prawnych; niezależny przegląd merytoryczny.
6. Zgoda właściciela na scalenie została udzielona; utrzymać numer **1.0.0**, nie scalać bez pozytywnych kontroli 1–5.

Źródło podstawy chorobowego: https://www.zus.pl/pl/swiadczenia/zasilki/zasilek-chorobowy/podstawa-wymiaru/dla-pracownikow ; ustawa o świadczeniach pieniężnych w razie choroby i macierzyństwa (art. 36–43).

Źródła: ustawa o rachunkowości Dz.U. 2026 poz. 522 — https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU20260000522 ; KSR nr 1 https://www.gov.pl/documents/1079560/1080340/20171106_KSR_nr_1_nowelizacja.pdf/b0281da0-4875-3e8c-87cf-3422a239f40c ; ZUS https://www.zus.pl/

Dodatkowe źródło dla art. 92 KP i limitu w roku choroby (również u innych pracodawców): https://bip.zus.pl/pl/web/guest/swiadczenia/zasilki/zasilek-chorobowy/z-ubezpieczenia-chorobowego/prawo-do-zasilku-i-okres-przyslugiwania .

Komunikat MF 30.10.2026 o podpisanym rozporządzeniu dotyczącym czasowego zwolnienia CIT z JPK_ST_KR: https://www.gov.pl/web/kas/rozporzadzenie-w-sprawie-czasowego-zwolnienia-w-cit-z-obowiazkow-w-zakresie-jpk-cit-dotyczacych-ewidencji-srodkow-trwalych-i-wartosci-niematerialnych-i-prawnych (wejście w życie po ogłoszeniu w Dz.U. — wymaga potwierdzenia).

## Weryfikacja nowych poprawek

CI dla poprzedniego SHA `141c5ad985ff20e101f7d271740e059accc213d9` zakończyło się sukcesem na Ubuntu i Windows (GitHub Actions run #41211497930). **Ten wynik nie dotyczy późniejszych commitów A-14–A-16** — ich końcowy SHA musi ponownie przejść oba systemy. Nawet zielone CI nie stanowi niezależnej opinii prawnej.

Oficjalne MF o `D_3` i dokumentach zbiorczych: https://podatki.gov.pl/podatki-firmowe/jednolity-plik-kontrolny/jpk_pd/pytania-i-odpowiedzi-jpk_pd (pytania 112, 121, 146); opcjonalność pola w XSD nie upoważnia do przypisywania innego kontrahenta.
