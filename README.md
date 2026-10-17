# Pełna Księgowość PSA

Desktopowa aplikacja Windows do prowadzenia **pełnej księgowości (ksiąg rachunkowych) polskiej prostej spółki
akcyjnej (PSA)** — zgodnie z ustawą o rachunkowości, z modułami VAT/JPK, CIT, KSeF, środków trwałych oraz
kapitału i akcji PSA. Działa lokalnie (SQLite) bez Internetu; opcjonalnie z serwerem PostgreSQL i kopiami
w OVHcloud Object Storage.

## Najważniejsze cechy

- Podwójny zapis Wn/Ma z kontrolą bilansowania, kwoty wyłącznie `decimal`, jawne zaokrąglenia.
- Zapisy zaksięgowane są niemodyfikowalne (logika + wyzwalacze bazy), korekty tylko przez storno i zapis
  korygujący; ciągła numeracja dziennika i łańcuch skrótów SHA-256 wykrywający manipulacje.
- Kontrola okresów (lista kontrolna przed zamknięciem, kontrolowane ponowne otwarcie), zamknięcie roku
  i automatyczny bilans otwarcia z przeniesieniem rozrachunków.
- Plan kont z wersjonowaniem per rok, rozrachunki per kontrahent, bank (CSV, MT940) z dopasowaniem płatności.
- Rejestry VAT i deklaracja, CIT (zaliczki, uzgodnienie wyniku bilansowego z podatkowym), środki trwałe
  (amortyzacja bilansowa i podatkowa osobno), zdarzenia kapitałowe PSA z wymogiem potwierdzenia dokumentów.
- Raporty z rzeczywistych zapisów: dziennik, księga główna, ZOiS, bilans, RZiS, rejestry VAT, rozrachunki,
  środki trwałe, uzgodnienia, raport zmian, raport integralności — eksport do PDF, XLSX i CSV.

## Dokumentacja

| Dokument | Treść |
|---|---|
| [docs/BUILD.md](docs/BUILD.md) | Kompilacja ze źródeł, testy, budowanie pliku `.exe` i instalatora |
| [docs/INSTALL.md](docs/INSTALL.md) | Instalacja u użytkownika końcowego, aktualizacja, odinstalowanie |
| [LICENSE](LICENSE) / [LICENSE.pl.md](LICENSE.pl.md) | Licencja: wolno używać (także w firmie), modyfikować i publikować; zakaz sprzedaży i odpłatnej dystrybucji |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Zasady współtworzenia i oświadczenie współtwórcy |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Licencje bibliotek zewnętrznych |
| [schemas/README.md](schemas/README.md) | Oficjalne schematy XSD dołączone do projektu |

## Szybki start dla programisty

```bash
git clone <adres-repozytorium>
cd <katalog-repozytorium>
dotnet test PelnaKsiegowosc.sln
```

Wymagany .NET SDK 10 — szczegóły w [docs/BUILD.md](docs/BUILD.md).

## Stan implementacji

| Obszar | Stan |
|---|---|
| Silnik księgowy (dziennik, KG, storno, okresy, zamknięcie roku, BO) | ✅ zaimplementowany, przetestowany technicznie |
| Dokumenty, sprzedaż, zakupy, korekty, załączniki | ✅ zaimplementowane, przetestowane technicznie |
| Bank i kasa (CSV, MT940, dopasowanie, uzgodnienie) | ✅ zaimplementowane; wymagają testów na rzeczywistych wyciągach |
| VAT (rejestry, deklaracja, rozliczenie) | ✅ zaimplementowane; wymagają weryfikacji księgowej |
| CIT, środki trwałe, kapitał PSA | ✅ zaimplementowane; wymagają weryfikacji księgowej/prawnej |
| Raporty, bilans, RZiS, eksport PDF/XLSX/CSV | ✅ zaimplementowane |
| Backup szyfrowany, OVHcloud S3, PostgreSQL, migracja baz | 🔧 w trakcie |
| KSeF (FA(3)), JPK_V7M(3), e-Sprawozdanie | 🔧 w trakcie |
| Interfejs WPF, kreator pierwszego uruchomienia | 🔧 w trakcie |
| Instalator MSI / pakiet przenośny `.exe` | 🔧 w trakcie (procedura opisana w docs/BUILD.md) |

## Licencja

Kod jest jawny (*source-available*): program można bezpłatnie używać — także w firmie do prowadzenia jej
ksiąg — kopiować, modyfikować i publikować na tej samej licencji. **Sprzedaż programu, odpłatna dystrybucja
i oferowanie go jako płatnej usługi są zabronione** bez odrębnej zgody licencjodawcy. Szczegóły:
[LICENSE](LICENSE), skrót: [LICENSE.pl.md](LICENSE.pl.md).

## Zastrzeżenie

Program nie stanowi porady prawnej, podatkowej ani księgowej i nie zastępuje księgowego ani biegłego
rewidenta. Odpowiedzialność za księgi ponosi kierownik jednostki (art. 4a ustawy o rachunkowości).
