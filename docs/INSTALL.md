# Instalacja programu — instrukcja dla użytkownika

## Wymagania

- Windows 10 (22H2) lub Windows 11, 64-bit.
- Ok. 400 MB wolnego miejsca na program + miejsce na dane i kopie zapasowe.
- **Nie trzeba** instalować .NET — środowisko jest dołączone do programu.
- Internet nie jest wymagany do księgowania; potrzebny tylko dla KSeF, opcjonalnego serwera PostgreSQL
  i kopii w OVHcloud Object Storage.

## Wariant A — instalator `.msi` (zalecany)

1. Pobierz `PelnaKsiegowosc-<wersja>-x64.msi` z zakładki **Releases**.
2. Sprawdź sumę kontrolną: w PowerShell `Get-FileHash .\PelnaKsiegowosc-<wersja>-x64.msi -Algorithm SHA256`
   i porównaj z wartością opublikowaną w opisie wydania.
3. Uruchom instalator i postępuj zgodnie z instrukcjami (wymagane uprawnienia administratora).
4. Uruchom „Pełna Księgowość PSA” z menu Start. Przy pierwszym uruchomieniu otworzy się kreator konfiguracji.

## Wariant B — pakiet przenośny `.zip`

1. Pobierz `PelnaKsiegowosc-win-x64.zip` i sprawdź sumę SHA-256 jak wyżej.
2. Rozpakuj do wybranego folderu (np. `C:\Programy\PelnaKsiegowosc`) — nie do folderu synchronizowanego
   z chmurą.
3. Uruchom `PelnaKsiegowosc.exe`.

## Gdzie są przechowywane dane

| Dane | Lokalizacja domyślna |
|---|---|
| Baza ksiąg (SQLite) | `%LOCALAPPDATA%\PelnaKsiegowosc\Dane\ksiegi.db` |
| Załączniki | `%LOCALAPPDATA%\PelnaKsiegowosc\Dane\Zalaczniki` |
| Kopie zapasowe | `%LOCALAPPDATA%\PelnaKsiegowosc\Kopie` (zmienialne w ustawieniach) |
| Hasła i klucze (serwer, S3, KSeF) | Menedżer poświadczeń Windows — nigdy w plikach |

## Aktualizacja

1. Wykonaj kopię zapasową (Backupy → Utwórz kopię) i sprawdź jej weryfikację.
2. Zainstaluj nowszy `.msi` (zastąpi poprzednią wersję) lub rozpakuj nowy `.zip` w miejsce starego.
3. Przy pierwszym uruchomieniu program wykona automatyczną kopię i migrację schematu bazy, a następnie
   kontrolę integralności. Dane nie są usuwane.

## Odinstalowanie

„Ustawienia → Aplikacje → Pełna Księgowość PSA → Odinstaluj”. Dane i kopie zapasowe **pozostają** na dysku
(obowiązek przechowywania ksiąg — art. 74 ustawy o rachunkowości). Usuń je ręcznie dopiero po upływie
wymaganych okresów przechowywania.

## Ostrzeżenie Windows SmartScreen

Jeżeli plik nie jest podpisany cyfrowo, Windows może wyświetlić ostrzeżenie. Instaluj wyłącznie pliki
pobrane z oficjalnej zakładki Releases i o zgodnej sumie SHA-256.
