# Kompleksowy cykl audytu technicznego i prawnego — wyłącznie `main`

**Data kontroli:** 1 listopada 2026 r.  
**Repozytorium:** `WertQuU/program-ksiegowy`  
**Wersja aplikacji:** 1.0.0 — bez podnoszenia numeru.  
**Zasada pracy:** zmiany kodu, testów i niniejszego dokumentu wprowadzano **bezpośrednio do `main`**. Nie otwierano nowych gałęzi i PR-ów w tym cyklu.  
**Ważne:** wynik audytu repozytorium nie jest certyfikatem zgodności z prawem. Do dopuszczenia produkcyjnego potrzebne są testy dokładnego końcowego SHA, weryfikacja na realnym środowisku i niezależny przegląd merytoryczny.

## 1. Dopracowanie istniejących błędów i problemów

Punkt wyjścia: `main` po scaleniu PR #23–#34. Weryfikowano kod odpowiedzialny za trwałość ksiąg, zamknięcie roku, zakres uprawnień i ewidencję aktywów. Stwierdzono m.in.:

1. **Wycena historyczna środków trwałych** traktowała naliczony odpis miesięczny tak, jakby obowiązywał od pierwszego dnia miesiąca, chociaż księgowanie AM ma datę końca miesiąca. Przed nabyciem środka metoda mogła wykazywać wartość początkową, a po pełnym rozchodzie pozostawiać niezerową wartość.
2. **Ulepszenia z datą wsteczną** mogły zmieniać wartość podstawy amortyzacji za okres, w którym istniały już naliczone odpisy, bez korekty historii.
3. **Bezpośrednie księgowanie** dodawało nowy dekret do `DbContext` przed pełnym sprawdzeniem zatwierdzonej polityki rachunkowości, planu kont i okresu. Odrzucenie operacji mogło pozostawić dekret w stanie `Added`.
4. **Dopasowanie operacji bankowych** weryfikowało kwoty odrębnych przydziałów do tego samego rozrachunku, a nie ich sumę, co umożliwiało zatwierdzenie łącznej kwoty przekraczającej saldo.
5. **Referencje między jednostkami**: część operacji bankowych i kapitałowych czytała powiązany rachunek, wyciąg, kontrahenta lub akcjonariusza tylko po identyfikatorze, bez dodatkowego warunku `CompanyId`.
6. **Błędne zaksięgowanie operacji bankowej** mogło zostawiać zmodyfikowany stan śledzony przez EF Core pomimo wycofania nieudanej transakcji, zwiększając ryzyko utrwalenia niekompletnej operacji przy późniejszym zapisie.

## 2. Audyt techniczny i wdrożone poprawki

| Obszar | Wdrożona zmiana w `main` | Kontrola |
|---|---|---|
| Wycena ŚT | `FixedAssetService.ValuesAsOfAsync`: wartości zero przed nabyciem i po rozchodzie; miesięczne odpisy dopiero po ostatnim dniu miesiąca; zakres firmy i podstawowa kontrola odczytu. | Test przed nabyciem, 1. i ostatni dzień miesiąca, sprzedaż, likwidacja. |
| Ulepszenia ŚT | Data nie może poprzedzać oddania do używania; wymagana podstawa opisowa; brak wstecznego ulepszenia wpływającego na już naliczone miesiące. | Test korekty wstecznej i dopuszczalnego ulepszenia wpływającego na kolejny miesiąc. |
| Dziennik | `LedgerService.CreateAndPostCoreAsync` wykonuje pełną walidację zapisu **przed** dodaniem go do EF Core. Walidacja została wydzielona do metody wspólnej z księgowaniem zapisu z bufora. | Test odrzuconej polityki oraz zamkniętego okresu i następnego, niezwiązanego `SaveChanges`. |
| Bank | `BankService.MatchAsync` sumuje przydziały według `OpenItemId`; sprawdza ich łączny wpływ na saldo. | Test wielokrotnego wskazania tego samego rozrachunku. |
| Bank — izolacja | Konta, wyciągi, rozrachunki i kontrahenci operacji kasowych ograniczone do aktywnej jednostki. | Test odrzucenia obcego kontrahenta i braku osieroconej operacji. |
| Bank — rollback | `PostTransactionAsync` wycofuje własną transakcję po błędzie i czyści zmiany śledzone przez EF Core. | Wymagany ponowny test integracyjny procesu zapłaty i aktualizacji statusu dokumentu. |
| Kapitały — izolacja | `CapitalService.CreateEventAsync` i `PostEventAsync` wymagają zgodności jednostki akcjonariusza i powiązanego kontrahenta. | Test obcego akcjonariusza i podmienionego kontrahenta. |

**Granica poprawki:** prewalidacja dekretu usuwa klasę błędów polegających na przedwczesnym dodaniu niepoprawnego wpisu, ale **nie dowodzi atomowości wszystkich operacji biznesowych** wykonywanych przez wywołujących `CreateAndPostAsync`. Nadal należy audytować rollback dla operacji rozrachunkowych wykrywanych po rozpoczęciu numeracji.

## 3. Praca nad kodem i testami regresyjnymi

Zmodyfikowano:
- `src/PelnaKsiegowosc.Application/FixedAssets/FixedAssetService.cs`
- `src/PelnaKsiegowosc.Application/Ledger/LedgerService.cs`
- `src/PelnaKsiegowosc.Application/Banking/BankService.cs`
- `src/PelnaKsiegowosc.Application/Capital/CapitalService.cs`

Dodano testy:
- `tests/PelnaKsiegowosc.Tests/Modules/FixedAssetAsOfAndImprovementTests.cs`
- `tests/PelnaKsiegowosc.Tests/Engine/DirectPostingMutationSafetyTests.cs`
- `tests/PelnaKsiegowosc.Tests/Modules/BankScopeAndAllocationSafetyTests.cs`
- `tests/PelnaKsiegowosc.Tests/Modules/CapitalCompanyIsolationTests.cs`

Najważniejsze commity zmian kodu: `b9fe3ec`, `b6c93a5`, `de1e6e4`, `584aad8`, `983858a`. Testy i ich korekty mają odrębne commity. Pełny wynik CI należy odczytać dla **końcowego commita**, nie dla wcześniejszego zatwierdzenia.

## 4. Audyt techniczno-prawny

### 4.1. Ustawa o rachunkowości

- Art. 20–25 UoR: wymagają dowodów źródłowych, kompletności, sprawdzalności i trwałości zapisów, właściwej daty, oraz dopuszczalnej drogi korekty po zamknięciu miesiąca. Poprawki związane z rejestrowaniem i odrzucaniem dekretów zmniejszają ryzyko zapisania operacji mimo odmowy. **Nie zastępują** pełnej kontroli dowodów źródłowych, procesu korekt, zamknięcia ksiąg i kontroli dostępu do bazy.
- Art. 71–72 UoR: wymagają ochrony dokumentacji, tworzenia kopii zapasowych, trwałości zapisu i ochrony przed zmianami i dostępem nieuprawnionych. Samo szyfrowanie lokalnego pliku, TLS i pomyślny test jednostkowy nie oznaczają spełnienia wymogów w środowisku docelowym.
- Operacje historycznej wyceny środków trwałych są krytyczne dla rzetelności raportów. Regresje techniczne ograniczają błędy, ale wycena wymaga porównania z przyjętą polityką rachunkowości, harmonogramami i dokumentacją.

**Źródło:** https://eli.gov.pl/eli/DU/2026/522/ogl/pol (tekst jednolity Dz.U. 2026 poz. 522; należy uwzględniać późniejsze zmiany).

### 4.2. JPK_KR_PD, JPK_ST_KR i podatki dochodowe

- Repozytorium nie zawiera kompletnego generatora **JPK_ST_KR**; samo JPK_KR_PD nie zastępuje wszystkich potencjalnych obowiązków.
- MF ogłosiło 30.10.2026 podpisanie 29.10.2026 rozporządzenia o czasowym zwolnieniu niektórych podmiotów CIT z obowiązków dotyczących JPK_ST_KR dla lat rozpoczynających się po 31.12.2025 i przed 01.01.2029. Informacja o **podpisaniu** nie jest równoznaczna z ogłoszeniem i wejściem w życie; MF wskazało wejście w życie z dniem następującym po ogłoszeniu w Dzienniku Ustaw.
- Dla każdego podatnika trzeba oddzielnie zweryfikować rodzaj podatku, lata, obowiązek JPK_V7M/V7K i **faktyczną publikację oraz brzmienie aktu prawnego**. Nie wyłączać generatora ani alarmów na podstawie zapowiedzi zwolnienia.
- Obliczenia CIT/VAT/PIT/ZUS nadal wymagają niezależnych wzorców i weryfikacji aktualnych schematów.

**Źródła:**
- https://www.gov.pl/web/kas/struktury-jpk-w-podatkach-dochodowych
- https://podatki.gov.pl/podatki-firmowe/jednolity-plik-kontrolny/jpk_pd/pliki-do-pobrania-jpk_pd
- https://www.gov.pl/web/kas/rozporzadzenie-w-sprawie-czasowego-zwolnienia-w-cit-z-obowiazkow-w-zakresie-jpk-cit-dotyczacych-ewidencji-srodkow-trwalych-i-wartosci-niematerialnych-i-prawnych

### 4.3. Kadry, płace i zasiłki

Nie zakończono zgodnej ze wszystkimi przypadkami implementacji historycznej podstawy świadczeń, składników zmiennych, okresów wyczekiwania, zasiłków, korekt e-ZLA i zmian wymiaru zatrudnienia. Otwarte issue **#3 i #5**. Należy przygotować niezależne testy na danych z kilku okresów zatrudnienia i wynikach potwierdzonych przez specjalistę kadr i płac.

### 4.4. Bezpieczeństwo danych i uprawnienia

Otwarte **P0 — issue #35**: operator posiadający bezpośrednie uprawnienia SQL do tabel biznesowych może ominąć aplikacyjne role, walidację i część zasad ochrony ksiąg. Ograniczenie zapytań EF przez `CompanyId` jest poprawką warstwy aplikacji, ale **nie zamyka P0**. Niezbędny jest model autoryzacji kontrolowany przez zaufaną usługę po stronie serwera, bez udostępniania operatorowi bezpośredniego zapisu do tabel. Wymagany niezależny przegląd bezpieczeństwa.

Ochrona danych osób fizycznych, retencja informacji kadrowych, uprawnienia administracyjne i rzeczywiste wdrożenie RODO wymagają osobnego przeglądu organizacyjno-technicznego.

## 5. Rozbudowana korekta ustaleń z etapów 1–4

Przeprowadzono drugie przejście po poprawkach modułów ŚT, dziennika i banku oraz dodano analogiczne sprawdzanie przynależności akcjonariuszy i kontrahentów w module kapitałowym. Uspójniono testy regresyjne z zasadą **odrzucona operacja nie może zostać utrwalona przez następny niezwiązany zapis do tego samego DbContext**.

Pozostałe prace nie mogą zostać oznaczone jako ukończone bez:
1. atomowości całych operacji biznesowych, w tym tworzenia dekretu, rozrachunków, numeracji i zmiany statusów,
2. wykonania testów na PostgreSQL serwerowym oraz Windows/Linux dla dokładnego commita,
3. weryfikacji stanu rzeczywistego systemu po odtworzeniu z kopii,
4. wdrożenia autoryzacji po stronie serwera.

## 6–8. Ponowione audyty techniczne i prawne; wspólna analiza ryzyka

| Priorytet | Problem / ryzyko | Stan w dniu kontroli | Warunek akceptacji |
|---|---|---|---|
| **P0** | Możliwość bezpośredniego SQL DML z poświadczeniami operatora — issue #35 | **Otwarte** | Odmowa DML do tabel biznesowych poza zaufanym serwerem, negatywne testy SQL, audyt tożsamości i uprawnień. |
| **P1** | Główna baza PostgreSQL/MySQL oraz lokalny SQLite wyłącznie jako kontrola — issue #36 | **Otwarta rozbieżność z wymaganiami produktu** | Migracja bez utraty danych, rozróżnienie trybu testowego, brak cichego fallbacku i testy spójności mirror. |
| **P1** | Brak generatora JPK_ST_KR — issue #4 | **Otwarte, warunkowane prawem i faktycznym zwolnieniem** | Weryfikacja Dziennika Ustaw i podmiotowości; jeśli wymagane — struktura XML, XSD i porównanie z MF. |
| **P1** | Niepełne płace/świadczenia — issue #3/#5 | **Otwarte** | Pełne przypadki naliczeń i uzgodnienia z niezależnymi wynikami. |
| **P1** | Odtwarzanie produkcyjne PostgreSQL + załączniki i ciągłość audytu | **Niezweryfikowane** | Test niezależnego odtworzenia, porównanie bilansów i sum kontrolnych, udokumentowane RPO/RTO. |
| **P1** | Atomowość wieloetapowych operacji aplikacji | **Częściowo poprawiona** | Transakcje i rollback + testy awaryjne na każdym etapie, także przy zewnętrznych transakcjach. |
| **P1** | Zgodność podatkowa i wydruków/sprawozdań | **Bez niezależnego potwierdzenia** | Walidacje aktualnych schematów, uzgodnienia z MF/ZUS, niezależne przypadki wzorcowe. |
| **P2** | Monitorowanie zmian prawa, schematów XML i wersji zależności | **Proces ciągły** | Rejestr zmian, wersjonowanie schematów, testy migracji i okresowe audyty. |

Wynik powtórnej kontroli jest **warunkowy**: kod ogranicza konkretne ryzyka, ale nie może być uznany za system o potwierdzonej pełnej zgodności lub gotowości produkcyjnej.

## 9. Wprowadzone zmiany

Wszystkie nowe zmiany kodu i testów z tej kontroli zostały zapisane bezpośrednio w `main` w repozytorium. Nie wykonywano resetów ani wymuszonych aktualizacji gałęzi i nie zmieniano numeru wydania. Kompilacja i uruchomienia GitHub Actions są częścią osobnej **bramki odbiorczej dla dokładnego SHA**. Jeśli najnowsze CI nadal jest w toku, należy oznaczyć etap potwierdzenia testów jako **oczekujący**, nie jako zaliczony.

## 10. Pełny audyt końcowy — kryteria i wniosek

### Co można potwierdzić na podstawie przeglądu kodu

- Zmiany weryfikujące właściciela referencji po `CompanyId` w poddanych korekcie ścieżkach banku i kapitałów.
- Walidację przed śledzeniem odrzuconego dekretu bezpośredniego.
- Wyceny aktywów rozdzielające dzień raportu, miesiąc amortyzacji i pełny rozchód, z testami regresyjnymi.
- Blokadę wstecznych ulepszeń wpływających na już naliczone odpisy.
- Jawne testy negatywne nowych ścieżek oraz deklarowany zakres istniejących testów integracyjnych.

### Czego nie wolno stwierdzić

- Że program ma **100% zgodności z aktualnymi przepisami** lub wszystkie wymagane deklaracje.
- Że działa bezpiecznie z bezpośrednimi kontami SQL operatorów na PostgreSQL.
- Że wszystkie operacje w całym systemie są atomowe (dotyczy szczególnie błędów po rozpoczęciu numeracji lub tworzenia rozrachunków).
- Że kopie i odtwarzanie produkcyjne oraz polityka RODO zostały sprawdzone niezależnie.
- Że sam pozytywny wynik CI jest równoważny zatwierdzeniu przez biegłego rewidenta, doradcę podatkowego lub specjalistę ds. kadr.

**Rekomendowany status wydania 1.0.0: nadal przedprodukcyjny.** Najważniejsza następna praca: zamknięcie P0 #35, potem test pełnej atomowości księgowań i odtwarzania bazy oraz uzupełnienie wymagań podatkowych i płacowych. Każdą taką zmianę należy ocenić w kolejnej niezależnej pętli audytu.

---
**Kontrola wersji i repozytorium:** dokument sporządzono w ramach cyklu pracy wyłącznie na `main` 01.11.2026. Ostateczny SHA i wynik CI należy pobrać z GitHub Actions po zakończeniu ostatniego przebiegu; nie należy przepisywać statusu trwających testów jako „sukces”.
