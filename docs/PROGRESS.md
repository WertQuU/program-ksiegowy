# Stan prac

Wersja: 1.0.0 (w trakcie przeglądów; nowe wydanie nie jest planowane — numer wersji pozostaje 1.0.0).

## Zrealizowane

- Silnik księgowy, okresy, zamknięcie roku z bilansem otwarcia, rozrachunki, kontrola integralności.
- Moduły: dokumenty i faktury (z korektami, ratami i załącznikami), bank i kasa, VAT i JPK_V7M/V7K, KSeF 2.0
  (pobieranie faktur zakupu), CIT (zaliczki, 9%/19%, zyski kapitałowe, art. 18f, straty, podatek odroczony, JPK_CIT
  z RPD), środki trwałe, magazyn, inwentaryzacja, kapitały (w tym P.S.A.), płace i kadry, deklaracje.
- Raporty z eksportem PDF/XLSX/CSV; e-Sprawozdanie XML (zał. 1, 4, 5 UoR) z kontrolą kompletności.
- Bezpieczeństwo: szyfrowanie bazy (SQLCipher) i kopii, TLS dla serwera, role operatorów, dziennik zdarzeń, obsługa żądań RODO.
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

Aktualny wykaz: [ACCOUNTING_REQUIREMENTS.md § 14](ACCOUNTING_REQUIREMENTS.md#14-poza-zakresem-programu--wymagające-działań-użytkownika)
i [COMPLIANCE_MATRIX.md](COMPLIANCE_MATRIX.md). Najważniejsze: KSeF wyłącznie do pobierania faktur, brak JPK_ST_KR,
ulgi CIT i limit art. 15e — ręcznie, wysyłka plików do urzędów i KRS poza programem.

## Weryfikacja wymagana przed użyciem produkcyjnym

Testy ręczne z [TEST_PLAN.md](TEST_PLAN.md#testy-ręczne-przed-wydaniem) oraz przegląd mapowań i wyników przez księgowego.
