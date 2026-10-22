# Plan testów

## Uruchamianie

```bash
dotnet test PelnaKsiegowosc.sln -c Release
```

Testy integracyjne z serwerami (pomijane bez zmiennych):

```bash
export PK_TEST_PG="Host=127.0.0.1;Username=pk;Password=...;Database=postgres"   # użytkownik z CREATEDB
export PK_TEST_S3_ENDPOINT="http://127.0.0.1:5055"                              # np. python3 -m moto.server -p 5055
```

Kontrola uruchomienia programu okienkowego (bez monitora, platforma headless):
`PelnaKsiegowosc --test-uruchomienia` (kod wyjścia 0 = sukces). Zmienna `PK_SELFTEST_SCREENSHOTS=<katalog>`
zapisuje zrzuty wszystkich ekranów.

## Zakres

| Poziom | Zakres | Pliki |
|---|---|---|
| Jednostkowe — domena | identyfikatory, pieniądze i zaokrąglenia, VAT (stawki, mapowanie JPK), faktura, amortyzacja, CIT (stawki, terminy, święta), walidacja zapisów, formy prawne, wymogi zdarzeń kapitałowych | `Domain/`, `Presentation/ScreensAndLegalFormsTests.cs` |
| Silnik księgowy | bufor, księgowanie, numeracja, łańcuch skrótów, blokady w bazie, storno czerwone/czarne, okresy, rozrachunki | `Engine/` |
| Moduły | dokumenty i korekty, dekretacja, bank (MT940, CSV, dopasowanie), VAT, CIT, ŚT, kapitały, raporty i eksport | `Modules/` |
| Integracyjne | pełny rok obrotowy z zamknięciem i BO (kontrola wyniku i sum bilansu), kopie (szyfrowanie, manipulacje, odtwarzanie), PostgreSQL (migracja, księgowanie, utrata połączenia), S3 | `Integration/` |
| Integracje zewnętrzne | KSeF 2.0 na atrapie API (szyfrowanie RSA/AES, skróty, odrzucenie, zły token), FA(3) z oficjalnym XSD, JPK_V7M | `Integrations/` |
| Ekrany (E2E bez interfejsu) | kreator dla różnych form prawnych, wszystkie sekcje, faktura → zatwierdzenie → księgowanie, zapis ręczny i storno, drugie stanowisko, walidacja kreatora | `Presentation/` |
| Wiersz poleceń | info, kopia, weryfikacja, odtworzenie, integralność, kody wyjścia | `Presentation/` (`CommandLineTests`) |
| Program okienkowy | uruchomienie procesu z `--test-uruchomienia`: okna, 20 widoków, operacje | `Presentation/` (`DesktopAppStartupTests`) |
| Pakiety (CI) | kontrola uruchomienia opublikowanych programów, instalacja `.deb` i `.msi`, uruchomienie zainstalowanego programu, odinstalowanie | `.github/workflows/ci.yml` |

## Testy ręczne przed wydaniem

1. Środowisko testowe KSeF: wysyłka faktury, pobranie UPO i faktur zakupowych z prawdziwym tokenem.
2. Konto OVHcloud Object Storage: test połączenia, kopia, retencja, pobranie i odtworzenie.
3. Wyciągi z banków użytkownika (MT940/CSV) — profile importu.
4. Walidacja JPK_V7M i sprawozdania finansowego oficjalnymi schematami i narzędziami MF.
5. Przegląd wyników (deklaracje, CIT, bilans, RZiS) przez księgowego na danych rzeczywistych.
6. Praca dwóch stanowisk na jednym serwerze PostgreSQL; zachowanie przy utracie połączenia.
