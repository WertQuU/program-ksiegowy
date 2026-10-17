# Kompilacja i budowanie wersji instalacyjnej

## Wybrany format dystrybucji

Program jest dystrybuowany w dwóch postaciach — obie **nie wymagają instalowania .NET ani SDK** u użytkownika:

1. **Instalator `.msi`** (WiX Toolset 5) — zalecany: skrót w menu Start, odinstalowanie z „Aplikacje i funkcje”,
   aktualizacje przez uruchomienie nowszego MSI.
2. **Pakiet przenośny `.zip`** z plikiem `PelnaKsiegowosc.exe` (publikacja *self-contained* `win-x64`) —
   rozpakuj i uruchom, bez uprawnień administratora.

Dane użytkownika nigdy nie są zapisywane w katalogu programu, tylko w `%LOCALAPPDATA%\PelnaKsiegowosc`
(lub w lokalizacji wskazanej w ustawieniach) — aktualizacja i odinstalowanie nie usuwają ksiąg.

## 1. Wymagania

| Narzędzie | Wersja | Uwagi |
|---|---|---|
| .NET SDK | 10.0.1xx (LTS) | https://dotnet.microsoft.com/download/dotnet/10.0 — `global.json` dopuszcza nowsze łatki 10.0 |
| Git | dowolna aktualna | |
| System do budowania `.exe`/`.msi` | Windows 10/11 x64 | WPF i WiX budują się na Windows; biblioteki i testy także na Linux/macOS |
| WiX Toolset | 5.x | instalowany automatycznie jako pakiet NuGet `WixToolset.Sdk` podczas budowania MSI |
| PostgreSQL (opcjonalnie) | 14+ | tylko do testów integracyjnych z serwerem |

Sprawdzenie: `dotnet --version` powinno zwrócić `10.0.x`.

## 2. Pobranie kodu i przywrócenie pakietów

```powershell
git clone <adres-repozytorium> PelnaKsiegowosc
cd PelnaKsiegowosc
dotnet restore PelnaKsiegowosc.sln
dotnet tool restore        # narzędzie dotnet-ef (migracje)
```

## 3. Kompilacja i testy

```powershell
dotnet build PelnaKsiegowosc.sln -c Release
dotnet test  PelnaKsiegowosc.sln -c Release
```

## 4. Budowanie pakietu przenośnego (`.exe`)

```powershell
dotnet publish src/PelnaKsiegowosc.App/PelnaKsiegowosc.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:PublishReadyToRun=true `
  -o artifacts/PelnaKsiegowosc-win-x64

Compress-Archive artifacts/PelnaKsiegowosc-win-x64/* artifacts/PelnaKsiegowosc-win-x64.zip
```

Wynik: `artifacts/PelnaKsiegowosc-win-x64/PelnaKsiegowosc.exe` (ok. 150 MB z runtime .NET).

## 5. Budowanie instalatora `.msi`

```powershell
dotnet build installer/PelnaKsiegowosc.Installer.wixproj -c Release `
  -p:PublishDir=..\artifacts\PelnaKsiegowosc-win-x64\
```

Wynik: `artifacts/PelnaKsiegowosc-<wersja>-x64.msi`. Instalator instaluje program w
`C:\Program Files\Pełna Księgowość PSA`, tworzy skrót w menu Start i obsługuje aktualizację (MajorUpgrade).

## 6. Podpisanie cyfrowe

Niepodpisany `.exe`/`.msi` wywołuje ostrzeżenie Windows SmartScreen. Do dystrybucji publicznej podpisz pliki
certyfikatem Code Signing (np. przez `signtool sign /fd SHA256 /tr <serwer-znacznika-czasu> /td SHA256 ...`).
Certyfikatów i haseł **nie umieszczaj w repozytorium** — w CI używaj zaszyfrowanych sekretów GitHub.

## 7. Budowanie w GitHub Actions

Plik `.github/workflows/ci.yml` uruchamia kompilację i testy przy każdym wypchnięciu zmian (Windows i Linux).

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
| `PelnaKsiegowosc.Application` | silnik księgowy i moduły (dokumenty, bank, VAT, CIT, ŚT, kapitał PSA, raporty) |
| `PelnaKsiegowosc.Infrastructure` | eksport PDF/XLSX/CSV, kopie zapasowe, S3, sekrety, KSeF, JPK |
| `PelnaKsiegowosc.Presentation` | modele widoków (MVVM) — testowalne bez Windows |
| `PelnaKsiegowosc.Cli` | narzędzie wiersza poleceń (backup, odtwarzanie, kontrola integralności) |
| `PelnaKsiegowosc.App` | aplikacja WPF (Windows) — w przygotowaniu |
| `tests/PelnaKsiegowosc.Tests` | testy jednostkowe, integracyjne i scenariusze end-to-end |
