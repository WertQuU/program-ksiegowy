# Architektura

## Warstwy

```
PelnaKsiegowosc.App (Avalonia, Windows/Linux)      PelnaKsiegowosc.Cli (pk-cli)
              │  widoki XAML, okna dialogowe                │
              ▼                                             ▼
PelnaKsiegowosc.Presentation — modele widoków (MVVM), kreator, sesja programu (AppSession)
              │
              ▼
PelnaKsiegowosc.Infrastructure — kopie (AES-256-GCM), S3/OVH, sekrety, KSeF, JPK, XML, eksport PDF/XLSX/CSV, migracja baz
              │
              ▼
PelnaKsiegowosc.Application — silnik księgowy i moduły: dokumenty, bank, VAT, CIT, ŚT, kapitały, okresy, raporty, audyt
              │
              ▼
PelnaKsiegowosc.Data (EF Core 10) + Migrations.Sqlite / Migrations.PostgreSql — model, profile połączeń, wyzwalacze
              │
              ▼
PelnaKsiegowosc.Domain — encje i reguły (bez zależności od bazy i interfejsu)
```

- **Domain** zawiera reguły niezależne od technologii: walidację identyfikatorów (NIP, REGON, KRS, PESEL, IBAN),
  formy prawne (`LegalForms`), walidację zapisów (`EntryValidator`), kalkulatory (faktura, amortyzacja, CIT),
  układ sprawozdania (`FinancialStatementLayout`, załącznik nr 1 UoR) i reguły VAT (`VatRules`, wersjonowane).
- **Application** realizuje przypadki użycia na kontekście bazy; każda operacja zmieniająca dane zapisuje wpis
  w dzienniku zdarzeń w tej samej transakcji (`AuditService`). Uprawnienia sprawdza `PermissionGuard`.
- **Infrastructure** izoluje zależności zewnętrzne (AWS SDK, HTTP KSeF, ClosedXML, PDFsharp, magazyny sekretów).
- **Presentation** nie zależy od Avalonia — modele widoków testuje się bez interfejsu (`AutomaticDialogService`).
  Każda operacja otwiera krótko żyjący kontekst bazy (`AppSession.OpenDb()`), więc ekrany widzą aktualne dane,
  także przy wielu stanowiskach na jednym serwerze.
- **App** to wyłącznie widoki (XAML z kompilowanymi powiązaniami), okna dialogowe i start programu.

## Dane

- Identyfikatory GUID v7 (uporządkowane czasowo, `ValueGeneratedNever`) — bezpieczna migracja między silnikami.
- Kwoty `decimal` (w SQLite przechowywane jako tekst — agregacje w pamięci, wynik identyczny dla obu silników).
- Daty `DateOnly`, znaczniki czasu w UTC; strefa księgowa Europe/Warsaw (`SystemClock`).
- Wyliczenia zapisywane jako tekst.
- Migracje schematu osobne dla SQLite i PostgreSQL; przy starcie programu kopia przed migracją, potem `Migrate()`.

## Niezmienność i integralność ksiąg

- Zapis w buforze jest edytowalny; zaksięgowanie nadaje ciągły numer pozycji dziennika w roku i numer
  w dzienniku częściowym, ustala skrót SHA-256 treści i poprzedniego zapisu (łańcuch per rok).
- Wyzwalacze bazy (SQLite i PostgreSQL) blokują zmianę i usunięcie zaksięgowanych zapisów, ich pozycji oraz
  wpisów dziennika zdarzeń; jedyną dozwoloną zmianą jest oznaczenie zapisu jako wystornowanego.
- Korekty wyłącznie stornem (czerwonym lub czarnym — wg polityki) i zapisem korygującym.
- `IntegrityService` sprawdza łańcuchy skrótów, zgodność rozrachunków z księgami, dziennik zdarzeń.

## Rozrachunki

Pozycja rozrachunkowa (`OpenItem`) powstaje przy księgowaniu na koncie rozrachunkowym; wiąże się z kolejnymi
latami po numerze konta (plan kont jest wersjonowany per rok). Płatności, kompensaty i korekty rozliczają
pozycje (`SettlementAllocation`); bilans otwarcia przenosi salda pozycji.

## Zamknięcie roku

Lista kontrolna → zapis zamknięcia (przeksięgowanie kont wynikowych na 860) → status „zamknięty” → utworzenie
kolejnego roku z kopią planu kont → bilans otwarcia (saldo 860 na 820) — w jednej transakcji, po kopii zapasowej.

## Kopie zapasowe

Archiwum `.pkbak`: spójna kopia pliku SQLite (Online Backup API) lub przenośny zrzut danych (PostgreSQL),
załączniki, konfiguracja bez sekretów i manifest z sumami SHA-256 → ZIP → szyfrowanie AES-256-GCM w porcjach
1 MiB (uwierzytelniony nagłówek, numer porcji i znacznik ostatniej porcji w AAD). Klucz: losowy 256-bitowy
(w magazynie sekretów, z kluczem odzyskiwania Base32) albo z hasła (PBKDF2-SHA256, 600 000 iteracji).
Po zapisie archiwum jest weryfikowane; odtwarzanie wyłącznie do nowej lokalizacji.

## Bazy danych i tryb serwerowy

Profil połączenia (`DatabaseProfile`) bez hasła — hasło w magazynie sekretów. Program nie przełącza się
samoczynnie między bazami: przy niedostępnym serwerze użytkownik wybiera ponowienie, osobną kopię roboczą
trybu awaryjnego (odtworzoną z ostatniej kopii, wyraźnie oznaczoną) albo zakończenie. Migracja SQLite →
PostgreSQL (`DatabaseMigrator`) wymaga pustej bazy docelowej, wykonuje kopię źródła i porównuje liczby rekordów,
obroty i skróty.

## Integracje

- **KSeF 2.0**: uwierzytelnienie tokenem (RSA-OAEP SHA-256 „token|znacznik czasu”), sesja interaktywna
  (AES-256-CBC, klucz szyfrowany kluczem publicznym MF), wysyłka i status faktur FA(3), pobieranie faktur
  zakupowych; ponawianie przy 429/5xx. Faktura FA(3) walidowana dołączonym oficjalnym schematem XSD przed wysyłką.
- **JPK_V7M** i **sprawozdanie finansowe (XML)**: generatory z walidacją XSD, gdy schemat jest dostępny lokalnie;
  w przeciwnym razie plik ma status „niezwalidowany”. Program nie wysyła JPK ani sprawozdań.
- **OVHcloud Object Storage (S3)**: wysyłka zaszyfrowanych archiwów z weryfikacją rozmiaru i SHA-256, retencja,
  usuwanie niedokończonych wysyłek wieloczęściowych, test zapisu/odczytu/usunięcia.

## Tożsamość operatora

Program nie ma własnych kont: operatorem jest zalogowany użytkownik systemu (`użytkownik@komputer`), z identyfikatorem
wyznaczanym deterministycznie z nazwy; wpisy dziennika zdarzeń zawierają też nazwę stanowiska. Ochronę dostępu
zapewnia konto systemu operacyjnego i (w trybie serwerowym) konto PostgreSQL. Moduł kont i MFA z wcześniejszej
wersji (`SecurityService`) pozostaje w kodzie, ale nie jest używany przez interfejs.
