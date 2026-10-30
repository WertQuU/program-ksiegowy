# Schematy XSD

Pliki w tym katalogu są **niezmienionymi kopiami oficjalnych schematów** Ministerstwa Finansów.

| Katalog | Źródło | Data pobrania |
|---|---|---|
| `KSeF/FA3` | https://github.com/CIRFMF/ksef-docs — `faktury/schemy/FA/` (schemat_FA(3)_v1-0E.xsd oraz `bazowe/`) | 17.10.2026 |

Schemat FA(3) importuje `StrukturyDanych_v10-0E.xsd` spod adresu `http://crd.gov.pl/...`.
Aplikacja **nie pobiera** schematów z Internetu podczas walidacji — `XsdValidator` mapuje adresy
`crd.gov.pl` na lokalne kopie (offline, odporne na niedostępność serwera MF).

Sumy kontrolne: `KSeF/FA3/SHA256SUMS`.

## Schematy MF (`MF/`)

Pobierane automatycznie przez GitHub Actions (`.github/workflows/schematy.yml`, `schematy-esf.yml`) z serwerów MF
(crd.gov.pl, gov.pl, podatki.gov.pl) — środowisko budowania programu nie ma do nich dostępu. Manifest z adresami
i sumami SHA-256: `MF/manifest.json`, log: `MF/esf2/log.txt`.

| Struktura | Plik używany przez program | Źródło |
|---|---|---|
| JPK_V7M(3) / JPK_V7K(3) | `Schemat_JPK_V7M(3)_v1-0E.xsd`, `Schemat_JPK_V7K(3)_v1-0E.xsd` | crd.gov.pl/wzor/2025/12/19/14090, 14089 |
| JPK_V7M(2) / JPK_V7K(2) | `Schemat_JPK_V7M(2)_v1-0E.xsd`, `Schemat_JPK_V7K(2)_v1-0E.xsd` | crd.gov.pl/wzor/2021/12/27/11148, 11149 |
| JPK_KR_PD(1) v1-1 (JPK_CIT) | `Schemat_JPK_KR_PD(1)_v1-1.xsd` | gov.pl/web/kas/struktury-jpk-w-podatkach-dochodowych |
| e-Sprawozdanie SFJINZ(2) | `esf2/nazwane/Schemat_SFJINZ(2)_v1-0E.xsd` (+ SFJINT, SFJMAZ, SFJMIZ) | crd.gov.pl/wzor/2025/08/07/13817 |

Pliki w `MF/` są niezmienionymi kopiami; nazwy „Schemat_…” to kopie plików `schemat.xsd` pod nazwami używanymi
przez program, a `esf2/nazwane/` — kopie plików `zalacznik_oi_*` (przekierowania CRWDE) pod nazwami z `schemaLocation`.

### e-Deklaracje (`MF/edek`)

Pobierane automatycznie (`tools/fetch-edek.py`, workflow „Schematy e-Deklaracji”) z tabeli struktur dokumentów XML MF.
Program generuje i waliduje: VAT-UE(5) (`nazwane/Schemat_VAT-UE(5)_v2-0E.xsd`, CRWDE 2021/01/12/10293), VAT-UEK(5)
(`nazwane/Schemat_VAT-UEK(5)_v2-1E.xsd`, CRWDE 2021/01/26/10316) i PIT-11(29)
(`nazwane/Schemat_PIT-11(29)_v1-1E.xsd`, CRWDE 2022/11/09/11890). Wersje aktualne według podatki.gov.pl (październik 2026).
