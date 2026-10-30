# Kompilacja i budowanie wersji instalacyjnej

## Formaty dystrybucji

Wszystkie pakiety zawierają środowisko .NET (publikacja *self-contained*) — u użytkownika nie trzeba instalować .NET:

| System | Pakiet | Budowanie |
|---|---|---|
| Windows x64 | `PelnaKsiegowosc-<wersja>-x64.msi` (WiX 5) | tylko na Windows |
| Windows x64 | `PelnaKsiegowosc-<wersja>-win-x64.zip` (przenośny) | Windows lub Linux |
| Linux x64 | `pelnaksiegowosc_<wersja>_amd64.deb` | Linux (`dpkg-deb`) |
| Linux x64 | `PelnaKsiegowosc-<wersja>-linux-x64.tar.gz` z `install.sh` | Linux |

Interfejs jest napisany w Avalonia UI, więc ten sam kod działa na Windows i Linux. Dane użytkownika nigdy nie są
zapisywane w katalogu programu — aktualizacja i odinstalowanie nie usuwają ksiąg.

## 1. Wymagania

| Narzędzie | Wersja | Uwagi |
|---|---|---|
| .NET SDK | 10.0.1xx (LTS) | https://dotnet.microsoft.com/download/dotnet/10.0 — `global.json` dopuszcza nowsze łatki 10.0 |
| Git | dowolna aktualna | |
| WiX Toolset | 5.0.2 | pobierany automatycznie jako pakiet NuGet `WixToolset.Sdk` (tylko budowa MSI, tylko Windows) |
| dpkg-deb | systemowy | tylko budowa `.deb` |
| PostgreSQL (opcjonalnie) | 14+ | testy integracyjne z serwerem (`PK_TEST_PG`) |

Sprawdzenie: `dotnet --version` powinno zwrócić `10.0.x`.

## 2. Pobranie kodu i przywrócenie pakietów

```bash
git clone <adres-repozytorium> PelnaKsiegowosc
cd PelnaKsiegowosc
dotnet restore PelnaKsiegowosc.sln
dotnet tool restore        # narzędzie dotnet-ef (migracje)
```

## 3. Kompilacja, testy i uruchomienie

```bash
dotnet build PelnaKsiegowosc.sln -c Release
dotnet test  PelnaKsiegowosc.sln -c Release
dotnet run --project src/PelnaKsiegowosc.App          # program okienkowy
dotnet run --project src/PelnaKsiegowosc.Cli -- pomoc # narzędzie wiersza poleceń
```

Testy integracyjne z PostgreSQL i S3 uruchamiają się po ustawieniu `PK_TEST_PG` (łańcuch połączenia z użytkownikiem
z uprawnieniem CREATEDB) i `PK_TEST_S3_ENDPOINT` (np. lokalny `moto_server`); bez nich są pomijane. Szczegóły:
[TEST_PLAN.md](TEST_PLAN.md).

Kontrola uruchomienia bez okna (kreator na tymczasowej bazie, wszystkie ekrany, faktura → księgowanie, raport, kopia):

```bash
dotnet src/PelnaKsiegowosc.App/bin/Release/net10.0/PelnaKsiegowosc.dll --test-uruchomienia
```

## 4. Publikacja (pakiet przenośny)

```bash
# Windows (z Windows lub Linuksa)
dotnet publish src/PelnaKsiegowosc.App/PelnaKsiegowosc.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
dotnet publish src/PelnaKsiegowosc.Cli/PelnaKsiegowosc.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64

# Linux
dotnet publish src/PelnaKsiegowosc.App/PelnaKsiegowosc.App.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
dotnet publish src/PelnaKsiegowosc.Cli/PelnaKsiegowosc.Cli.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
```

Wynik: `PelnaKsiegowosc.exe`/`PelnaKsiegowosc` (program) i `pk-cli` w jednym katalogu (ok. 130 MB z runtime .NET).
Pakiet `.zip` na Windows: `Compress-Archive artifacts/win-x64/* PelnaKsiegowosc-win-x64.zip`.

## 5. Instalator `.msi` (Windows)

```powershell
dotnet build installer/PelnaKsiegowosc.Installer.wixproj -c Release -p:ProductVersion=1.0.0 "-p:AppPublishDir=$PWD/artifacts/win-x64" -o artifacts/msi
```

Instalator instaluje program w `C:\Program Files\PelnaKsiegowosc`, pokazuje licencję (`installer/Licencja.rtf`,
generowany z `LICENSE` skryptem `tools/licencja-rtf.py`), tworzy skrót w menu Start i obsługuje aktualizację
(MajorUpgrade). WiX działa wyłącznie na Windows.

## 6. Pakiety Linux (`.deb`, `.tar.gz`)

```bash
packaging/linux/build-packages.sh artifacts/linux-x64 1.0.0 artifacts/packages
```

## 7. Podpisanie cyfrowe i CI

Niepodpisany `.exe`/`.msi` wywołuje ostrzeżenie SmartScreen. Do dystrybucji publicznej podpisz pliki certyfikatem
Code Signing (`signtool sign /fd SHA256 /tr <serwer-znacznika-czasu> /td SHA256 ...`). Certyfikatów i haseł
**nie umieszczaj w repozytorium** — w CI używaj zaszyfrowanych sekretów GitHub.

`.github/workflows/ci.yml`: testy na Windows i Linux (na Linuksie z PostgreSQL i atrapą S3), publikacja,
kontrola uruchomienia opublikowanych programów, budowa i próbna instalacja `.deb` oraz `.msi`. Wypchnięcie tagu
`v<wersja>` (np. `v1.0.0`) tworzy wydanie z pakietami i plikiem `SHA256SUMS.txt`.

Statystyki kompilacji wysyłane przez pakiet Avalonia.BuildServices są wyłączone w `Directory.Build.targets`.

## 8. Migracje bazy danych (dla programistów)

Schemat bazy jest zarządzany migracjami EF Core — osobnymi dla każdego silnika:

```powershell
dotnet ef migrations add NazwaZmiany --project src/PelnaKsiegowosc.Migrations.Sqlite     --startup-project src/PelnaKsiegowosc.Migrations.Sqlite
dotnet ef migrations add NazwaZmiany --project src/PelnaKsiegowosc.Migrations.PostgreSql --startup-project src/PelnaKsiegowosc.Migrations.PostgreSql
```

Program stosuje migracje automatycznie przy starcie (po wykonaniu kopii zapasowej). Skrypt
`tools/regen-migrations.sh` odtwarza migracje początkowe i służy wyłącznie przed pierwszym wydaniem 1.0.

## Struktura rozwiązania

| Projekt | Rola |
|---|---|
| `PelnaKsiegowosc.Domain` | encje i reguły księgowe/podatkowe (bez zależności od bazy i UI) |
| `PelnaKsiegowosc.Data` | EF Core, konfiguracja modelu, profile połączeń, wyzwalacze niezmienności |
| `PelnaKsiegowosc.Migrations.Sqlite` / `.PostgreSql` | migracje schematu dla każdego silnika |
| `PelnaKsiegowosc.Application` | silnik księgowy i moduły (dokumenty, bank, VAT, CIT, ŚT, kapitały, raporty) |
| `PelnaKsiegowosc.Infrastructure` | eksport PDF/XLSX/CSV, kopie zapasowe, S3, sekrety, KSeF, JPK |
| `PelnaKsiegowosc.Presentation` | modele widoków (MVVM), kreator, sesja programu — testowalne bez interfejsu |
| `PelnaKsiegowosc.Cli` | narzędzie wiersza poleceń `pk-cli` (kopie, odtwarzanie, integralność, migracja) |
| `PelnaKsiegowosc.App` | aplikacja okienkowa Avalonia (Windows i Linux) |
| `installer/`, `packaging/linux/` | instalator MSI (WiX 5), pakiety deb i tar.gz |
| `tests/PelnaKsiegowosc.Tests` | testy jednostkowe, integracyjne i scenariusze end-to-end |
