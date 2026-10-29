# Stan prac

Wersja: 0.9.0 (przed pierwszym wydaniem 1.0).

## Zrealizowane

- Silnik księgowy, okresy, zamknięcie roku z bilansem otwarcia, rozrachunki, kontrola integralności.
- Moduły: dokumenty i faktury (z korektami i załącznikami), bank i kasa, VAT i JPK_V7M, KSeF 2.0 (FA(3)),
  CIT, środki trwałe, kapitały i właściciele — dla różnych form prawnych jednostek.
- Raporty z eksportem PDF/XLSX/CSV; XML sprawozdania finansowego.
- Szyfrowane kopie zapasowe z weryfikacją i odtwarzaniem, kopie automatyczne, Object Storage (S3/OVHcloud),
  migracja SQLite → PostgreSQL, tryb awaryjny.
- Program okienkowy dla Windows i Linux (Avalonia): kreator pierwszego uruchomienia z akceptacją licencji, 20 sekcji.
- Narzędzie wiersza poleceń `pk-cli`.
- Pakiety: MSI i zip (Windows), deb i tar.gz (Linux); CI z testami na obu systemach i próbną instalacją pakietów.
- Testy automatyczne: jednostkowe, integracyjne (SQLite, PostgreSQL, S3), atrapa KSeF, E2E ekranów,
  uruchomienie programu okienkowego bez monitora.

- Ułatwienia dla osób bez przygotowania księgowego: asystent kontroli ksiąg z kalendarzem terminów, kategorie
  wydatków z regułami VAT/KUP, kreatory planu kont i rozliczenia miesiąca, księgowanie jednym kliknięciem,
  biała lista VAT, kurs NBP, rodzaje operacji bankowych, objaśnienia sekcji i słowniczek.

## Znane ograniczenia

Zob. [README](../README.md#znane-ograniczenia) i [COMPLIANCE_MATRIX.md](COMPLIANCE_MATRIX.md). Najważniejsze:
brak JPK_V7K i JPK dla osób fizycznych, brak PIT, JPK i e-Sprawozdanie bez walidacji oficjalnym XSD,
KSeF sprawdzony wyłącznie na atrapie API, brak inwentaryzacji i informacji dodatkowej.

## Weryfikacja wymagana przed użyciem produkcyjnym

Testy ręczne z [TEST_PLAN.md](TEST_PLAN.md#testy-ręczne-przed-wydaniem) oraz przegląd mapowań i wyników przez księgowego.
