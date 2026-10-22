# Pełna Księgowość

Program do prowadzenia **pełnej księgowości (ksiąg rachunkowych)** zgodnie z polską ustawą o rachunkowości —
dla spółek kapitałowych (sp. z o.o., P.S.A., S.A.), spółek osobowych, fundacji, stowarzyszeń, spółdzielni
i działalności gospodarczej prowadzącej księgi rachunkowe. Działa na **Windows i Linux**, lokalnie bez Internetu
(SQLite) albo na wielu stanowiskach z serwerem PostgreSQL. Moduły VAT/JPK, KSeF, CIT, środki trwałe, kapitały,
zamknięcie roku, sprawozdania i szyfrowane kopie zapasowe (także w OVHcloud Object Storage).

![Pulpit](docs/zrzuty/pulpit.png)

## Najważniejsze cechy

- **Silnik księgowy**: podwójny zapis Wn/Ma z kontrolą bilansowania, kwoty wyłącznie `decimal`, zapisy zaksięgowane
  niemodyfikowalne (logika + wyzwalacze bazy), korekty tylko stornem, ciągła numeracja dziennika i łańcuch skrótów
  SHA-256 wykrywający manipulacje, bufor zapisów.
- **Okresy i rok obrotowy**: lista kontrolna przed zamknięciem miesiąca, kontrolowane ponowne otwarcie
  z uzasadnieniem, zamknięcie roku z automatycznym bilansem otwarcia i przeniesieniem rozrachunków.
- **Dokumenty**: faktury sprzedaży i zakupu, korekty, noty, PK, dowody wewnętrzne, załączniki z sumą SHA-256,
  zatwierdzanie i dekretacja (propozycja + edycja), historia zmian.
- **Forma prawna jednostki** wpływa na plan kont (nazwa kapitału podstawowego), politykę rachunkowości,
  kontrolę firmy i KRS, dostępność CIT (spółki osobowe i osoby fizyczne → PIT wspólników) i wymogi zdarzeń kapitałowych.
- **VAT i JPK**: rejestry, wyliczenie deklaracji, rozliczenie na kontach, plik JPK_V7M; **KSeF 2.0** — faktury FA(3)
  walidowane z oficjalnym schematem, wysyłka i pobieranie faktur zakupowych.
- **Bank i kasa**: import MT940 i CSV, dopasowanie do rozrachunków, księgowanie, operacje kasowe; rozrachunki
  z wiekowaniem i kompensatami.
- **CIT** (zaliczki, uzgodnienie wyniku bilansowego z podatkowym), **środki trwałe** (amortyzacja bilansowa
  i podatkowa), **kapitał i właściciele** (wkłady, podział zysku, dywidendy, pożyczki — po potwierdzeniu dokumentów).
- **Raporty** z rzeczywistych zapisów: dziennik, księga główna, zestawienie obrotów i sald, bilans, RZiS,
  rejestry VAT, rozrachunki, środki trwałe, uzgodnienia, dziennik zdarzeń, kontrola integralności —
  eksport do **PDF, XLSX i CSV**; sprawozdanie finansowe w XML.
- **Kopie zapasowe** szyfrowane AES-256-GCM, weryfikowane po zapisie, odtwarzanie do nowej lokalizacji,
  kopie automatyczne i przy zamknięciu programu, opcjonalnie w OVHcloud Object Storage (S3).
- **Bez osobnego logowania**: dostęp chroni konto systemu operacyjnego (i konto serwera PostgreSQL);
  operator jest zapisywany w dzienniku zdarzeń. Hasła i klucze w magazynie systemu (Windows: Menedżer
  poświadczeń, Linux: pęk kluczy) — nigdy w plikach konfiguracji.

| Kreator pierwszego uruchomienia | Faktura sprzedaży |
|---|---|
| ![Kreator](docs/zrzuty/kreator.png) | ![Faktura](docs/zrzuty/faktura-sprzedazy.png) |

## Pobranie i instalacja

Gotowe pakiety (bez potrzeby instalowania .NET) są budowane automatycznie w GitHub Actions i publikowane
w zakładce **Releases**:

| System | Pakiet |
|---|---|
| Windows 10/11 x64 | `PelnaKsiegowosc-<wersja>-x64.msi` (instalator) lub `PelnaKsiegowosc-<wersja>-win-x64.zip` (przenośny) |
| Linux x64 (Debian/Ubuntu) | `pelnaksiegowosc_<wersja>_amd64.deb` |
| Linux x64 (inne dystrybucje) | `PelnaKsiegowosc-<wersja>-linux-x64.tar.gz` z `install.sh` (bez uprawnień administratora) |

Instrukcja: [docs/INSTALL.md](docs/INSTALL.md). Kompilacja ze źródeł: [docs/BUILD.md](docs/BUILD.md).

## Dokumentacja

| Dokument | Treść |
|---|---|
| [docs/INSTALL.md](docs/INSTALL.md) | Instalacja, aktualizacja, odinstalowanie, położenie danych (Windows i Linux) |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Instrukcja obsługi: kreator, codzienna praca, zamknięcie miesiąca i roku, kopie |
| [docs/BUILD.md](docs/BUILD.md) | Kompilacja, testy, budowanie pakietów MSI, zip, deb, tar.gz |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Architektura: warstwy, dane, niezmienność zapisów, integracje |
| [docs/ACCOUNTING_REQUIREMENTS.md](docs/ACCOUNTING_REQUIREMENTS.md) | Wymagania księgowe i sposób ich realizacji |
| [docs/COMPLIANCE_MATRIX.md](docs/COMPLIANCE_MATRIX.md) | Macierz zgodności z przepisami (z oznaczeniem pozycji do weryfikacji) |
| [docs/SECURITY.md](docs/SECURITY.md) | Model bezpieczeństwa programu |
| [docs/TEST_PLAN.md](docs/TEST_PLAN.md) | Plan testów i sposób ich uruchamiania |
| [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) / [docs/PROGRESS.md](docs/PROGRESS.md) | Plan i stan prac, znane ograniczenia |
| [LICENSE](LICENSE) / [LICENSE.pl.md](LICENSE.pl.md) | Licencja: wolno używać (także w firmie), modyfikować i publikować; zakaz sprzedaży |
| [SECURITY.md](SECURITY.md) / [CONTRIBUTING.md](CONTRIBUTING.md) | Zgłaszanie podatności, zasady współtworzenia |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) / [schemas/README.md](schemas/README.md) | Licencje bibliotek, oficjalne schematy XSD |

## Znane ograniczenia

- JPK_V7K (rozliczenie kwartalne), JPK dla osoby fizycznej (sekcja OsobaFizyczna) i zaliczki PIT — nieobsługiwane.
- Schematy XSD JPK_V7M i e-Sprawozdania nie są dołączone — pliki mają status „niezwalidowany” do czasu
  walidacji oficjalnym schematem (szczegóły: [schemas/README.md](schemas/README.md)).
- KSeF: uwierzytelnianie tokenem; podpis XAdES (kwalifikowany/Profil Zaufany) nieobsługiwany. Integracja
  sprawdzona testami na atrapie API — przed użyciem produkcyjnym przetestuj ją w środowisku testowym KSeF.
- Wariant kalkulacyjny RZiS i informacja dodatkowa sprawozdania — nieobsługiwane.
- Mapowania podatkowe i księgowe wymagają weryfikacji przez księgowego lub doradcę podatkowego
  ([docs/COMPLIANCE_MATRIX.md](docs/COMPLIANCE_MATRIX.md)).

## Licencja

Kod jest jawny (*source-available*): program można bezpłatnie używać — także w firmie do prowadzenia jej
ksiąg — kopiować, modyfikować i publikować na tej samej licencji. **Sprzedaż programu, odpłatna dystrybucja
i oferowanie go jako płatnej usługi są zabronione** bez odrębnej zgody licencjodawcy. Szczegóły:
[LICENSE](LICENSE), skrót: [LICENSE.pl.md](LICENSE.pl.md).

## Odpowiedzialność

Program jest udostępniany bez jakichkolwiek gwarancji. **Pełną odpowiedzialność za księgi rachunkowe,
deklaracje, pliki JPK, faktury KSeF, sprawozdania finansowe i rozliczenia z urzędem skarbowym ponosi
użytkownik** — w tym za weryfikację wyników przed ich wykorzystaniem. Autor programu nie odpowiada za
zaległości podatkowe, odsetki, kary, sankcje ani inne szkody wynikające z korzystania z programu.
Program nie stanowi porady prawnej, podatkowej ani księgowej. Szczegóły: § 6 pliku [LICENSE](LICENSE).
