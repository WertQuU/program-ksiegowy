# Pełna Księgowość

Program do prowadzenia **pełnej księgowości (ksiąg rachunkowych)** zgodnie z polską ustawą o rachunkowości —
dla spółek kapitałowych (sp. z o.o., P.S.A., S.A.), spółek osobowych, fundacji, stowarzyszeń, spółdzielni
i działalności gospodarczej prowadzącej księgi rachunkowe. Działa na **Windows i Linux**, lokalnie bez Internetu
(SQLite) albo na wielu stanowiskach z serwerem PostgreSQL. Moduły VAT/JPK, KSeF, CIT, środki trwałe, kapitały,
zamknięcie roku, sprawozdania i szyfrowane kopie zapasowe (także w OVHcloud Object Storage).

![Pulpit](docs/zrzuty/pulpit.png)

## Najważniejsze cechy

**Dla osób bez przygotowania księgowego:**

- **Asystent kontroli ksiąg** na Pulpicie — sam wykrywa błędy, niezgodności i zaległości (dokumenty niezaksięgowane
  w terminie, brak odliczenia VAT, faktury dla firm bez KSeF, duplikaty, płatności gotówkowe ponad 15 000 zł,
  niezapłacone faktury po 90 dniach, ujemna kasa, salda po złej stronie, brak wyciągów, amortyzacji, kopii…),
  objaśnia je prostym językiem i prowadzi do miejsca naprawy.
- **Kalendarz terminów** — JPK_V7M/V7K i VAT, zaliczki CIT, ZUS i PIT od wynagrodzeń, PIT-4R/PIT-11, JPK_CIT,
  zaksięgowanie miesiąca, sprawozdanie finansowe, CIT-8.
- **Sekcja „Deklaracje”** — wszystkie deklaracje w jednym miejscu z terminami: CIT-8 z CIT-8/O, VAT-UE, IFT-2R/CIT-10Z,
  PIT-36/PIT-36L (właściciel, wspólnicy), uchwały roczne, inwentaryzacja (metody, potwierdzenia sald, protokół kasy)
  oraz skróty do JPK_V7, JPK_CIT, ZUS DRA/RCA, PIT-4R/PIT-8AR/PIT-11 i sprawozdania.
- **Rodzaje wydatków** (paliwo, samochód osobowy, telefon, najem, hotel, restauracja, reprezentacja…) — program sam
  dobiera konto, procent odliczenia VAT i część kosztu nieuznawaną podatkowo.
- **Kreator „Rozlicz miesiąc”** — dokumenty, bank, amortyzacja, VAT, JPK, zaliczka CIT, kopia i zamknięcie, każdy
  krok jednym przyciskiem. **„Zapisz i zaksięguj”** w fakturach.
- **Kreator planu kont** dopasowany do działalności, **biała lista VAT** (dane kontrahenta po NIP, sprawdzenie
  rachunku), **kurs NBP** jednym przyciskiem, **rodzaje operacji bankowych** zamiast numerów kont, objaśnienia
  w każdej sekcji i słowniczek pojęć.

**Księgowość:**

- **Silnik księgowy**: podwójny zapis Wn/Ma z kontrolą bilansowania, kwoty wyłącznie `decimal`, zapisy zaksięgowane
  niemodyfikowalne (logika + wyzwalacze bazy), korekty tylko stornem, ciągła numeracja dziennika i łańcuch skrótów
  SHA-256 wykrywający manipulacje, bufor zapisów.
- **Okresy i rok obrotowy**: lista kontrolna przed zamknięciem miesiąca, kontrolowane ponowne otwarcie
  z uzasadnieniem, zamknięcie roku z automatycznym bilansem otwarcia i przeniesieniem rozrachunków.
- **Dokumenty**: faktury sprzedaży i zakupu, korekty, noty, PK, dowody wewnętrzne, załączniki z sumą SHA-256,
  zatwierdzanie i dekretacja (propozycja + edycja), historia zmian.
- **Forma prawna jednostki** wpływa na plan kont (nazwa kapitału podstawowego), politykę rachunkowości,
  kontrolę firmy i KRS, dostępność CIT (spółki osobowe i osoby fizyczne → PIT wspólników) i wymogi zdarzeń kapitałowych.
- **VAT i JPK**: rejestry, wyliczenie deklaracji (miesięcznej i kwartalnej), rozliczenie na kontach, pliki JPK_V7M
  i JPK_V7K (wersje 2 i 3, także dla osoby fizycznej), JPK_CIT (JPK_KR_PD — księgi ze znacznikami MF), VAT-UE —
  **walidowane oficjalnymi schematami XSD Ministerstwa Finansów** dołączonymi do programu (`schemas/MF`);
  **KSeF 2.0** — faktury FA(3) walidowane schematem, wysyłka i pobieranie faktur, logowanie tokenem lub certyfikatem (XAdES).
- **Bank i kasa**: import MT940 i CSV, dopasowanie do rozrachunków, księgowanie, operacje kasowe; rozrachunki
  z wiekowaniem i kompensatami.
- **CIT** (zaliczki, uzgodnienie wyniku bilansowego z podatkowym, CIT-8), **PIT** właściciela i wspólników,
  **płace** (umowa o pracę, zlecenie, dzieło, powołanie; ZUS, zdrowotna i PIT 2026, PPK, choroba, urlop bezpłatny,
  listy płac z księgowaniem, DRA/RCA, PIT-4R, PIT-8AR, PIT-11), **magazyn** (PZ/WZ/RW/PW, cena przeciętna lub FIFO,
  spis z natury),
  **środki trwałe** (amortyzacja bilansowa i podatkowa), **kapitał i właściciele** (wkłady, podział zysku, dywidendy, pożyczki — po potwierdzeniu dokumentów).
- **Sprawozdanie finansowe**: bilans, RZiS porównawczy i kalkulacyjny (klucz podziału kosztów), zestawienie zmian
  w kapitale, rachunek przepływów (metoda pośrednia), informacja dodatkowa (tabele z ksiąg + opisy), informacja
  o podatku dochodowym; **e-Sprawozdanie XML w oficjalnej strukturze SFJINZ(2)** walidowane schematem MF.
- **Monitor przepisów** — sprawdza zmiany ustaw (API Sejmu) i pokazuje je w panelu; **weryfikacja kontrahenta**
  w bezpłatnych rejestrach (biała lista VAT, KRS — likwidacja/upadłość/restrukturyzacja, VIES).
- **Raporty** z rzeczywistych zapisów: dziennik, księga główna, zestawienie obrotów i sald, bilans, RZiS,
  rejestry VAT, rozrachunki, środki trwałe, uzgodnienia, dziennik zdarzeń, kontrola integralności —
  eksport do **PDF, XLSX i CSV**; sprawozdanie finansowe w XML.
- **Kopie zapasowe** szyfrowane AES-256-GCM, weryfikowane po zapisie, odtwarzanie do nowej lokalizacji,
  kopie automatyczne i przy zamknięciu programu, opcjonalnie w OVHcloud Object Storage (S3).
- **Bez osobnego logowania**: dostęp chroni konto systemu operacyjnego (i konto serwera PostgreSQL);
  operator jest zapisywany w dzienniku zdarzeń. Hasła i klucze w magazynie systemu (Windows: Menedżer
  poświadczeń, Linux: pęk kluczy) — nigdy w plikach konfiguracji.

| Wydatek z kategorią | Rozlicz miesiąc |
|---|---|
| ![Wydatek](docs/zrzuty/wydatek.png) | ![Rozlicz miesiąc](docs/zrzuty/rozlicz-miesiac.png) |
| **Kreator pierwszego uruchomienia** | **Faktura sprzedaży** |
| ![Kreator](docs/zrzuty/kreator.png) | ![Faktura](docs/zrzuty/faktura-sprzedazy.png) |
| **Płace** | **Deklaracje** |
| ![Płace](docs/zrzuty/place.png) | ![Deklaracje](docs/zrzuty/deklaracje.png) |

## Pobranie i instalacja

Trwają prace rozwojowe i przeglądy. Stare wydania zostały wycofane; obecnie nie ma oficjalnego wydania.
Po zakończeniu analiz planowane jest wydanie **1.0.0**. Aktualny kod znajduje się na gałęzi **main**.
Pakiety testowe są budowane w GitHub Actions jako artefakty; publikacja w **Releases** wymaga osobnego,
świadomego uruchomienia workflow z numerem wydania.

Docelowe formaty pakietów (bez potrzeby instalowania .NET):

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

- Oficjalne schematy XSD MF (JPK_V7M/V7K, JPK_KR_PD, e-Sprawozdanie SFJINZ(2), FA(3)) są dołączone do programu
  (`schemas/`) i odświeżane automatycznie w GitHub Actions; nowe wersje można też pobrać w *Ustawienia → Schematy XSD*.
- e-Sprawozdanie: struktury SFJINZ/SFJINT(2) (pełna), SFJMAZ/SFJMAT(2) (jednostka mała) i SFJMIZ/SFJMIT(2) (mikro) —
  w złotych lub w tysiącach złotych (zaokrąglenia do tysięcy sprawdź przed podpisaniem).
- Pliki XML e-Deklaracji: VAT-UE(5), korekta VAT-UEK(5) i PIT-11(29) z adresem zamieszkania (aktualne wzory, walidowane schematem MF). CIT-8(34), PIT-4R(13),
  PIT-8AR(14), IFT-2R(12), PIT-36/36L oraz ZUS DRA/RCA — MF i ZUS nie publikują aktualnych schematów XSD tych wzorów
  (MF udostępnia je tylko w e-Urzędzie), więc program wylicza kwoty i drukuje zestawienia;
  wysyłka elektroniczna (e-Deklaracje, e-Urząd Skarbowy, PUE ZUS/Płatnik) odbywa się poza programem.
- Płace: zasiłki finansowane przez ZUS (od 34. dnia choroby, macierzyński) wypłaca ZUS — program nalicza wynagrodzenie
  chorobowe pracodawcy; urlop wypoczynkowy przy stałej pensji nie zmienia wynagrodzenia.
- KSeF: logowanie tokenem albo certyfikatem (.pfx — podpis kwalifikowany, pieczęć, certyfikat KSeF). Profil Zaufany
  wymaga podpisania pliku na stronie gov.pl — program przyjmuje wtedy certyfikat KSeF.
- Biała lista VAT i kursy NBP: integracje sprawdzone testami na atrapach API (publiczne API MF i NBP).
- Reguły kategorii wydatków odpowiadają typowym przypadkom — w nietypowej sytuacji procenty (VAT, NKUP) zmienia się
  ręcznie w pozycji faktury.
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
