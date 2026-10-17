# Licencje komponentów zewnętrznych

Wersje i licencje zweryfikowano 17.10.2026 na podstawie metadanych pakietów w nuget.org.
Wersje są zarządzane centralnie w `Directory.Packages.props` (z przypinaniem zależności przechodnich).

Komponenty poniżej zachowują własne licencje; ograniczenia licencji projektu (zakaz sprzedaży, § 3 pliku `LICENSE`)
dotyczą wyłącznie kodu projektu „Pełna Księgowość P.S.A.”. Licencje MIT, Apache-2.0 i PostgreSQL dopuszczają
łączenie z oprogramowaniem na innej licencji pod warunkiem zachowania informacji o prawach autorskich i licencji.

| Komponent | Wersja | Licencja | Zastosowanie |
|---|---|---|---|
| .NET Runtime / ASP.NET Core / WPF (Microsoft) | 10.0 | MIT | środowisko uruchomieniowe, interfejs Windows |
| Microsoft.EntityFrameworkCore (+ Sqlite, Relational, Design) | 10.0.13 | MIT | dostęp do bazy danych, migracje |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | sterownik SQLite |
| SQLitePCLRaw (bundle_e_sqlite3) | 2.1.x | Apache-2.0 | natywna biblioteka SQLite |
| SQLite | 3.x | domena publiczna | silnik bazy lokalnej |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.x | PostgreSQL License | sterownik PostgreSQL |
| AWSSDK.S3, AWSSDK.Core | 4.0.x | Apache-2.0 | OVHcloud Object Storage (API S3) |
| ClosedXML, ClosedXML.Parser | 0.105.1 / 2.0.0 | MIT | eksport XLSX |
| DocumentFormat.OpenXml | 3.x | MIT | zależność ClosedXML |
| ExcelNumberFormat | 1.1.0 | MIT | zależność ClosedXML |
| RBush.Signed | 4.0.0 | MIT | zależność ClosedXML |
| SixLabors.Fonts | 1.0.0 | Apache-2.0 | zależność ClosedXML |
| PDFsharp, PDFsharp-MigraDoc | 6.2.4 | MIT | eksport PDF |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | warstwa MVVM interfejsu |
| System.Security.Cryptography.ProtectedData / Pkcs | 10.0.13 | MIT | DPAPI, kryptografia |
| xUnit, Microsoft.NET.Test.Sdk, coverlet | 2.9 / 17.14 / 6.0 | Apache-2.0 / MIT / MIT | wyłącznie testy (nie są dystrybuowane) |
| WiX Toolset (narzędzie budowania instalatora) | 5.x | MS-RL | wyłącznie budowanie MSI — instalator wynikowy nie jest objęty MS-RL |

## Dokumenty urzędowe

| Plik | Źródło | Status |
|---|---|---|
| `schemas/KSeF/FA3/*.xsd` | Ministerstwo Finansów, https://github.com/CIRFMF/ksef-docs | niezmienione kopie; suma SHA-256 w `SHA256SUMS` |

## Czcionki w raportach PDF

Program nie dołącza plików czcionek. Do PDF osadzana jest czcionka systemowa (Windows: Arial lub Segoe UI;
Linux: Liberation Sans lub DejaVu Sans) zgodnie z licencją danej czcionki dopuszczającą osadzanie w dokumentach.
