# Instalacja programu — instrukcja dla użytkownika

Program nie wymaga instalowania .NET — środowisko jest dołączone do pakietów. Internet nie jest potrzebny
do księgowania; korzystają z niego tylko KSeF, opcjonalny serwer PostgreSQL i kopie w OVHcloud Object Storage.
Pakiety pobieraj wyłącznie z zakładki **Releases** repozytorium i sprawdzaj sumę SHA-256 z pliku `SHA256SUMS.txt`.

## Windows 10/11 (64-bit)

### Instalator `.msi` (zalecany)

1. Pobierz `PelnaKsiegowosc-<wersja>-x64.msi`.
2. Sprawdź sumę: `Get-FileHash .\PelnaKsiegowosc-<wersja>-x64.msi -Algorithm SHA256` (PowerShell).
3. Uruchom instalator, zaakceptuj licencję (wymagane uprawnienia administratora). Program trafi do
   `C:\Program Files\PelnaKsiegowosc`, skrót — do menu Start.
4. Uruchom „Pełna Księgowość”. Przy pierwszym uruchomieniu otworzy się kreator.

### Pakiet przenośny `.zip`

Rozpakuj `PelnaKsiegowosc-<wersja>-win-x64.zip` do wybranego folderu (nie do folderu synchronizowanego z chmurą)
i uruchom `PelnaKsiegowosc.exe`. Uprawnienia administratora nie są potrzebne.

Niepodpisany cyfrowo plik może wywołać ostrzeżenie Windows SmartScreen — instaluj tylko pliki o zgodnej sumie SHA-256.

## Linux (x64)

### Debian, Ubuntu i pochodne — pakiet `.deb`

```bash
sha256sum pelnaksiegowosc_<wersja>_amd64.deb        # porównaj z SHA256SUMS.txt
sudo apt install ./pelnaksiegowosc_<wersja>_amd64.deb
pelnaksiegowosc                                    # albo z menu aplikacji: „Pełna Księgowość”
```

Program instaluje się w `/opt/pelnaksiegowosc`, polecenia `pelnaksiegowosc` i `pk-cli` trafiają do `/usr/bin`.
Zalecany pakiet `libsecret-tools` (polecenie `secret-tool`) pozwala przechowywać hasła i klucze w pęku kluczy
systemu (GNOME Keyring, KWallet).

### Inne dystrybucje — archiwum `.tar.gz` (bez uprawnień administratora)

```bash
tar -xzf PelnaKsiegowosc-<wersja>-linux-x64.tar.gz
cd PelnaKsiegowosc-<wersja>-linux-x64
./install.sh            # instalacja do ~/.local/opt/pelnaksiegowosc, skrót w menu, polecenia w ~/.local/bin
```

Wymagane biblioteki systemowe (zwykle obecne w środowisku graficznym): fontconfig, libX11, libICE, libSM,
OpenSSL 3, ICU. Do eksportu PDF potrzebna jest czcionka DejaVu lub Liberation. Odinstalowanie: `./uninstall.sh`.

## Pierwsze uruchomienie

Kreator prowadzi przez: akceptację warunków licencji, wybór bazy (plik na tym komputerze albo serwer PostgreSQL),
dane i formę prawną jednostki, rok obrotowy, plan kont, politykę rachunkowości, kierownika jednostki i klucz
kopii zapasowych. **Klucz odzyskiwania kopii zapisz poza komputerem** — bez niego kopii nie da się odtworzyć.
Jeżeli wskazana baza zawiera już księgi (kolejne stanowisko na serwerze), kreator tylko podłącza stanowisko.
Szczegóły pracy z programem: [USER_GUIDE.md](USER_GUIDE.md).

## Gdzie są przechowywane dane

| Dane | Windows | Linux |
|---|---|---|
| Baza ksiąg (SQLite) | `%LOCALAPPDATA%\PelnaKsiegowosc\Dane\ksiegi.db` | `~/.local/share/PelnaKsiegowosc/Dane/ksiegi.db` |
| Załączniki | `…\Dane\Zalaczniki` | `…/Dane/Zalaczniki` |
| Kopie zapasowe | `%LOCALAPPDATA%\PelnaKsiegowosc\Kopie` | `~/.local/share/PelnaKsiegowosc/Kopie` |
| Konfiguracja (bez haseł) | `%LOCALAPPDATA%\PelnaKsiegowosc\konfiguracja.json` | `~/.local/share/PelnaKsiegowosc/konfiguracja.json` |
| Hasła i klucze | Menedżer poświadczeń Windows | pęk kluczy (Secret Service) albo zaszyfrowany plik `Sekrety/` z uprawnieniami 0600 |

Katalog danych można zmienić zmienną środowiskową `PELNAKSIEGOWOSC_HOME`; katalog kopii i załączników — w programie.

## Aktualizacja

1. Wykonaj kopię zapasową (Backupy → Wykonaj kopię teraz) i sprawdź, że jest zweryfikowana.
2. Zainstaluj nowszy pakiet (`.msi` zastępuje poprzednią wersję; `.deb` — `sudo apt install ./nowy.deb`;
   `.tar.gz` — ponowne `./install.sh`).
3. Przy pierwszym uruchomieniu program wykona kopię przed aktualizacją schematu bazy, a następnie migrację.
   Dane nie są usuwane.

## Odinstalowanie

Windows: „Ustawienia → Aplikacje → Pełna Księgowość → Odinstaluj”. Linux: `sudo apt remove pelnaksiegowosc`
albo `./uninstall.sh`. **Dane i kopie zapasowe pozostają na dysku** (obowiązek przechowywania ksiąg —
art. 74 ustawy o rachunkowości); usuń je ręcznie dopiero po upływie wymaganych okresów przechowywania.

## Narzędzie wiersza poleceń

`pk-cli` (Windows: `pk-cli.exe` w katalogu programu) wykonuje kopie, weryfikuje i odtwarza archiwa, sprawdza
integralność ksiąg i migruje dane na serwer PostgreSQL — np. do harmonogramu zadań. Opis: `pk-cli pomoc`.
