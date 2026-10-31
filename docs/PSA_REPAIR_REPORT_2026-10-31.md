# Naprawy P.S.A. — 31.10.2026

## Zakres i gałąź

Na aktualne polecenie użytkownika prace prowadzone są na `main`, wyłącznie w zakresie P.S.A.
i wspólnych usług potrzebnych do jej księgowości. Numer aplikacji pozostaje **1.0.0**.
Wcześniejsza gałąź `fix/post-merge-audit-readme-20261031` nie jest scalana w całości.
Z niej przeniesiono przejrzane poprawki izolowanych autotestów kreatora i testów operatorów.
Nie jest to pełny audyt ani potwierdzenie gotowości produkcyjnej.

## Zmiany

| Obszar | Poprawka i dowód |
| --- | --- |
| Kapitał P.S.A. | `PostEventAsync` ponownie weryfikuje wymagane dokumenty i potwierdzenie dopuszczalności wypłaty, również po wcześniejszym zatwierdzeniu. Odrzuca ujemne kwoty i podatek przewyższający wypłatę. `PsaCapitalPostingTests`: cofnięte potwierdzenie, usunięta uchwała, podatek 501 zł przy wypłacie 500 zł; brak nowego zapisu w dzienniku. |
| Załączniki | Każdy zapis dostaje własny plik tymczasowy, tworzony bez nadpisania cudzego pliku. Migracja nie usuwa cudzych plików tymczasowych. Test dwóch magazynów z tym samym kluczem, zapisujących równocześnie tę samą ścieżkę, sprawdza pełny odczyt po odszyfrowaniu i sprzątnięcie plików roboczych. Przy tej samej ścieżce nadal obowiązuje ostatni zakończony zapis. |
| KSeF | Nierozstrzygnięta wysyłka z zapisanym numerem referencyjnym blokuje kolejną wysyłkę. Test sprawdza brak wywołań HTTP i zachowanie statusu. Nie rozwiązuje awarii przed zapisaniem numeru ani równoległych pierwszych wysyłek. |
| Pobieranie XSD | Kontrola granicy domeny odrzuca np. `attacker-gov.pl`; dopuszczone są HTTP/HTTPS w `gov.pl` i subdomenach. Test sprawdza odmowę przed żądaniem HTTP. Nie jest to pełny audyt przekierowań, importów ani aktualności schematów. |
| Workflow XSD | Przerwanie/błąd kroku pobierania nie uruchamia już zapisu częściowych rezultatów przez `if: always()`. Same skrypty nadal potrafią raportować część problemów wyłącznie w logu. |
| CI / kreator | Izolowane autotesty używają własnego SQLite, podczas gdy normalne otwarcie ksiąg wymaga PostgreSQL. Test CLI wstrzykuje izolowaną sesję przez metodę wewnętrzną; publiczne uruchomienie CLI nie udostępnia przełącznika omijającego wymaganie PostgreSQL. |
| Dokumentacja | Usunięto twierdzenia o stałym monitorowaniu XSD, niepublikowaniu schematów przez MF oraz o tym, że każdy zasiłek wypłaca ZUS. Oddzielono warstwę usług KSeF od funkcji interfejsu i prób na atrapie od serwera MF. |

## Wyniki weryfikacji

- Punkt wyjścia na wcześniejszej gałęzi: 298 testów, 292 zaliczone, 2 błędy, 4 pominięte.
- Błędy: test oczekiwał nieaktualnego komunikatu o statusie CIT spółki jawnej; test CLI próbował otwierać izolowaną SQLite publiczną ścieżką wymagającą PostgreSQL.
- Po pierwszych poprawkach testy ukierunkowane: **8/8**, bez pominięć (kreator, CLI, KSeF, współbieżny zapis załączników).
- Pełny przebieg na `main` przed dodaniem trzech testów kapitału P.S.A.: **301 zaliczonych, 4 pominięte, 0 błędów** (305 łącznie).
- Końcowy przebieg po dodaniu testów kapitału P.S.A.: **304 zaliczone, 4 pominięte, 0 błędów** (308 łącznie), `dotnet test PelnaKsiegowosc.sln -c Release --no-restore`, Windows / .NET SDK 10.0.403. CI dla nowych zmian nie uruchomiono: wysłanie na main oczekuje na wyraźne zatwierdzenie publikacji.
- Porównano z CRWDE 11 schematów głównych wskazanych w `SchemaLibrary`: JPK_V7M/K, sześć wariantów e-sprawozdań, VAT-UE/K i PIT-11. **11/11 zgodnych po kanonikalizacji XML i usunięciu białych znaków brzegowych**. Nie jest to zgodność binarna (m.in. końce linii), kontrola wszystkich importów ani dowód braku nowszego wzoru. [Adresy, SHA-256 pobranych plików i pasujące pliki lokalne](XSD_CRWDE_VERIFICATION_2026-10-31.json).
- Lokalnie brak skonfigurowanych usług PostgreSQL/S3, dlatego ich testów nie wolno zaliczać jako wykonanych. CI Linux przygotowuje PostgreSQL oraz atrapę S3; CI Windows ich nie przygotowuje.

## Nieusunięte ograniczenia dotyczące P.S.A.

| Priorytet | Obszar | Co pozostaje |
| --- | --- | --- |
| P0 | Uprawnienia SQL | Role w aplikacji nie zapewniają izolacji wobec bezpośredniego DML. Brak zweryfikowanej kompletnej polityki uprawnień SQL i prób z kontami operatorów o minimalnych prawach. Niezmienność zaksięgowanych zapisów nie chroni wszystkich tabel i ról. |
| P0 | Płace i zasiłki | Nadal nie ma kompletnego modelu podstaw historycznych, premii kwartalnych/rocznych, zmian etatu, odcinków choroby, wszystkich zbiegów i płatnika zasiłków. Zabezpieczenia blokujące nie są pełnym naliczaniem. |
| P0 | Kapitał i wypłaty P.S.A. | Brak automatycznej pełnej kontroli kwoty do podziału, strat, akcji własnych, wymaganych rezerw, zasilania kapitału z art. 300^19, procedur ochrony wierzycieli i prognozy na 6 miesięcy. Potwierdzenie operatora wymaga dokumentacji poza programem. Kontrola przed księgowaniem nie wiąże kryptograficznie zatwierdzenia z całą treścią zdarzenia ani nie zastępuje ochrony przed równoległą zmianą. |
| P1 | Rejestr akcjonariuszy | Moduł księgowy nie zastępuje rejestru prowadzonego przez podmiot uprawniony. Wkłady pracy/usług i ich skutki składkowe/podatkowe wymagają odrębnej obsługi. |
| P1 | CIT | Zwykły CIT i odrębne zyski kapitałowe są testowane w zestawie, ale brak niezależnej kompletnej próby referencyjnej P.S.A., WHT, zwolnień dywidendowych i szczególnych przypadków. Nie deklaruje się obsługi estońskiego CIT na podstawie tych zmian. |
| P1 | VAT/JPK/e-sprawozdanie | XSD nie dowodzi poprawności kwot ani wyboru właściwego wzoru i okresu. Nadal potrzebne scenariusze referencyjne, szczególne momenty VAT, JPK_ST_KR i niezależna kontrola e-sprawozdań. Nie potwierdzono aktualności wszystkich dołączonych XSD na dzień raportu. |
| P1 | KSeF | Brak potwierdzonego pełnego przebiegu na TEST MF, pełnej obsługi awarii/offline, wznowienia statusu/UPO i deduplikacji po utracie odpowiedzi przed zapisem numeru. Wysyłka nie staje się przez tę poprawkę funkcją interfejsu. |
| P1 | Odtwarzanie i współbieżność | Potrzebny pełny drill odtworzenia PostgreSQL do nowej instancji, próby awarii, równoczesnej numeracji i księgowania oraz spójnego backupu bazy i plików. |
| P1 | Załączniki sieciowe | Próba dwóch instancji na lokalnym systemie plików nie potwierdza zachowania SMB/NFS, blokad, uprawnień ani pracy po utracie sieci. Pliki tymczasowe po twardej awarii wymagają ostrożnej administracyjnej obsługi. |
| P1 | Archiwizacja | PDF i indeks SHA-256 nie zastępują oryginalnych XML/faktur, chronionego indeksu, podpisu ani retencji. |

## Źródła kontrolne

- [KSH, tekst jednolity — art. 300^3, 300^15 i 300^19](https://eli.gov.pl/api/acts/DU/2024/18/text.html): kapitał, ograniczenia wypłat, sześciomiesięczna wypłacalność i obowiązek zasilania kapitału. To wskazanie podstawy kontroli, nie opinia o kompletności stanu prawnego całego programu.
- [ZUS — płatnicy zasiłku](https://www.zus.pl/en/swiadczenia/zasilki/zasilek-chorobowy/postepowanie-w-sprawie-zasilkow/platnicy-zasilku): rozróżnienie finansowania świadczenia i jego wypłaty; znaczenie liczby ubezpieczonych.
- [MF — dokumentacja IT e-Deklaracji](https://podatki.gov.pl/e-deklaracje-zloz-deklaracje-podatkowa-online/bramka-ubd-i-dokumentacja-it): publiczne struktury i dokumentacja.
- [MF — changelog API KSeF](https://github.com/CIRFMF/ksef-api/blob/main/api-changelog.md): dokumentacja zmian; jej odczyt nie stanowi testu integracji programu.
