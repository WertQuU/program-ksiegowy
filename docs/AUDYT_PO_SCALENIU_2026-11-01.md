# Audyt integracyjny i powtórna kontrola — 1 listopada 2026

**Repozytorium:** `WertQuU/program-ksiegowy`. **Wersja:** 1.0.0 (bez zmiany numeru).  
**Punkt odniesienia:** `main` po scaleniu PR #23–#29, commit `614aef976ebbb40609fba977ccfcf372a7018102`.  
**Kolejny zestaw poprawek:** PR #30, „wiarygodne storna i atomowe korekty księgowe”. Niniejszy dokument opisuje ustalenia z dwóch przeglądów kodu; samo przygotowanie poprawek nie oznacza, że są już scalone.

## 1. Metoda i granice

- Odczyt kodu silnika dziennika, walidatorów, modułu płac, kadr, środków trwałych, konfiguracji PostgreSQL i dokumentacji. Porównanie PR #23–#29 z bazą oraz sprawdzenie uruchomień CI Windows i Linux.
- Testy automatyczne obejmują scenariusze integracyjne, lecz nie certyfikują podatków, ZUS, odporności na włamania ani kompletności ewidencji.
- Nie wykonywano testu odtwarzania rzeczywistej bazy produkcyjnej, oceny przez niezależnego księgowego lub doradcę podatkowego, audytu infrastruktury użytkownika ani testu eksploatacyjnego na rzeczywistych danych firmy.
- „100% zgodności” nie można stwierdzić na podstawie repozytorium, pojedynczego przeglądu i samych zielonych testów.

## 2. Ustalenia po scaleniu — poprawione w PR #30

| Nr | Waga | Ustalenie | Rozwiązanie i test akceptacyjny |
|---|---|---|---|
| A1 | Wysoka | Publiczne metody dekretacji przyjmowały `EntryKind.Storno` niezwiązane z zaksięgowanym oryginałem, co dopuszczało ujemne zapisy poza oficjalną ścieżką korekty. | Blokada sztucznego storna w tworzeniu, edycji i księgowaniu bufora; dodatni test `ReverseAsync` z `ReversesEntryId`. |
| A2 | Wysoka | `CorrectAsync` sprawdzało poprawny dekret dopiero po próbie wystornowania oryginału. Błąd mógł pozostawić niepełny stan EF Core, który utrwaliłby się przy późniejszym `SaveChangesAsync`. | Wstępna walidacja obu stron korekty, jedna transakcja i odrzucenie częściowego stanu; test złego konta oraz nieistniejącego rozrachunku w dekrecie poprawnym. |
| A3 | Wysoka | Odrzucone `ReverseAsync` i `PostAsync` mogły pozostawić śledzone częściowe zmiany, włącznie z numeracją lub roboczym stornem. | Rollback przy własnej transakcji i czyszczenie stanu EF Core; test odmowy storna w zamkniętym okresie i odmowy księgowania nieistniejącego rozrachunku. |

**Ważne:** w przypadku transakcji należącej do zewnętrznego wywołującego, to wywołujący odpowiada za rollback i odrzucenie niepoprawnego `DbContext`. Przegląd nie obejmuje wszystkich analogicznych ścieżek modułów biznesowych.

## 3. Stan najważniejszych luk po poprawkach — drugi przegląd

| Priorytet | Obszar | Stan i warunek zamknięcia |
|---|---|---|
| **P0 / blokuje produkcję** | Model zaufania PostgreSQL | Operator nadal może mieć bezpośredni `INSERT/UPDATE/DELETE` do tabel biznesowych, omijając reguły autoryzacji aplikacji. PR #24 chroni audyt i migracje tylko częściowo. Potrzebny zaufany backend/API lub równoważna serwerowa autoryzacja; test odmowy nieuprawnionego DML i próba penetracyjna. |
| **P1** | Wynagrodzenia i zasiłki | Nie ma kompletnego kalkulatora historycznej podstawy i składników zmiennych, wszystkich świadczeń ZUS, okresu wyczekiwania i wieloodcinkowych absencji. Trzeba potwierdzić przypadki z art. 36–43 ustawy zasiłkowej na dokumentach i niezależnych wzorcach. |
| **P1** | JPK_CIT / JPK_ST_KR | Jest `JPK_KR_PD`, nie ma generatora `JPK_ST_KR`. MF ogłosiło 30.10.2026 podpisanie czasowego zwolnienia dla określonych podatników i lat 2026–2028; przed stosowaniem należy sprawdzić **ogłoszenie, datę wejścia w życie i kwalifikację konkretnej firmy**. Samo podpisanie nie oznacza obowiązywania zwolnienia. |
| **P1** | KSeF i deklaracje | W GUI KSeF obsługa odbioru faktur zakupowych; sprzedaż wystawia się poza programem. Potrzebne potwierdzone scenariusze E2E, walidacja najnowszych schematów i porównanie wyników VAT/CIT/PIT/ZUS z niezależnym źródłem. |
| **P1** | Ciągłość pracy | Brak potwierdzonej procedury awaryjnego przywrócenia produkcyjnego PostgreSQL, historii ksiąg i załączników na osobnym serwerze; konieczny test odtworzenia oraz określenie RPO/RTO. |
| **P1** | Ochrona danych / RODO | Konieczna niezależna analiza uprawnień danych kadrowych, dostępu do informacji o nieobecnościach, retencji, kopii, eksportów i audytu odczytów. |
| **P2** | Walidacje brzegowe i dokumentacja | Kontynuować testy błędów, zmian prawa i przegląd formalnych polityk jednostki. Poprawić rozbieżności dokumentacji konfiguracji SQLite/PostgreSQL, jeśli występują. |

## 4. Powtórna kontrola po wdrożeniu PR #30

Przed zmianą statusu któregokolwiek z A1–A3 na **zweryfikowane** należy:
1. Sprawdzić wynik CI **dokładnego SHA** PR #30 na Windows i Linux (nie tylko wcześniejszego commita).
2. Scalić PR #30, uruchomić testy **scalonego `main`**, potwierdzić integralność dziennika i brak regresji storna/korekty/rozrachunków.
3. Powtórzyć próby: sztuczne ujemne storno, błędny dekret poprawny, nieistniejący rozrachunek, zamknięty okres, następny niezależny `SaveChangesAsync`, numeracja dziennika po rollbacku.
4. Sprawdzić, czy jakikolwiek publiczny interfejs nadal umożliwia oznaczanie zwykłego dekretu jako zamknięcia roku lub bilansu otwarcia bez właściwej autoryzacji; to **otwarty punkt audytu**.
5. Zachować brak gotowości produkcyjnej do czasu zamknięcia P0 i pełnego planu akceptacji.

## 5. Podstawy i materiały

- Ustawa o rachunkowości, tekst jednolity Dz.U. 2026 poz. 522, zwłaszcza art. 20–25: https://eli.gov.pl/eli/DU/2026/522
- Ministerstwo Finansów, struktury JPK_PD i aktualizacje JPK_KR_PD/JPK_ST_KR: https://podatki.gov.pl/podatki-firmowe/jednolity-plik-kontrolny/jpk_pd/pliki-do-pobrania-jpk_pd
- Ministerstwo Finansów, komunikat 30.10.2026 o podpisanym rozporządzeniu JPK_ST_KR: https://www.gov.pl/web/kas/rozporzadzenie-w-sprawie-czasowego-zwolnienia-w-cit-z-obowiazkow-w-zakresie-jpk-cit-dotyczacych-ewidencji-srodkow-trwalych-i-wartosci-niematerialnych-i-prawnych
- Szczegółowe istniejące ograniczenia programu: `README.md`, `docs/SECURITY.md`, `docs/REVIEW_2026-11-01.md`.

**Wniosek:** poprawki zwiększają bezpieczeństwo i rzetelność, lecz **nie jest możliwe uznanie programu za w pełni gotowy do prowadzenia rzeczywistej księgowości** bez zamknięcia ryzyk P0/P1 i niezależnej weryfikacji. Nie wolno zastępować tego warunku deklaracją „100% zgodności”.
