# Plan implementacji

| Etap | Zakres | Stan |
|---|---|---|
| 1. Fundamenty | rozwiązanie .NET 10, domena, EF Core (SQLite, PostgreSQL), migracje, wyzwalacze niezmienności | zrealizowany |
| 2. Silnik księgowy | bufor, księgowanie, numeracja, łańcuch skrótów, storno, okresy, zamknięcie roku, BO, rozrachunki | zrealizowany |
| 3. Moduły | dokumenty i faktury, bank i kasa, VAT, CIT, środki trwałe, kapitały i właściciele | zrealizowany |
| 4. Raporty | dziennik, KG, ZOiS, bilans, RZiS, rejestry, rozrachunki, uzgodnienia; PDF/XLSX/CSV | zrealizowany |
| 5. Infrastruktura | szyfrowane kopie, Object Storage, magazyny sekretów (Windows, Linux), migracja baz, KSeF, JPK, XML sprawozdania | zrealizowany (JPK i e-SF bez walidacji XSD) |
| 6. Uogólnienie | formy prawne jednostek, dopasowanie planu kont, polityki, CIT, kapitałów | zrealizowany |
| 7. Interfejs | Avalonia (Windows, Linux), kreator, 20 sekcji, bez logowania, kopie automatyczne, tryb awaryjny | zrealizowany |
| 8. Narzędzia i pakiety | `pk-cli`, MSI, zip, deb, tar.gz, CI z testami instalacji, wydania | zrealizowany |
| 9. Weryfikacja zewnętrzna | środowisko testowe KSeF, OVHcloud, wyciągi bankowe, schematy JPK/e-SF, przegląd księgowy | do wykonania przez użytkownika ([TEST_PLAN.md](TEST_PLAN.md)) |

## Kolejne kroki (propozycje)

1. JPK_V7K i sekcja OsobaFizyczna (osoby fizyczne prowadzące księgi).
2. Walidacja JPK_V7M i e-Sprawozdania oficjalnymi XSD (schematy do pobrania z serwisów MF do katalogu `Schematy`).
3. Informacja dodatkowa i zestawienie zmian w kapitale w sprawozdaniu; rachunek przepływów pieniężnych.
4. Inwentaryzacja, odpisy aktualizujące należności, rezerwy.
5. Uwierzytelnianie XAdES w KSeF, pobieranie UPO w interfejsie.
6. Profile importu CSV dla popularnych banków.
