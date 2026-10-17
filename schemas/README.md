# Schematy XSD

Pliki w tym katalogu są **niezmienionymi kopiami oficjalnych schematów** Ministerstwa Finansów.

| Katalog | Źródło | Data pobrania |
|---|---|---|
| `KSeF/FA3` | https://github.com/CIRFMF/ksef-docs — `faktury/schemy/FA/` (schemat_FA(3)_v1-0E.xsd oraz `bazowe/`) | 17.10.2026 |

Schemat FA(3) importuje `StrukturyDanych_v10-0E.xsd` spod adresu `http://crd.gov.pl/...`.
Aplikacja **nie pobiera** schematów z Internetu podczas walidacji — `XsdValidator` mapuje adresy
`crd.gov.pl` na lokalne kopie (offline, odporne na niedostępność serwera MF).

Sumy kontrolne: `KSeF/FA3/SHA256SUMS`.

## Schematy, których nie dołączono

Schematów JPK_V7M(3) (CRWDE 2025/12/19/14090) oraz e-Sprawozdań finansowych (JednostkaInnaWZlotych)
nie udało się pobrać z tego środowiska (brak dostępu do crd.gov.pl / gov.pl). Aplikacja pozwala
wczytać je w *Ustawienia → Schematy XSD*; do tego czasu pliki JPK/e-SF mają status
„WYGENEROWANY — NIEZWALIDOWANY” i nie są oznaczane jako poprawne.
