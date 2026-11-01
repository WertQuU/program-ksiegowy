# Weryfikacja czterech uwag z audytu Gemini

Data: 2026-11-01. Projekt: Pełna Księgowość, wersja 1.0.0. Analiza odnosi się do kodu opartego na commicie 42cfa25 oraz aktualnych zmian kreatora. Jest to weryfikacja wskazanych tez, a nie pełny audyt zgodności programu.

## 1. P.S.A. — teza częściowo nieaktualna, rzeczywiste ograniczenie wkładów pracy/usług

Program już rozróżnia `LegalForm.ProstaSpolkaAkcyjna`. `LegalForms.ShareCapitalName` zwraca dla niej kapitał akcyjny, a usługi kapitału mają osobne gałęzie dla tej formy. Dodawanie równoległej flagi `CompanyType.PSA` dublowałoby istniejący model.

`Domain/Capital/PsaPayoutAssessment.cs` zawiera limit podziału, dane do prognozy sześciomiesięcznej, kontrolę kapitału po wypłacie i wyliczenie obowiązkowego zasilenia. `Application/Capital/CapitalService.cs` oraz `Domain/Capital/CapitalEvent.cs` wykorzystują te kontrole. Sama deklaracja użytkownika i wprowadzone kwoty nie potwierdzają autentyczności dokumentów ani kompletności prognozy.

Odpis nie jest bezwarunkowy: art. 300¹⁹ KSH wiąże co najmniej 8% zysku z nieosiągnięciem przez kapitał progu 5% zobowiązań ze wskazanego sprawozdania. Kod uwzględnia tę zależność.

**Potwierdzone ograniczenie:** `ContributionType.PracaLubUslugi` istnieje, ale `CreateEventAsync` jawnie odrzuca księgowanie takiego wkładu jako funkcję niezaimplementowaną. Nie należy usuwać tej blokady i księgować pracy/usług na kapitał akcyjny. Potrzebny jest osobny proces ewidencji, dokumentów i oceny księgowej.

Źródło: [KSH, art. 300², 300³, 300¹⁵ i 300¹⁹](https://eli.gov.pl/api/acts/DU/2024/18/text.html).

## 2. Niezmienność — triggery i storna już istnieją; izolacja SQL pozostaje odrębnym problemem

`Data/ImmutabilityTriggers.cs` definiuje PostgreSQL i SQLite: blokadę zmian/usuwania zaksięgowanych zapisów, ochronę ich pozycji i ochronę dziennika audytu. Migracje `ImmutabilityGuards` i `ImmutabilityGuardsV2` instalują te mechanizmy. `Application/Ledger/LedgerService.cs` implementuje storna i korekty ze śladem audytu; walidator księgowania sprawdza rok i okres.

Teza o całkowitym braku blokad bazodanowych jest nieprawdziwa. Nie oznacza to pełnej ochrony: wskazane triggery nie stanowią kompletnej autoryzacji wszystkich operacji SQL i wszystkich tabel według statusu okresu. Zagadnienie bezpośredniego DML operatorów jest już opisane w `docs/SECURITY.md` jako otwarte ryzyko. Dalsza kontrola powinna obejmować próby bezpośredniego SQL i model uprawnień, a nie samo dodanie flagi `IsLocked`.

Korekta nie może oznaczać dopisywania do ostatecznie zamkniętych ksiąg. Art. 12 ust. 5 i art. 25 UoR wymagają zachowania nieodwracalności zamknięcia i odrębnych zapisów korygujących.

Źródło: [UoR, tekst jednolity 2026, art. 12 i 25](https://api.sejm.gov.pl/eli/acts/DU/2026/522/text.pdf).

## 3. Zaokrąglenia — istnieją; nazwa nowego serwisu nie dowodzi braku funkcji

`Domain/Common/Money.cs` rozdziela `Round` do groszy i `RoundToFullZloty`. `Domain/Cit/CitCalculator.cs` stosuje pełne złote do podstawy i podatku, a `Application/Vat/VatService.cs` do pól deklaracyjnych. Stwierdzenie, że program nie rozdziela tych precyzji, nie jest potwierdzone.

Dalsza weryfikacja wymaga listy konkretnych pól każdego obsługiwanego formularza, przypadków granicznych i wyjątków. Nie wolno globalnie zaokrąglić wszystkich danych XML do złotych. Art. 63 Ordynacji podatkowej zawiera wyjątki.

Przywołanie art. 61a KKS jako ogólnej sankcji za każdą wadliwą deklarację jest zbyt szerokie: przepis odnosi się do przesyłania księgi. Konkretna kwalifikacja wymaga ustalenia rodzaju dokumentu i zachowania.

Źródła: [Ordynacja podatkowa, art. 63](https://api.sejm.gov.pl/eli/acts/DU/2025/111/text.pdf), [KKS, art. 61a](https://api.sejm.gov.pl/eli/acts/DU/2025/633/text.pdf).

## 4. KSeF i deklaracje — walidacja XSD już działa; nie jest pełną walidacją merytoryczną

`Infrastructure/Ksef/KsefService.cs`, `SendAsync`, wywołuje `BuildAsync` i odrzuca niepoprawny wynik XSD przed uwierzytelnieniem i transmisją. `Fa3InvoiceBuilder.Validate` korzysta z `XsdValidator`. Teza o braku obowiązkowej kontroli XSD przed wysyłką KSeF jest nieaktualna.

PIT-11 i VAT-UE są walidowane w swoich builderach. `Presentation/Services/DeclarationXmlExport.cs` pozwala zapisać wynik również ze statusem NIEZWALIDOWANY. Program nie przesyła tych deklaracji do bramki MF — eksport i wysyłka to oddzielne czynności. Dostępność pliku nie dowodzi przyjęcia go przez MF.

**Zakres do dalszej kontroli:** reguły merytoryczne, zależności kwot, aktualność właściwych wersji schematów oraz testy każdego obsługiwanego formularza. Zgodność XSD nie daje gwarancji zgodności podatkowej ani przyjęcia przez API.

## Konsekwencje dla bieżącej zmiany

Nie wprowadzono na podstawie tego podsumowania zmian reguł prawnych, migracji ani nowych klas dublujących istniejące funkcje. Bieżąca poprawka dotyczy kreatora: połączenie i pobieranie danych w kroku 2, uzupełnianie braków oraz usunięcie migracji wykonywanej kontem operatora w kroku 10.
