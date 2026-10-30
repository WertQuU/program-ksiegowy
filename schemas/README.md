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

Schematów JPK_V7M(3) (CRWDE 2025/12/19/14090), JPK_V7K(3), JPK_KR_PD (JPK_CIT) oraz e-Sprawozdań
finansowych (JednostkaInnaWZlotych) nie udało się pobrać ze środowiska budowania (brak dostępu do crd.gov.pl / gov.pl).
W programie: *Ustawienia → Schematy XSD*:

- **Pobierz z serwera MF** — pobiera schemat JPK_V7M(3) z crd.gov.pl wraz ze schematami importowanymi
  (tylko z domen gov.pl);
- **Wczytaj z pliku…** — plik `.xsd` albo archiwum `.zip` pobrane ze strony MF (podatki.gov.pl).

Program generuje pliki JPK w przestrzeni nazw odczytanej z wczytanego schematu. Do czasu wczytania schematu pliki
JPK/e-SF mają status „WYGENEROWANY — NIEZWALIDOWANY” i nie są oznaczane jako poprawne.
