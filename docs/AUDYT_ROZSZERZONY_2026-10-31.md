# Audyt rozszerzony — 31.10.2026 (wersja 1.0.0)

Repozytorium: WertQuU/program-ksiegowy
Gałąź: audit/february-sick-pay-20261031
PR: https://github.com/WertQuU/program-ksiegowy/pull/2

## Zakres

Priorytet 1: zgodność z polskim prawem rachunkowym, podatkowym, ubezpieczeniowym i ochroną danych.
Pozostałe obszary: bezpieczeństwo, kopie zapasowe, spójność i trwałość zapisów, integralność danych, migracje, współbieżność, wydajność i testy.
Numer wersji: **1.0.0**. Nie scalać do main bez zgody właściciela.
Audyt statyczny i CI nie gwarantują 100% zgodności we wszystkich stanach faktycznych.

## Poprawki w tej gałęzi (wymagają pozytywnego CI dla ostatecznego SHA)

| Id | Problem | Poprawka |
|---|---|---|
| A-01 | Cały luty choroby (28/29 dni) mógł pozostawiać wynagrodzenie za pracę | Wykrywanie całego miesiąca kalendarzowego, walidacja i testy |
| A-02 | RPP G zawierało dodatkowo E.1 | G = F ± D według załącznika nr 1 do UoR i KSR 1; test |
| A-03 | Nieudane kody TOTP nie blokowały konta | Blokada po pięciu błędnych MFA; test |
| A-04 | Sprawdzanie ścieżek kopii akceptowało podobny prefiks katalogu | Kontrola prawdziwej granicy katalogu, ochrona przed traversal; test |

## Otwarte zagadnienia zgodności

### L-01 — Podstawa świadczenia chorobowego (P0, potwierdzone ryzyko)

PayrollCalculator.Compute używa SickBasis ?? gross. PayrollService nie wylicza podstawy z historycznych wypłat (zwykle 12 wcześniejszych miesięcy). Przy zmiennych składnikach wynagrodzenia może to prowadzić do błędnej kwoty.
Potrzebne: udokumentowana podstawa chorobowego, historia wynagrodzeń, reguły ZUS dla składników zmiennych, zmiany etatu, przerw i okresów ubezpieczenia, testy; bez wystarczających danych wstrzymać automatyczne uznanie wyniku za ostateczny.

### L-02 — Rozliczenia płac, PIT i ZUS (P1, do weryfikacji)

Testować PIT-2, zbiegi tytułów, 50% koszty autorskie, ulgę dla młodych, PPK i moment przychodu, kilka wypłat jednego miesiąca, korekty, deklaracje PIT i ZUS na oficjalnych wzorach.

### L-03 — JPK i sprawozdawczość (P1, do weryfikacji)

Weryfikować aktualne XSD i zgodność merytoryczną JPK_V7, JPK_KR_PD oraz e-sprawozdań. RPP: różnice kursowe, leasing, inwestycje, operacje niepieniężne, przelewy wewnętrzne, porównanie danych z bilansem i dokumentacją.

### L-04 — Księgi rachunkowe (P1, do weryfikacji)

Testować kompletność dowodów i trwałość zapisów, numerację, historię korekt, granice miesięcy i lat, zamknięcie/otwarcie zgodnie z art. 12, 20–25 UoR. Tryb wyjątkowy nie może zastępować podstawy prawnej.

## Otwarte zagadnienia techniczne i bezpieczeństwa

### T-01 — Różnica czasu sprawdzenia loginu (P2, potwierdzone ryzyko)

SecurityService.LoginAsync dla nieznanego loginu uruchamia PBKDF2 z maksymalnie 10 000 iteracji, a dla istniejącego konta zwykle z 600 000. Może to ułatwiać enumerację użytkowników. Wyrównać koszt bez zwiększania ryzyka DoS oraz zastosować limity prób.

### T-02 — Kopie zapasowe (P1, do weryfikacji)

Przetestować odtwarzanie po awarii, brak miejsca, uszkodzenie archiwum, klucze i rotację, pełną zgodność bazy/załączników po odtworzeniu oraz kompatybilność SQLite/PostgreSQL. Test odzyskania jest konieczny, by uznać kopię za użyteczną.

### T-03 — Współbieżność (P1, do weryfikacji)

Testować równoległą numerację dzienników, łańcuch hash, operacje na rozrachunkach, korekty, dwie próby zarachowania tej samej wpłaty CIT i kilka równoczesnych list płac. Sprawdzać atomiczność transakcji i zachowanie na PostgreSQL.

### T-04 — Ochrona danych i logi (P1, do weryfikacji)

Zweryfikować retencję, żądania osób, uprawnienia, szyfrowanie i magazyn kluczy, kopie ze starszymi danymi, logowanie bezpieczeństwa oraz procedury organizacyjne RODO.

### T-05 — Wersjonowanie i migracje (P1, do weryfikacji)

Nie zwiększać numeru wersji, nie publikować wydania ani nie scalać PR bez wyraźnej decyzji właściciela. Migracje sprawdzić na danych testowych z obu silników i kopii starych wersji.

## Bramka akceptacji

1. CI dla ostatniego commita zakończone sukcesem (Windows i Linux), smoke oraz testy integracyjne.
2. Testy SQLite/PostgreSQL, backupu i odtwarzania, konfliktów współbieżności.
3. Test regresyjny dla każdej naprawionej niezgodności i przypadków granicznych.
4. Porównanie wyliczeń podatkowych, kadrowych, JPK i sprawozdań z przykładami urzędowymi.
5. Brak znanych blokujących rozbieżności księgowych/prawnych; niezależny przegląd merytoryczny.
6. Wyraźna zgoda właściciela na scalenie, utrzymanie numeru **1.0.0**.

Źródła: ustawa o rachunkowości Dz.U. 2026 poz. 522 — https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU20260000522 ; KSR nr 1 https://www.gov.pl/documents/1079560/1080340/20171106_KSR_nr_1_nowelizacja.pdf/b0281da0-4875-3e8c-87cf-3422a239f40c ; ZUS https://www.zus.pl/
