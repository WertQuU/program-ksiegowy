# Model bezpieczeństwa programu

Zgłaszanie podatności: [../SECURITY.md](../SECURITY.md).

## Dostęp i tożsamość

- Program nie ma własnego logowania. Dostęp do ksiąg chroni **konto systemu operacyjnego** (pliki danych w profilu
  użytkownika) oraz — w trybie serwerowym — **konto PostgreSQL** z szyfrowanym połączeniem TLS (domyślnie VerifyFull).
- Operatorem zapisywanym w dzienniku zdarzeń jest `użytkownik@komputer`; identyfikator jest stały dla nazwy konta.
- Zalecenia: osobne konto systemowe dla każdej osoby, szyfrowanie dysku (BitLocker / LUKS), blokada ekranu,
  na serwerze — osobni użytkownicy PostgreSQL z minimalnymi uprawnieniami i dostęp sieciowy ograniczony do stanowisk.

## Sekrety

| Sekret | Gdzie |
|---|---|
| Hasło PostgreSQL, klucze Object Storage, token KSeF, klucz kopii | Windows: Menedżer poświadczeń; Linux: pęk kluczy (Secret Service przez `secret-tool`), a gdy niedostępny — plik zaszyfrowany AES-GCM kluczem z osobnego pliku, oba z uprawnieniami 0600 |
| Konfiguracja (`konfiguracja.json`) | bez sekretów — tylko nazwy wpisów w magazynie |

Sekrety nie trafiają do logów, dziennika zdarzeń, kopii zapasowych ani repozytorium. Tokeny KSeF są przekazywane
do API wyłącznie w postaci zaszyfrowanej kluczem publicznym MF. `secret-tool` otrzymuje sekret przez standardowe
wejście, nigdy w argumentach. Magazyn plikowy na Linuksie chroni sekrety tylko uprawnieniami konta — zalecany pęk kluczy.

## Integralność ksiąg

- Zapisy zaksięgowane i dziennik zdarzeń są chronione przed zmianą wyzwalaczami bazy i logiką programu.
- Łańcuchy skrótów SHA-256 (zapisy w roku, dziennik zdarzeń) wykrywają zmianę lub usunięcie wpisu, także wykonane
  bezpośrednio w bazie; kontrola integralności jest dostępna w programie i w `pk-cli integralnosc`.
- Załączniki przechowywane pod losowymi nazwami (ochrona przed path traversal), z sumą SHA-256 sprawdzaną przy odczycie;
  dozwolone typy i rozmiar są weryfikowane (sygnatura pliku).

## Kopie zapasowe

AES-256-GCM w porcjach z uwierzytelnionym nagłówkiem, numerem i znacznikiem ostatniej porcji (wykrywa podmianę,
przestawienie i obcięcie). Klucz losowy 256-bitowy z kluczem odzyskiwania albo hasło (PBKDF2-SHA256, 600 000 iteracji).
Weryfikacja po zapisie; odtwarzanie wyłącznie do nowego katalogu. Kopie w Object Storage są szyfrowane po stronie
klienta przed wysłaniem (opcjonalnie dodatkowo SSE po stronie serwera).

## Brak telemetrii

Program nie wysyła danych księgowych ani statystyk użycia. Połączenia sieciowe nawiązuje wyłącznie z usługami
skonfigurowanymi przez użytkownika (serwer PostgreSQL, KSeF, Object Storage). Statystyki kompilacji pakietu
Avalonia.BuildServices są wyłączone w procesie budowania.

## Zależności

Wersje bibliotek przypięte centralnie (`Directory.Packages.props`, także zależności przechodnie); biblioteki o
licencjach niezgodnych z dystrybucją programu (np. PDFsharp 6.5 — AGPL) są przypięte do wersji na licencji MIT.
Wykaz: [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
