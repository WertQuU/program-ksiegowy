# Ponowny audyt main — Pełna Księgowość 1.0.0

**Data przeglądu:** 31.10.2026  
**Gałąź:** main  
**Punkt odniesienia (kod po integracji i poprawkach):** 3e60c9eefe4231158e1ee6df6d6a2fc6121343b1  
**Charakter oceny:** przegląd statyczny wybranych plików krytycznych, wcześniejszych raportów, konfiguracji GitHub oraz aktualnych oficjalnych źródeł prawa. Nie jest certyfikacją, testem penetracyjnym, wynikiem uruchomienia wszystkich modułów ani opinią prawną. Wnioski niewykazane testem oznaczono jako niepotwierdzone.

## 1. Wynik i zakres

**Werdykt: nie potwierdzono gotowości produkcyjnej i pełnej zgodności z prawem.** Wersja pozostaje **1.0.0**. Zgoda na prace na main nie oznacza automatycznej zgody na wydanie produkcyjne. Nie wolno opierać niezweryfikowanych deklaracji, list płac ani sprawozdań wyłącznie na wynikach programu.

Przejrzane elementy obejmują: kontrolę operacji i numeracji ksiąg, ochronę niezmienności zapisów, PostgreSQL i SQLite/SQLCipher, TLS, operatorów, kopie/odtwarzanie i załączniki, szyfrowanie i przechowywanie kluczy, KSeF, import XSD, generator JPK, wybrane usługi VAT/CIT/płac/kapitału, logi błędów, testy oraz workflow wydaniowe. Pełny test wszystkich ścieżek, migracji i przypadków podatkowych pozostaje otwarty.

**Klasyfikacja:** P0 — blokada użycia funkcji bez dodatkowej weryfikacji lub naprawy; P1 — ważny problem/niepewność do zamknięcia przed produkcją; P2 — pozostałe usprawnienia.

## 2. Problemy P0

| ID | Obszar | Ustalenie | Dowód w repozytorium | Wymagane działania |
|---|---|---|---|---|
| P0-01 | Autoryzacja SQL | Reguły aplikacji i filtr EF nie zabezpieczają przed bezpośrednim DML operatora posiadającego zbyt szerokie prawa SQL. | src/PelnaKsiegowosc.Application/Security/OperatorAccessService.cs; src/PelnaKsiegowosc.Data/KsiegiDbContext.cs | Osobne role administracji/migracji/operatorów; minimalne uprawnienia; polityka RLS lub bezpieczna warstwa API; test bezpośrednich SQL z kont operatorskich |
| P0-02 | Płace i zasiłki | Brak kompletnych zasad wyliczeń podstaw historycznych, premii zmiennych i kwartalnych, zmian etatu i wszystkich szczególnych zbiegów. Blokady części przypadków nie zastępują kompletnego algorytmu. | src/PelnaKsiegowosc.Application/Payroll/PayrollService.cs; docs/AUDYT_ROZSZERZONY_2026-10-31.md | Model okresów i źródeł danych, blokady nieobsługiwanych przypadków, niezależne porównanie z przykładami ZUS |
| P0-03 | P.S.A. | Potwierdzenie dokumentów kapitałowych nie zapewnia automatycznego ustalenia dopuszczalności wypłat, rezerw, straty i prognozy wypłacalności. | src/PelnaKsiegowosc.Application/Capital/CapitalService.cs; docs/PSA_REPAIR_REPORT_2026-10-31.md | Odrębne zatwierdzenie prawne i finansowe, reguły art. 300(15) i 300(19) KSH, dane uchwał i zamkniętych sprawozdań |
| P0-04 | Bramka jakości | Brak niezależnej pełnej kontroli poprawności merytorycznej wszystkich wyliczeń księgowych i podatkowych na zbiorze referencyjnym. | docs/TEST_PLAN.md; docs/COMPLIANCE_MATRIX.md | Porównanie z urzędowymi wzorami i specjalistą, protokoły wyników, brak otwartych błędów krytycznych |

P0-01 ma charakter systemowy: to, że triggery blokują część modyfikacji, nie zastępuje ścisłej polityki uprawnień do pozostałych tabel, ról, funkcji i danych innej jednostki.

## 3. Problemy P1 — bezpieczeństwo, niezawodność i dane

| ID | Ustalenie | Dowód | Test naprawczy / kryterium |
|---|---|---|---|
| P1-01 | Eksport bazy, sum i załączników w różnych momentach może prowadzić do niespójnego backupu przy równoczesnych zapisach. | Infrastructure/Backup/BackupService.cs:75–144 | Transakcyjna migawka, spójny punkt odniesienia załączników; test współbieżnego zapisu podczas kopii |
| P1-02 | Odzyskiwanie PostgreSQL i załączników nie jest jednym atomowym przełączeniem kompletnego zestawu. | Infrastructure/Backup/BackupService.cs:438–451 | Restore do izolowanego środowiska, kontrola manifestu, sum, sald i plików, dopiero potem przełączenie |
| P1-03 | Zmiana jawnej SQLite na SQLCipher nadpisuje oryginał przed końcowym przeniesieniem nowej bazy. | Data/SqliteEncryption.cs:41–79 | Bezpieczna procedura commit/rollback i testy przerwania zasilania oraz WAL |
| P1-04 | Lokalny magazyn sekretów Linuksa może przechowywać klucz i szyfrogram w tym samym katalogu konta. | Infrastructure/Secrets/SecretStores.cs:110–154 | Preferowany magazyn systemowy, zasady rotacji i odzyskania kluczy, szyfrowanie nośnika |
| P1-05 | TLS PostgreSQL dopuszcza zdalny tryb Require bez pełnej weryfikacji nazwy serwera. | Data/DatabaseProfile.cs:34–40, 88–115 | Produkcyjne VerifyFull oraz testy błędnego CA/hosta |
| P1-06 | Weryfikacja przez skróty SHA-256 nie chroni przed pełnym administratorem, który może przeliczyć skróty. | Data/ImmutabilityTriggers.cs; Application/Ledger/LedgerService.cs | Niezależnie utrwalane odciski stanu i dziennik audytu poza bazą |
| P1-07 | Potrzebne próby konfliktów równoległej numeracji, storna i zarachowania CIT. | Application/Ledger/NumberingService.cs; Data/KsiegiDbContext.cs | Testy wieloprocesowe PostgreSQL, idempotencja i rollback |
| P1-08 | Nie potwierdzono retencji i ochrony wszystkich typów danych, eksportów i kopii w ujęciu RODO. | Application/Security/PersonalDataService.cs; docs/SECURITY.md | Udokumentowana macierz retencji, osoby upoważnione, usuwanie i legal hold |
| P1-09 | Brak potwierdzonego pełnego testu przywrócenia produkcyjnego PostgreSQL na odrębną instancję. | docs/TEST_PLAN.md | Odtworzenie z nieprawidłowym kluczem, uszkodzonym archiwum, brakiem miejsca i urwanym transferem |
| P1-10 | Zakres uprawnień repozytorium: main pozostaje bez ochrony gałęzi. | GitHub API branches/main | Branch protection, wymagane checki i recenzje dla zwykłych zmian, kontrolowane wyjątki |

## 4. Problemy P1 — prawo rachunkowe i podatki

| ID | Ustalenie | Warunek zakończenia |
|---|---|---|
| LEG-01 | Potrzebny scenariusz pełnego roku z każdą kategorią kont, kontami pomocniczymi, BO, bilansem, RZiS, inwentaryzacją i korektami. | Niezależne zestawienie kontrolne, suma Wn/Ma, powiązania dowód-zapis, uzgodnienie sprawozdania |
| LEG-02 | Kontrola zdarzeń powodujących otwarcie/zamknięcie ksiąg z art. 12 UoR nie jest kompleksowo potwierdzona. | Testy roku oraz likwidacji, połączenia i zdarzeń szczególnych |
| LEG-03 | Polityka rachunkowości, plan kont i opis IT z art. 10 UoR muszą być kompletne i rzeczywiście przyjęte dla konkretnej jednostki. | Wersjonowanie dokumentacji i data rozpoczęcia stosowania programu |
| TAX-01 | XSD JPK i e-sprawozdania nie dowodzi prawidłowości kwot, znaczników, kwalifikacji oraz doboru okresu. | Walidacja na schemacie i merytoryczne porównanie do wzorców MF |
| TAX-02 | JPK_ST_KR: MF 30.10.2026 podało podpisanie regulacji o czasowym zwolnieniu wybranych podmiotów za lata 2026–2028; w analizie nie potwierdzono publikacji w Dz.U. | Zweryfikować ogłoszenie, datę wejścia, podmioty i zakres; generator/eksport dla niezwolnionych |
| TAX-03 | CIT, WHT, rozliczenia międzynarodowe i korekty historyczne nie mają kompletnych wyników referencyjnych. | Porównanie pełnych zestawów danych i zgodność z obowiązującymi normami |
| TAX-04 | KSeF ma istniejący klient wysyłki, ale bez pełnego udowodnionego procesu odzyskania nierozstrzygniętego statusu i pobrania UPO. | Testy na TEST MF i ręczne uzgodnienie przy braku numeru referencyjnego |
| TAX-05 | Wszystkie nieobsługiwane przypadki kadrowo-płacowe muszą być bezpiecznie blokowane, a nie wyliczane na domyślnych założeniach. | Matryca przypadków i niezależny podpis kontrolującego |

## 5. Poprawki wykonane bezpośrednio w main po integracji

1. PR #8 scalono własnym commitem dwurodzicowym, rozstrzygając konflikt README z zachowaniem nowszych korekt merytorycznych. Pozostałych pięć plików PR miało już identyczną zawartość na main.
2. KSeF: POST faktury nie jest automatycznie ponawiany; przed transmisją utrwalany jest stan próby i atomowa rezerwacja faktury, chroniąca przed równoległymi próbami. Dodany test utraty odpowiedzi po odebraniu dokumentu; test współbieżności dwóch instancji nadal wymagany.
3. XSD: wymuszony HTTPS dla oficjalnych domen, podnoszenie historycznych adresów importów HTTP do HTTPS, limit 8 MiB pojedynczego pliku i 64 MiB/128 plików w ZIP, zakaz DTD, testy nadużyć.
4. Logi: zapisywany tylko typ wyjątku, bez treści i stosu mogących ujawnić dane/sekrety; prywatne uprawnienia logów na Linuksie; test regresyjny.
5. GitHub Actions: trzy automatyczne aktualizatory schematów nie zapisują pobranych plików bezpośrednio do repozytorium. Publikują artefakty workflow do ręcznej kontroli i oddzielnej integracji. Próba automatycznego PR została wycofana, ponieważ token workflow nie ma uprawnień do publikowania gałęzi zawierających zmiany plików workflow.

**Nie zaliczać tych zmian jako ostatecznie potwierdzonych przed ukończeniem CI dla końcowego SHA.** CI wykryło i pozwoliło poprawić błąd kompilacji przy użyciu uprawnień Unix; wymagany jest ponowny zielony przebieg. Działania na poziomie polityki GitHub i serwera PostgreSQL wymagają również konfiguracji poza kodem.

## 6. Scenariusze bezpieczeństwa do wykonania

- Bezpośrednie połączenie psql kontem operatora; próby odczytu innych jednostek, zmian w Users/AuditEvents, podmiany wpisów zatwierdzonych, migracji oraz obejścia triggerów.
- 20 jednoczesnych żądań księgowania, reset połączenia i awaria między rezerwacją numeru a zapisaniem łańcucha hash.
- PostgreSQL z błędnym certyfikatem, CA, nazwą serwera oraz próbą wymuszenia Require zamiast VerifyFull.
- Niekompletna kopia, częściowo zapisany ZIP, zły klucz, naruszenie sum, brak załącznika, brak miejsca, przerwane S3 multipart.
- Odtwarzanie do nowej instancji: porównanie liczby rekordów wszystkich tabel, Wn/Ma per rok, hash ostatniego zapisu, dokumentów, rozrachunków i plików załączników.
- Sfałszowane XSD: HTTP, inna domena, niebezpieczne przekierowanie, DTD, masowe importy i rozpakowanie ZIP przekraczające limity.
- KSeF: timeout przed wysyłką, utrata odpowiedzi po przyjęciu, odrzucenie, status nieustalony, ponowne uruchomienie i pobranie UPO.
- RODO: dostęp do danych osobowych bez uprawnienia, dostęp do logów, niepoprawna retencja oraz odczyt danych z kopii po ich usunięciu z bieżącej bazy.

## 7. Kolejność prac

**Etap A (bezpieczeństwo P0):** minimalne uprawnienia SQL, RLS lub API, izolacja jednostek, egzekwowanie zakazu DML, testy psql. Wyłączyć funkcje płacowe/kapitałowe dla przypadków niezweryfikowanych.

**Etap B (integralność i odtwarzanie):** spójna migawka bazy/załączników, testy współbieżności, niezmienność archiwów, kompletne odtworzenie, polityka kluczy oraz retencji.

**Etap C (zgodność prawna):** niezależne porównanie pełnego roku księgowego, JPK, CIT, VAT, e-sprawozdania, kadr i zdarzeń P.S.A.; weryfikacja aktualnych schematów i terminów obowiązków.

**Etap D (wydanie):** zielony CI dla dokładnego SHA, skany zależności, SAST, kontrola sekretów, SBOM, uprawnienia branch protection, testy instalacji Windows/Linux, scenariusz wycofania.

## 8. Kryteria przyjęcia

- Brak otwartych P0 w zakresie udostępnianym produkcyjnie.
- P1 zamknięte albo funkcja świadomie wyłączona, z udokumentowaną kontrolą ręczną tylko tam, gdzie to dopuszczalne.
- Raporty z testów integracyjnych, bezpieczeństwa i odtwarzania.
- Zatwierdzona dokumentacja jednostki z art. 10 UoR, okresami, retencją i uprawnieniami.
- Niezależna ocena merytoryczna wszystkich generowanych deklaracji i wyników podatkowych.
- Kontrolowany proces publikacji bez zwiększania wersji 1.0.0, dopóki właściciel nie zdecyduje inaczej.

## 9. Źródła urzędowe

- Ustawa o rachunkowości, tekst jednolity Dz.U. 2026 poz. 522, z późniejszymi zmianami: https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU20260000522
- JPK_V7M(3)/JPK_V7K(3), źródła MF: https://podatki.gov.pl/podatki-firmowe/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/pliki-do-pobrania
- JPK w podatkach dochodowych: https://www.gov.pl/web/kas/struktury-jpk-w-podatkach-dochodowych
- Komunikat MF z 30.10.2026 o zwolnieniu JPK_ST_KR: https://www.gov.pl/web/finanse/rozporzadzenie-w-sprawie-czasowego-zwolnienia-w-cit-z-obowiazkow-w-zakresie-jpk-cit-dotyczacych-ewidencji-srodkow-trwalych-i-wartosci-niematerialnych-i-prawnych
- KSeF 2.0, etapy: https://ksef.podatki.gov.pl/etapy-wdrozenia-ksef/
- Dokumentacja API KSeF: https://api.ksef.mf.gov.pl/
- RODO: https://eur-lex.europa.eu/eli/reg/2016/679/oj?locale=pl
- OWASP ASVS: https://owasp.org/www-project-application-security-verification-standard/

Weryfikację prawną należy aktualizować na dzień użycia konkretnej funkcji. Nie zastępować kodem interpretacji prawa przez osobę odpowiedzialną za księgi.
